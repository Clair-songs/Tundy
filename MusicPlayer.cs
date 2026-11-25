using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Tundy;

/// <param name="SpotifyId">Set for Spotify links; the song is then looked up on YouTube.</param>
public sealed record SongRequest(string Url, string Sender, string? SpotifyId = null)
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Spotify details already known (from a playlist), so they don't have to be looked up again.</summary>
    public SpotifyTrack? Track { get; init; }

    /// <summary>Name of the playlist this song came from, if any.</summary>
    public string? Playlist { get; init; }
    public DateTime RequestedAt { get; } = DateTime.Now;

    /// <summary>The song's name once it's known (looked up in the background, or from the download).</summary>
    public string? Title { get; set; }

    public string Display => Title ?? (Track != null ? Track.Display : SpotifyId != null ? $"Spotify track {SpotifyId}" : Url);

    /// <summary>The song's length in seconds once it's known (from the lookup, the playlist or Spotify).</summary>
    public int? Seconds { get; set; }

    public int? KnownSeconds => Seconds ?? Track?.DurationSeconds;

    /// <summary>The YouTube video the song comes from (for Spotify links, the matched one once it's found), for its cover.</summary>
    public string? VideoId { get; set; }

    public string? CoverId => VideoId ?? RetryPolicy.YouTubeId(Url);

    /// <summary>The requester as the game sent it, for replying by tell (null for songs you added yourself).</summary>
    public byte[]? EncodedSender { get; init; }
}

/// <param name="ScdPath">Game sound file for sharing through Lightless, or null if not shared.</param>
public sealed record Track(string File, string Title, SongRequest Request, string? ScdPath = null);

public sealed record HistoryEntry(string Title, SongRequest Request, DateTime PlayedAt);

/// <summary>Downloads requests in order and plays them one after another.</summary>
public sealed class MusicPlayer : IDisposable
{
    private const int MaxHistory = 100;

    private readonly YtDlp ytDlp;
    private readonly SongSharer sharer;
    private readonly string cacheDir;
    private readonly SongLibrary library;

    /// <summary>Saved songs, for the Settings tab (size, clear).</summary>
    public SongLibrary Library => library;
    private volatile bool skipRequested;
    private readonly Configuration config;
    private readonly Action<string> notify;
    private readonly Action<SongRequest, string, bool> onFailed; // request, reason, whether retrying could help

    private readonly object queueLock = new();
    private readonly List<SongRequest> queue = new();
    private readonly List<HistoryEntry> history = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource cts = new();

    // The song after the current one is downloaded while the current one plays.
    private SongRequest? prefetchReq;
    private Task<Track?>? prefetchTask;
    private CancellationTokenSource? prefetchCts;

    private readonly object playLock = new();
    private WaveOutEvent? output;
    private VolumeSampleProvider? volume; // Tundy-only volume (never the device/session volume)
    private MediaFoundationReader? reader;

    public Track? Current { get; private set; }

    /// <summary>Raised (on a background thread) when a song starts playing.</summary>
    public event Action<Track>? TrackStarted;
    public SongRequest? Downloading { get; private set; }

    /// <summary>True while the current song is being played for other players through Lightless.</summary>
    public bool CurrentIsShared => Current?.ScdPath != null;

    /// <summary>True between applying a shared song and it starting (waiting for Lightless to sync).</summary>
    public bool WaitingForSync { get; private set; }

    public MusicPlayer(YtDlp ytDlp, SongSharer sharer, string cacheDir, Configuration config, Action<string> notify, Action<SongRequest, string, bool> onFailed)
    {
        this.ytDlp = ytDlp;
        this.sharer = sharer;
        this.cacheDir = cacheDir;
        library = new SongLibrary(Path.Combine(Path.GetDirectoryName(cacheDir)!, "library"), config);
        this.config = config;
        this.notify = notify;
        this.onFailed = onFailed;

        // Clear leftovers from previous sessions.
        try { if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true); } catch { /* ignored */ }
        Directory.CreateDirectory(cacheDir);

        Task.Run(() => LoopAsync(cts.Token));
    }

    // ------------------------------------------------------------------ queue state

    public SongRequest[] QueueSnapshot()
    {
        lock (queueLock) return queue.ToArray();
    }

    /// <summary>The prefetched song that plays next, and whether its download has finished.</summary>
    public (SongRequest? Request, bool Ready) UpNext
    {
        get
        {
            lock (queueLock) return (prefetchReq, prefetchTask?.IsCompletedSuccessfully == true && prefetchTask.Result != null);
        }
    }

    public HistoryEntry[] HistorySnapshot()
    {
        lock (queueLock) return history.AsEnumerable().Reverse().ToArray();
    }

    /// <summary>Songs waiting, including the one being downloaded and the prefetched one.</summary>
    public int PendingCount
    {
        get
        {
            lock (queueLock) return queue.Count + (prefetchReq != null ? 1 : 0) + (Downloading != null ? 1 : 0);
        }
    }

    // ------------------------------------------------------------------ queue control

    /// <param name="force">Ignore the queue length limit (used for your own manual adds).</param>
    public bool TryEnqueue(SongRequest req, bool force = false)
    {
        lock (queueLock)
        {
            var size = queue.Count + (prefetchReq != null ? 1 : 0) + (Downloading != null ? 1 : 0) + (Current != null ? 1 : 0);
            if (!force && size >= config.MaxQueueLength)
                return false;

            queue.Add(req);
            if (Current != null && prefetchTask == null)
                StartPrefetchLocked();
        }
        signal.Release();
        LookUpTitle(req);
        return true;
    }

    // At most two name lookups at once, so a big playlist doesn't start dozens of yt-dlp processes.
    private readonly SemaphoreSlim titleLookups = new(2);

    /// <summary>Finds the song's name in the background so the queue shows it instead of the link.</summary>
    private void LookUpTitle(SongRequest req)
    {
        if ((req.Title != null && req.KnownSeconds != null) || req.Track != null) return;
        _ = Task.Run(async () =>
        {
            await titleLookups.WaitAsync();
            try
            {
                if (req.Title != null && req.KnownSeconds != null) return; // the download got there first
                string? title;
                int? seconds;
                if (req.SpotifyId != null)
                {
                    var t = await SpotifyResolver.GetTrackAsync(await SpotifyResolver.ResolveIdAsync(req.SpotifyId, default), default);
                    (title, seconds) = (t.Display, t.DurationSeconds);
                }
                else
                {
                    var info = await ytDlp.GetInfoAsync(req.Url, default);
                    (title, seconds) = (info?.DisplayTitle, info?.Seconds);
                }
                if (!string.IsNullOrWhiteSpace(title)) req.Title ??= title;
                req.Seconds ??= seconds;
            }
            catch (Exception ex) { Plugin.Log.Debug(ex, $"Couldn't look up the name of {req.Url}"); }
            finally { titleLookups.Release(); }
        });
    }

    public void Remove(Guid id)
    {
        lock (queueLock)
        {
            if (prefetchReq?.Id == id)
            {
                CancelPrefetchLocked(requeue: false);
                if (Current != null) StartPrefetchLocked();
                return;
            }
            queue.RemoveAll(r => r.Id == id);
        }
    }

    /// <summary>
    /// The whole queue in play order: the song being prepared to play next (downloaded and converted early, while
    /// the current one plays) first, then the rest.
    /// </summary>
    public (SongRequest Request, bool IsNext)[] FullQueue()
    {
        lock (queueLock)
        {
            var list = new List<(SongRequest, bool)>();
            if (prefetchReq != null) list.Add((prefetchReq, true));
            list.AddRange(queue.Select(r => (r, false)));
            return list.ToArray();
        }
    }

    /// <summary>
    /// Changes the play order (prepared song included). If a different song ends up first, the early preparation
    /// of the old first song is dropped and the new first one is prepared instead.
    /// </summary>
    private void ReorderLocked(Func<List<SongRequest>, List<SongRequest>> change)
    {
        var all = new List<SongRequest>();
        if (prefetchReq != null) all.Add(prefetchReq);
        all.AddRange(queue);
        var reordered = change(all);

        if (prefetchReq != null && (reordered.Count == 0 || reordered[0].Id != prefetchReq.Id))
        {
            CancelPrefetchLocked(requeue: false);
            queue.Clear();
            queue.AddRange(reordered);
            if (Current != null) StartPrefetchLocked();
        }
        else
        {
            queue.Clear();
            queue.AddRange(prefetchReq != null ? reordered.Skip(1) : reordered);
        }
    }

    /// <summary>Moves a song up (negative) or down (positive) in the play order.</summary>
    public void Move(Guid id, int delta)
    {
        lock (queueLock)
        {
            ReorderLocked(all =>
            {
                var i = all.FindIndex(r => r.Id == id);
                var j = Math.Clamp(i + delta, 0, all.Count - 1);
                if (i < 0 || i == j) return all;
                var item = all[i];
                all.RemoveAt(i);
                all.Insert(j, item);
                return all;
            });
        }
    }

    /// <summary>Stops the current song and plays this queued or history song right away.</summary>
    public void PlayNow(SongRequest req)
    {
        lock (queueLock)
        {
            // Already prepared to play next: just skip to it.
            if (prefetchReq?.Id != req.Id)
            {
                CancelPrefetchLocked(requeue: true);
                queue.RemoveAll(r => r.Id == req.Id);
                queue.Insert(0, req);
            }
        }
        signal.Release();
        LookUpTitle(req);
        Skip();
    }

    /// <summary>Puts a song first in the queue (after the current one), ignoring the queue limit. Used by Retry.</summary>
    public void EnqueueNext(SongRequest req)
    {
        lock (queueLock)
            ReorderLocked(all =>
            {
                all.Insert(0, req);
                return all;
            });
        signal.Release();
        LookUpTitle(req);
    }

    public void Shuffle()
    {
        lock (queueLock)
            ReorderLocked(all => all.OrderBy(_ => Random.Shared.Next()).ToList());
    }

    public void ClearQueue()
    {
        lock (queueLock)
        {
            queue.Clear();
            CancelPrefetchLocked(requeue: false);
        }
    }

    public void ClearHistory()
    {
        lock (queueLock) history.Clear();
    }

    private void StartPrefetchLocked()
    {
        if (queue.Count == 0) return;
        var next = queue[0];
        queue.RemoveAt(0);
        prefetchReq = next;
        prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        prefetchTask = DownloadAsync(next, prefetchCts.Token);
    }

    private void CancelPrefetchLocked(bool requeue)
    {
        if (prefetchReq == null) return;
        if (requeue) queue.Insert(0, prefetchReq);

        var task = prefetchTask;
        prefetchCts?.Cancel();
        _ = task?.ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully || t.Result == null) return;
            if (!library.Contains(t.Result.File)) DeleteFile(t.Result.File);
            if (t.Result.ScdPath != null) DeleteFile(t.Result.ScdPath); // its converted copy for sharing
        });

        prefetchReq = null;
        prefetchTask = null;
        prefetchCts = null;
    }

    // ------------------------------------------------------------------ playback control

    public void Skip()
    {
        lock (playLock)
        {
            if (Current == null && !WaitingForSync) return;
            skipRequested = true;
            output?.Stop(); // triggers PlaybackStopped -> next track
        }
    }

    public void Stop()
    {
        ClearQueue();
        Skip();
    }

    public TimeSpan Position
    {
        get { lock (playLock) return reader?.CurrentTime ?? TimeSpan.Zero; }
    }

    public TimeSpan Duration
    {
        get { lock (playLock) return reader?.TotalTime ?? TimeSpan.Zero; }
    }

    public void Seek(TimeSpan to)
    {
        lock (playLock)
        {
            if (reader == null) return;
            var max = reader.TotalTime - TimeSpan.FromMilliseconds(250);
            reader.CurrentTime = to < TimeSpan.Zero ? TimeSpan.Zero : to > max ? max : to;
        }
    }

    public void ApplyVolume()
    {
        lock (playLock)
            if (volume != null)
                volume.Volume = VolumeFor(Current);
    }

    /// <summary>Tundy's own playback volume. While a song is shared, its in-game copy is what you hear instead.</summary>
    private float VolumeFor(Track? track) =>
        config.Muted || (track?.ScdPath != null && config.ShareSilenceLocal) ? 0f : Math.Clamp(config.Volume, 0f, 1f);

    /// <summary>
    /// Older versions set WaveOutEvent.Volume, which in Windows changes the volume of the whole game process
    /// (and Windows remembers it). Put it back to full so in-game audio works again.
    /// </summary>
    /// <param name="force">Always reset to 100%. Otherwise only when it's (almost) silent.</param>
    public static void RestoreGameVolume(bool force)
    {
        try
        {
            using var probe = new WaveOutEvent();
            probe.Init(new SilenceProvider(new WaveFormat(44100, 16, 2)));
            if (force || probe.Volume < 0.05f)
            {
                Plugin.Log.Information($"Restoring game audio volume (was {probe.Volume:P0})");
                probe.Volume = 1f;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not restore game audio volume");
        }
    }

    // ------------------------------------------------------------------ worker

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Track? track = null;
                Task<Track?>? ready;
                lock (queueLock) ready = prefetchTask;

                if (ready != null)
                {
                    track = await ready;
                    lock (queueLock)
                    {
                        // Only take it if it wasn't cancelled/replaced meanwhile.
                        if (prefetchTask == ready)
                        {
                            prefetchReq = null;
                            prefetchTask = null;
                            prefetchCts = null;
                        }
                        else
                        {
                            track = null;
                        }
                    }
                }
                else
                {
                    SongRequest? req = null;
                    lock (queueLock)
                    {
                        if (queue.Count > 0)
                        {
                            req = queue[0];
                            queue.RemoveAt(0);
                            Downloading = req;
                        }
                    }

                    if (req == null)
                    {
                        await signal.WaitAsync(ct);
                        continue;
                    }

                    try { track = await DownloadAsync(req, ct); }
                    finally { lock (queueLock) Downloading = null; }
                }

                if (track == null) continue;

                var playTask = PlayAsync(track, ct);
                lock (queueLock)
                    if (prefetchTask == null)
                        StartPrefetchLocked();

                await playTask;
                // Saved songs stay for instant replays (the library trims itself); other downloads are removed.
                if (config.DeleteAfterPlay && !library.Contains(track.File)) DeleteFile(track.File);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Music loop error");
            }
        }
    }

    private async Task<Track?> DownloadAsync(SongRequest req, CancellationToken ct)
    {
        try
        {
            var target = req.Url;
            string? spotifyName = null;
            if (req.SpotifyId != null)
            {
                var track = req.Track ?? await SpotifyResolver.GetTrackAsync(await SpotifyResolver.ResolveIdAsync(req.SpotifyId, ct), ct);
                var match = await SongMatcher.FindAsync(track, ytDlp, config.MaxDurationSeconds, ct,
                    s => Plugin.Log.Information($"Spotify match: {s}"));
                Plugin.Log.Information($"Spotify \"{track.Display}\" -> {match.VideoTitle} ({match.Url}, score {match.Score:F0})");
                target = match.Url;
                spotifyName = track.Display; // show Spotify's own "Artist - Title", not the YouTube video's title
            }

            req.VideoId ??= RetryPolicy.YouTubeId(target);
            var (file, downloadedTitle) = await DownloadWithRetriesAsync(req, target, ct);
            var title = spotifyName ?? downloadedTitle;
            req.Title = title;

            // Some of YouTube's m4a files read as silent in Windows' decoder; this fixes them (also saved copies).
            try
            {
                if (Mp4Fix.StripEditLists(file) > 0)
                    Plugin.Log.Information($"Fixed \"{title}\" so Windows can decode it (removed its edit list)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Plugin.Log.Debug(ex, $"Couldn't check {file} for edit lists");
            }

            string? scd = null;
            if (sharer.CanShare)
            {
                try
                {
                    scd = await Task.Run(() => sharer.BuildScd(file, ct), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Plugin.Log.Warning(ex, "Could not prepare song for sharing");
                    notify($"Couldn't prepare \"{title}\" for sharing, playing it just for you. ({ex.Message})");
                }
            }

            return new Track(file, title, req, scd);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (cts.IsCancellationRequested) throw;
            return null; // removed from the queue while downloading
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Download failed for {req.Url}");
            var (reason, canRetry) = RetryPolicy.Explain(ex);
            onFailed(req, reason, canRetry);
            notify($"Couldn't play {req.Sender}'s song: {reason}.");
            return null;
        }
    }

    private bool updatedYtDlp;

    /// <summary>
    /// Downloads with automatic retries for temporary failures (network, YouTube throttling or bot checks):
    /// waits a few seconds between tries, and updates yt-dlp once before the last try. Failures a retry can't
    /// fix (private, removed, age-restricted, too long, live...) aren't retried.
    /// </summary>
    private async Task<(string File, string Title)> DownloadWithRetriesAsync(SongRequest req, string target, CancellationToken ct)
    {
        // Saved from an earlier request? Then it starts right away.
        var videoId = RetryPolicy.YouTubeId(target);
        if (config.CacheSongs && videoId != null && library.TryGet(videoId) is { } saved)
        {
            Plugin.Log.Information($"Using saved copy of {videoId}: {saved.Title}");
            return saved;
        }

        var retries = config.AutoRetry ? Math.Clamp(config.RetryAttempts, 0, 5) : 0;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var keep = config.CacheSongs && videoId != null;
                var song = await ytDlp.DownloadAudioAsync(target, keep ? library.Folder : cacheDir, config.MaxDurationSeconds, ct,
                    keep ? videoId : null);
                var title = song.DisplayTitle;
                req.Seconds ??= song.Seconds;
                if (keep) library.Save(videoId!, title, song.File);
                return (song.File, title);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < retries && RetryPolicy.IsRetryable(ex))
            {
                var reason = RetryPolicy.Short(ex.Message);
                Plugin.Log.Warning($"Download failed for {target} (try {attempt + 1}/{retries + 1}): {reason}");
                notify($"Download failed for {req.Sender}'s song ({reason}). Retrying ({attempt + 1}/{retries})...");

                if (attempt == retries - 1 && !updatedYtDlp)
                {
                    updatedYtDlp = true; // once per session: YouTube changes often break older yt-dlp versions
                    try { await ytDlp.UpdateAsync(ct); } catch (Exception uex) when (uex is not OperationCanceledException) { Plugin.Log.Debug(uex, "yt-dlp update failed"); }
                }
                await Task.Delay(TimeSpan.FromSeconds(4 * (attempt + 1)), ct);
            }
        }
    }

    private async Task PlayAsync(Track track, CancellationToken ct)
    {
        skipRequested = false;

        if (track.ScdPath != null)
        {
            try
            {
                WaitingForSync = true;
                notify($"Sharing \"{track.Title}\" through Lightless, starting in a few seconds...");
                await sharer.PrepareAsync(track.ScdPath, () => skipRequested, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Plugin.Log.Warning(ex, "Sharing failed");
                notify($"Couldn't share through Lightless, playing it just for you. ({ex.Message})");
                track = track with { ScdPath = null };
            }
            finally
            {
                WaitingForSync = false;
            }

            if (skipRequested)
            {
                await sharer.ClearAsync(redrawCharacter: true);
                return;
            }
        }

        TaskCompletionSource done;
        lock (playLock)
        {
            reader = new MediaFoundationReader(track.File);
            // Volume is applied to Tundy's own samples. WaveOutEvent.Volume would change the volume of the
            // whole game process in Windows, which is what used to mute all in-game audio.
            volume = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = VolumeFor(track) };
            output = new WaveOutEvent();
            output.Init(volume);
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, _) => done.TrySetResult();
            Current = track;
            output.Play();
        }

        lock (queueLock)
        {
            history.Add(new HistoryEntry(track.Title, track.Request, DateTime.Now));
            if (history.Count > MaxHistory) history.RemoveAt(0);
        }

        notify($"Now playing: {track.Title} (from {track.Request.Sender})");
        TrackStarted?.Invoke(track);

        if (track.ScdPath != null && config.ShareAutoEmote)
        {
            var scd = track.ScdPath;
            _ = Task.Run(async () =>
            {
                try
                {
                    await sharer.StartForEveryoneAsync(scd, ct);
                    if (!sharer.LastCheckOk) notify(sharer.LastCheck);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Plugin.Log.Warning(ex, "Could not perform share emote");
                }
            });
        }

        using (ct.Register(() => done.TrySetResult()))
            await done.Task;

        // Take the song off your character. On skip/stop, redraw too so the in-game copy stops. When a song simply
        // ends there's nothing to stop (music mod songs only loop the silence at their end), so you keep dancing.
        if (track.ScdPath != null)
            await sharer.ClearAsync(redrawCharacter: skipRequested);

        lock (playLock)
        {
            output?.Dispose();
            reader?.Dispose();
            output = null;
            reader = null;
            volume = null;
            Current = null;
        }
    }

    private static void DeleteFile(string file)
    {
        try { File.Delete(file); } catch { /* ignored */ }
    }

    public void Dispose()
    {
        cts.Cancel();
        lock (playLock)
        {
            output?.Stop();
            output?.Dispose();
            reader?.Dispose();
        }
        try { Directory.Delete(cacheDir, true); } catch { /* ignored */ }
    }
}
