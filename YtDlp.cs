using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tundy;

/// <summary>One YouTube search result.</summary>
public sealed record YouTubeResult(string Id, string Title, int? Seconds, string? Channel, long? Views = null, string? Description = null)
{
    /// <summary>YouTube's own auto-generated upload of the official recording ("Artist - Topic" channels).</summary>
    public bool IsOfficialAudio =>
        (Description?.StartsWith("Provided to YouTube", StringComparison.OrdinalIgnoreCase) ?? false)
        || (Channel?.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>A downloaded song: the file, the raw video title, and YouTube's artist/track fields when it has them.</summary>
public sealed record SongDownload(string File, string Title, string? Artist, string? Track, int? Seconds = null)
{
    /// <summary>The name to show: "Artist - Track" when YouTube knows them, otherwise the cleaned-up video title.</summary>
    public string DisplayTitle => TitleCleaner.Display(Title, Artist, Track);
}

/// <summary>Thin wrapper around the yt-dlp command line tool.</summary>
public sealed class YtDlp
{
    // Official release assets of the yt-dlp and Deno projects.
    private const string DownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    private readonly string toolDir;
    private readonly Func<string> customPath;
    private readonly SemaphoreSlim installLock = new(1, 1);
    private readonly SemaphoreSlim denoLock = new(1, 1);

    public YtDlp(string toolDir, Func<string> customPath)
    {
        this.toolDir = toolDir;
        this.customPath = customPath;
        Directory.CreateDirectory(toolDir);
    }

    private string ManagedExe => Path.Combine(toolDir, "yt-dlp.exe");
    private string ManagedDeno => Path.Combine(toolDir, "deno.exe");

    // Soundy ships its own deno; using it saves a ~40 MB download when Soundy is installed.
    private string SoundyDeno => Path.Combine(Directory.GetParent(Directory.GetParent(toolDir)!.FullName)!.FullName, "Soundy", "tools", "deno.EXE");

    public string ExePath
    {
        get
        {
            var custom = customPath();
            return string.IsNullOrWhiteSpace(custom) ? ManagedExe : custom;
        }
    }

    public bool IsInstalled => File.Exists(ExePath);

    /// <summary>True while yt-dlp or deno is being downloaded (for the "Getting ready" card).</summary>
    public bool DownloadingYtDlp { get; private set; }
    public bool DownloadingDeno { get; private set; }

    /// <summary>Why the last download of a tool failed, or null.</summary>
    public string? LastToolError { get; set; }

    /// <summary>
    /// Connect to YouTube over IPv4 only (yt-dlp's -4). Turned on for the rest of the session after YouTube answers
    /// a download with HTTP 403 or 503, which often happens over IPv6 (YouTube blocks some IPv6 address ranges).
    /// </summary>
    public bool ForceIpv4 { get; set; }

    /// <summary>
    /// The JavaScript runtime yt-dlp uses to get past YouTube's download checks (deno), or null if none is there yet.
    /// Without it YouTube downloads fail more often and some audio formats are missing.
    /// </summary>
    public string? DenoPath => File.Exists(ManagedDeno) ? ManagedDeno : File.Exists(SoundyDeno) ? SoundyDeno : null;

    /// <summary>Downloads yt-dlp.exe if no copy exists yet.</summary>
    public async Task EnsureInstalledAsync(CancellationToken ct)
    {
        await installLock.WaitAsync(ct);
        try
        {
            if (File.Exists(ExePath))
                return;

            if (!string.IsNullOrWhiteSpace(customPath()))
                throw new FileNotFoundException($"yt-dlp not found at configured path: {customPath()}");

            Plugin.Log.Information("Downloading yt-dlp...");
            DownloadingYtDlp = true;
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Tundy-Dalamud-Plugin");
            var bytes = await http.GetByteArrayAsync(DownloadUrl, ct);
            var tmp = ManagedExe + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, ManagedExe, true);
            Plugin.Log.Information("yt-dlp installed.");
        }
        finally
        {
            DownloadingYtDlp = false;
            installLock.Release();
        }
    }

    /// <summary>Downloads deno (official release) if neither Tundy nor Soundy has a copy.</summary>
    public async Task EnsureDenoAsync(CancellationToken ct)
    {
        if (DenoPath != null) return;
        await denoLock.WaitAsync(ct);
        try
        {
            if (DenoPath != null) return;
            Plugin.Log.Information("Downloading deno (JavaScript runtime yt-dlp needs for YouTube)...");
            DownloadingDeno = true;
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Tundy-Dalamud-Plugin");
            var zip = await http.GetByteArrayAsync(DenoUrl, ct);
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("deno.exe not found in the download.");
            var tmp = ManagedDeno + ".tmp";
            entry.ExtractToFile(tmp, true);
            File.Move(tmp, ManagedDeno, true);
            Plugin.Log.Information("deno installed.");
        }
        finally
        {
            DownloadingDeno = false;
            denoLock.Release();
        }
    }

    public async Task<(int Exit, string Stdout, string Stderr)> UpdateAsync(CancellationToken ct)
    {
        await EnsureInstalledAsync(ct);
        var result = await RunAsync(new[] { "-U" }, ct);
        if (result.Exit != 0)
            Plugin.Log.Warning($"yt-dlp update failed: {result.Stderr}");
        else
            Plugin.Log.Information($"yt-dlp update: {result.Stdout.Trim().Split('\n').LastOrDefault()}");
        return result;
    }

    /// <summary>
    /// Downloads the audio of a single video into <paramref name="outDir"/> as "<paramref name="fileStem"/>.ext".
    /// Picks an m4a/AAC stream so no ffmpeg is required for playback via Media Foundation.
    /// </summary>
    public Task<SongDownload> DownloadAudioAsync(string url, string outDir, int maxDurationSeconds, CancellationToken ct, string? fileStem = null) =>
        DownloadAudioAsync(url, outDir, maxDurationSeconds, ct, fileStem, Gentle, AacFormat);

    // YouTube's best AAC (m4a, about 130 kbit/s): Windows can play it without ffmpeg.
    private const string AacFormat = "bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/bestaudio";

    /// <summary>
    /// Downloads the video's Opus audio (webm, about 130 kbit/s: at that size it sounds better than the AAC version).
    /// Windows can't play it, so it's only used as the source when ffmpeg converts a song for sharing. Throws when
    /// the video has no Opus version (rare); the caller then uses the AAC file.
    /// </summary>
    public Task<SongDownload> DownloadOpusAsync(string url, string outDir, int maxDurationSeconds, CancellationToken ct) =>
        DownloadAudioAsync(url, outDir, maxDurationSeconds, ct, Guid.NewGuid().ToString("N") + "-opus", Gentle, "bestaudio[acodec=opus]");

    /// <summary>
    /// Download gently: one piece at a time and longer waits between yt-dlp's own retries. Turned on for the rest of
    /// the session once a gentle try gets past a 403/503 that a normal one didn't.
    /// (Picking YouTube player types by hand was tried and left out: on 2026-11-24 every type other than yt-dlp's
    /// own default choice returned no audio at all.)
    /// </summary>
    public bool Gentle { get; private set; }

    private async Task<SongDownload> DownloadAudioAsync(string url, string outDir, int maxDurationSeconds, CancellationToken ct, string? fileStem, bool gentle, string format)
    {
        await EnsureInstalledAsync(ct);
        Directory.CreateDirectory(outDir);

        var stem = string.IsNullOrWhiteSpace(fileStem) ? Guid.NewGuid().ToString("N") : fileStem;
        var template = Path.Combine(outDir, stem + ".%(ext)s");

        var args = new List<string>
        {
            "--no-playlist",
            "--no-progress",
            "--no-warnings",
            "--restrict-filenames",
            // Normally several pieces at once (faster). Gently, one at a time: fewer requests in a burst, which is
            // what makes YouTube answer 503.
            "--concurrent-fragments", gentle ? "1" : "4",
        };
        if (gentle)
        {
            args.AddRange(new[]
            {
                "--retries", "6", "--fragment-retries", "6",
                "--retry-sleep", "http:exp=2:20", "--retry-sleep", "fragment:exp=2:20", // wait 2, 4, 8, 16, 20 s...
            });
        }
        args.AddRange(new[]
        {
            "--match-filter", $"duration <= {maxDurationSeconds} & !is_live",
            "-f", format,
            "-o", template,
            "--print", "after_move:%(title)s\t%(artist)s\t%(track)s\t%(duration)s",
            "--print", "after_move:filepath",
            "--", url,
        });

        var (exit, stdout, stderr) = await RunAsync(args.ToArray(), ct);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (exit != 0 || lines.Length < 2)
        {
            var reason = stderr.Trim();
            // Turned down by the length/live filter: find out which, so the reason is clear.
            if ((string.IsNullOrEmpty(reason) && lines.Length == 0) || reason.Contains("does not pass filter", StringComparison.OrdinalIgnoreCase))
                throw await RejectedAsync(url, maxDurationSeconds, ct);

            if (RetryPolicy.IsBlockedByYouTube(reason))
            {
                // 1. YouTube often refuses IPv6 connections with 403/503. Try again right away over IPv4, and keep
                //    using IPv4 for the rest of the session.
                if (!ForceIpv4)
                {
                    ForceIpv4 = true;
                    Plugin.Log.Warning($"YouTube refused the download of {url} ({RetryPolicy.Short(reason)}). Trying again over IPv4.");
                    return await DownloadAudioAsync(url, outDir, maxDurationSeconds, ct, fileStem, gentle, format);
                }

                // 2. Still refused: wait a moment, then try gently (see Gentle). If that works, keep it for the session.
                if (!gentle)
                {
                    Plugin.Log.Warning($"YouTube refused the download of {url} again ({RetryPolicy.Short(reason)}). "
                                       + "Trying gently: one piece at a time, with longer waits.");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    var song = await DownloadAudioAsync(url, outDir, maxDurationSeconds, ct, fileStem, gentle: true, format);
                    if (!Gentle)
                    {
                        Gentle = true;
                        Plugin.Log.Information("The gentle download worked; Tundy keeps downloading that way for the rest of the session.");
                    }
                    return song;
                }
            }
            throw new InvalidOperationException(reason.Length > 300 ? reason[..300] : reason);
        }

        var file = lines[^1];
        if (!File.Exists(file))
            throw new FileNotFoundException("yt-dlp reported a file that does not exist.", file);

        var (title, artist, track, seconds) = ParseInfo(lines[^2]);
        return new SongDownload(file, title, artist, track, seconds);
    }

    /// <summary>
    /// Why the length/live filter turned a video down: a live stream, a premiere that hasn't started, or too long.
    /// One quick extra look at the video, since yt-dlp only says the filter as a whole didn't pass.
    /// </summary>
    private async Task<SongRejectedException> RejectedAsync(string url, int maxDurationSeconds, CancellationToken ct)
    {
        var limit = RetryPolicy.Length(maxDurationSeconds);
        try
        {
            var (exit, stdout, _) = await RunAsync(new[]
            {
                "--no-playlist", "--no-warnings", "--skip-download",
                "--print", "%(duration)s\t%(live_status)s",
                "--", url,
            }, ct);
            var p = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()?.Split('\t');
            if (exit == 0 && p is { Length: >= 2 })
            {
                if (p[1] == "is_live") return new SongRejectedException("it's a live stream");
                if (p[1] == "is_upcoming") return new SongRejectedException("it's a premiere or stream that hasn't started yet");
                if (double.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                    && d > maxDurationSeconds)
                    return new SongRejectedException($"it's {RetryPolicy.Length((int)Math.Round(d))} long, and the limit is {limit}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Plugin.Log.Debug(ex, $"Couldn't check why {url} was turned down"); }
        return new SongRejectedException($"it's a live stream or longer than the {limit} limit");
    }

    /// <summary>A video's title and YouTube's artist/track fields, without downloading. Null if it can't be read.</summary>
    public async Task<SongDownload?> GetInfoAsync(string url, CancellationToken ct)
    {
        await EnsureInstalledAsync(ct);
        var (exit, stdout, _) = await RunAsync(new[]
        {
            "--no-playlist", "--no-warnings", "--skip-download",
            "--print", "%(title)s\t%(artist)s\t%(track)s\t%(duration)s",
            "--", url,
        }, ct);
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (exit != 0 || string.IsNullOrWhiteSpace(line)) return null;
        var (title, artist, track, seconds) = ParseInfo(line);
        return new SongDownload("", title, artist, track, seconds);
    }

    /// <summary>"title \t artist \t track \t duration" (as printed above) -> its parts; "NA" means unknown.</summary>
    private static (string Title, string? Artist, string? Track, int? Seconds) ParseInfo(string line)
    {
        static string? Field(string s) => s is "NA" or "" ? null : s.Trim();
        var p = line.Split('\t');
        if (p.Length < 3) return (line, null, null, null);
        int? seconds = p.Length >= 4 && double.TryParse(p[^1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : null;
        // A title with a tab in it: everything before the last 3 fields is the title.
        var titleParts = p.Length >= 4 ? p.Length - 3 : 1;
        return (string.Join('\t', p.Take(titleParts)), Field(p[titleParts]), Field(p[titleParts + 1]), seconds);
    }

    /// <summary>Quick YouTube search (no video pages opened): id, title, length and channel of the top results.</summary>
    public async Task<IReadOnlyList<YouTubeResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        await EnsureInstalledAsync(ct);
        var (exit, stdout, stderr) = await RunAsync(new[]
        {
            "--flat-playlist", "--no-warnings", "--skip-download",
            // Description goes last: it can contain line breaks, and only its start matters ("Provided to YouTube by").
            "--print", "%(id)s\t%(duration)s\t%(channel)s\t%(view_count)s\t%(title)s\t%(description).100s",
            "--", $"ytsearch{Math.Clamp(count, 1, 20)}:{query}",
        }, ct);
        if (exit != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "YouTube search failed." : stderr.Trim());

        static string? Field(string s) => s is "NA" or "" ? null : s;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var list = new List<YouTubeResult>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('\t');
            if (p.Length < 6 || p[0].Length != 11) continue;
            int? seconds = double.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out var d) ? (int)Math.Round(d) : null;
            long? views = long.TryParse(p[3], System.Globalization.NumberStyles.Integer, inv, out var v) ? v : null;
            list.Add(new YouTubeResult(p[0], p[4], seconds, Field(p[2]), views, Field(string.Join(' ', p.Skip(5)))));
        }
        return list;
    }

    /// <summary>Lists a YouTube playlist without opening each video: its name, and each video's id, title and length.</summary>
    public async Task<(string? Name, IReadOnlyList<YouTubeResult> Entries)> ListPlaylistAsync(string url, int maxItems, CancellationToken ct)
    {
        await EnsureInstalledAsync(ct);
        var (exit, stdout, stderr) = await RunAsync(new[]
        {
            "--flat-playlist", "--no-warnings", "--skip-download", "--yes-playlist",
            "--playlist-end", Math.Clamp(maxItems, 1, 500).ToString(),
            "--print", "%(id)s\t%(duration)s\t%(playlist_title)s\t%(title)s",
            "--", url,
        }, ct);
        if (exit != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "Couldn't read that YouTube playlist." : stderr.Trim());

        string? name = null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var list = new List<YouTubeResult>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('\t');
            if (p.Length < 4 || p[0].Length != 11) continue;
            int? seconds = double.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out var d) ? (int)Math.Round(d) : null;
            if (name == null && p[2] is not ("NA" or "")) name = p[2];
            list.Add(new YouTubeResult(p[0], string.Join('\t', p.Skip(3)), seconds, null));
        }
        if (list.Count == 0) throw new InvalidOperationException("That YouTube playlist is empty or private.");
        return (name, list);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // yt-dlp on Windows otherwise writes in the old cp1252 code page, which turns titles in other alphabets
        // (Japanese, Korean, Cyrillic...) into junk like "()".
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.ArgumentList.Add("--encoding");
        psi.ArgumentList.Add("utf-8");
        if (DenoPath is { } deno && args.FirstOrDefault() != "-U")
        {
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add($"deno:{deno}");
        }
        if (ForceIpv4 && args.FirstOrDefault() != "-U")
            psi.ArgumentList.Add("--force-ipv4");
        foreach (var a in args)
            psi.ArgumentList.Add(a); // ArgumentList escapes each argument: no shell injection from chat text.

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start yt-dlp.");
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* ignored */ }
            throw;
        }

        return (proc.ExitCode, await outTask, await errTask);
    }
}
