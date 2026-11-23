using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace Tundy;

/// <summary>An emote whose sound file carries the song to other players.</summary>
/// <param name="SwapFromKey">Optional: an animation of this emote to replace (action timeline key)...</param>
/// <param name="SwapToKey">...with this other emote's animation, whose sound carries the song.</param>
/// <param name="Routes">
/// Set when the song plays through a music mod (see <see cref="MegaMusicMod"/>): every one of these game sound
/// files gets the song, and between songs they're left to the music mod instead of a copy of a game sound.
/// </param>
public sealed record ShareEmote(string Command, string Name, string ScdPath, bool UnlockedByDefault, string Note = "",
                                string? SwapFromKey = null, string? SwapToKey = null, IReadOnlyList<string>? Routes = null);

/// <summary>
/// Shares songs with nearby players through Lightless Sync.
/// The song goes on a game sound file through a real Penumbra mod ("Tundy Song") on your character: either the
/// song slot of a music mod like the Mega Music Mod (played by its DJ dance), or an emote's own sound.
/// Lightless syncs your character's mods to the people you're paired with, so when you do the emote they hear
/// the song too, without installing Tundy.
/// </summary>
public sealed class SongSharer : IDisposable
{
    private const int ModPriority = 10000; // win over any other mod touching the same emote files

    // Found by scanning the game's emote animations. Best are one-time emotes whose animation itself plays
    // its sound exactly once: the song then starts once and isn't cut off when a visual effect ends.
    // Looping emotes play their sound again every loop, which restarts the song on top of itself.
    public static readonly ShareEmote[] Emotes =
    {
        new("/fume", "Fume", "sound/battle/etc/se_bt_etc_clap.scd", true,
            "Recommended. Everyone has it, it plays once, and its sound plays exactly one time. "
            + "(It shares its sound file with /clap, so don't clap while a song is shared.)"),
        new("/highfive", "High Five", "sound/battle/etc/se_bt_etc_emote_highfive.scd", false,
            "Plays once, and its sound plays exactly one time."),
        new("/uchiwasshoi", "Uchiwasshoi", "sound/battle/etc/se_bt_etc_emote_bigfan.scd", false,
            "Plays once, and its sound plays exactly one time."),
        new("/photograph", "Photograph", "sound/vfx/etc/se_vfx_etc_emote_instcam.scd", true,
            "Everyone has it. Its sound comes from a visual effect, so the song may stop when the effect ends."),
        new("/eureka", "Eureka", "sound/vfx/etc/se_vfx_etc_emot_idea.scd", false, "Sound comes from a visual effect; may cut off."),
        new("/powerup", "Power Up", "sound/vfx/etc/se_vfx_etc_emot_aura_c.scd", false, "Sound comes from a visual effect; may cut off."),
        new("/snap", "Snap", "sound/vfx/etc/se_vfx_etc_emot_fingersnap.scd", false, "Sound comes from a visual effect; may cut off."),
        new("/gratuity", "Gratuity", "sound/vfx/etc/se_vfx_etc_emot_gratuity.scd", false, "Sound comes from a visual effect; may cut off."),
        new("/linkpearl", "Linkpearl", "sound/vfx/etc/se_vfx_etc_emot_linkshell.scd", false, "Sound comes from a visual effect; may cut off."),
        // Cheer Jump: Red has no sound of its own. Its one-time start animation is swapped for the
        // Linkpearl animation (hand to ear), whose sound carries the song; then the normal glowstick loop plays.
        new("/cheerjumpred", "Cheer Jump: Red, as music player", "sound/vfx/etc/se_vfx_etc_emot_linkshell.scd", false,
            "Starts with the Linkpearl pose (hand to ear) and the song, then the glowstick dance. Sound comes from a visual effect; may cut off.",
            SwapFromKey: "emote_sp/sp12_start", SwapToKey: "emote/act_emot28"),
        new("/shakedrink", "Shake Drink (loops)", "sound/battle/etc/se_bt_etc_emote_shaker.scd", false,
            "Not recommended: it loops, and each loop starts the song again on top of itself."),
        new("/water", "Water (loops)", "sound/vfx/etc/se_vfx_etc_emote_watering.scd", false,
            "Not recommended: it loops, and each loop starts the song again on top of itself."),
    };

    private readonly IDalamudPluginInterface pi;
    private readonly IDataManager data;
    private readonly IFramework framework;
    private readonly Configuration config;
    private readonly string dir;

    private readonly ApiVersion apiVersion;
    private readonly GetCollectionForObject getCollection;
    private readonly GetModDirectory getModDirectory;
    private readonly GetModList getModList;
    private readonly AddMod addMod;
    private readonly ReloadMod reloadMod;
    private readonly TrySetMod trySetMod;
    private readonly TrySetModPriority trySetModPriority;
    private readonly RedrawObject redraw;

    /// <summary>Folder name of the real Penumbra mod that carries the song (inside Penumbra's mod folder).</summary>
    public const string ModFolderName = "Tundy Song";
    private bool modChecked;

    private readonly IObjectTable objects;
    private readonly IChatGui chat;
    private readonly IDisposable resolvedSubscription;

    private readonly Dictionary<string, ShareEmote> builtFor = new();
    private readonly MegaMusicMod music;
    private readonly FfmpegTool ffmpegTool;
    private Guid? appliedCollection;
    private string? appliedFile;

    // Diagnostics: which of our redirected files the game actually loaded, the same signal Lightless uses.
    private readonly object loadLock = new();
    private HashSet<string> appliedGamePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> playerLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> otherLoads = new(StringComparer.OrdinalIgnoreCase);
    private nint playerAddress;
    private DateTime lastEmoteAt = DateTime.MinValue;

    public string Status { get; private set; } = "Idle";

    /// <summary>Plain-language result of the last share, shown in the Sharing tab.</summary>
    public string LastCheck { get; private set; } = "No song shared yet.";
    public bool LastCheckOk { get; private set; } = true;

    /// <summary>Last error the game printed right after we did the emote (e.g. emote not learned).</summary>
    public string? LastGameError { get; private set; }

    public SongSharer(IDalamudPluginInterface pi, IDataManager data, IFramework framework, IObjectTable objects, IChatGui chat,
                      Configuration config, MegaMusicMod music, FfmpegTool ffmpegTool, string dir)
    {
        this.pi = pi;
        this.music = music;
        this.ffmpegTool = ffmpegTool;
        this.data = data;
        this.framework = framework;
        this.objects = objects;
        this.chat = chat;
        this.config = config;
        this.dir = dir;

        apiVersion = new ApiVersion(pi);
        getCollection = new GetCollectionForObject(pi);
        getModDirectory = new GetModDirectory(pi);
        getModList = new GetModList(pi);
        addMod = new AddMod(pi);
        reloadMod = new ReloadMod(pi);
        trySetMod = new TrySetMod(pi);
        trySetModPriority = new TrySetModPriority(pi);
        redraw = new RedrawObject(pi);
        resolvedSubscription = GameObjectResourcePathResolved.Subscriber(pi, OnResourceResolved);

        framework.Update += OnFrameworkUpdate;
        chat.ChatMessage += OnChatMessage;

        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* ignored */ }
        Directory.CreateDirectory(dir);
    }

    private bool startupCleanupDone;

    private void OnFrameworkUpdate(IFramework _)
    {
        playerAddress = objects.LocalPlayer?.Address ?? 0;

        // If the game closed while a song was shared, the mod may still hold the old song. Put the original
        // sound back (this also makes sure the mod points at the emote you picked).
        if (!startupCleanupDone && playerAddress != 0)
        {
            startupCleanupDone = true;
            try
            {
                if (PenumbraAvailable && getModList.Invoke().ContainsKey(ModFolderName))
                    SetEmoteSound(Emote, null);
            }
            catch (Exception ex) { Plugin.Log.Debug(ex, "Startup cleanup of Tundy Song mod failed"); }
        }
    }

    // Called by Penumbra (possibly off the main thread) whenever an object loads a modded file.
    private void OnResourceResolved(nint gameObject, string gamePath, string localPath)
    {
        // Only Tundy Song's own files count: the music mod also puts a file on the song slot, and seeing that one
        // load says nothing about whether the song is on.
        if (!localPath.Replace('/', '\\').Contains("\\" + ModFolderName + "\\", StringComparison.OrdinalIgnoreCase)) return;
        lock (loadLock)
        {
            if (!appliedGamePaths.Contains(gamePath)) return;
            if (gameObject != 0 && gameObject == playerAddress) playerLoads[gamePath] = DateTime.Now;
            else otherLoads[gamePath] = DateTime.Now;
        }
    }

    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        if (message.LogKind != Dalamud.Game.Text.XivChatType.ErrorMessage) return;
        if ((DateTime.Now - lastEmoteAt).TotalSeconds > 3) return;
        LastGameError = message.Message.TextValue;
        Plugin.Log.Warning($"Game error after share emote: {LastGameError}");
    }

    private bool LoadedForPlayerSince(string gamePath, DateTime since)
    {
        lock (loadLock) return playerLoads.TryGetValue(gamePath, out var t) && t >= since;
    }

    private bool LoadedForOtherSince(string gamePath, DateTime since)
    {
        lock (loadLock) return otherLoads.TryGetValue(gamePath, out var t) && t >= since;
    }

    /// <summary>
    /// Whether Lightless has already learned this emote's sound file for your character. Lightless saves the
    /// emote/sound files it has seen your character use in its transient.json; until then it doesn't send them.
    /// </summary>
    private bool LightlessKnows(ShareEmote emote)
    {
        try
        {
            var file = Path.Combine(Directory.GetParent(pi.GetPluginConfigDirectory())!.FullName, "LightlessSync", "transient.json");
            if (!File.Exists(file)) return false;
            var text = File.ReadAllText(file);
            var needed = new List<string> { emote.ScdPath };
            if (emote.SwapFromKey != null) needed.Add($"chara/action/{emote.SwapFromKey}.tmb");
            return needed.All(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not read Lightless transient.json");
            return false;
        }
    }

    /// <summary>The emote (or music mod slot) a song file was made for; the current one if it isn't known.</summary>
    private ShareEmote EmoteFor(string scdFile)
    {
        lock (builtFor) return builtFor.GetValueOrDefault(scdFile) ?? Emote;
    }

    /// <summary>
    /// Gets a shared song ready so your Lightless pairs have it before it starts:
    /// 1. puts it on your character (Penumbra mod + redraw),
    /// 2. the first time an emote is used, does the emote once so Lightless sees your character load the
    ///    song's files and learns them (it only shares emote sounds it has seen you use), waits for it to
    ///    upload, then redraws to stop that practice copy,
    /// 3. waits the sync delay so your pairs can download it.
    /// </summary>
    public async Task PrepareAsync(string scdFile, Func<bool> skipRequested, CancellationToken ct)
    {
        var emote = EmoteFor(scdFile);

        if (!LightlessKnows(emote))
        {
            // Teach Lightless the emote's sound file with the emote's ORIGINAL sound (copied into the mod), so
            // this practice emote doesn't play the song and there's never a second copy of it.
            Status = "Teaching Lightless the emote's sound (first time only)...";
            Plugin.Log.Information($"Lightless doesn't know {emote.ScdPath} yet, teaching it with {emote.Command}");
            await framework.RunOnFrameworkThread(() =>
            {
                SetEmoteSound(emote, null);
                redraw.Invoke(0, RedrawType.Redraw);
            });
            await DelayAsync(2, skipRequested, ct); // let the redraw finish
            var primedAt = DateTime.Now;
            await PerformEmoteAsync(emote);
            await WaitForAsync(() => LoadedForPlayerSince(emote.ScdPath, primedAt), 6, skipRequested, ct);
            ReportLoad(emote, primedAt, priming: true);
            await DelayAsync(8, skipRequested, ct); // Lightless waits 5 s after a new file, then saves it
            Plugin.Log.Information($"Lightless knows the sound now: {LightlessKnows(emote)}");
        }

        await ApplyAsync(scdFile);
        Status = $"Waiting {config.ShareSyncDelaySeconds}s for your pairs to download it...";
        await DelayAsync(config.ShareSyncDelaySeconds, skipRequested, ct);
    }

    // True when the song went on while you were still dancing: it then starts with a redraw (which restarts the
    // dance with the new song) instead of the emote.
    private bool redrawAtStart;

    private static unsafe bool InEmoteLoop(IObjectTable objects)
    {
        var me = objects.LocalPlayer;
        if (me == null) return false;
        var c = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)me.Address;
        return c->Mode == FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.EmoteLoop;
    }

    /// <summary>Does the emote for real when the song starts, then checks the game loaded the song.</summary>
    public async Task StartForEveryoneAsync(string scdFile, CancellationToken ct)
    {
        var emote = EmoteFor(scdFile);
        var startedAt = DateTime.Now;
        var useRedraw = redrawAtStart;
        redrawAtStart = false;
        if (useRedraw && await framework.RunOnFrameworkThread(() => InEmoteLoop(objects)))
        {
            // Still dancing: one redraw restarts the dance, and with it the new song.
            lastEmoteAt = DateTime.Now;
            await framework.RunOnFrameworkThread(() => redraw.Invoke(0, RedrawType.Redraw));
            Plugin.Log.Information("Started the song with a redraw (kept dancing)");
            Status = "Playing for nearby Lightless pairs (kept dancing)";
        }
        else
        {
            // Not dancing (any more): start it with the emote. A redraw first, in case the song went on without one.
            if (useRedraw) await framework.RunOnFrameworkThread(() => redraw.Invoke(0, RedrawType.Redraw));
            await PerformEmoteAsync(emote);
            Status = $"Playing for nearby Lightless pairs ({emote.Command})";
        }
        await WaitForAsync(() => LoadedForPlayerSince(emote.ScdPath, startedAt), 5, () => false, ct);
        ReportLoad(emote, startedAt, priming: false);
    }

    private void ReportLoad(ShareEmote emote, DateTime since, bool priming)
    {
        var sound = LoadedForPlayerSince(emote.ScdPath, since);
        var anim = emote.SwapFromKey == null || LoadedForPlayerSince($"chara/action/{emote.SwapFromKey}.tmb", since);
        var wrongObject = !sound && LoadedForOtherSince(emote.ScdPath, since);

        if (sound)
        {
            LastCheckOk = true;
            LastCheck = priming
                ? $"Lightless saw your character use {emote.Command}'s sound. It can send songs on it to your pairs now."
                : $"The song played on {emote.Command} for your character, so Lightless is sending it to your pairs.";
        }
        else
        {
            LastCheckOk = false;
            LastCheck = LastGameError != null
                ? $"{emote.Command} didn't play: the game said \"{LastGameError}\""
                : wrongObject
                    ? $"The song loaded, but not for your character, so Lightless won't send it. Try another emote."
                    : !anim
                        ? $"{emote.Command} didn't seem to play (the game never loaded it). Do you own it? Are you able to emote right now?"
                        : $"{emote.Command} played, but its sound never loaded, so nobody can hear the song. Try another emote.";
        }
        Plugin.Log.Information($"Share check ({(priming ? "priming" : "start")}): sound={sound} anim={anim} otherObject={wrongObject} error={LastGameError ?? "-"} -> {LastCheck}");
    }

    private static async Task DelayAsync(int seconds, Func<bool> stop, CancellationToken ct)
    {
        for (var i = 0; i < seconds * 10 && !stop(); i++)
            await Task.Delay(100, ct);
    }

    private static async Task WaitForAsync(Func<bool> done, int seconds, Func<bool> stop, CancellationToken ct)
    {
        for (var i = 0; i < seconds * 10 && !done() && !stop(); i++)
            await Task.Delay(100, ct);
    }

    /// <summary>True when songs play through a music mod's song slot (like SoundyQ), false for an emote's own sound.</summary>
    public bool UsesMusicMod => config.ShareTarget == 0;

    /// <summary>How the next song is shared: the music mod's slot and its emote, or the chosen emote.</summary>
    public ShareEmote Emote => UsesMusicMod
        ? new ShareEmote(MegaMusicMod.SafeEmote(config.QueueEmoteCommand), config.QueueTargetMod, music.Routes[0], true,
                         Routes: music.Routes)
        : Emotes.FirstOrDefault(e => e.Command == config.ShareEmoteCommand) ?? Emotes[0];

    public bool PenumbraAvailable
    {
        get
        {
            try { return apiVersion.Invoke().Breaking >= 5; }
            catch { return false; }
        }
    }

    public bool LightlessLoaded =>
        pi.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName.Contains("Lightless", StringComparison.OrdinalIgnoreCase));

    public bool CanShare => config.ShareViaLightless && PenumbraAvailable;

    /// <summary>
    /// Deletes song files left in the "Tundy Song" mod (not the one on right now, and not the silence/original
    /// copies). Returns (files, bytes).
    /// </summary>
    public (int Files, long Bytes) CleanSongFolder()
    {
        int files = 0;
        long bytes = 0;
        try
        {
            var songs = new DirectoryInfo(Path.Combine(getModDirectory.Invoke(), ModFolderName, "songs"));
            if (!songs.Exists) return (0, 0);
            var keep = appliedFile == null ? null : Path.GetFileName(appliedFile);
            foreach (var f in songs.GetFiles("*.scd").Where(f => f.Name != keep && !f.Name.StartsWith("original_", StringComparison.Ordinal)))
            {
                bytes += f.Length;
                f.Delete();
                files++;
            }
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Couldn't clean the Tundy Song mod"); }
        return (files, bytes);
    }

    /// <summary>Converts a downloaded song into a game sound file for the chosen emote or music mod slot.</summary>
    public string BuildScd(string audioFile, CancellationToken ct)
    {
        var emote = Emote;
        var template = emote.Routes != null
            ? music.TemplateFor(emote.ScdPath, ModFolderName)
            : data.GetFile(emote.ScdPath)?.Data ?? throw new FileNotFoundException($"Game file not found: {emote.ScdPath}");

        Status = "Converting song for sharing...";
        byte[] scd;
        if (emote.Routes != null)
        {
            // Music mod tracks are streamed music and only play Vorbis, like the mod's own songs.
            Status = ffmpegTool.CurrentPath == null ? "Downloading ffmpeg (once, about 120 MB)..." : Status;
            var ffmpeg = ffmpegTool.Get(ct);
            Status = "Converting song for sharing...";
            var ogg = VorbisScd.EncodeOgg(ffmpeg, audioFile, config.ShareSmallerFiles ? 3 : 6, ct);
            scd = VorbisScd.Build(template, ogg, loopSilentTail: true);
            try { PrepareSilence(emote.ScdPath, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Plugin.Log.Warning(ex, "Couldn't make the silent track"); }
        }
        else
        {
            var audio = ScdBuilder.EncodeAdpcmMono(audioFile, config.ShareSmallerFiles ? 22050 : 44100, ct);
            scd = ScdBuilder.Build(template, audio);
        }

        var path = Path.Combine(dir, $"{Guid.NewGuid():N}.scd");
        File.WriteAllBytes(path, scd);
        lock (builtFor) builtFor[path] = emote; // remember which emote this file was made for
        Plugin.Log.Information($"Built {path} ({scd.Length / 1024} KB) for {emote.Command}");
        Status = "Ready";
        return path;
    }

    /// <summary>Puts the song on the emote (see <see cref="SetEmoteSound"/>) and redraws you so Lightless sends it.</summary>
    public async Task ApplyAsync(string scdFile)
    {
        // Music mod while you're still dancing from the last song: put the song on without a redraw. A redraw
        // restarts the dance, which plays whatever is on the song slot, so it's saved for when the song should
        // start (see StartForEveryoneAsync): no bow, no new emote, the dance just goes on with the new song.
        var musicMod = EmoteFor(scdFile).Routes != null;
        redrawAtStart = musicMod && await framework.RunOnFrameworkThread(() => InEmoteLoop(objects));

        await framework.RunOnFrameworkThread(() =>
        {
            SetEmoteSound(EmoteFor(scdFile), scdFile);
            LastGameError = null;
            if (!redrawAtStart) redraw.Invoke(0, RedrawType.Redraw);
        });
        if (redrawAtStart) Plugin.Log.Information("Still dancing: the next song goes on now and starts with a redraw after the wait");
        Status = $"Syncing through Lightless ({Emote.Command})...";
    }

    /// <summary>
    /// Points the emote's sound at <paramref name="songFile"/>, or at an exact copy of the emote's original
    /// sound when it's null, inside a real Penumbra mod ("Tundy Song" in your Penumbra mod folder).
    ///
    /// Why it works this way:
    /// - Lightless only shares files that live inside the Penumbra mod folder, so it has to be a real mod.
    /// - Lightless only shares emote sounds it has seen your character use, and forgets them as soon as they're
    ///   back to the game's own file. So between songs the mod stays on with the original sound copied in: the
    ///   emote sounds normal, and Lightless keeps remembering it, so the next song goes out right away.
    /// Must run on the framework thread.
    /// </summary>
    private void SetEmoteSound(ShareEmote emote, string? songFile)
    {
        var modDir = EnsureModFolder();
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.Combine(modDir, "songs"));

        string soundRel = "";
        if (songFile != null)
        {
            soundRel = Path.Combine("songs", Path.GetFileName(songFile));
            File.Copy(songFile, Path.Combine(modDir, soundRel), true);
            foreach (var route in emote.Routes ?? new[] { emote.ScdPath })
                files[route] = soundRel;
        }
        else if (emote.Routes == null)
        {
            soundRel = Path.Combine("songs", $"original_{emote.Command.Trim('/')}.scd");
            var original = data.GetFile(emote.ScdPath)?.Data
                           ?? throw new FileNotFoundException($"Game file not found: {emote.ScdPath}");
            File.WriteAllBytes(Path.Combine(modDir, soundRel), original);
            files[emote.ScdPath] = soundRel;
        }
        else if (!config.BetweenSongsSilence)
        {
            // Music mod between songs, set to "the music mod's own song": nothing on the slot, so the track picked
            // in the music mod (e.g. one imported with Tundy) plays when you dance.
        }
        else
        {
            // Music mod between songs: silence stays on the song slot. Your character keeps dancing after a song,
            // and the dance would otherwise play one of the music mod's own tracks. Keeping a Tundy file there
            // also keeps Lightless knowing the slot (it forgets files that aren't modded any more).
            soundRel = Path.Combine("songs", "original_silence.scd");
            var silence = Path.Combine(modDir, soundRel);
            if (!File.Exists(silence)) EnsureSilence(emote.ScdPath, silence);
            foreach (var route in emote.Routes)
                files[route] = soundRel;
        }

        foreach (var (gamePath, source) in AnimationSwapFiles(emote))
        {
            var rel = Path.Combine("swap", emote.Command.Trim('/'), Path.GetFileName(source));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(modDir, rel))!);
            File.Copy(source, Path.Combine(modDir, rel), true);
            files[gamePath] = rel;
        }

        WriteDefaultMod(modDir, files);
        DeleteOldSongs(modDir, keep: Path.GetFileName(soundRel));

        var (_, _, collection) = getCollection.Invoke(0);
        var ec = reloadMod.Invoke(ModFolderName, string.Empty);
        if (ec != PenumbraApiEc.Success)
            throw new InvalidOperationException($"Penumbra couldn't load the Tundy Song mod ({ec}).");
        trySetModPriority.Invoke(collection.Id, ModFolderName, ModPriority);
        ec = trySetMod.Invoke(collection.Id, ModFolderName, true);
        if (ec != PenumbraApiEc.Success && ec != PenumbraApiEc.NothingChanged)
            throw new InvalidOperationException($"Penumbra couldn't turn on the Tundy Song mod ({ec}).");

        // Watch the song slot even between songs, so a practice emote shows whether the game loads it.
        lock (loadLock) appliedGamePaths = new HashSet<string>(files.Keys.Concat(emote.Routes ?? Array.Empty<string>()), StringComparer.OrdinalIgnoreCase);
        appliedCollection = collection.Id;
        appliedFile = songFile;
        Plugin.Log.Information(songFile != null
            ? $"Song is on {emote.Command} (collection \"{collection.Name}\", {files.Count} files)"
            : $"{emote.Command} has its original sound again (collection \"{collection.Name}\")");
    }

    private string SilenceCache => Path.Combine(pi.GetPluginConfigDirectory(), "silence.scd");

    /// <summary>
    /// Makes the music-mod-shaped silent track (once, kept in Tundy's folder) so it's ready when a song ends.
    /// Called while songs are converted, off the framework thread.
    /// </summary>
    private void PrepareSilence(string route, CancellationToken ct)
    {
        if (File.Exists(SilenceCache)) return;
        var ffmpeg = ffmpegTool.CurrentPath;
        if (ffmpeg == null) return;
        var scd = VorbisScd.Build(music.TemplateFor(route, ModFolderName), VorbisScd.EncodeSilence(ffmpeg, 2, ct));
        File.WriteAllBytes(SilenceCache, scd);
        Plugin.Log.Information($"Made the silent track for between songs ({scd.Length / 1024} KB)");
    }

    /// <summary>Puts the silent track at <paramref name="target"/>; without ffmpeg, a copy of the music mod's own track.</summary>
    private void EnsureSilence(string route, string target)
    {
        try
        {
            PrepareSilence(route, default);
            if (File.Exists(SilenceCache))
            {
                File.Copy(SilenceCache, target, true);
                return;
            }
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Couldn't make the silent track"); }
        File.WriteAllBytes(target, music.TemplateFor(route, ModFolderName));
    }

    /// <summary>Creates the "Tundy Song" mod folder inside Penumbra's mod folder and registers it once.</summary>
    private string EnsureModFolder()
    {
        var root = getModDirectory.Invoke();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new InvalidOperationException("Penumbra's mod folder isn't set up.");

        var modDir = Path.Combine(root, ModFolderName);
        Directory.CreateDirectory(modDir);
        var meta = Path.Combine(modDir, "meta.json");
        if (!File.Exists(meta))
        {
            File.WriteAllText(meta, """
                {
                  "FileVersion": 3,
                  "Name": "Tundy Song",
                  "Author": "Tundy",
                  "Description": "Managed by the Tundy plugin. Puts the song you're sharing on the music mod's song slot (or an emote's sound) so Lightless Sync can send it to your pairs. Between songs it holds a copy of the original sound. Don't edit it by hand.",
                  "Image": "",
                  "Version": "1.0.0",
                  "Website": "",
                  "ModTags": [ "Tundy" ],
                  "DefaultPreferredItems": []
                }
                """);
            WriteDefaultMod(modDir, new Dictionary<string, string>());
        }

        if (!modChecked)
        {
            if (!getModList.Invoke().ContainsKey(ModFolderName))
            {
                var ec = addMod.Invoke(ModFolderName);
                if (ec != PenumbraApiEc.Success)
                    throw new InvalidOperationException($"Penumbra couldn't add the Tundy Song mod ({ec}).");
                Plugin.Log.Information($"Added Penumbra mod \"{ModFolderName}\"");
            }
            modChecked = true;
        }
        return modDir;
    }

    /// <summary>
    /// Writes the mod's file redirects where Penumbra reads them. Older mods (FileVersion 3) keep them in
    /// default_mod.json. Newer Penumbra upgrades mods to FileVersion 4, which keeps them in meta.json under
    /// "DefaultData" and ignores default_mod.json, so for those the redirects go into meta.json.
    /// </summary>
    internal static void WriteDefaultMod(string modDir, Dictionary<string, string> files)
    {
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var metaFile = Path.Combine(modDir, "meta.json");
        var defaultFile = Path.Combine(modDir, "default_mod.json");

        System.Text.Json.Nodes.JsonObject? meta = null;
        try
        {
            if (File.Exists(metaFile))
                meta = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(metaFile)) as System.Text.Json.Nodes.JsonObject;
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Couldn't read the Tundy Song mod's meta.json"); }

        var fileVersion = meta?["FileVersion"]?.GetValue<int>() ?? 3;
        if (meta != null && fileVersion >= 4)
        {
            var filesNode = new System.Text.Json.Nodes.JsonObject();
            foreach (var (gamePath, rel) in files) filesNode[gamePath] = rel;
            meta["DefaultData"] = new System.Text.Json.Nodes.JsonObject
            {
                ["Files"] = filesNode,
                ["FileSwaps"] = new System.Text.Json.Nodes.JsonObject(),
                ["Manipulations"] = new System.Text.Json.Nodes.JsonArray(),
            };
            if (meta["Groups"] == null) meta["Groups"] = new System.Text.Json.Nodes.JsonArray();
            File.WriteAllText(metaFile, meta.ToJsonString(options));
            try { if (File.Exists(defaultFile)) File.Delete(defaultFile); } catch { /* Penumbra ignores it anyway */ }
            return;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 0,
            Files = files,
            FileSwaps = new Dictionary<string, string>(),
            Manipulations = Array.Empty<object>(),
        }, options);
        File.WriteAllText(defaultFile, json);
    }

    /// <summary>Keeps the mod folder small: the original-sound copies, the current song and the one before it stay.</summary>
    private static void DeleteOldSongs(string modDir, string keep)
    {
        try
        {
            var old = new DirectoryInfo(Path.Combine(modDir, "songs")).GetFiles("*.scd")
                .Where(f => f.Name != keep && !f.Name.StartsWith("original_", StringComparison.Ordinal))
                .OrderByDescending(f => f.LastWriteTimeUtc).Skip(1);
            foreach (var f in old) f.Delete();
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, "Could not clean old songs"); }
    }

    /// <summary>
    /// Takes the song off your character by putting the emote's original sound back (the mod stays on, so
    /// Lightless keeps remembering the emote). Redrawing also cuts off a copy that's still playing.
    /// </summary>
    public async Task ClearAsync(bool redrawCharacter)
    {
        // Skipped or stopped: the song slot gets silence and you're redrawn, which cuts the song off. If you're
        // dancing, the redraw restarts the dance with that silence, so you keep dancing (no bow).
        string? oldFile = null;
        await framework.RunOnFrameworkThread(() =>
        {
            oldFile = appliedFile;
            if (oldFile == null) return;
            try { SetEmoteSound(EmoteFor(oldFile), null); }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Could not put the emote's original sound back"); }
            if (redrawCharacter) redraw.Invoke(0, RedrawType.Redraw);
        });
        Status = "Idle";

        if (oldFile != null && config.DeleteAfterPlay)
        {
            await Task.Delay(TimeSpan.FromSeconds(5)); // let the game let go of the file first
            try { File.Delete(oldFile); } catch { /* ignored */ }
        }
    }

    // Body skeletons of every playable race/gender, and which one the game falls back to when a
    // race has no animation file of its own (Highlander male -> Midlander male, Lalafell female -> Lalafell male).
    private static readonly string[] Skeletons =
    {
        "c0101", "c0201", "c0301", "c0401", "c0501", "c0601", "c0701", "c0801", "c0901",
        "c1001", "c1101", "c1201", "c1301", "c1401", "c1501", "c1601", "c1701", "c1801",
    };
    private static readonly Dictionary<string, string> SkeletonFallback = new() { ["c0301"] = "c0101", ["c1201"] = "c1101" };

    private readonly Dictionary<string, List<(string GamePath, string File)>> swapCache = new();

    /// <summary>
    /// Copies the replacement emote's timeline (.tmb) and animations (.pap, one per body type) out of the
    /// game data to disk, and returns them as game path -> file redirects for the emote being replaced.
    /// Real files (not path swaps) are used so Lightless can upload them like any other mod file.
    /// </summary>
    private List<(string GamePath, string File)> AnimationSwapFiles(ShareEmote emote)
    {
        if (emote.SwapFromKey == null || emote.SwapToKey == null) return new();
        if (swapCache.TryGetValue(emote.Command, out var cached) && cached.All(c => File.Exists(c.File))) return cached;

        var outDir = Path.Combine(dir, "swap", emote.Command.Trim('/'));
        Directory.CreateDirectory(outDir);
        var list = new List<(string, string)>();

        void Add(string gamePath, string sourceGamePath, string fileName)
        {
            var bytes = data.GetFile(sourceGamePath)?.Data;
            if (bytes == null) return;
            var file = Path.Combine(outDir, fileName);
            File.WriteAllBytes(file, bytes);
            list.Add((gamePath, file));
        }

        Add($"chara/action/{emote.SwapFromKey}.tmb", $"chara/action/{emote.SwapToKey}.tmb", "timeline.tmb");
        foreach (var sk in Skeletons)
        {
            var source = $"chara/human/{sk}/animation/a0001/bt_common/{emote.SwapToKey}.pap";
            if (!data.FileExists(source) && SkeletonFallback.TryGetValue(sk, out var fb))
                source = $"chara/human/{fb}/animation/a0001/bt_common/{emote.SwapToKey}.pap";
            Add($"chara/human/{sk}/animation/a0001/bt_common/{emote.SwapFromKey}.pap", source, $"{sk}.pap");
        }

        if (list.Count < 2)
            throw new FileNotFoundException($"Could not find the animation files for {emote.Name}.");

        Plugin.Log.Information($"Prepared {list.Count} animation swap files for {emote.Command}");
        swapCache[emote.Command] = list;
        return list;
    }

    /// <summary>Does the share emote (silently, without a chat message) so the song plays for everyone.</summary>
    public Task PerformEmoteAsync() => PerformEmoteAsync(Emote);

    private Task PerformEmoteAsync(ShareEmote emote)
    {
        // Only emotes from the fixed list, or a plain "/word" checked by MegaMusicMod.SafeEmote, are ever sent.
        var cmd = emote.Command + " motion";
        lastEmoteAt = DateTime.Now;
        Plugin.Log.Information($"Doing {cmd}");
        return framework.RunOnFrameworkThread(() => SendChatCommand(cmd));
    }

    internal static unsafe void SendChatCommand(string command)
    {
        var ui = UIModule.Instance();
        if (ui == null) return;
        var str = Utf8String.FromString(command);
        try { ui->ProcessChatBoxEntry(str); }
        finally { str->Dtor(true); }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        chat.ChatMessage -= OnChatMessage;
        resolvedSubscription.Dispose();
        // If a song is still on the emote, put the original sound back.
        try
        {
            framework.RunOnFrameworkThread(() =>
            {
                if (appliedFile != null) SetEmoteSound(EmoteFor(appliedFile), null);
            }).Wait(TimeSpan.FromSeconds(3));
        }
        catch { /* ignored */ }
        try { Directory.Delete(dir, true); } catch { /* ignored */ }
    }
}
