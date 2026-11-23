using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace Tundy;

/// <summary>The full Tundy control panel.</summary>
public sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly PlayerControls controls;
    private readonly ImportTab importTab;
    private bool selectImport;
    private string cleanResult = string.Empty;

    private string linkInput = string.Empty;
    private string linkError = string.Empty;
    private bool onlyIgnored;
    private bool selectSettings;
    private bool selectQueue;
    private string aliasInput = string.Empty;
    private string shuffleInput = string.Empty;
    private string ruleName = string.Empty;
    private string ruleWorld = string.Empty;
    private int rulePermission = (int)RequestPermission.AutoApprove;
    private List<(string Route, int Uses)>? scannedRoutes;
    private string ytDlpStatus = string.Empty;
    private string megaSetupResult = string.Empty;

    public MainWindow(Plugin plugin) : base("Tundy###TundyMain")
    {
        this.plugin = plugin;
        controls = new PlayerControls(plugin);
        importTab = new ImportTab(plugin);
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(460, 360), MaximumSize = new Vector2(1600, 1400) };
        Size = new Vector2(620, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void SelectSettingsTab() => selectSettings = true;

    public void SelectImportTab() => selectImport = true;

    public override void Draw()
    {
        DrawStatusBar();
        ImGui.PushTextWrapPos();
        if (plugin.TellTunesLoaded)
            ImGui.TextColored(ImGuiColors.DalamudRed,
                "TellTunes is also on, so Tundy ignores song requests. Turn TellTunes off in /xlplugins.");
        if (plugin.SoundyQListening)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed,
                "SoundyQ (in Soundy) is also on and answers the same tells, so Tundy ignores song requests. "
                + "Turn SoundyQ off in its window (Soundy's other tools can stay on).");
            if (ImGui.SmallButton("Open SoundyQ")) plugin.OpenSoundyQ();
        }
        ImGui.PopTextWrapPos();
        ImGui.Separator();

        if (!ImGui.BeginTabBar("tabs")) return;

        Tab("Player", DrawPlayerTab);
        Tab("Sharing", DrawSharingTab);
        var waiting = plugin.Pending.Count;
        Tab(waiting > 0 ? $"Queue ({plugin.Player.PendingCount} + {waiting} to approve)###queue" : $"Queue ({plugin.Player.PendingCount})###queue",
            DrawQueueTab, selectQueue);
        selectQueue = false;
        Tab("History", DrawHistoryTab);
        Tab("Requests", DrawRequestsTab);
        Tab("Access", DrawAccessTab);
        Tab("Import", importTab.Draw, selectImport);
        selectImport = false;
        Tab("Settings", DrawSettingsTab, selectSettings);
        selectSettings = false;

        ImGui.EndTabBar();
    }

    private static void Tab(string label, Action draw, bool select = false)
    {
        if (!ImGui.BeginTabItem(label, select ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None)) return;
        ImGui.Spacing();
        draw();
        ImGui.EndTabItem();
    }

    // ------------------------------------------------------------------ top bar

    private void DrawStatusBar()
    {
        var cfg = plugin.Config;

        var color = cfg.Enabled ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey3;
        ImGui.PushStyleColor(ImGuiCol.Button, color with { W = 0.35f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color with { W = 0.55f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color with { W = 0.75f });
        if (ImGui.Button(cfg.Enabled ? "Accepting !q requests" : "Requests paused"))
        {
            cfg.Enabled = !cfg.Enabled;
            plugin.SaveConfig();
        }
        ImGui.PopStyleColor(3);
        Ui.Tooltip(cfg.Enabled
            ? "People can send you songs with /tell. Click to pause."
            : "Tell requests are ignored. Click to start accepting them again.");

        ImGui.SameLine();
        ImGui.TextDisabled(cfg.QueueAccessMode switch
        {
            AccessMode.EveryonePreQueue => "· Anyone, with your approval",
            AccessMode.ListedOnly => "· Listed players only",
            _ => "· Anyone can request",
        });
        Ui.Tooltip("Change it in the Access tab.");
        ImGui.SameLine();
        if (plugin.Sharer.CanShare && plugin.Sharer.UsesMusicMod)
        {
            var ready = plugin.Music.SetupProblems().Count == 0;
            ImGui.TextColored(ready ? ImGuiColors.ParsedBlue : ImGuiColors.DalamudOrange, ready ? "· Via the music mod" : "· Music mod needs setup");
            Ui.Tooltip("Songs play through the music mod's DJ dance for you and your Lightless pairs nearby. See the Sharing tab.");
        }
        else if (plugin.Sharer.CanShare)
        {
            ImGui.TextColored(ImGuiColors.ParsedBlue, "· Shared via Lightless");
            Ui.Tooltip("Songs play for your Lightless pairs nearby. Set it up in the Sharing tab.");
        }
        else
        {
            ImGui.TextDisabled("· Just for you");
        }

        var waiting = plugin.Pending.Count;
        if (waiting > 0)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
            if (ImGui.SmallButton($"{waiting} to approve")) selectQueue = true;
            ImGui.PopStyleColor();
            Ui.Tooltip("Requests waiting for your approval. Click to see them in the Queue tab.");
        }

        // Right-aligned "Now playing" window toggle
        var mini = cfg.ShowMiniPlayer;
        const string label = "Now playing window";
        var w = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - w);
        if (ImGui.Checkbox(label, ref mini))
            plugin.SetMiniPlayer(mini);
        Ui.Tooltip("Small window with the song's name and time. Also: /tundy mini");
    }

    // ------------------------------------------------------------------ Player

    private void DrawPlayerTab()
    {
        var player = plugin.Player;
        var width = ImGui.GetContentRegionAvail().X;

        ImGui.TextDisabled("NOW PLAYING");
        if (player.Current is { } t)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(t.Title);
            ImGui.PopTextWrapPos();
            ImGui.TextDisabled("Requested by");
            ImGui.SameLine();
            Ui.SenderCell(plugin, t.Request.Sender, "np");
            if (player.CurrentIsShared)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.ParsedBlue, plugin.Sharer.UsesMusicMod
                    ? $"(playing through the music mod for Lightless pairs, {plugin.Sharer.Emote.Command})"
                    : $"(playing for Lightless pairs via {plugin.Sharer.Emote.Command})");
            }
        }
        else if (player.WaitingForSync)
        {
            ImGui.TextUnformatted("Syncing through Lightless...");
            ImGui.TextDisabled($"Starts in a few seconds so your pairs can download it. ({plugin.Sharer.Status})");
        }
        else
        {
            ImGui.TextUnformatted(player.Downloading != null ? "Downloading..." : "Nothing playing");
            ImGui.TextDisabled(player.Downloading != null ? $"from {player.Downloading.Sender}" : "Waiting for requests.");
        }

        ImGui.Spacing();
        controls.DrawSeekBar(width);
        controls.DrawButtons();
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(12, 0));
        ImGui.SameLine();
        controls.DrawVolume(Math.Min(220 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight()));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var (next, ready) = player.UpNext;
        ImGui.TextDisabled("UP NEXT");
        if (next != null)
        {
            ImGui.TextUnformatted(next.Display);
            ImGui.TextDisabled($"from {next.Sender} · {(ready ? "ready" : "downloading...")}");
        }
        else
        {
            var q = player.QueueSnapshot();
            ImGui.TextUnformatted(q.Length > 0 ? q[0].Display : "Nothing queued");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawAddLink("player");
    }

    private string lastSubmitted = string.Empty;
    private DateTime lastSubmittedAt = DateTime.MinValue;

    /// <param name="id">Unique per place it's drawn, so two boxes never share their text or input.</param>
    private void DrawAddLink(string id)
    {
        ImGui.PushID(id);
        ImGui.TextDisabled("ADD A SONG YOURSELF");
        var submit = false;
        const string label = "Add to queue";
        var buttonW = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - buttonW - ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.InputTextWithHint("##link", "Paste a YouTube or Spotify track link", ref linkInput, 512, ImGuiInputTextFlags.EnterReturnsTrue))
            submit = true;
        ImGui.SameLine();
        if (ImGui.Button(label)) submit = true;
        ImGui.PopID();

        // Ignore the same link sent again within 2 seconds (e.g. Enter and a click together).
        if (submit && linkInput.Trim() == lastSubmitted && (DateTime.Now - lastSubmittedAt).TotalSeconds < 2)
            submit = false;

        if (submit && !string.IsNullOrWhiteSpace(linkInput))
        {
            lastSubmitted = linkInput.Trim();
            lastSubmittedAt = DateTime.Now;
            if (plugin.AddManual(linkInput, out var err))
            {
                linkInput = string.Empty;
                linkError = string.Empty;
            }
            else
            {
                linkError = err;
            }
        }

        if (linkError.Length > 0)
            ImGui.TextColored(ImGuiColors.DalamudRed, linkError);
    }

    // ------------------------------------------------------------------ Sharing

    private void DrawSharingTab()
    {
        var cfg = plugin.Config;
        var sharer = plugin.Sharer;
        var changed = false;

        ImGui.PushTextWrapPos();
        ImGui.TextUnformatted("Let people around you hear the music. Tundy puts each song on your character as a Penumbra mod, "
                              + "Lightless Sync sends it to the people you're paired with, and when the song starts your character "
                              + "does an emote that plays it. They don't need Tundy, only Lightless.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        // Requirements
        Check("Penumbra", sharer.PenumbraAvailable, "Needed to put the song on your character.");
        Check("Lightless Sync", sharer.LightlessLoaded, "Needed to send it to other players.");
        ImGui.Spacing();

        var share = cfg.ShareViaLightless;
        if (ImGui.Checkbox("Share songs with nearby Lightless pairs", ref share)) { cfg.ShareViaLightless = share; changed = true; }
        Ui.Help("Off = songs play only for you. Also: /tundy share");

        if (!share) ImGui.BeginDisabled();

        ImGui.Spacing();
        ImGui.TextUnformatted("How the song plays in game:");
        var target = cfg.ShareTarget;
        if (ImGui.RadioButton("Through a music mod's DJ dance (recommended, like SoundyQ)", ref target, 0)) { cfg.ShareTarget = 0; changed = true; }
        Ui.Help("The music mod (Yue & Lu's Mega Music Mod) plays one sound file during its dance. Tundy puts the song on that "
                + "file while it plays. The music mod itself isn't changed.");
        if (ImGui.RadioButton("On one emote's own sound", ref target, 1)) { cfg.ShareTarget = 1; changed = true; }
        Ui.Help("Works without any music mod: the song replaces an emote's normal sound while it plays.");

        var current = sharer.Emote;
        if (sharer.UsesMusicMod)
            changed |= DrawMusicModSection();
        else
            changed |= DrawEmoteChoice(current);

        if (sharer.UsesMusicMod)
        {
            var between = cfg.BetweenSongsSilence ? 0 : 1;
            ImGui.TextUnformatted("Between requests, the dance plays:");
            if (ImGui.RadioButton("Nothing (quiet)", ref between, 0) | ImGui.RadioButton("The song picked in the music mod", ref between, 1))
            {
                cfg.BetweenSongsSilence = between == 0;
                changed = true;
            }
            Ui.Help("Quiet: Tundy keeps silence on the song slot between requests. The music mod's song: what you picked in "
                    + "the mod (for example a song added in the Import tab) plays whenever you dance. Applies after the next song.");
        }

        Ui.Section("Starting songs");
        var auto = cfg.ShareAutoEmote;
        if (ImGui.Checkbox("Do the emote automatically when a song starts", ref auto)) { cfg.ShareAutoEmote = auto; changed = true; }
        Ui.Help("Off = do the emote yourself (or click the button below) when you want people to hear it.");

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var delay = cfg.ShareSyncDelaySeconds;
        if (ImGui.SliderInt("Wait before starting (seconds)", ref delay, 0, 60)) { cfg.ShareSyncDelaySeconds = delay; changed = true; }
        Ui.Help("Gives Lightless time to upload the song and your pairs time to download it. "
                + "Raise it if people say the song plays late or not at all.");

        var silence = cfg.ShareSilenceLocal;
        if (ImGui.Checkbox("Only hear the in-game copy (no echo)", ref silence))
        {
            cfg.ShareSilenceLocal = silence;
            plugin.Player.ApplyVolume();
            changed = true;
        }
        Ui.Help("While sharing, the song also plays for you through the game's sound effects. "
                + "This mutes Tundy's own copy so you don't hear it twice. Use the game's sound effect volume to adjust it.");

        var small = cfg.ShareSmallerFiles;
        if (ImGui.Checkbox("Smaller files (syncs faster, sounds duller)", ref small)) { cfg.ShareSmallerFiles = small; changed = true; }
        Ui.Help(sharer.UsesMusicMod
            ? "Music mod songs are stereo Vorbis: about 1.2 MB per minute of music, or about 0.7 MB smaller. Applies to songs downloaded after you change it."
            : "Normal: about 1.4 MB per minute of music. Smaller: about 0.7 MB. Applies to songs downloaded after you change it.");
        if (sharer.UsesMusicMod && plugin.Ffmpeg.CurrentPath == null)
            ImGui.TextColored(ImGuiColors.DalamudOrange, plugin.Ffmpeg.Downloading
                ? plugin.Ffmpeg.Status
                : "ffmpeg isn't installed yet: the music mod needs it. It downloads with the first song, or in Settings → Download tools.");

        ImGui.Spacing();
        if (ImGui.Button($"Do {current.Command} now"))
            _ = sharer.PerformEmoteAsync();
        Ui.Tooltip("Plays the shared song again for people nearby. Also useful the very first time, so Lightless learns the sound file.");

        if (!share) ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextDisabled($"Status: {sharer.Status}");
        ImGui.PushTextWrapPos();
        ImGui.TextColored(sharer.LastCheckOk ? ImGuiColors.HealerGreen : ImGuiColors.DalamudOrange, $"Last check: {sharer.LastCheck}");
        ImGui.PopTextWrapPos();

        Ui.Section("Good to know");
        ImGui.PushTextWrapPos();
        ImGui.BulletText("Only people paired with you in Lightless (directly or in a Syncshell) and close to you can hear it.");
        ImGui.BulletText("The first time you share with an emote, your character does it once with its normal sound (about 10 seconds early) so Lightless learns it. After that, songs start sooner.");
        ImGui.BulletText(sharer.UsesMusicMod
            ? "The \"Tundy Song\" mod stays on in Penumbra. Between songs it holds silence, so the dance doesn't play one of the music mod's own tracks."
            : "The \"Tundy Song\" mod stays on in Penumbra. Between songs it holds a copy of the emote's original sound, so the emote sounds normal.");
        ImGui.BulletText("The volume bar only changes Tundy's own player. While a song is shared you hear the in-game copy, so use the game's sound effect volume for that.");
        ImGui.BulletText("Your pairs need a moment to download each song. If they hear it late or not at all, raise \"Wait before starting\" (10+ seconds is safest).");
        ImGui.BulletText("Seeking is off while sharing. Skip and stop work, and stop the in-game copy for you. Others may hear the end of it.");
        ImGui.BulletText("The sound comes from your character, so it gets quieter as people walk away.");
        ImGui.PopTextWrapPos();

        if (changed) plugin.SaveConfig();
    }

    /// <summary>The music mod, its song slot and its emote. Returns true if a setting changed.</summary>
    private bool DrawMusicModSection()
    {
        var cfg = plugin.Config;
        var music = plugin.Music;
        var changed = false;
        Ui.Section("Music mod");

        ImGui.PushTextWrapPos();
        var problems = music.SetupProblems();
        if (problems.Count == 0)
            ImGui.TextColored(ImGuiColors.HealerGreen, $"\"{cfg.QueueTargetMod}\" is on. Songs play through its DJ dance on {MegaMusicMod.SafeEmote(cfg.QueueEmoteCommand)}.");
        else
            foreach (var p in problems) ImGui.TextColored(ImGuiColors.DalamudOrange, p);
        ImGui.PopTextWrapPos();

        if (music.IsRecommended && music.State().Installed)
        {
            if (ImGui.Button("Set up Mega Music Mod for me"))
                megaSetupResult = music.SetUp();
            Ui.Tooltip("Turns the mod on in your Penumbra collection, picks its \"Dj (Cheer Jump)\" dance and the DJ deck for your race.");
            if (megaSetupResult.Length > 0)
            {
                ImGui.PushTextWrapPos();
                ImGui.TextDisabled(megaSetupResult);
                ImGui.PopTextWrapPos();
            }
        }

        // Which mod
        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Music mod", cfg.QueueTargetMod))
        {
            foreach (var (folder, name) in music.ModList().OrderBy(m => m.Value, StringComparer.OrdinalIgnoreCase))
            {
                if (ImGui.Selectable(name == folder ? name : $"{name} ({folder})", folder == cfg.QueueTargetMod))
                {
                    cfg.QueueTargetMod = folder;
                    scannedRoutes = null;
                    music.Recheck();
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        Ui.Help("The Penumbra mod whose dance plays the song. Default: [Yue & Lu's] Mega Music Mod.");
        if (!music.IsRecommended)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Use Mega Music Mod"))
            {
                cfg.QueueTargetMod = MegaMusicMod.RecommendedMod;
                scannedRoutes = null;
                music.Recheck();
                changed = true;
            }
        }

        // Which sound file(s)
        ImGui.TextUnformatted($"Song slot: {string.Join(", ", music.Routes)}");
        Ui.Help("The game sound file the dance plays. For the Mega Music Mod it's sound/lolo.scd.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Scan the mod")) scannedRoutes = music.ScanRoutes();
        Ui.Tooltip("Lists the sound files this mod uses, most used first. The song slot is usually the top one.");
        if (scannedRoutes != null)
        {
            if (scannedRoutes.Count == 0) ImGui.TextDisabled("No sound files found in this mod.");
            ImGui.BeginChild("routes", new Vector2(0, Math.Min(scannedRoutes.Count, 6) * ImGui.GetFrameHeightWithSpacing() + 4), true);
            foreach (var (route, uses) in scannedRoutes)
            {
                var on = cfg.QueueScdRoutes.Contains(route, StringComparer.OrdinalIgnoreCase);
                if (ImGui.Checkbox($"{route}  ({uses} options)##{route}", ref on))
                {
                    if (on) cfg.QueueScdRoutes.Add(route);
                    else cfg.QueueScdRoutes.RemoveAll(r => r.Equals(route, StringComparison.OrdinalIgnoreCase));
                    changed = true;
                }
            }
            ImGui.EndChild();
        }
        if (!cfg.QueueScdRoutes.Contains(MegaMusicMod.RecommendedRoute, StringComparer.OrdinalIgnoreCase) && music.IsRecommended)
        {
            if (ImGui.SmallButton($"Use {MegaMusicMod.RecommendedRoute}"))
            {
                cfg.QueueScdRoutes = new List<string> { MegaMusicMod.RecommendedRoute };
                changed = true;
            }
        }

        // Which emote
        var emote = cfg.QueueEmoteCommand;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Emote that plays it", ref emote, 32))
        {
            cfg.QueueEmoteCommand = emote;
            changed = true;
        }
        Ui.Help("The emote the music mod's dance is on. For the Mega Music Mod's DJ dance it's /cheerjumpred. "
                + "Done once when each song starts, without a chat message.");
        if (MegaMusicMod.SafeEmote(emote) != emote.Trim().ToLowerInvariant())
            ImGui.TextColored(ImGuiColors.DalamudOrange, $"That isn't a plain emote command; {MegaMusicMod.RecommendedEmote} is used instead.");

        return changed;
    }

    /// <summary>The emote whose own sound carries the song. Returns true if a setting changed.</summary>
    private bool DrawEmoteChoice(ShareEmote current)
    {
        var cfg = plugin.Config;
        var changed = false;
        Ui.Section("Emote");
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Emote that plays the song", $"{current.Name} ({current.Command})"))
        {
            foreach (var e in SongSharer.Emotes)
            {
                var label = e.UnlockedByDefault ? $"{e.Name} ({e.Command}) - everyone has it" : $"{e.Name} ({e.Command})";
                if (ImGui.Selectable(label, e.Command == current.Command))
                {
                    cfg.ShareEmoteCommand = e.Command;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        Ui.Help("You need to own the emote. Its normal sound is replaced by the song only while a song is shared. "
                + "If a song cuts off early, try a different emote.");
        if (current.Note.Length > 0)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextDisabled(current.Note);
            ImGui.PopTextWrapPos();
        }
        return changed;
    }

    private static void Check(string name, bool ok, string why)
    {
        ImGui.TextColored(ok ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed, ok ? $"{name}: found" : $"{name}: not found");
        Ui.Tooltip(why);
    }

    // ------------------------------------------------------------------ Queue

    private void DrawQueueTab()
    {
        DrawPendingSection();

        var player = plugin.Player;
        var queue = player.FullQueue(); // play order, the song being prepared to play next first
        var (_, ready) = player.UpNext;
        var downloading = player.Downloading;

        if (Ui.IconButton("shuffle", FontAwesomeIcon.Random, "Shuffle the queue", queue.Length < 2))
            player.Shuffle();
        ImGui.SameLine();
        if (Ui.IconButton("clear", FontAwesomeIcon.Trash, "Clear the queue (hold Ctrl)", queue.Length == 0)
            && ImGui.GetIO().KeyCtrl)
            player.ClearQueue();
        ImGui.SameLine();
        ImGui.TextDisabled($"{player.PendingCount} waiting · limit {plugin.Config.MaxQueueLength} for tell requests");

        var footer = ImGui.GetFrameHeightWithSpacing() * 3.5f;
        var rowH = ImGui.GetFrameHeight();
        var actionsW = (rowH + ImGui.GetStyle().ItemSpacing.X) * 4;

        if (ImGui.BeginTable("queue", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, ImGui.GetContentRegionAvail().Y - footer)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 26 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Song", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, actionsW);
            ImGui.TableHeadersRow();

            if (downloading != null && player.Current == null)
                SpecialRow("...", downloading, "downloading", allowRemove: false);

            for (var i = 0; i < queue.Length; i++)
            {
                var (r, isNext) = queue[i];
                ImGui.PushID(r.Id.ToString());
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (isNext) ImGui.TextColored(ImGuiColors.ParsedBlue, (i + 1).ToString());
                else ImGui.TextDisabled((i + 1).ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.Display);
                if (isNext)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(ready ? "(ready)" : "(getting it ready...)");
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{r.Url}\nRequested {Ui.Ago(r.RequestedAt)}"
                    + (isNext ? "\nDownloaded and converted early so it starts right after the current song. "
                                + "Moving it (or another song above it) prepares the new first song instead." : ""));
                ImGui.TableNextColumn();
                Ui.SenderCell(plugin, r.Sender, "s");
                ImGui.TableNextColumn();
                if (Ui.IconButton("now", FontAwesomeIcon.Play, "Play now")) player.PlayNow(r);
                ImGui.SameLine();
                if (Ui.IconButton("up", FontAwesomeIcon.ArrowUp, "Move up", i == 0)) player.Move(r.Id, -1);
                ImGui.SameLine();
                if (Ui.IconButton("down", FontAwesomeIcon.ArrowDown, "Move down", i == queue.Length - 1)) player.Move(r.Id, 1);
                ImGui.SameLine();
                if (Ui.IconButton("rm", FontAwesomeIcon.Times, "Remove")) player.Remove(r.Id);
                ImGui.PopID();
            }

            if (queue.Length == 0 && downloading == null)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                ImGui.TextDisabled("The queue is empty.");
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        DrawAddLink("queue");
    }

    private void SpecialRow(string tag, SongRequest r, string state, bool allowRemove)
    {
        ImGui.PushID("special" + r.Id);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(ImGuiColors.ParsedBlue, tag);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(r.Display);
        ImGui.SameLine();
        ImGui.TextDisabled($"({state})");
        ImGui.TableNextColumn();
        Ui.SenderCell(plugin, r.Sender, "s");
        ImGui.TableNextColumn();
        if (allowRemove && Ui.IconButton("rm", FontAwesomeIcon.Times, "Remove"))
            plugin.Player.Remove(r.Id);
        ImGui.PopID();
    }

    /// <summary>Requests waiting for your approval, shown above the queue when there are any.</summary>
    private void DrawPendingSection()
    {
        var pending = plugin.Pending.Snapshot();
        if (pending.Length == 0) return;

        ImGui.TextColored(ImGuiColors.DalamudOrange, $"WAITING FOR YOUR APPROVAL ({pending.Length})");
        ImGui.SameLine();
        if (ImGui.SmallButton("Approve all")) plugin.ApproveAll();

        var rowH = ImGui.GetFrameHeight();
        var actionsW = (rowH + ImGui.GetStyle().ItemSpacing.X) * 3;
        var height = Math.Min(pending.Length, 5) * (rowH + ImGui.GetStyle().CellPadding.Y * 2) + ImGui.GetFrameHeightWithSpacing();
        if (ImGui.BeginTable("pending", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, height)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Song", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, actionsW);
            ImGui.TableHeadersRow();

            foreach (var p in pending)
            {
                ImGui.PushID(p.Id.ToString());
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled(Ui.Ago(p.RequestedAt));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(p.Display);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(p.Url + "\nClick to copy the link");
                if (ImGui.IsItemClicked()) ImGui.SetClipboardText(p.Url);
                ImGui.TableNextColumn();
                Ui.SenderCell(plugin, p.Sender, "s");
                ImGui.TableNextColumn();
                if (Ui.IconButton("ok", FontAwesomeIcon.Check, "Approve: add it to the queue")) plugin.Approve(p.Id);
                ImGui.SameLine();
                if (Ui.IconButton("always", FontAwesomeIcon.UserCheck, "Approve, and always approve this person from now on"))
                {
                    plugin.SetRule(p.Sender, RequestPermission.AutoApprove);
                    plugin.Approve(p.Id);
                }
                ImGui.SameLine();
                if (Ui.IconButton("no", FontAwesomeIcon.Times, "Reject")) plugin.Reject(p.Id);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    // ------------------------------------------------------------------ History

    private void DrawHistoryTab()
    {
        var player = plugin.Player;
        var history = player.HistorySnapshot();

        if (Ui.IconButton("clearh", FontAwesomeIcon.Trash, "Clear history", history.Length == 0))
            player.ClearHistory();
        ImGui.SameLine();
        ImGui.TextDisabled($"{history.Length} songs played this session");

        var rowH = ImGui.GetFrameHeight();
        var actionsW = (rowH + ImGui.GetStyle().ItemSpacing.X) * 3;

        if (!ImGui.BeginTable("history", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, ImGui.GetContentRegionAvail().Y)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Song", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, actionsW);
        ImGui.TableHeadersRow();

        for (var i = 0; i < history.Length; i++)
        {
            var h = history[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextDisabled(Ui.Ago(h.PlayedAt));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(h.Title);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(h.Request.Url);
            ImGui.TableNextColumn();
            Ui.SenderCell(plugin, h.Request.Sender, "s");
            ImGui.TableNextColumn();
            if (Ui.IconButton("replay", FontAwesomeIcon.Play, "Play again now"))
                player.PlayNow(new SongRequest(h.Request.Url, "You", h.Request.SpotifyId));
            ImGui.SameLine();
            if (Ui.IconButton("add", FontAwesomeIcon.Plus, "Add to the end of the queue"))
                player.TryEnqueue(new SongRequest(h.Request.Url, "You", h.Request.SpotifyId), force: true);
            ImGui.SameLine();
            if (Ui.IconButton("copy", FontAwesomeIcon.Copy, "Copy link"))
                ImGui.SetClipboardText(h.Request.Url);
            ImGui.PopID();
        }

        if (history.Length == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TextDisabled("Nothing played yet.");
        }

        ImGui.EndTable();
    }

    // ------------------------------------------------------------------ Requests log

    private void DrawRequestsTab()
    {
        var log = plugin.RequestLogSnapshot();
        if (onlyIgnored) log = log.Where(l => !l.Accepted).ToArray();

        if (Ui.IconButton("clearl", FontAwesomeIcon.Trash, "Clear the request log"))
            plugin.ClearRequestLog();
        ImGui.SameLine();
        ImGui.Checkbox("Only show ignored / failed", ref onlyIgnored);
        ImGui.SameLine();
        ImGui.TextDisabled("· right-click a name to block it");

        if (!ImGui.BeginTable("log", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable,
                new Vector2(0, ImGui.GetContentRegionAvail().Y)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("Link", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableHeadersRow();

        for (var i = 0; i < log.Length; i++)
        {
            var e = log[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextDisabled(e.Time.ToString("HH:mm:ss"));
            ImGui.TableNextColumn();
            Ui.SenderCell(plugin, e.Sender, "s");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(e.Link);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(e.Link + "\nClick to copy");
            if (ImGui.IsItemClicked()) ImGui.SetClipboardText(e.Link);
            ImGui.TableNextColumn();
            ImGui.TextColored(e.Accepted ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed, e.Outcome);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(e.Outcome);
            ImGui.PopID();
        }

        if (log.Length == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TextDisabled("No requests yet.");
        }

        ImGui.EndTable();
    }

    // ------------------------------------------------------------------ Access

    private void DrawAccessTab()
    {
        var cfg = plugin.Config;
        var changed = false;
        ImGui.BeginChild("access", Vector2.Zero, false);

        // Request commands
        Ui.Section("Request commands");
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled("People send \"/tell <you> <command> <link>\". \"<command> list\" tells them what's playing and how many songs are waiting.");
        ImGui.PopTextWrapPos();
        for (var i = 0; i < cfg.QueueAliases.Count; i++)
        {
            ImGui.PushID($"alias{i}");
            if (Ui.IconButton("x", FontAwesomeIcon.Times, "Remove", cfg.QueueAliases.Count <= 1))
            {
                cfg.QueueAliases.RemoveAt(i);
                changed = true;
                ImGui.PopID();
                break;
            }
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(cfg.QueueAliases[i]);
            ImGui.PopID();
            if (i < cfg.QueueAliases.Count - 1) ImGui.SameLine(0, 18 * ImGuiHelpers.GlobalScale);
        }
        ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
        var addAlias = ImGui.InputTextWithHint("##alias", "!play", ref aliasInput, 24, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add command") || addAlias) && !string.IsNullOrWhiteSpace(aliasInput))
        {
            var a = aliasInput.Trim();
            if (!a.Contains(' ') && !a.Equals("!skip", StringComparison.OrdinalIgnoreCase)
                && !cfg.QueueAliases.Contains(a, StringComparer.OrdinalIgnoreCase))
            {
                cfg.QueueAliases.Add(a);
                changed = true;
            }
            aliasInput = string.Empty;
        }
        Ui.Help("One word, no spaces, e.g. !play. !skip is always the skip command.");

        // Shuffle commands
        Ui.Section("Shuffle commands");
        var allowShuffle = cfg.AllowShuffleCommand;
        if (ImGui.Checkbox("Accept shuffle commands", ref allowShuffle)) { cfg.AllowShuffleCommand = allowShuffle; changed = true; }
        Ui.Help("\"<command> <playlist link>\" queues the playlist shuffled (with the max songs per playlist, a random "
                + "selection from the whole playlist), whatever the \"Shuffle playlists\" setting says. A single song link "
                + "is queued normally. The same rules as for requests apply: who can request, approval, cooldown.");
        if (!allowShuffle) ImGui.BeginDisabled();
        for (var i = 0; i < cfg.ShuffleCommands.Count; i++)
        {
            ImGui.PushID($"shuf{i}");
            if (Ui.IconButton("x", FontAwesomeIcon.Times, "Remove"))
            {
                cfg.ShuffleCommands.RemoveAt(i);
                changed = true;
                ImGui.PopID();
                break;
            }
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            var cmdText = cfg.ShuffleCommands[i];
            if (cfg.QueueAliases.Contains(cmdText, StringComparer.OrdinalIgnoreCase))
                ImGui.TextColored(ImGuiColors.DalamudOrange, $"{cmdText} (also a request command, so it doesn't shuffle)");
            else
                ImGui.TextUnformatted(cmdText);
            ImGui.PopID();
            if (i < cfg.ShuffleCommands.Count - 1) ImGui.SameLine(0, 18 * ImGuiHelpers.GlobalScale);
        }
        if (cfg.ShuffleCommands.Count == 0) ImGui.TextDisabled("No shuffle commands.");
        ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
        var addShuffle = ImGui.InputTextWithHint("##shuffleAdd", "!shuffle", ref shuffleInput, 24, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add shuffle command") || addShuffle) && !string.IsNullOrWhiteSpace(shuffleInput))
        {
            var s = shuffleInput.Trim();
            if (!s.Contains(' ') && !s.Equals("!skip", StringComparison.OrdinalIgnoreCase)
                && !cfg.ShuffleCommands.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                cfg.ShuffleCommands.Add(s);
                changed = true;
            }
            shuffleInput = string.Empty;
        }
        if (!allowShuffle) ImGui.EndDisabled();

        // Who can request
        Ui.Section("Who can send you songs");
        var mode = (int)cfg.QueueAccessMode;
        if (ImGui.RadioButton("Anyone, straight into the queue", ref mode, (int)AccessMode.EveryoneAutoApprove)
            | ImGui.RadioButton("Anyone, but I approve each request first", ref mode, (int)AccessMode.EveryonePreQueue)
            | ImGui.RadioButton("Only the players listed below", ref mode, (int)AccessMode.ListedOnly))
        {
            cfg.QueueAccessMode = (AccessMode)mode;
            changed = true;
        }
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled("Players below get their own setting instead. Right-click a name anywhere in this window to change theirs.");
        ImGui.PopTextWrapPos();

        // Player rules
        Ui.Section($"Players ({cfg.QueuePlayerRules.Count})");
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
        var submit = ImGui.InputTextWithHint("##rname", "Firstname Lastname", ref ruleName, 40, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        submit |= ImGui.InputTextWithHint("##rworld", "World (any)", ref ruleWorld, 24, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
        ImGui.Combo("##rperm", ref rulePermission, Ui.PermissionNames, Ui.PermissionNames.Length);
        ImGui.SameLine();
        if ((ImGui.Button("Add player") || submit) && !string.IsNullOrWhiteSpace(ruleName))
        {
            plugin.SetRule(ruleName.Trim(), (RequestPermission)rulePermission);
            if (plugin.RuleFor(ruleName.Trim()) is { } added) added.World = ruleWorld.Trim();
            ruleName = ruleWorld = string.Empty;
            changed = true;
        }

        if (ImGui.BeginTable("rules", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 2);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn("Their requests", ImGuiTableColumnFlags.WidthFixed, 150 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.X);
            ImGui.TableHeadersRow();

            for (var i = 0; i < cfg.QueuePlayerRules.Count; i++)
            {
                var r = cfg.QueuePlayerRules[i];
                ImGui.PushID($"rule{i}");
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                if (r.Enabled) ImGui.TextUnformatted(r.Name);
                else ImGui.TextDisabled($"{r.Name} (off)");
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(r.World.Length > 0 ? r.World : "any");
                ImGui.TableNextColumn();
                var perm = (int)r.Permission;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##perm", ref perm, Ui.PermissionNames, Ui.PermissionNames.Length))
                {
                    r.Permission = (RequestPermission)perm;
                    changed = true;
                }
                ImGui.TableNextColumn();
                var enabled = r.Enabled;
                if (ImGui.Checkbox("##on", ref enabled)) { r.Enabled = enabled; changed = true; }
                Ui.Tooltip(r.Enabled ? "On. Untick to ignore this rule for now." : "Off: the general setting applies.");
                ImGui.SameLine();
                if (Ui.IconButton("x", FontAwesomeIcon.Times, "Remove"))
                {
                    cfg.QueuePlayerRules.RemoveAt(i);
                    changed = true;
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }

            if (cfg.QueuePlayerRules.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled("No players yet.");
            }
            ImGui.EndTable();
        }

        ImGui.EndChild();
        if (changed) plugin.SaveConfig();
    }

    // ------------------------------------------------------------------ Settings

    private void DrawSettingsTab()
    {
        var cfg = plugin.Config;
        var changed = false;

        ImGui.BeginChild("settings", Vector2.Zero, false);

        Ui.Section("Requests");
        var enabled = cfg.Enabled;
        if (ImGui.Checkbox($"Accept song request tells ({string.Join(", ", plugin.RequestCommands)})", ref enabled)) { cfg.Enabled = enabled; changed = true; }
        Ui.Help("Change the commands and who can request in the Access tab.");

        var spotify = cfg.AllowSpotify;
        if (ImGui.Checkbox("Accept Spotify track links", ref spotify)) { cfg.AllowSpotify = spotify; changed = true; }
        Ui.Help("Spotify audio can't be downloaded, so the same song is found and played from YouTube.");

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var maxMin = cfg.MaxDurationSeconds / 60;
        if (ImGui.SliderInt("Max song length (minutes)", ref maxMin, 1, 60)) { cfg.MaxDurationSeconds = maxMin * 60; changed = true; }

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var maxQ = cfg.MaxQueueLength;
        if (ImGui.SliderInt("Max queue length", ref maxQ, 1, 100)) { cfg.MaxQueueLength = maxQ; changed = true; }
        Ui.Help("Tell requests are ignored when the queue is this long. Songs you add yourself always go in.");

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var cd = cfg.PerSenderCooldownSeconds;
        if (ImGui.SliderInt("Cooldown per person (seconds)", ref cd, 0, 600)) { cfg.PerSenderCooldownSeconds = cd; changed = true; }
        Ui.Help("How long one person has to wait between requests.");

        Ui.Section("Announce songs in chat");
        var announce = cfg.AnnounceSongs;
        if (ImGui.Checkbox("Say the song name and who requested it when a song starts", ref announce)) { cfg.AnnounceSongs = announce; changed = true; }
        Ui.Help("Posts one message from your character each time a song starts, in the channel below. "
                + "At most one message every 20 seconds, so skipping songs doesn't spam the channel.");
        if (!announce) ImGui.BeginDisabled();

        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        var channelName = Announcer.Channels.FirstOrDefault(c => c.Command == cfg.AnnounceChannel).Name ?? "Yell";
        if (ImGui.BeginCombo("Channel", $"{channelName} ({cfg.AnnounceChannel})"))
        {
            foreach (var (command, name) in Announcer.Channels)
                if (ImGui.Selectable($"{name} ({command})", command == cfg.AnnounceChannel))
                {
                    cfg.AnnounceChannel = command;
                    changed = true;
                }
            ImGui.EndCombo();
        }

        var template = cfg.AnnounceTemplate;
        ImGui.SetNextItemWidth(380 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Message", ref template, 200)) { cfg.AnnounceTemplate = template; changed = true; }
        Ui.Help("{title} becomes the song name and {requester} the person who sent it. "
                + "< and > are removed (the game would turn <pos> or <t> into your position or target), and so are line breaks.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset")) { cfg.AnnounceTemplate = Announcer.DefaultTemplate; changed = true; }

        ImGui.PushTextWrapPos();
        ImGui.TextDisabled($"Preview: {cfg.AnnounceChannel} {plugin.Announcer.Build(cfg.AnnounceTemplate, "Blinding Lights", "Alex Example")}");
        ImGui.PopTextWrapPos();
        if (ImGui.Button("Send a test message")) plugin.Announcer.SendTest();
        Ui.Tooltip($"Really posts the preview (with \"Example Song\") in {channelName}.");
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.Announcer.LastResult);
        if (!announce) ImGui.EndDisabled();

        Ui.Section("Skipping with !skip");
        string[] skipModes = { "Nobody (!skip is off)", "Only the person who requested the song", "Anyone who's allowed to request songs" };
        var skipMode = Math.Clamp(cfg.SkipPermission, 0, 2);
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("Who can skip", ref skipMode, skipModes, skipModes.Length)) { cfg.SkipPermission = skipMode; changed = true; }
        Ui.Help("People send you \"/tell <you> !skip\" to skip the song that's playing. "
                + "Blocked people can never skip, and one person can skip at most once every 10 seconds. "
                + "With \"Tell people where their song is in the queue\" on, they also get a short reply.");

        Ui.Section("Reply to requesters");
        var reply = cfg.ReplyQueuePosition;
        if (ImGui.Checkbox("Tell people where their song is in the queue", ref reply)) { cfg.ReplyQueuePosition = reply; changed = true; }
        Ui.Help("After a request is accepted, sends that person a tell like: \"Your song is in the queue. 3 songs play before it.\" "
                + "It uses /tell Name@World to reach exactly that person; /r could reach whoever messaged you last instead.");
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled("Also tells people when their request waits for your approval, was approved or rejected, or is already in the queue.");
        if (reply) ImGui.TextDisabled(plugin.Replier.LastResult);
        ImGui.PopTextWrapPos();

        Ui.Section("Failed downloads");
        var retry = cfg.AutoRetry;
        if (ImGui.Checkbox("Retry automatically when a song's download fails", ref retry)) { cfg.AutoRetry = retry; changed = true; }
        Ui.Help("For temporary problems (network, YouTube slowing downloads down, bot checks). Songs that can't ever play "
                + "(private, removed, age-restricted, blocked in your region, too long, live) aren't retried.");
        if (!retry) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var tries = cfg.RetryAttempts;
        if (ImGui.SliderInt("Retries per song", ref tries, 1, 5)) { cfg.RetryAttempts = tries; changed = true; }
        Ui.Help("Waits a few seconds longer before each try. Before the last try Tundy also updates yt-dlp, since YouTube changes often break older versions.");
        if (!retry) ImGui.EndDisabled();

        Ui.Section("Playlists");
        var allowPl = cfg.AllowPlaylists;
        if (ImGui.Checkbox("Accept playlists (YouTube playlists, Spotify playlists and albums)", ref allowPl)) { cfg.AllowPlaylists = allowPl; changed = true; }
        Ui.Help("Send them like a song: !q <playlist link>. Live streams and songs longer than the max length are left out. "
                + "A plain YouTube video link that happens to be in a playlist still plays just that video.");
        if (!allowPl) ImGui.BeginDisabled();
        var shuffle = cfg.ShufflePlaylists;
        if (ImGui.Checkbox("Shuffle playlists", ref shuffle)) { cfg.ShufflePlaylists = shuffle; changed = true; }
        Ui.Help("Mixes up the playlist's songs before they're queued. With the song limit below, that also means a random selection from the whole playlist.");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var maxPl = cfg.MaxPlaylistSongs;
        if (ImGui.SliderInt("Max songs per playlist", ref maxPl, 1, 50)) { cfg.MaxPlaylistSongs = maxPl; changed = true; }
        Ui.Help("So one tell can't fill the whole queue.");
        if (!allowPl) ImGui.EndDisabled();

        Ui.Section("Playback");
        var echo = cfg.EchoToChat;
        if (ImGui.Checkbox("Show \"Now playing / Queued\" messages in chat", ref echo)) { cfg.EchoToChat = echo; changed = true; }

        var keepSongs = cfg.CacheSongs;
        if (ImGui.Checkbox("Keep recent songs so repeats start instantly", ref keepSongs)) { cfg.CacheSongs = keepSongs; changed = true; }
        Ui.Help("A song that's requested again, or replayed from History, plays from the saved copy instead of downloading again. "
                + "When the folder is full, the songs played longest ago are removed first.");
        if (keepSongs)
        {
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            var mb = cfg.CacheSizeMb;
            if (ImGui.SliderInt("Space for saved songs (MB)", ref mb, 100, 5000)) { cfg.CacheSizeMb = mb; changed = true; }
            if (ImGui.IsItemDeactivatedAfterEdit()) plugin.Player.Library.Prune(plugin.Player.Current?.File);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Clear ({plugin.Player.Library.SizeMb:F0} MB)")) plugin.Player.Library.Clear(plugin.Player.Current?.File);
            Ui.Tooltip("Deletes all saved songs (not the one playing right now).");
        }

        var del = cfg.DeleteAfterPlay;
        if (ImGui.Checkbox("Delete other audio files after playing", ref del)) { cfg.DeleteAfterPlay = del; changed = true; }
        Ui.Help("Downloads that aren't kept above (and shared-song files) are removed once they've played.");


        Ui.Section("Now playing window");
        var showMini = cfg.ShowMiniPlayer;
        if (ImGui.Checkbox("Show the \"Now playing\" window (song name and time)", ref showMini)) plugin.SetMiniPlayer(showMini);
        var lockMini = cfg.LockMiniPlayer;
        if (ImGui.Checkbox("Lock its position", ref lockMini)) { cfg.LockMiniPlayer = lockMini; changed = true; }

        Ui.Section("Download tools");
        if (plugin.YtDlp.IsInstalled)
            ImGui.TextColored(ImGuiColors.HealerGreen, "Installed");
        else
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Not installed yet (downloads automatically)");
        ImGui.TextDisabled(plugin.YtDlp.ExePath);
        ImGui.TextDisabled(cfg.LastYtDlpUpdateUtc == DateTime.MinValue
            ? "Updates itself once a day."
            : $"Updates itself once a day. Last update: {Ui.Ago(cfg.LastYtDlpUpdateUtc.ToLocalTime())}.");
        if (plugin.YtDlp.DenoPath is { } deno)
            ImGui.TextColored(ImGuiColors.HealerGreen, $"YouTube helper (deno): found ({deno})");
        else
            ImGui.TextColored(ImGuiColors.DalamudYellow, "YouTube helper (deno): not found yet (downloads automatically)");
        Ui.Help("yt-dlp needs deno to get past YouTube's download checks. Without it, downloads fail more often.");

        var path = cfg.YtDlpPath;
        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputTextWithHint("Custom yt-dlp.exe path", "Leave empty to use the automatic copy", ref path, 512)) { cfg.YtDlpPath = path; changed = true; }

        if (ImGui.Button("Update yt-dlp"))
        {
            ytDlpStatus = "Updating...";
            _ = plugin.YtDlp.UpdateAsync(default).ContinueWith(t =>
                ytDlpStatus = t.IsCompletedSuccessfully && t.Result.Exit == 0
                    ? "Up to date."
                    : "Update failed. Check /xllog for details.");
        }
        Ui.Tooltip("Run this if downloads start failing; YouTube changes often.");
        if (ytDlpStatus.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(ytDlpStatus);
        }

        // ffmpeg (music mod songs and the importer)
        ImGui.Spacing();
        var ff = plugin.Ffmpeg;
        if (ff.Downloading)
            ImGui.TextColored(ImGuiColors.ParsedBlue, ff.Status);
        else if (ff.CurrentPath == null)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "ffmpeg: not installed yet (downloads with the first music mod song or import)");
        else if (ff.UsingSoundysCopy)
            ImGui.TextColored(ImGuiColors.HealerGreen, $"ffmpeg: using Soundy's copy ({ff.CurrentPath})");
        else
            ImGui.TextColored(ImGuiColors.HealerGreen, $"ffmpeg: installed ({ff.CurrentPath})");
        Ui.Help("ffmpeg converts songs to the music mod's format and applies loudness/EQ for imports. "
                + "Tundy's own copy comes from the official Windows builds at gyan.dev (about 120 MB) and is checked against its published checksum.");
        if (ff.Downloading) ImGui.BeginDisabled();
        if (ImGui.Button(ff.CurrentPath == null || ff.UsingSoundysCopy ? "Download Tundy's own ffmpeg" : "Download ffmpeg again"))
            _ = ff.DownloadAsync(default).ContinueWith(_ => { });
        if (ff.Downloading) ImGui.EndDisabled();
        Ui.Tooltip("So Tundy doesn't need Soundy's copy any more (for example before uninstalling Soundy).");
        if (!ff.Downloading && ff.Status.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(ff.Status);
        }

        Ui.Section("Clean up");
        if (ImGui.Button("Clear temporary files"))
            cleanResult = Maintenance.Describe(Maintenance.ClearTempFiles());
        Ui.Tooltip("Leftover conversions and import downloads in the Windows temp folder.");
        ImGui.SameLine();
        if (ImGui.Button("Clean the Tundy Song mod"))
            cleanResult = Maintenance.Describe(plugin.Sharer.CleanSongFolder());
        Ui.Tooltip("Deletes old song files in the \"Tundy Song\" mod (never the one playing, or the silent track).");
        var soundyLeft = Maintenance.SoundyQLeftoverBytes(plugin.Importer.ModFolder(cfg.QueueTargetMod));
        if (soundyLeft > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Delete SoundyQ's old songs ({soundyLeft / 1048576.0:F0} MB)") && ImGui.GetIO().KeyCtrl)
                cleanResult = Maintenance.Describe(Maintenance.ClearSoundyQLeftovers(plugin.Importer.ModFolder(cfg.QueueTargetMod)));
            Ui.Tooltip("Hold Ctrl and click. Songs SoundyQ left in the music mod's \"soundy\\soundyq\" folder; nothing else is touched.");
        }
        if (ImGui.Button("Check imported songs"))
        {
            var problems = plugin.Importer.CheckImports();
            cleanResult = problems.Count == 0 ? "Everything imported with Tundy is fine." : string.Join("\n", problems);
        }
        Ui.Tooltip("Looks for songs imported with Tundy whose files are missing (for example after a mod was renamed or moved), "
                   + "and for mods Tundy is set to use that aren't in Penumbra any more.");
        ImGui.SameLine();
        if (ImGui.Button("Remove broken imports") && ImGui.GetIO().KeyCtrl)
            cleanResult = $"Removed {plugin.Importer.RemoveBrokenImports()} broken import(s).";
        Ui.Tooltip("Hold Ctrl and click. Removes imported songs whose files are gone from their mods' option lists.");
        if (cleanResult.Length > 0)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextDisabled(cleanResult);
            ImGui.PopTextWrapPos();
        }

        Ui.Section("Chat commands");
        ImGui.TextDisabled("/tundy  ·  mini  ·  play <link>  ·  skip  ·  stop  ·  approve [all]  ·  import  ·  on  ·  off  ·  share  ·  mute  ·  vol <0-100>");

        ImGui.EndChild();

        if (changed)
            plugin.SaveConfig();
    }
}
