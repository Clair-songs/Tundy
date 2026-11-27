using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.Config;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Tundy;

/// <param name="FailedId">For failures: the entry in Plugin.Failed this line can be retried from.</param>
public sealed record RequestLogEntry(DateTime Time, string Sender, string Link, string Outcome, bool Accepted, Guid? FailedId = null);

/// <summary>A request that couldn't be played, kept so it can be retried by hand.</summary>
/// <param name="Song">The song, or null for a playlist that couldn't be read.</param>
/// <param name="Playlist">The playlist, for a playlist that couldn't be read.</param>
/// <param name="CanRetry">False when trying again can't help (private, removed, too long, ...).</param>
public sealed record FailedRequest(Guid Id, DateTime At, string Sender, byte[]? EncodedSender, string Url, string Display,
                                   string Reason, bool CanRetry, SongRequest? Song, PlaylistLink? Playlist, bool Shuffle);

/// <param name="Elapsed">Null when the start time isn't known.</param>
/// <param name="Source">"Music mod", "Lightless", "Tundy" or "Syncing".</param>
/// <param name="CoverId">YouTube video id for the song's cover, if known.</param>
public sealed record NowPlayingInfo(string? Title, string? Requester, TimeSpan? Elapsed, TimeSpan? Duration, string Source, string? CoverId = null);

public sealed partial class Plugin : IDalamudPlugin
{
    private const string MainCommand = "/tundy";
    private const int MaxLog = 200;

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    /// <summary>Song covers for the player (YouTube thumbnails).</summary>
    public CoverArt Covers { get; }

    /// <summary>The song as your character's title, through the Honorific plugin.</summary>
    public HonorificTitle Honorific { get; }

    /// <summary>A bigger font for the song's name in the player.</summary>
    public IFontHandle TitleFont { get; }

    public Configuration Config { get; }
    public MusicPlayer Player { get; }
    public SongSharer Sharer { get; }
    public MegaMusicMod Music { get; }
    public YtDlp YtDlp { get; }
    public FfmpegTool Ffmpeg { get; }

    /// <summary>Soundy's import tools: songs into a mod's playlist, and the game sound replacer.</summary>
    public ModImporter Importer { get; }

    /// <summary>Requests waiting for your approval.</summary>
    public PreQueue Pending { get; } = new();

    /// <summary>Optional "Now playing" announcements in a chat channel.</summary>
    public Announcer Announcer { get; }

    /// <summary>Optional tells to requesters about their song's place in the queue.</summary>
    public QueueReplier Replier { get; }

    private readonly WindowSystem windows = new("Tundy");
    private readonly MainWindow mainWindow;
    private readonly MiniPlayerWindow miniWindow;
    private readonly Dictionary<string, DateTime> lastRequest = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> lastSkip = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> lastList = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> lastPlaylist = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RequestLogEntry> requestLog = new();

    // "!skip"
    [GeneratedRegex(@"^\s*!skip\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SkipRegex();

    // "!skipall": remove all of your own songs
    [GeneratedRegex(@"^\s*!skipall\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SkipAllRegex();

    /// <summary>The fixed commands, which can't be used as request or shuffle commands.</summary>
    public static bool IsReservedCommand(string command) =>
        command.Equals("!skip", StringComparison.OrdinalIgnoreCase) || command.Equals("!skipall", StringComparison.OrdinalIgnoreCase);

    // "<command> <link>", e.g. "!q <link>"; which commands count is decided by RequestCommands.
    [GeneratedRegex(@"^\s*(?<cmd>\S+)\s+(?<url>\S+)\s*$")]
    private static partial Regex RequestRegex();

    // Only YouTube hosts are accepted, so senders can't make yt-dlp fetch arbitrary sites.
    [GeneratedRegex(@"^https?://(?:(?:www\.|m\.|music\.)?youtube\.com/(?:watch\?\S*v=|shorts/|live/)[\w-]{11}\S*|youtu\.be/[\w-]{11}\S*)$",
        RegexOptions.IgnoreCase)]
    internal static partial Regex YouTubeRegex();

    // Spotify single tracks: https://open.spotify.com/(intl-xx/)track/<id> or spotify:track:<id>
    [GeneratedRegex(@"^(?:https?://open\.spotify\.com/(?:intl-[\w-]+/)?track/|spotify:track:)(?<id>[A-Za-z0-9]{22})(?:[?#]\S*)?$",
        RegexOptions.IgnoreCase)]
    internal static partial Regex SpotifyRegex();

    // YouTube playlists: https://www.youtube.com/playlist?list=PL... (watch?v=...&list=... links stay single songs)
    [GeneratedRegex(@"^https?://(?:www\.|m\.|music\.)?youtube\.com/playlist\?(?:\S*?&)?list=(?<list>[\w-]{10,})\S*$", RegexOptions.IgnoreCase)]
    internal static partial Regex YouTubePlaylistRegex();

    // Spotify playlists and albums: https://open.spotify.com/(intl-xx/)playlist/<id>, .../album/<id>, spotify:playlist:<id>
    [GeneratedRegex(@"^(?:https?://open\.spotify\.com/(?:intl-[\w-]+/)?(?<kind>playlist|album)/|spotify:(?<kind>playlist|album):)(?<id>[A-Za-z0-9]{22})(?:[?#]\S*)?$",
        RegexOptions.IgnoreCase)]
    internal static partial Regex SpotifyCollectionRegex();

    // Spotify short links from the phone app's Share button: https://spotify.link/AbC123
    [GeneratedRegex(@"^https?://(?:spotify\.link|spotify\.app\.link)/[A-Za-z0-9]+/?(?:\?\S*)?$", RegexOptions.IgnoreCase)]
    internal static partial Regex SpotifyShortRegex();

    public Plugin()
    {
        Config = SettingsImporter.LoadOrImport(PluginInterface, out var importNotes);
        LoadPlaylistCooldowns();
        Migrate(); // also saves

        var dir = PluginInterface.GetPluginConfigDirectory();
        YtDlp = new YtDlp(Path.Combine(dir, "bin"), () => Config.YtDlpPath);
        Announcer = new Announcer(Config, Framework, ObjectTable);
        Replier = new QueueReplier(Config, Framework);
        Music = new MegaMusicMod(PluginInterface, ObjectTable, Config);
        Ffmpeg = new FfmpegTool(Path.Combine(dir, "bin"));
        Importer = new ModImporter(PluginInterface, Framework, DataManager, YtDlp, Ffmpeg, Config);
        Sharer = new SongSharer(PluginInterface, DataManager, Framework, ObjectTable, Chat, Config, Music, Ffmpeg, Path.Combine(dir, "share"));
        Player = new MusicPlayer(YtDlp, Sharer, Path.Combine(dir, "cache"), Config, Notify,
            OnSongFailed);
        Player.TrackStarted += t => Announcer.SongStarted(t.Title, t.Request.Sender);
        Covers = new CoverArt(TextureProvider, Path.Combine(dir, "covers"));
        Honorific = new HonorificTitle(PluginInterface, Framework, ObjectTable, Config, () =>
            (Player.Current?.Title, Player.CurrentIsShared, Player.WaitingForSync || Player.Downloading != null || Player.PendingCount > 0));
        TitleFont = PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var size = UiBuilder.DefaultFontSizePx * 1.35f;
            var font = tk.AddDalamudDefaultFont(size);
            tk.AttachExtraGlyphsForDalamudLanguage(new SafeFontConfig { SizePx = size, MergeFont = font });
        }));

        mainWindow = new MainWindow(this);
        miniWindow = new MiniPlayerWindow(this) { IsOpen = Config.ShowMiniPlayer };
        windows.AddWindow(mainWindow);
        windows.AddWindow(miniWindow);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;

        CommandManager.AddHandler(MainCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Tundy control panel. Subcommands: mini, play <link>, skip, stop, retry, approve [all], import, on, off, share, vol <0-100>, mute",
        });

        Chat.ChatMessage += OnChatMessage;

        foreach (var note in importNotes) Chat.Print($"[Tundy] {note}");
        if (TellTunesLoaded)
            Chat.PrintError("[Tundy] TellTunes is also on. Tundy ignores song requests until you turn TellTunes off in /xlplugins.");
        if (SoundyQListening)
            Chat.PrintError("[Tundy] SoundyQ (in Soundy) is also on and answers the same tells. Tundy ignores song requests until you turn SoundyQ off in /soundyq.");
        else if (SoundyQHoldsSongSlot)
            Chat.PrintError("[Tundy] SoundyQ (in Soundy) is on and holds the music mod's song slot, so nearby players won't hear Tundy's songs. Turn SoundyQ off in /soundyq.");

        ImportNotes = importNotes;
        if (Config.ShowWelcome)
            Chat.Print("[Tundy] Tundy is ready. Type /tundy to set it up (song requests are paused until you do).");

        StartToolDownloads(updateYtDlp: true);
    }

    /// <summary>ffmpeg is needed: songs are shared through the music mod (and the welcome guide is done).</summary>
    public bool NeedsFfmpeg => !Config.ShowWelcome && Config.ShareViaLightless && Config.ShareTarget == 0;

    /// <summary>What was copied from TellTunes and Soundy the first time (shown in the welcome guide).</summary>
    public IReadOnlyList<string> ImportNotes { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Gets the download tools ready in the background so the first request is fast: yt-dlp, deno (yt-dlp needs it
    /// for YouTube), ffmpeg when songs are shared through the music mod (not before the welcome guide is done, since
    /// you may not share at all), and a yt-dlp update at most once a day. The "Getting ready" card shows progress.
    /// </summary>
    public void StartToolDownloads(bool updateYtDlp = false)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            YtDlp.LastToolError = null;
            try { await YtDlp.EnsureInstalledAsync(default); }
            catch (Exception ex) { Log.Warning(ex, "Could not install yt-dlp yet"); YtDlp.LastToolError = $"yt-dlp: {ex.Message}"; return; }

            try { await YtDlp.EnsureDenoAsync(default); }
            catch (Exception ex) { Log.Warning(ex, "Could not install deno yet; YouTube downloads may fail more often"); YtDlp.LastToolError = $"deno: {ex.Message}"; }

            // Music mod songs need ffmpeg. Get Tundy's own copy now if there's none at all (not even Soundy's).
            if (NeedsFfmpeg && Ffmpeg.CurrentPath == null && !Ffmpeg.Downloading)
            {
                try { await Ffmpeg.DownloadAsync(default); }
                catch (Exception ex) { Log.Warning(ex, "Could not install ffmpeg yet"); YtDlp.LastToolError = $"ffmpeg: {ex.Message}"; }
            }

            if (!updateYtDlp) return;

            if (string.IsNullOrWhiteSpace(Config.YtDlpPath) && DateTime.UtcNow - Config.LastYtDlpUpdateUtc > TimeSpan.FromDays(1))
            {
                try
                {
                    var (exit, _, _) = await YtDlp.UpdateAsync(default);
                    if (exit == 0)
                    {
                        Config.LastYtDlpUpdateUtc = DateTime.UtcNow;
                        SaveConfig();
                    }
                }
                catch (Exception ex) { Log.Warning(ex, "Daily yt-dlp update failed"); }
            }
        });
    }

    /// <summary>Brings older settings (TellTunes' and earlier Tundy versions) up to date.</summary>
    private void Migrate()
    {
        if (Config.Version < 2)
        {
            // v2: Cheer Jump: Red music player became the default share emote.
            if (Config.ShareEmoteCommand == "/photograph") Config.ShareEmoteCommand = "/cheerjumpred";
            Config.Version = 2;
        }
        if (Config.Version < 3)
        {
            // v3: /shakedrink became the default share emote.
            Config.ShareEmoteCommand = "/shakedrink";
            Config.Version = 3;
        }
        if (Config.Version < 4)
        {
            // v4: /shakedrink loops and restarted the song every loop; /fume plays its sound once.
            // Also undo the old volume bug, which could leave the whole game's audio turned down.
            Config.ShareEmoteCommand = "/fume";
            Config.ShareSilenceLocal = true;
            Config.Version = 4;
            MusicPlayer.RestoreGameVolume(force: true);
        }
        else
        {
            MusicPlayer.RestoreGameVolume(force: false);
        }
        if (Config.Version < 5)
        {
            // v5 (Tundy 0.2): Tundy plays songs through the music mod itself instead of handing them to SoundyQ,
            // and the allow/block lists became per-player rules.
            Config.ShareTarget = 0;
            foreach (var name in Config.AllowedSenders) SetRuleInternal(name, RequestPermission.AutoApprove);
            foreach (var name in Config.BlockedSenders) SetRuleInternal(name, RequestPermission.Blocked);
            if (Config.UseAllowList) Config.QueueAccessMode = AccessMode.ListedOnly;
            Config.AllowedSenders.Clear();
            Config.BlockedSenders.Clear();
            Config.UseAllowList = false;
            if (Config.QueueAliases.Count == 0) Config.QueueAliases.Add("!q");
            Config.Version = 5;
        }

        // Before 0.4.2 every load added the default entry to these lists again ("sound/lolo.scd" 50 times, "!q"
        // back after you removed it). Saved lists now replace the defaults; this removes the copies.
        static List<string> Distinct(List<string> list) =>
            list.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Config.QueueAliases = Distinct(Config.QueueAliases);
        Config.ShuffleCommands = Distinct(Config.ShuffleCommands);
        Config.QueueScdRoutes = Distinct(Config.QueueScdRoutes);
        Config.ImportRoutes = Distinct(Config.ImportRoutes);
        if (Config.QueueAliases.Count == 0) Config.QueueAliases.Add("!q");
        if (Config.QueueScdRoutes.Count == 0) Config.QueueScdRoutes.Add(MegaMusicMod.RecommendedRoute);

        if (Config.Version < 6)
        {
            // v6: the Honorific title swaps every 15 seconds by default (a test build saved 5).
            if (Config.HonorificSwapSeconds == 5) Config.HonorificSwapSeconds = 15;
            Config.Version = 6;
        }

        // 0.4.6: the pastel on/off switch became a choice of looks; "off" was Dalamud's own look.
        if (!Config.PastelTheme)
        {
            Config.ThemeChoice = ThemeStyle.Dalamud;
            Config.PastelTheme = true;
        }

        // 0.4.3: "Include the artist" became one of the choices for what the Honorific title shows.
        if (Config.HonorificWithArtist)
        {
            Config.HonorificShow = HonorificTitle.ShowSongThenArtist;
            Config.HonorificWithArtist = false;
        }
        SaveConfig();
    }

    public void SaveConfig() => PluginInterface.SavePluginConfig(Config);


    // ------------------------------------------------------------------ request log

    public RequestLogEntry[] RequestLogSnapshot()
    {
        lock (requestLog) return requestLog.AsEnumerable().Reverse().ToArray();
    }

    public void ClearRequestLog()
    {
        lock (requestLog) requestLog.Clear();
    }

    private void AddLog(string sender, string link, string outcome, bool accepted, Guid? failedId = null)
    {
        lock (requestLog)
        {
            requestLog.Add(new RequestLogEntry(DateTime.Now, sender, link, outcome, accepted, failedId));
            if (requestLog.Count > MaxLog) requestLog.RemoveAt(0);
        }
    }

    // ------------------------------------------------------------------ failed requests (Retry)

    private const int MaxFailed = 20;
    private readonly List<FailedRequest> failed = new();

    /// <summary>Requests that couldn't be played, newest first.</summary>
    public FailedRequest[] FailedSnapshot()
    {
        lock (failed) return failed.AsEnumerable().Reverse().ToArray();
    }

    /// <summary>Whether this failure is still in the list (it can be retried or dismissed).</summary>
    public FailedRequest? FailedById(Guid id)
    {
        lock (failed) return failed.FirstOrDefault(f => f.Id == id);
    }

    private void RememberFailed(FailedRequest f)
    {
        lock (failed)
        {
            failed.Add(f);
            if (failed.Count > MaxFailed) failed.RemoveAt(0);
        }
    }

    /// <summary>
    /// A song that still failed after the automatic retries: log it, keep it for Retry, and (unless turned off) tell
    /// the requester why when it can't ever play, so they can send another link.
    /// </summary>
    private void OnSongFailed(SongRequest req, string reason, bool canRetry)
    {
        var f = new FailedRequest(Guid.NewGuid(), DateTime.Now, req.Sender, req.EncodedSender, req.Url, req.Display, reason, canRetry, req, null, false);
        RememberFailed(f);
        AddLog(req.Sender, req.Url, $"Failed: {reason}", false, f.Id);
        if (!canRetry && Config.ReplyWhyRejected && req.EncodedSender is { Length: > 0 } who)
            Replier.Send(who, req.Sender, $"Sorry, your song can't be played: {reason}. Try another link.", always: true);
    }

    /// <summary>Tries a failed request again: a song goes in as the next song, a playlist is read again.</summary>
    public bool Retry(Guid id)
    {
        FailedRequest? f;
        lock (failed)
        {
            f = failed.FirstOrDefault(x => x.Id == id);
            if (f == null || !f.CanRetry) return false;
            failed.Remove(f);
        }

        if (f.Playlist != null)
        {
            HandlePlaylist(f.EncodedSender ?? Array.Empty<byte>(), f.Sender, f.Playlist, shuffle: f.Shuffle);
        }
        else if (f.Song is { } s)
        {
            // A fresh request (new id) with everything already known about the song.
            Player.EnqueueNext(new SongRequest(s.Url, s.Sender, s.SpotifyId)
            {
                Track = s.Track,
                Playlist = s.Playlist,
                Title = s.Title,
                Seconds = s.Seconds,
                EncodedSender = s.EncodedSender,
            });
        }
        AddLog(f.Sender, f.Url, "Retrying (by hand)", true);
        Notify($"Trying {f.Sender}'s {(f.Playlist != null ? "playlist" : "song")} again.");
        return true;
    }

    /// <summary>The newest failure that can be retried, if any.</summary>
    public FailedRequest? LatestRetryable()
    {
        lock (failed) return failed.LastOrDefault(f => f.CanRetry);
    }

    public void DismissFailed(Guid id)
    {
        lock (failed) failed.RemoveAll(f => f.Id == id);
    }

    public void ClearFailed()
    {
        lock (failed) failed.Clear();
    }

    // ------------------------------------------------------------------ other plugins answering the same tells

    private bool tellTunesLoaded;
    private DateTime tellTunesCheckedAt = DateTime.MinValue;

    /// <summary>
    /// True while the TellTunes plugin is loaded too (checked every 2 seconds). Both would answer the same
    /// "!q" tells, so Tundy ignores requests until TellTunes is off.
    /// </summary>
    public bool TellTunesLoaded
    {
        get
        {
            if ((DateTime.Now - tellTunesCheckedAt).TotalSeconds < 2) return tellTunesLoaded;
            tellTunesCheckedAt = DateTime.Now;
            try { tellTunesLoaded = PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == "TellTunes"); }
            catch (Exception ex) { Log.Debug(ex, "Couldn't check for TellTunes"); }
            return tellTunesLoaded;
        }
    }

    private bool soundyQListening;
    private DateTime soundyCheckedAt = DateTime.MinValue;
    private DateTime soundyFileWrite = DateTime.MinValue;
    private bool soundyQEnabled;
    private List<string> soundyQAliases = new();

    /// <summary>
    /// True while Soundy is loaded with SoundyQ turned on and listening for one of Tundy's request commands
    /// (checked every 2 seconds, from Soundy's settings file). Both would queue the same song and swap the same
    /// sound file, so Tundy ignores requests until SoundyQ is off. Soundy's other tools can stay on.
    /// </summary>
    public bool SoundyQListening
    {
        get
        {
            RefreshSoundyQ();
            return soundyQListening;
        }
    }

    private bool soundyQOn;

    /// <summary>
    /// True while SoundyQ is on and songs are shared through the music mod. SoundyQ puts its song on the music mod's
    /// song slot (sound/lolo.scd) as a Penumbra temporary mod, and temporary mods win over normal ones like "Tundy
    /// Song", whatever request commands either one uses. Nearby players then hear SoundyQ's song (or nothing)
    /// instead of Tundy's. Also true when the game was seen loading SoundyQ's file on the slot in the last 10 minutes.
    /// </summary>
    public bool SoundyQHoldsSongSlot
    {
        get
        {
            RefreshSoundyQ();
            if (!Sharer.CanShare || !Sharer.UsesMusicMod) return false;
            return soundyQOn || (soundyLoaded && (DateTime.Now - Sharer.SoundyQSeenOnSlotAt).TotalMinutes < 10);
        }
    }

    private bool soundyLoaded;

    private void RefreshSoundyQ()
    {
        if ((DateTime.Now - soundyCheckedAt).TotalSeconds < 2) return;
        soundyCheckedAt = DateTime.Now;
        try
        {
            soundyLoaded = PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == "Soundy");
            if (!soundyLoaded)
            {
                soundyQListening = soundyQOn = false;
                return;
            }

            var file = Path.Combine(Directory.GetParent(PluginInterface.GetPluginConfigDirectory())!.FullName, "Soundy.json");
            if (File.Exists(file) && File.GetLastWriteTimeUtc(file) != soundyFileWrite)
            {
                soundyFileWrite = File.GetLastWriteTimeUtc(file);
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                soundyQEnabled = root.TryGetProperty("SoundyQEnabled", out var en) && en.ValueKind == JsonValueKind.True;
                soundyQAliases = root.TryGetProperty("SoundyQAliases", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).ToList()
                    : new List<string> { "!ytqueue" };
            }
            soundyQOn = soundyQEnabled;
            soundyQListening = soundyQEnabled && soundyQAliases.Intersect(RequestCommands, StringComparer.OrdinalIgnoreCase).Any();
        }
        catch (IOException) { /* Soundy is writing the file; check again later */ }
        catch (Exception ex) { Log.Debug(ex, "Couldn't check SoundyQ"); }
    }

    /// <summary>Opens SoundyQ's window (to turn it off there).</summary>
    public void OpenSoundyQ() => CommandManager.ProcessCommand("/soundyq");

    // ------------------------------------------------------------------ who can request

    /// <summary>The commands people request songs with ("!q", "!play", ...), from the Access tab.</summary>
    public IReadOnlyList<string> RequestCommands
    {
        get
        {
            var list = Config.QueueAliases.Select(a => a.Trim()).Where(a => a.Length > 0 && !a.Contains(' '))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return list.Count > 0 ? list : new List<string> { "!q" };
        }
    }

    /// <summary>
    /// The commands that queue a playlist shuffled ("!shuffle", ...), from the Access tab. Empty while they're turned
    /// off. A request command with the same name stays a normal request.
    /// </summary>
    public IReadOnlyList<string> ShuffleCommands =>
        !Config.AllowShuffleCommand
            ? Array.Empty<string>()
            : Config.ShuffleCommands.Select(a => a.Trim())
                .Where(a => a.Length > 0 && !a.Contains(' ') && !IsReservedCommand(a)
                            && !RequestCommands.Contains(a, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The player's own rule, if they have one.</summary>
    public QueuePlayerRule? RuleFor(string name, string? world = null) =>
        Config.QueuePlayerRules.FirstOrDefault(r => r.Enabled && Names.Same(r.Name, name)
            && (r.World.Length == 0 || world == null || r.World.Equals(world, StringComparison.OrdinalIgnoreCase)));

    /// <summary>What happens to a request from this person: their own rule, otherwise the general setting.</summary>
    public RequestPermission PermissionFor(string name, string? world = null)
    {
        if (name == "You") return RequestPermission.AutoApprove;
        if (RuleFor(name, world) is { } rule) return rule.Permission;
        return Config.QueueAccessMode switch
        {
            AccessMode.EveryonePreQueue => RequestPermission.PreQueue,
            AccessMode.ListedOnly => RequestPermission.Blocked,
            _ => RequestPermission.AutoApprove,
        };
    }

    /// <summary>Gives a player their own permission (replacing an existing rule for them).</summary>
    public void SetRule(string name, RequestPermission permission)
    {
        if (name == "You") return;
        SetRuleInternal(name, permission);
        SaveConfig();
    }

    private void SetRuleInternal(string name, RequestPermission permission)
    {
        name = Names.Clean(name);
        if (name.Length == 0) return;
        var rule = Config.QueuePlayerRules.FirstOrDefault(r => Names.Same(r.Name, name));
        if (rule == null) Config.QueuePlayerRules.Add(rule = new QueuePlayerRule { Name = name });
        rule.Permission = permission;
        rule.Enabled = true;
    }

    public void RemoveRule(string name)
    {
        Config.QueuePlayerRules.RemoveAll(r => Names.Same(r.Name, name));
        SaveConfig();
    }

    public bool IsBlocked(string name) => name != "You" && RuleFor(name)?.Permission == RequestPermission.Blocked;

    // ------------------------------------------------------------------ !skip

    /// <summary>"!skip" by tell: skips the song that's playing, if the sender may (see <see cref="Configuration.SkipPermission"/>).</summary>
    private void HandleSkipRequest(IHandleableChatMessage message)
    {
        var sender = SenderName(message);
        var encoded = message.Sender.Encode();
        var world = QueueReplier.Target(encoded)?.World;

        void Done(string outcome, bool ok, string? reply)
        {
            AddLog(sender, "!skip", outcome, ok);
            if (reply != null) Replier.Send(encoded, sender, reply);
        }

        var title = Player.Current?.Title;
        var requester = Player.Current?.Request.Sender;
        var permission = PermissionFor(sender, world);

        if (Config.SkipPermission == 0) { Done("Ignored: !skip is turned off", false, null); return; }
        if (!Config.Enabled) { Done("Ignored: requests are paused", false, null); return; }
        if (permission == RequestPermission.Blocked) { Done("Ignored: sender can't request songs", false, null); return; }
        if (title == null) { Done("Ignored: nothing is playing", false, "Nothing is playing right now."); return; }

        var isRequester = requester != null
            && (Names.Same(requester, sender)
                || (requester == "You" && Names.Same(ObjectTable.LocalPlayer?.Name.TextValue ?? "", sender)));
        var allowed = Config.SkipPermission == 2 || isRequester;
        if (!allowed)
        {
            Done($"Ignored: \"{title}\" was requested by {requester ?? "someone else"}", false, "You can only skip songs you requested.");
            return;
        }

        // A short wait between skips from the same person, so a few tells can't run through the whole queue.
        var now = DateTime.UtcNow;
        if (lastSkip.TryGetValue(sender, out var last) && (now - last).TotalSeconds < 10)
        {
            Done("Ignored: skipped less than 10 seconds ago", false, null);
            return;
        }
        lastSkip[sender] = now;

        Skip();
        Done($"Skipped \"{title}\"", true, $"Skipped \"{title}\".");
        Notify($"{sender} skipped \"{title}\".");
    }

    /// <summary>
    /// "!skipall": removes all of the sender's own songs: the one playing (or about to start), the ones waiting in
    /// the queue (also the one prepared to play next, and their playlist songs) and their requests waiting for
    /// approval. Nobody else's songs are touched. Songs you added yourself never count as theirs.
    /// </summary>
    private void HandleSkipAll(IHandleableChatMessage message)
    {
        var sender = SenderName(message);
        var encoded = message.Sender.Encode();
        var world = QueueReplier.Target(encoded)?.World;

        void Done(string outcome, bool ok, string? reply)
        {
            AddLog(sender, "!skipall", outcome, ok);
            if (reply != null) Replier.Send(encoded, sender, reply);
        }

        if (!Config.AllowSkipAll) { Done("Ignored: !skipall is turned off", false, null); return; }
        if (!Config.Enabled) { Done("Ignored: requests are paused", false, null); return; }
        if (PermissionFor(sender, world) == RequestPermission.Blocked) { Done("Ignored: sender can't request songs", false, null); return; }

        // The same short wait as !skip, shared with it.
        var now = DateTime.UtcNow;
        if (lastSkip.TryGetValue(sender, out var last) && (now - last).TotalSeconds < 10)
        {
            Done("Ignored: skipped less than 10 seconds ago", false, null);
            return;
        }
        lastSkip[sender] = now;

        bool Theirs(string requester) => requester != "You" && Names.Same(requester, sender);

        var queued = Player.RemoveWhere(r => Theirs(r.Sender));
        var waiting = Pending.TakeWhere(p => Theirs(p.Sender)).Length;
        var playing = Player.Current?.Request ?? Player.Syncing?.Request;
        var skipPlaying = playing != null && Theirs(playing.Sender);
        if (skipPlaying) Skip();

        var removed = queued + waiting;
        var total = removed + (skipPlaying ? 1 : 0);
        if (total == 0)
        {
            Done("Nothing of theirs to skip", false, "You don't have any songs in the queue.");
            return;
        }

        var reply = removed > 0
            ? $"Removed your {removed} song{(removed == 1 ? "" : "s")} from the queue{(skipPlaying ? " and skipped the one playing" : "")}."
            : "Skipped your song that was playing.";
        Done($"Skipped all their songs ({total})", true, reply);
        Notify($"{sender} removed all {total} of their song{(total == 1 ? "" : "s")}.");
    }

    // ------------------------------------------------------------------ links

    /// <summary>Recognizes YouTube playlist links and Spotify playlist/album links.</summary>
    private static PlaylistLink? ParsePlaylist(string url)
    {
        var yt = YouTubePlaylistRegex().Match(url);
        if (yt.Success) return new PlaylistLink("youtube", yt.Groups["list"].Value, url);
        var sp = SpotifyCollectionRegex().Match(url);
        if (sp.Success)
        {
            var kind = sp.Groups["kind"].Value.ToLowerInvariant();
            var id = sp.Groups["id"].Value;
            return new PlaylistLink(kind, id, $"https://open.spotify.com/{kind}/{id}");
        }
        return null;
    }

    /// <summary>Why a playlist link can't be used (or null if it can).</summary>
    private string? PlaylistProblem(PlaylistLink link)
    {
        if (!Config.AllowPlaylists) return "Playlists are turned off";
        if (link.IsSpotify && !Config.AllowSpotify) return "Spotify links are turned off";
        // YouTube Mixes (RD...) and auto-generated radio lists never end.
        if (!link.IsSpotify && link.Id.StartsWith("RD", StringComparison.Ordinal)) return "YouTube Mixes aren't supported (they never end)";
        return null;
    }

    /// <summary>Checks a link and returns the Spotify ID (or null for YouTube). Returns false if unsupported.</summary>
    private bool TryParseLink(string url, out string? spotifyId, out string error)
    {
        spotifyId = null;
        error = string.Empty;
        if (YouTubeRegex().IsMatch(url))
            return true;

        var sp = SpotifyRegex().Match(url);
        if (sp.Success || SpotifyShortRegex().IsMatch(url))
        {
            if (!Config.AllowSpotify)
            {
                error = "Spotify links are turned off";
                return false;
            }
            // Short links are resolved to a track ID when the song is looked up.
            spotifyId = sp.Success ? sp.Groups["id"].Value : url;
            return true;
        }

        error = "Not a YouTube or Spotify track link";
        return false;
    }

    /// <summary>Checks a link; on success it's either a playlist or a song (with its Spotify ID, if any).</summary>
    private bool TryParseRequest(string url, out PlaylistLink? playlist, out string? spotifyId, out string error)
    {
        playlist = ParsePlaylist(url);
        spotifyId = null;
        if (playlist != null)
        {
            error = PlaylistProblem(playlist) ?? string.Empty;
            return error.Length == 0;
        }
        return TryParseLink(url, out spotifyId, out error);
    }

    /// <summary>The same song already waiting, playing or up next? (YouTube video or Spotify track.)</summary>
    private bool IsDuplicate(string url, string? spotifyId)
    {
        var video = RetryPolicy.YouTubeId(url);
        if (video == null && spotifyId == null) return false;

        bool Same(string otherUrl, string? otherSpotify) =>
            (video != null && RetryPolicy.YouTubeId(otherUrl) == video)
            || (spotifyId != null && otherSpotify != null && spotifyId == otherSpotify);

        var requests = Player.QueueSnapshot().AsEnumerable();
        if (Player.UpNext.Request is { } next) requests = requests.Append(next);
        if (Player.Downloading is { } dl) requests = requests.Append(dl);
        if (Player.Current is { } cur) requests = requests.Append(cur.Request);
        return requests.Any(r => Same(r.Url, r.SpotifyId))
               || Pending.Snapshot().Any(p => p.Playlist == null && Same(p.Url, p.SpotifyId));
    }

    // ------------------------------------------------------------------ requests

    /// <summary>How much longer this person has to wait before another playlist, or null if they don't.</summary>
    private TimeSpan? PlaylistCooldownLeft(string sender)
    {
        if (sender == "You" || !lastPlaylist.TryGetValue(sender, out var at)) return null;
        var left = TimeSpan.FromSeconds(Config.PlaylistCooldownSeconds) - (DateTime.UtcNow - at);
        return left > TimeSpan.Zero ? left : null;
    }

    /// <summary>Starts this person's playlist cooldown, saved so it still counts after Tundy reloads.</summary>
    private void RememberPlaylist(string sender, DateTime utc)
    {
        lastPlaylist[sender] = utc;
        Config.LastPlaylistRequests[sender] = utc;
        SaveConfig();
    }

    /// <summary>Loads the saved playlist cooldowns that haven't run out yet, and forgets the rest.</summary>
    private void LoadPlaylistCooldowns()
    {
        var keep = TimeSpan.FromSeconds(Math.Max(0, Config.PlaylistCooldownSeconds));
        foreach (var (name, at) in Config.LastPlaylistRequests.ToList())
        {
            if (DateTime.UtcNow - at < keep) lastPlaylist[name] = at;
            else Config.LastPlaylistRequests.Remove(name);
        }
    }

    private static string Wait(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Ceiling(t.TotalSeconds)} seconds"
        : Math.Ceiling(t.TotalMinutes) is var m && m == 1 ? "1 minute" : $"{Math.Ceiling(t.TotalMinutes)} minutes";

    private static string SenderName(IHandleableChatMessage message) =>
        (message.Sender.Payloads.OfType<PlayerPayload>().FirstOrDefault()?.PlayerName ?? message.Sender.TextValue).Trim();

    /// <summary>Adds a song yourself, skipping cooldown, approval and the queue limit.</summary>
    public bool AddManual(string url, out string error)
    {
        url = url.Trim();
        // Allow pasting a whole request like "!q <link>" or "!shuffle <playlist>".
        var shuffle = false;
        var space = url.IndexOf(' ');
        if (space > 0)
        {
            var cmd = url[..space];
            shuffle = ShuffleCommands.Contains(cmd, StringComparer.OrdinalIgnoreCase);
            if (shuffle || RequestCommands.Contains(cmd, StringComparer.OrdinalIgnoreCase))
                url = url[(space + 1)..].Trim();
        }

        if (!TryParseRequest(url, out var playlist, out var spotifyId, out error))
            return false;
        Accept(Array.Empty<byte>(), "You", url, playlist, spotifyId, force: true, approved: false, shuffle);
        return true;
    }

    /// <summary>
    /// Puts an accepted request into the queue: a song, a playlist, or a Spotify short link (which can be either).
    /// </summary>
    /// <param name="force">Ignore the queue length limit (your own adds and requests you approved).</param>
    /// <param name="shuffle">Sent with a shuffle command: a playlist is shuffled (a single song just plays).</param>
    /// <param name="title">The song's name and length if already known (looked up while it waited for approval).</param>
    private void Accept(byte[] encodedSender, string sender, string url, PlaylistLink? playlist, string? spotifyId, bool force, bool approved,
                        bool shuffle = false, string? title = null, int? seconds = null)
    {
        var replyStart = approved ? "Your request was approved. " : "";
        if (playlist != null)
        {
            HandlePlaylist(encodedSender, sender, playlist, replyStart, shuffle);
            return;
        }
        if (spotifyId != null && spotifyId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            HandleShortLink(encodedSender, sender, url, force, replyStart, shuffle);
            return;
        }

        var request = new SongRequest(url, sender, spotifyId)
        {
            EncodedSender = sender == "You" ? null : encodedSender,
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            Seconds = seconds,
        };
        if (Player.TryEnqueue(request, force))
        {
            AddLog(sender, url, sender == "You" ? "Queued (manual)" : approved ? "Approved and queued" : "Queued", true);
            Replier.Send(encodedSender, sender, replyStart + QueueReplier.Message(null, SongsBefore(alreadyQueued: true)));
            if (sender != "You") Notify($"Queued request from {sender}.");
        }
        else
        {
            AddLog(sender, url, "Ignored: queue full", false);
            Replier.Send(encodedSender, sender, "The queue is full right now. Try again in a little while.");
            Notify($"Queue full, ignored request from {sender}.");
        }
    }

    /// <summary>How many songs will play before a song that's added right now (or was just queued).</summary>
    private int SongsBefore(bool alreadyQueued) =>
        Math.Max(0, Player.PendingCount - (alreadyQueued ? 1 : 0)) + (Player.Current != null || Player.WaitingForSync ? 1 : 0);

    /// <summary>
    /// Reads a playlist, leaves out live streams and songs over the max length, shuffles it if that's turned on,
    /// keeps up to the per-playlist limit, and queues the songs.
    /// </summary>
    private void HandlePlaylist(byte[] encodedSender, string sender, PlaylistLink link, string replyStart = "", bool shuffle = false)
    {
        AddLog(sender, link.Url, "Reading playlist...", true);
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var contents = await PlaylistReader.ReadAsync(link, YtDlp, Config.MaxDurationSeconds, default);
                if (contents.Songs.Count == 0)
                {
                    AddLog(sender, link.Url, $"Ignored: no playable songs in \"{contents.Name}\" (all too long or live)", false);
                    return;
                }

                var shuffled = shuffle || Config.ShufflePlaylists;
                var picked = PlaylistReader.Pick(contents.Songs, Config.MaxPlaylistSongs, shuffled);
                var skipped = new List<string>();
                if (contents.SkippedLong > 0) skipped.Add($"{contents.SkippedLong} too long");
                if (contents.SkippedLive > 0) skipped.Add($"{contents.SkippedLive} live");
                var left = contents.Songs.Count - picked.Count;
                if (left > 0) skipped.Add($"{left} over the {Config.MaxPlaylistSongs}-song limit");
                var summary = $"\"{contents.Name}\": {picked.Count} song(s){(shuffled ? ", shuffled" : "")}"
                              + (skipped.Count > 0 ? $" (left out: {string.Join(", ", skipped)})" : "");

                var before = SongsBefore(alreadyQueued: false);
                foreach (var s in picked)
                    Player.TryEnqueue(new SongRequest(s.YouTubeUrl ?? $"https://open.spotify.com/track/{s.Spotify!.Id}", sender, s.Spotify?.Id)
                    {
                        EncodedSender = sender == "You" ? null : encodedSender,
                        Track = s.Spotify,
                        Playlist = contents.Name,
                        Title = string.IsNullOrWhiteSpace(s.Title) ? null : s.Title,
                        Seconds = s.Seconds,
                    }, force: true);
                AddLog(sender, link.Url, $"Queued playlist {summary}", true);
                Notify($"Queued {sender}'s playlist {summary}.");
                Replier.Send(encodedSender, sender, replyStart + QueueReplier.PlaylistMessage(contents.Name, picked.Count, before, shuffled));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Playlist failed");
                var (reason, canRetry) = RetryPolicy.Explain(ex);
                var failed = new FailedRequest(Guid.NewGuid(), DateTime.Now, sender, sender == "You" ? null : encodedSender, link.Url,
                    $"Playlist {link.Url}", reason, canRetry, null, link, shuffle);
                RememberFailed(failed);
                AddLog(sender, link.Url, $"Failed: {reason}", false, failed.Id);
                Notify($"Couldn't read {sender}'s playlist: {reason}.");
            }
        });
    }

    /// <summary>A spotify.link short link can be a song, a playlist or an album; find out which, then handle it.</summary>
    private void HandleShortLink(byte[] encodedSender, string sender, string url, bool force, string replyStart, bool shuffle = false)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var (kind, id) = await SpotifyResolver.ResolveShortLinkKindAsync(url, default);
                if (kind == null || id == null) throw new InvalidOperationException("That Spotify link doesn't lead to a song, playlist or album.");

                if (kind is "playlist" or "album")
                {
                    var link = new PlaylistLink(kind, id, $"https://open.spotify.com/{kind}/{id}");
                    if (PlaylistProblem(link) is { } problem) { AddLog(sender, url, $"Ignored: {problem}", false); return; }
                    // A share link only shows it's a playlist now, so the playlist cooldown is checked here
                    // (not for your own adds or requests you approved).
                    if (!force && PlaylistCooldownLeft(sender) is { } wait)
                    {
                        AddLog(sender, url, "Ignored: playlist cooldown", false);
                        Replier.Send(encodedSender, sender, $"You can send another playlist in {Wait(wait)}.");
                        return;
                    }
                    if (sender != "You") RememberPlaylist(sender, DateTime.UtcNow);
                    HandlePlaylist(encodedSender, sender, link, replyStart, shuffle);
                }
                else if (Player.TryEnqueue(new SongRequest(url, sender, id) { EncodedSender = sender == "You" ? null : encodedSender }, force))
                {
                    AddLog(sender, url, sender == "You" ? "Queued (manual)" : "Queued", true);
                    Replier.Send(encodedSender, sender, replyStart + QueueReplier.Message(null, SongsBefore(alreadyQueued: true)));
                }
                else
                {
                    AddLog(sender, url, "Ignored: queue full", false);
                }
            }
            catch (Exception ex)
            {
                AddLog(sender, url, $"Failed: {ex.Message}", false);
            }
        });
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.TellIncoming)
            return;
        if (TellTunesLoaded)
            return; // TellTunes answers these; see TellTunesLoaded

        var text = message.Message.TextValue;
        if (SkipAllRegex().IsMatch(text))
        {
            if (!SoundyQListening) HandleSkipAll(message);
            return;
        }
        if (SkipRegex().IsMatch(text))
        {
            if (!SoundyQListening) HandleSkipRequest(message);
            return;
        }

        var match = RequestRegex().Match(text);
        if (!match.Success)
            return;

        var command = match.Groups["cmd"].Value;
        var shuffle = ShuffleCommands.Contains(command, StringComparer.OrdinalIgnoreCase);
        if (!shuffle && !RequestCommands.Contains(command, StringComparer.OrdinalIgnoreCase))
            return;

        var url = match.Groups["url"].Value;
        var sender = SenderName(message);
        var encodedSender = message.Sender.Encode();
        var world = QueueReplier.Target(encodedSender)?.World;

        if (SoundyQListening)
        {
            AddLog(sender, url, "Ignored: SoundyQ is also on (turn it off in /soundyq)", false);
            return;
        }
        if (!Config.Enabled)
        {
            AddLog(sender, url, "Ignored: requests are paused", false);
            return;
        }

        var permission = PermissionFor(sender, world);
        if (permission == RequestPermission.Blocked)
        {
            AddLog(sender, url, RuleFor(sender, world) != null ? "Ignored: sender is blocked" : "Ignored: not on your list", false);
            return;
        }

        if (url.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            ReplyWithQueue(encodedSender, sender);
            return;
        }

        if (!TryParseRequest(url, out var playlist, out var spotifyId, out var error))
        {
            AddLog(sender, url, $"Ignored: {error}", false);
            Replier.Send(encodedSender, sender, shuffle
                ? $"Please send a YouTube or Spotify playlist link, like: {command} https://www.youtube.com/playlist?list=..."
                : $"Please send a YouTube or Spotify link, like: {command} https://youtu.be/...");
            return;
        }

        // The playlist cooldown is checked first, so someone who sends a playlist too soon always hears how long
        // to wait, even within the few seconds of the per-person cooldown (which ignores requests silently).
        var now = DateTime.UtcNow;
        if (playlist != null && PlaylistCooldownLeft(sender) is { } wait)
        {
            AddLog(sender, url, "Ignored: playlist cooldown", false);
            Replier.Send(encodedSender, sender, $"You can send another playlist in {Wait(wait)}.");
            return;
        }
        if (lastRequest.TryGetValue(sender, out var last) && (now - last).TotalSeconds < Config.PerSenderCooldownSeconds)
        {
            AddLog(sender, url, "Ignored: cooldown", false);
            return;
        }

        if (playlist == null && IsDuplicate(url, spotifyId))
        {
            AddLog(sender, url, "Ignored: already in the queue", false);
            Replier.Send(encodedSender, sender, "That song is already in the queue.");
            return;
        }

        lastRequest[sender] = now;
        if (playlist != null) RememberPlaylist(sender, now);
        if (permission == RequestPermission.PreQueue)
        {
            if (Pending.Count >= Config.MaxQueueLength)
            {
                AddLog(sender, url, "Ignored: too many requests waiting for approval", false);
                return;
            }
            AddPending(encodedSender, sender, url, playlist, spotifyId, shuffle);
            return;
        }

        Accept(encodedSender, sender, url, playlist, spotifyId, force: false, approved: false, shuffle);
    }

    // ------------------------------------------------------------------ pre-queue (approval)

    private void AddPending(byte[] encodedSender, string sender, string url, PlaylistLink? playlist, string? spotifyId, bool shuffle)
    {
        var item = new PendingRequest
        {
            EncodedSender = encodedSender,
            Sender = sender,
            Url = playlist?.Url ?? url,
            Playlist = playlist,
            SpotifyId = spotifyId,
            Shuffle = shuffle,
        };
        Pending.Add(item);
        AddLog(sender, url, "Waiting for your approval", true);
        Replier.Send(encodedSender, sender, "Your request is waiting for approval.");
        Chat.Print($"[Tundy] {sender} requested a {(playlist != null ? "playlist" : "song")}. Approve it in /tundy → Now playing, or with /tundy approve.");

        // Look up the name in the background so you can see what you're approving.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                if (playlist != null)
                    item.Title = shuffle ? "Playlist (shuffled)" : "Playlist";
                else if (spotifyId == null || !spotifyId.StartsWith("http", StringComparison.OrdinalIgnoreCase)) // not a short link
                {
                    // The same lookup the queue uses; what's found goes along when you approve it.
                    var (title, seconds) = await Player.LookUpAsync(url, spotifyId);
                    item.Seconds = seconds;
                    item.Title = title;
                }
            }
            catch (Exception ex) { Log.Debug(ex, $"Couldn't look up {url}"); }
        });
    }

    /// <summary>Approves a waiting request: it goes into the queue (even if the queue is full).</summary>
    public void Approve(Guid id)
    {
        if (Pending.Take(id) is not { } r) return;
        AcceptApproved(r);
    }

    public void ApproveAll()
    {
        foreach (var r in Pending.TakeAll()) AcceptApproved(r);
    }

    private void AcceptApproved(PendingRequest r)
    {
        Accept(r.EncodedSender, r.Sender, r.Url, r.Playlist, r.SpotifyId, force: true, approved: true, r.Shuffle, r.Title, r.Seconds);
    }

    public void Reject(Guid id)
    {
        if (Pending.Take(id) is not { } r) return;
        AddLog(r.Sender, r.Url, "Rejected", false);
        Replier.Send(r.EncodedSender, r.Sender, "Sorry, your request wasn't accepted.");
    }

    /// <summary>"!q list": tells the person what's playing and how many songs are waiting (at most every 10 s).</summary>
    private void ReplyWithQueue(byte[] encodedSender, string sender)
    {
        var now = DateTime.UtcNow;
        if (lastList.TryGetValue(sender, out var last) && (now - last).TotalSeconds < 10) return;
        lastList[sender] = now;

        var waiting = Player.PendingCount;
        var playing = Player.Current?.Title;
        var mine = Player.FullQueue().Select((q, i) => (r: q.Request, i)).FirstOrDefault(x => Names.Same(x.r.Sender, sender));
        var parts = new List<string>
        {
            playing != null ? $"Now playing: {playing}." : "Nothing is playing right now.",
            waiting switch { 0 => "The queue is empty.", 1 => "1 song is waiting.", _ => $"{waiting} songs are waiting." },
        };
        if (mine.r != null) parts.Add($"Yours is number {mine.i + 1}.");
        AddLog(sender, "list", "Sent the queue", true);
        Replier.Send(encodedSender, sender, string.Join(" ", parts), always: true);
    }

    // ---- Playback ----

    public bool CanSkip => Player.Current != null || Player.WaitingForSync;

    public void Skip() => Player.Skip();

    /// <summary>What's playing right now.</summary>
    public NowPlayingInfo NowPlaying()
    {
        var source = Player.CurrentIsShared ? (Sharer.UsesMusicMod ? "Music mod" : "Lightless") : "Tundy";
        if (Player.Current is { } track)
            return new NowPlayingInfo(track.Title, track.Request.Sender, Player.Position, Player.Duration, source, track.Request.CoverId);
        return new NowPlayingInfo(null, null, null, null, Player.WaitingForSync ? "Syncing" : "Tundy");
    }

    /// <summary>
    /// True when the song plays inside the game (shared through the music mod or an emote) rather than in
    /// Tundy's own player. Volume then means the game's "Self" sound volume.
    /// </summary>
    public bool SongPlaysInGame => Player.WaitingForSync || (Player.CurrentIsShared && Config.ShareSilenceLocal);

    // ------------------------------------------------------------------ UI / commands

    public void SetMiniPlayer(bool show)
    {
        miniWindow.IsOpen = show;
        if (Config.ShowMiniPlayer == show) return;
        Config.ShowMiniPlayer = show;
        SaveConfig();
    }

    public void ToggleMainWindow() => mainWindow.Toggle();

    /// <summary>Opens Dalamud's plugin list (to turn TellTunes off there).</summary>
    public void OpenPluginList() => CommandManager.ProcessCommand("/xlplugins");

    /// <summary>The song after the current one, for the mini player.</summary>
    public SongRequest? NextSong => Player.FullQueue() is { Length: > 0 } q ? q[0].Request : null;

    private void OpenSettings()
    {
        mainWindow.IsOpen = true;
        mainWindow.SelectSettingsTab();
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts.FirstOrDefault()?.ToLowerInvariant())
        {
            case null:
                mainWindow.Toggle();
                break;
            case "play" when parts.Length > 1:
                if (!AddManual(parts[1], out var err))
                    Chat.PrintError($"[Tundy] {err}");
                break;
            case "skip":
                Skip();
                break;
            case "stop":
                Player.Stop();
                Chat.Print("[Tundy] Stopped and cleared the queue.");
                break;
            case "retry":
                if (LatestRetryable() is { } last && Retry(last.Id))
                    Chat.Print($"[Tundy] Trying {last.Sender}'s {(last.Playlist != null ? "playlist" : "song")} again: it plays next.");
                else
                    Chat.Print("[Tundy] Nothing to retry: no failed song that trying again could fix.");
                break;
            case "approve":
                var pending = Pending.Snapshot();
                if (pending.Length == 0) { Chat.Print("[Tundy] Nothing is waiting for approval."); break; }
                if (parts.Length > 1 && parts[1].Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    ApproveAll();
                    Chat.Print($"[Tundy] Approved {pending.Length} request(s).");
                }
                else
                {
                    Approve(pending[0].Id);
                    Chat.Print($"[Tundy] Approved {pending[0].Sender}'s request: {pending[0].Display}");
                }
                break;
            case "mini":
                SetMiniPlayer(!miniWindow.IsOpen);
                break;
            case "import":
                mainWindow.IsOpen = true;
                mainWindow.SelectImportTab();
                break;
            case "on":
                Config.Enabled = true; SaveConfig();
                Chat.Print($"[Tundy] Listening for {string.Join(", ", RequestCommands)} tells.");
                break;
            case "off":
                Config.Enabled = false; SaveConfig();
                Chat.Print("[Tundy] Ignoring song request tells.");
                break;
            case "share":
                Config.ShareViaLightless = !Config.ShareViaLightless; SaveConfig();
                Chat.Print(Config.ShareViaLightless
                    ? "[Tundy] Sharing songs with nearby Lightless pairs."
                    : "[Tundy] Songs play just for you.");
                break;
            case "mute" when SongPlaysInGame:
                if (GameConfig.TryGet(SystemConfigOption.SoundPlayer, out uint self) && self > 0)
                {
                    Config.SavedSelfVolume = self; SaveConfig();
                    GameConfig.Set(SystemConfigOption.SoundPlayer, 0u);
                }
                else
                {
                    GameConfig.Set(SystemConfigOption.SoundPlayer, Config.SavedSelfVolume is > 0 and <= 100 ? Config.SavedSelfVolume : 100u);
                }
                break;
            case "mute":
                Config.Muted = !Config.Muted; SaveConfig();
                Player.ApplyVolume();
                break;
            case "vol" when parts.Length > 1 && int.TryParse(parts[1], out var v):
                if (SongPlaysInGame)
                {
                    GameConfig.Set(SystemConfigOption.SoundPlayer, (uint)Math.Clamp(v, 0, 100));
                    Chat.Print($"[Tundy] Song volume (game \"Self\" volume): {Math.Clamp(v, 0, 100)}%");
                }
                else
                {
                    Config.Volume = Math.Clamp(v, 0, 100) / 100f; SaveConfig();
                    Player.ApplyVolume();
                }
                break;
            default:
                Chat.Print("[Tundy] Usage: /tundy [mini|play <link>|skip|stop|approve [all]|import|on|off|share|mute|vol <0-100>]");
                break;
        }
    }

    private void Notify(string text)
    {
        Log.Information(text);
        if (Config.EchoToChat)
            Chat.Print($"[Tundy] {text}");
    }

    public void Dispose()
    {
        Chat.ChatMessage -= OnChatMessage;
        CommandManager.RemoveHandler(MainCommand);
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        windows.RemoveAllWindows();
        Honorific.Dispose();
        Player.Dispose();
        Sharer.Dispose();
        TitleFont.Dispose();
        Covers.Dispose();
    }
}
