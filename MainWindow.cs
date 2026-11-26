using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace Tundy;

/// <summary>The full Tundy control panel.</summary>
public sealed partial class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly PlayerControls controls;
    private readonly ImportTab importTab;
    private string cleanResult = string.Empty;

    private string linkInput = string.Empty;
    private string linkError = string.Empty;
    private bool onlyIgnored;
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

    private enum Page { NowPlaying, Queue, People, Sharing, Import, Settings }

    private Page page = Page.NowPlaying;

    public void SelectSettingsTab() => page = Page.Settings;

    public void SelectImportTab() => page = Page.Import;

    public override void PreDraw() => Theme.Push(plugin.Config.ThemeChoice);

    public override void PostDraw() => Theme.Pop();

    public override void Draw()
    {
        if (GuideActive)
        {
            DrawGuide();
            return;
        }

        DrawStatusBar();
        DrawProblems();
        DrawGettingReady();
        ImGui.Spacing();
        DrawTabStrip();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginChild("page", Vector2.Zero, false);
        switch (page)
        {
            case Page.NowPlaying: DrawPlayerTab(); break;
            case Page.Queue: DrawQueueTab(); break;
            case Page.People: DrawAccessTab(); break;
            case Page.Sharing: DrawSharingTab(); break;
            case Page.Import: importTab.Draw(); break;
            case Page.Settings: DrawSettingsTab(); break;
        }
        ImGui.EndChild();
    }

    /// <summary>Something stops Tundy from working right, with one button that fixes it (or opens where to fix it).</summary>
    private sealed record Problem(string Title, string Detail, string? Button, Action? Fix);

    private List<Problem> FindProblems()
    {
        var list = new List<Problem>();
        var sharer = plugin.Sharer;
        var cfg = plugin.Config;

        if (plugin.TellTunesLoaded)
            list.Add(new Problem("TellTunes is also on",
                "Both answer the same tells, so Tundy ignores song requests until TellTunes is off.",
                "Open the plugin list", plugin.OpenPluginList));

        if (plugin.SoundyQListening)
            list.Add(new Problem("SoundyQ answers the same tells",
                "SoundyQ (in Soundy) listens for the same request command, so Tundy ignores song requests. "
                + "Untick SoundyQ in its window. Soundy's other tools can stay on.",
                "Open SoundyQ", plugin.OpenSoundyQ));
        else if (plugin.SoundyQHoldsSongSlot)
            list.Add(new Problem("SoundyQ is holding the song slot",
                "SoundyQ puts its own song on the music mod's song slot, and it wins over Tundy's, so nearby players hear "
                + "SoundyQ's song (or nothing) instead of yours. Untick SoundyQ in its window. Soundy's other tools can stay on.",
                "Open SoundyQ", plugin.OpenSoundyQ));

        if (!cfg.ShareViaLightless) return list;

        if (!sharer.PenumbraAvailable)
        {
            list.Add(new Problem("Penumbra isn't running",
                "Songs play only for you until Penumbra is on: it puts the song on your character.", null, null));
            return list;
        }

        // Sharing problems say what's wrong; the fix buttons live in one place, the setup steps at the top of the
        // Sharing tab (and the Music mod section), and the banner just opens it.
        const string open = "Open the Sharing setup";
        if (sharer.UsesMusicMod)
        {
            var setup = plugin.Music.SetupProblems();
            if (setup.Count > 0)
                list.Add(new Problem("The music mod needs setting up", setup[0], open, OpenMusicMod));

            if (plugin.Ffmpeg.CurrentPath == null)
                list.Add(new Problem("ffmpeg isn't installed",
                    plugin.Ffmpeg.Downloading ? plugin.Ffmpeg.Status : "The music mod needs it to convert songs (about 120 MB, once).",
                    plugin.Ffmpeg.Downloading ? null : open, () => page = Page.Sharing));
        }

        if (sharer.EmoteOwned() == false)
            list.Add(new Problem($"Your character hasn't learned {sharer.Emote.Command}",
                sharer.UsesMusicMod
                    ? $"The music mod's dance plays the songs, and it's on {sharer.Emote.Command}. Learn that emote, or pick a dance on one you have."
                    : $"The songs play when your character does {sharer.Emote.Command}. Learn it, or pick another emote.",
                open, sharer.UsesMusicMod ? OpenMusicMod : OpenHowItPlays));

        if (sharer.HasChecked && !sharer.LastCheckOk && !plugin.SoundyQHoldsSongSlot)
            list.Add(new Problem("The last song didn't play for others", sharer.LastCheck, open, () => page = Page.Sharing));

        return list;
    }

    /// <summary>
    /// The download tools Tundy fetches on first use, while any of them is still missing: what's there, what's
    /// downloading, and a retry when a download failed.
    /// </summary>
    private void DrawGettingReady()
    {
        var yt = plugin.YtDlp;
        var ff = plugin.Ffmpeg;
        var ytOk = yt.IsInstalled;
        var denoOk = yt.DenoPath != null;
        var ffNeeded = plugin.NeedsFfmpeg;
        var ffOk = ff.CurrentPath != null;
        if (ytOk && denoOk && (!ffNeeded || ffOk)) return;

        var busy = yt.DownloadingYtDlp || yt.DownloadingDeno || ff.Downloading;
        ImGui.Spacing();
        Ui.BeginCard();
        Ui.Label("GETTING READY");
        Tool("yt-dlp", "downloads the songs", ytOk, yt.DownloadingYtDlp, "downloading...");
        Tool("deno · about 40 MB", "gets past YouTube's download checks", denoOk, yt.DownloadingDeno, "downloading...");
        if (ffNeeded) Tool("ffmpeg · about 120 MB", "converts songs for the music mod", ffOk, ff.Downloading, ff.Status.Length > 0 ? ff.Status : "downloading...");

        if (!busy && yt.LastToolError != null)
        {
            ImGui.TextColored(Theme.Bad, $"Download failed: {yt.LastToolError}");
            if (ImGui.Button("Try again")) plugin.StartToolDownloads();
        }
        else
        {
            ImGui.TextColored(Theme.Muted, "Songs can be requested now. The first one starts once these are done.");
        }
        Ui.EndCard();

        static void Tool(string name, string what, bool ok, bool downloading, string progress)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(ok ? Theme.Good : downloading ? Theme.Info : Theme.Warn,
                (ok ? FontAwesomeIcon.CheckCircle : downloading ? FontAwesomeIcon.Download : FontAwesomeIcon.Clock).ToIconString());
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.TextUnformatted(name);
            Ui.Tooltip(what);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, ok ? "ready" : downloading ? progress : "waiting");
        }
    }

    private void DrawProblems()
    {
        var problems = FindProblems();
        if (problems.Count == 0) return;

        ImGui.Spacing();
        Ui.BeginCard();
        for (var i = 0; i < problems.Count; i++)
        {
            var p = problems[i];
            ImGui.PushID($"problem{i}");
            if (i > 0) ImGui.Spacing();
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(Theme.Bad, FontAwesomeIcon.ExclamationTriangle.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.TextColored(Theme.Bad, p.Title);
            ImGui.TextColored(Theme.Muted, p.Detail);
            if (p.Button != null && p.Fix != null && ImGui.Button(p.Button)) p.Fix();
            ImGui.EndGroup();
            ImGui.PopID();
        }
        Ui.EndCard(Theme.Bad with { W = 0.12f }, Theme.Bad with { W = 0.45f });
    }

    private void DrawTabStrip()
    {
        var waiting = plugin.Pending.Count;
        var queued = plugin.Player.PendingCount;
        (Page Page, FontAwesomeIcon Icon, string Label)[] tabs =
        {
            (Page.NowPlaying, FontAwesomeIcon.Music, waiting > 0 ? $"Now playing ({waiting})" : "Now playing"),
            (Page.Queue, FontAwesomeIcon.ListOl, queued > 0 ? $"Queue ({queued})" : "Queue"),
            (Page.People, FontAwesomeIcon.Users, "People"),
            (Page.Sharing, FontAwesomeIcon.ShareAlt, "Sharing"),
            (Page.Import, FontAwesomeIcon.FileImport, "Import"),
            (Page.Settings, FontAwesomeIcon.Cog, "Settings"),
        };

        var minimal = Theme.IsMinimal;
        var scale = ImGuiHelpers.GlobalScale;
        for (var i = 0; i < tabs.Length; i++)
        {
            var (p, icon, label) = tabs[i];
            var selected = page == p;
            ImGui.PushID($"tab{i}");
            if (minimal)
            {
                // Minimal: tabs are plain text, the open one bright with a line under it.
                var padding = new Vector2(2 * scale, ImGui.GetStyle().FramePadding.Y);
                if (i > 0)
                {
                    ImGui.SameLine(0, 14 * scale);
                    if (ImGui.GetCursorPosX() + ImGui.CalcTextSize(label).X + padding.X * 2 > ImGui.GetWindowContentRegionMax().X)
                        ImGui.NewLine();
                }
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, padding);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.05f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.08f));
                ImGui.PushStyleColor(ImGuiCol.Text, selected ? Theme.TextLight : Theme.TextMuted);
                if (ImGui.Button(label)) page = p;
                ImGui.PopStyleColor(4);
                ImGui.PopStyleVar(2);
                if (selected)
                {
                    var min = ImGui.GetItemRectMin();
                    var max = ImGui.GetItemRectMax();
                    ImGui.GetWindowDrawList().AddLine(new Vector2(min.X, max.Y), max, ImGui.GetColorU32(Theme.TextLight), 1.5f * scale);
                }
            }
            else
            {
                if (i > 0) Ui.SameLineIfFits(ImGuiComponents.GetIconButtonWithTextWidth(icon, label));
                ImGui.PushStyleColor(ImGuiCol.Button, selected ? Theme.TabOn : Theme.TabOff);
                ImGui.PushStyleColor(ImGuiCol.Text, selected || !Theme.Active ? Theme.TextLight : Theme.TextMuted);
                if (ImGuiComponents.IconButtonWithText(icon, label)) page = p;
                ImGui.PopStyleColor(2);
            }
            ImGui.PopID();
            if (p == Page.NowPlaying && waiting > 0) Ui.Tooltip($"{waiting} request(s) waiting for your approval");
        }
    }

    // ------------------------------------------------------------------ top bar

    private void DrawStatusBar()
    {
        var cfg = plugin.Config;

        ImGui.AlignTextToFramePadding();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(Theme.Accent, FontAwesomeIcon.Music.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextColored(Theme.Heading, "Tundy");
        ImGui.SameLine();

        var requests = cfg.Enabled ? "Accepting requests" : "Requests paused";
        if (Ui.Pill(requests, cfg.Enabled ? Theme.Good : Theme.Muted, cfg.Enabled
                ? "People can send you songs with /tell. Click to pause."
                : "Tell requests are ignored. Click to start accepting them again."))
        {
            cfg.Enabled = !cfg.Enabled;
            plugin.SaveConfig();
        }

        var who = cfg.QueueAccessMode switch
        {
            AccessMode.EveryonePreQueue => "Anyone, with approval",
            AccessMode.ListedOnly => "Listed players only",
            _ => "Anyone can request",
        };
        Ui.SameLineIfFits(ImGui.CalcTextSize(who).X + 20 * ImGuiHelpers.GlobalScale);
        if (Ui.Pill(who, Theme.Info, "Who can send you songs. Click to change it in People."))
            page = Page.People;

        string sharing;
        Vector4 shareColor;
        if (plugin.Sharer.CanShare && plugin.Sharer.UsesMusicMod)
        {
            var ready = plugin.Music.SetupProblems().Count == 0;
            sharing = ready ? "Via the music mod" : "Music mod needs setup";
            shareColor = ready ? Theme.Accent : Theme.Warn;
        }
        else if (plugin.Sharer.CanShare)
        {
            sharing = "Shared via Lightless";
            shareColor = Theme.Accent;
        }
        else
        {
            sharing = "Just for you";
            shareColor = Theme.Muted;
        }
        Ui.SameLineIfFits(ImGui.CalcTextSize(sharing).X + 20 * ImGuiHelpers.GlobalScale);
        if (Ui.Pill(sharing, shareColor, "How other players hear the songs. Click to open Sharing."))
            page = Page.Sharing;

        var waiting = plugin.Pending.Count;
        if (waiting > 0)
        {
            var label = $"{waiting} to approve";
            Ui.SameLineIfFits(ImGui.CalcTextSize(label).X + 20 * ImGuiHelpers.GlobalScale);
            if (Ui.Pill(label, Theme.Warn, "Requests waiting for your approval. Click to see them."))
                page = Page.NowPlaying;
        }

        // Right-aligned "Now playing" window toggle
        var mini = cfg.ShowMiniPlayer;
        var w = ImGui.GetFrameHeight();
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - w);
        if (Ui.IconButton("mini", mini ? FontAwesomeIcon.WindowClose : FontAwesomeIcon.WindowRestore,
                mini ? "Hide the small \"Now playing\" window (/tundy mini)" : "Show the small \"Now playing\" window (/tundy mini)"))
            plugin.SetMiniPlayer(!mini);
    }

    // ------------------------------------------------------------------ Now playing

    private void DrawPlayerTab()
    {
        var player = plugin.Player;

        // Now playing card: cover on the left, the song and its controls on the right
        Ui.BeginCard();
        var scale = ImGuiHelpers.GlobalScale;
        var coverSize = 92 * scale;
        var showCover = plugin.Config.ShowCoverArt && ImGui.GetContentRegionAvail().X > 360 * scale;
        var coverId = player.Current?.Request.CoverId ?? (player.WaitingForSync ? null : player.Downloading?.CoverId);
        if (showCover)
        {
            Ui.Cover(plugin, coverId, coverSize);
            ImGui.SameLine(0, 12 * scale);
        }
        ImGui.BeginGroup();
        Ui.Label("NOW PLAYING");
        if (player.Current is { } t)
        {
            using (plugin.TitleFont.Push()) ImGui.TextUnformatted(t.Title);
            ImGui.TextDisabled("from");
            ImGui.SameLine();
            Ui.SenderCell(plugin, t.Request.Sender, "np");
            if (player.CurrentIsShared)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Accent, plugin.Sharer.UsesMusicMod
                    ? $"· via the music mod ({plugin.Sharer.Emote.Command})"
                    : $"· shared via {plugin.Sharer.Emote.Command}");
            }
        }
        else if (player.WaitingForSync)
        {
            using (plugin.TitleFont.Push()) ImGui.TextUnformatted("Syncing through Lightless...");
            ImGui.TextDisabled($"Starts in a few seconds so your pairs can download it. ({plugin.Sharer.Status})");
        }
        else
        {
            using (plugin.TitleFont.Push()) ImGui.TextUnformatted(player.Downloading != null ? "Downloading..." : "Nothing playing");
            ImGui.TextDisabled(player.Downloading != null ? $"{player.Downloading.Display} · from {player.Downloading.Sender}" : "Waiting for requests.");
        }

        ImGui.Spacing();
        var width = ImGui.GetContentRegionAvail().X - 10 * scale;
        controls.DrawSeekBar(width);
        controls.DrawButtons();
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(12, 0));
        ImGui.SameLine();
        controls.DrawVolume(Math.Min(220 * scale, ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() * 2 - 10 * scale));
        ImGui.EndGroup();
        Ui.EndCard();

        // A song that couldn't play and can be retried (only when there is one)
        DrawFailedNotice();

        // Approvals card (only when someone waits)
        DrawPendingSection();

        // Up next card
        Ui.BeginCard();
        var queue = player.FullQueue();
        var (_, ready) = player.UpNext;
        Ui.Label("UP NEXT");
        if (queue.Length > 0)
        {
            var summary = $"{queue.Length} song{(queue.Length == 1 ? "" : "s")}{TotalLength(queue.Select(q => q.Request))}";
            Ui.TextRight(summary, Theme.Muted, 14 * scale);
        }
        if (queue.Length == 0)
        {
            ImGui.TextDisabled($"Nothing queued. Paste a link below, or have someone send \"/tell <you> {plugin.RequestCommands[0]} <link>\".");
        }
        else
        {
            for (var i = 0; i < Math.Min(queue.Length, 3); i++)
            {
                var (r, isNext) = queue[i];
                ImGui.PushID($"next{r.Id}");
                ImGui.TextColored(isNext ? Theme.Accent : Theme.Muted, $"{i + 1}.");
                ImGui.SameLine();
                var right = string.Join(" · ", new[] { Ui.Length(r.KnownSeconds), isNext ? (ready ? "ready" : "getting it ready...") : "" }
                    .Where(s => s.Length > 0));
                var info = $" · {r.Sender}";
                var rightW = right.Length > 0 ? ImGui.CalcTextSize(right).X + 28 * scale : 0;
                var nameW = ImGui.GetContentRegionAvail().X - rightW - ImGui.CalcTextSize(info).X - 14 * scale;
                ImGui.PushTextWrapPos(-1f); // one line per song: the name is shortened to fit instead
                ImGui.TextUnformatted(Ui.Ellipsize(r.Display, Math.Max(nameW, 60 * scale)));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(r.Display);
                ImGui.SameLine(0, 0);
                ImGui.TextDisabled(info);
                ImGui.PopTextWrapPos();
                if (right.Length > 0) Ui.TextRight(right, Theme.Muted, 14 * scale);
                ImGui.PopID();
            }
            if (queue.Length > 3)
            {
                if (ImGui.SmallButton($"See all {queue.Length} in the queue")) page = Page.Queue;
            }
        }
        Ui.EndCard();

        // Add a song card
        Ui.BeginCard();
        DrawAddLink("player");
        Ui.EndCard();
    }

    /// <summary>" · about 38 min of music", or " · 30+ min of music" while some lengths aren't known yet.</summary>
    private static string TotalLength(IEnumerable<SongRequest> songs)
    {
        var list = songs.ToList();
        var known = list.Where(r => r.KnownSeconds is > 0).Sum(r => r.KnownSeconds!.Value);
        if (known == 0) return "";
        if (list.All(r => r.KnownSeconds is > 0)) return $" · {Ui.About(known)} of music";
        return $" · {Math.Max(1, known / 60)}+ min of music";
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
            ImGui.TextColored(Theme.Bad, linkError);
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

        // Step-by-step setup
        Ui.BeginCard();
        Ui.Label("SHARING SETUP");
        ImGui.Spacing();

        changed |= DrawSetupSteps(inGuide: false);
        Ui.EndCard();

        var share = cfg.ShareViaLightless;
        if (!share) ImGui.BeginDisabled();

        if (openHowItPlays) ImGui.SetNextItemOpen(true);
        openHowItPlays = false;
        if (Ui.Group("How it plays"))
        {
        ImGui.TextUnformatted("How the song plays in game:");
        var target = cfg.ShareTarget;
        if (ImGui.RadioButton("Through a music mod's dance (recommended, like SoundyQ)", ref target, 0)) { cfg.ShareTarget = 0; changed = true; }
        Ui.Help("A music mod (the Mega Music Mod, or another dance mod) plays a sound file during its dance. Tundy puts the "
                + "song on that file while it plays. The music mod itself isn't changed. Pick the mod in the Music mod section below.");
        if (ImGui.RadioButton("On one emote's own sound", ref target, 1)) { cfg.ShareTarget = 1; changed = true; }
        Ui.Help("Works without any music mod: the song replaces an emote's normal sound while it plays.");

        var current = sharer.Emote;
        if (sharer.UsesMusicMod)
        {
            // Only a summary here: the music mod, its song file and emote are chosen in one place (Music mod below).
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Theme.Muted, $"Music mod: \"{cfg.QueueTargetMod}\" · song file {string.Join(", ", plugin.Music.Routes)} · "
                                           + $"emote {current.Command}. Change it under Music mod below.");
            ImGui.PopTextWrapPos();
        }
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
        }

        if (sharer.UsesMusicMod)
        {
            if (openMusicMod) ImGui.SetNextItemOpen(true);
            openMusicMod = false;
            if (Ui.Group("Music mod"))
                changed |= DrawMusicModGroup();
        }

        if (Ui.Group("Starting songs"))
        {
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

        ImGui.Spacing();
        if (ImGui.Button($"Do {sharer.Emote.Command} now"))
            _ = sharer.PerformEmoteAsync();
        Ui.Tooltip("Plays the shared song again for people nearby. Also useful the very first time, so Lightless learns the sound file.");
        ImGui.SameLine();
        ImGui.TextDisabled($"Status: {sharer.Status}");
        }

        if (!share) ImGui.EndDisabled();

        if (Ui.Group("Good to know"))
        {
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
        }

        if (changed) plugin.SaveConfig();
    }

    /// <summary>
    /// The sharing setup checks: plugins, the sharing switch, the music mod or emote, and Test now. Used by the
    /// Sharing tab and the welcome guide (which has its own "who hears it" choice instead of the switch, and no
    /// links to the Sharing tab's sections). Saves any change itself.
    /// </summary>
    private bool DrawSetupSteps(bool inGuide)
    {
        var cfg = plugin.Config;
        var sharer = plugin.Sharer;
        var changed = false;

        // 1. Plugins
        var plugins = sharer.PenumbraAvailable && sharer.LightlessLoaded;
        Step(plugins ? StepState.Done : StepState.Failed, "Penumbra and Lightless Sync",
            plugins ? "Both are running."
            : !sharer.PenumbraAvailable && !sharer.LightlessLoaded ? "Install both: Penumbra puts the song on your character, Lightless sends it to your pairs."
            : !sharer.PenumbraAvailable ? "Penumbra isn't running. It puts the song on your character."
            : "Lightless Sync isn't running. It sends the song to your pairs.");

        // 2. The switch
        var share = cfg.ShareViaLightless;
        if (!inGuide)
        {
            Step(share ? StepState.Done : StepState.Todo, "Sharing is on", share ? "Songs go out to nearby Lightless pairs." : "Off: songs play only for you.");
            StepBody(() =>
            {
                if (ImGui.Checkbox("Share songs with nearby Lightless pairs", ref share)) { cfg.ShareViaLightless = share; changed = true; }
                Ui.Help("Off = songs play only for you. Also: /tundy share");
            });
        }

        // 3. What plays it
        var owned = sharer.EmoteOwned();
        if (sharer.UsesMusicMod)
        {
            var problems = plugin.Music.SetupProblems();
            var ffmpeg = plugin.Ffmpeg.CurrentPath != null;
            var detail = problems.Count > 0 ? problems[0]
                : !ffmpeg ? (plugin.Ffmpeg.Downloading ? plugin.Ffmpeg.Status : "ffmpeg isn't installed yet: it converts songs for the music mod (about 120 MB, once).")
                : owned == false ? $"Your character hasn't learned {sharer.Emote.Command}, the emote the music mod's dance is on."
                : $"\"{cfg.QueueTargetMod}\" plays the songs through its dance on {sharer.Emote.Command}.";
            var ok = problems.Count == 0 && ffmpeg && owned != false;
            Step(ok ? StepState.Done : StepState.Failed, plugin.Music.IsRecommended ? "Mega Music Mod set up" : "Music mod set up", detail);
            if (!ok)
            {
                StepBody(() =>
                {
                    var music = plugin.Music;
                    var other = !music.State().Installed && music.IsRecommended ? music.InstalledMega() : null;
                    if (other != null && ImGui.Button($"Use \"{other}\""))
                    {
                        music.UseMod(other);
                        changed = true;
                    }
                    else if (problems.Count > 0 && music.IsRecommended && music.State().Installed && ImGui.Button("Set it up for me"))
                        megaSetupResult = music.SetUp();
                    else if (problems.Count == 0 && !ffmpeg && !plugin.Ffmpeg.Downloading && ImGui.Button("Install ffmpeg"))
                        _ = plugin.Ffmpeg.DownloadAsync(default).ContinueWith(_ => { });
                    else if (!inGuide && (problems.Count > 0 || owned == false) && ImGui.Button("Show the music mod settings"))
                        openMusicMod = true;
                    if (megaSetupResult.Length > 0) ImGui.TextDisabled(megaSetupResult);
                });
            }
        }
        else
        {
            var ok = owned != false;
            Step(ok ? StepState.Done : StepState.Failed, $"Emote: {sharer.Emote.Name} ({sharer.Emote.Command})",
                ok ? "Its sound is replaced by the song while one is shared."
                   : $"Your character hasn't learned {sharer.Emote.Command}. Pick another emote {(inGuide ? "in the Sharing tab later" : "under \"How it plays\"")}.");
            if (!ok && !inGuide) StepBody(() => { if (ImGui.Button("Pick another emote")) openHowItPlays = true; });
        }

        // 4. Test
        var canTest = share && sharer.PenumbraAvailable && plugin.Player.Current == null && !plugin.Player.WaitingForSync && !sharer.Testing;
        Step(sharer.Testing ? StepState.Todo : !sharer.HasChecked ? StepState.Todo : sharer.LastCheckOk ? StepState.Done : StepState.Failed,
            "Test it",
            sharer.Testing ? "Testing..." : sharer.HasChecked ? sharer.LastCheck
            : "Does the emote once and checks that the game loads Tundy's sound for your character, which is what Lightless sends. "
              + "Each song is also checked when it starts.");
        StepBody(() =>
        {
            if (!canTest) ImGui.BeginDisabled();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, "Test now")) _ = sharer.TestAsync();
            if (!canTest) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(!share ? "Turn sharing on first."
                    : plugin.Player.Current != null || plugin.Player.WaitingForSync ? "Works while no song is playing."
                    : "Your character does the emote (quietly, no chat message). The first time, this also teaches Lightless the sound file, so the first song starts sooner.");
        });

        if (changed) plugin.SaveConfig();
        return false; // already saved
    }

    private string otherModFilter = string.Empty;
    private string otherModResult = string.Empty;
    private string modSearch = string.Empty;
    private bool openMusicMod;

    /// <summary>Opens the Sharing tab with the Music mod section unfolded.</summary>
    private void OpenMusicMod()
    {
        page = Page.Sharing;
        openMusicMod = true;
    }

    /// <summary>
    /// The one place to choose the music mod, and its song file and emote: for the Mega Music Mod its own setup; for
    /// any other mod a scan of its dances (follow the one on in Penumbra, or pick one), or by hand. Returns true if a
    /// setting changed.
    /// </summary>
    private bool DrawMusicModGroup()
    {
        var cfg = plugin.Config;
        var music = plugin.Music;
        var changed = false;
        var scale = ImGuiHelpers.GlobalScale;

        // Which mod
        ImGui.SetNextItemWidth(320 * scale);
        var modItems = music.ModList().Where(m => !m.Key.Equals("Tundy Song", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Value, StringComparer.OrdinalIgnoreCase)
            .Select(m => (m.Key, m.Value == m.Key ? m.Value : $"{m.Value} ({m.Key})"));
        if (Ui.SearchCombo("Mod##musicMod", cfg.QueueTargetMod, cfg.QueueTargetMod, modItems, ref modSearch, out var pickedMod, "Search your mods")
            && pickedMod != cfg.QueueTargetMod)
        {
            music.UseMod(pickedMod!);
            scannedRoutes = null;
            otherModResult = string.Empty;
            changed = true;
        }
        Ui.Help("The Penumbra mod whose dance plays the songs: the Mega Music Mod (recommended), or any other dance or music mod.");
        if (!music.IsRecommended && music.InstalledMega() is { } mega)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Use Mega Music Mod"))
            {
                music.UseMod(mega);
                scannedRoutes = null;
                changed = true;
            }
        }

        // The Mega Music Mod: its own setup (the DJ dance and deck), its slot and emote by hand if wanted.
        if (music.IsRecommended)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(music.SetupProblems().Count == 0 ? Theme.Good : Theme.Warn, music.SetupProblems().Count == 0
                ? $"Songs play through its DJ dance on {MegaMusicMod.SafeEmote(cfg.QueueEmoteCommand)}, from {string.Join(", ", music.Routes)}."
                : "It needs setting up: see the steps at the top of this tab, or use the button below.");
            ImGui.PopTextWrapPos();
            if (music.State().Installed)
            {
                if (ImGui.Button("Set up Mega Music Mod for me")) megaSetupResult = music.SetUp();
                Ui.Tooltip("Turns the mod on in your Penumbra collection, picks its \"Dj (Cheer Jump)\" dance and the DJ deck for your race.");
                if (megaSetupResult.Length > 0)
                {
                    ImGui.PushTextWrapPos();
                    ImGui.TextDisabled(megaSetupResult);
                    ImGui.PopTextWrapPos();
                }
            }
            if (ImGui.TreeNode("Song file and emote by hand##megaHand"))
            {
                changed |= DrawManualSlot();
                ImGui.TreePop();
            }
            return changed;
        }

        // Another mod: read its dances.
        var scan = music.Scan;
        if (scan == null && !music.Scanning) music.StartScan();
        if (music.Scanning)
        {
            ImGui.TextColored(Theme.Info, "Scanning the mod's dances... (big mods can take half a minute)");
            return changed;
        }
        ImGui.TextColored(Theme.Muted, music.ScanStatus);
        ImGui.SameLine();
        if (ImGui.SmallButton("Scan again")) music.StartScan();
        Ui.Tooltip("Read the mod again, for example after it was updated.");

        var hasDances = scan is { Songs.Count: > 0 };
        if (!hasDances)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Theme.Warn, "Tundy found no dance in this mod that plays a song file of its own. Pick its song file "
                                          + "and emote by hand below.");
            ImGui.PopTextWrapPos();
            return changed | DrawManualSlot();
        }

        // Follow, pick, or by hand
        ImGui.Spacing();
        var mode = cfg.OtherModByHand ? 2 : cfg.OtherModFollow ? 0 : 1;
        var modeChanged = ImGui.RadioButton("Use the dance that's turned on in Penumbra", ref mode, 0);
        modeChanged |= ImGui.RadioButton("Always use the dance I pick below", ref mode, 1);
        modeChanged |= ImGui.RadioButton("Song file and emote by hand", ref mode, 2);
        if (modeChanged)
        {
            cfg.OtherModFollow = mode == 0;
            cfg.OtherModByHand = mode == 2;
            music.Recheck();
            changed = true;
        }
        Ui.Help("Turned on in Penumbra: when you switch dances in Penumbra, Tundy switches with it (checked every few seconds). "
                + "Picked: always the same dance. By hand: you choose the song file(s) and the emote, and Tundy doesn't change them.");

        if (cfg.OtherModByHand)
            return changed | DrawManualSlot();

        ImGui.PushTextWrapPos();
        if (music.CurrentDance is { } now)
            ImGui.TextColored(Theme.Good, $"Now: \"{now.Option}\". Songs go on {now.Route}"
                                          + (now.Emote is { } ne ? $", played by {ne.Command} ({ne.Name})." : "; its emote isn't known, so pick \"by hand\" to set it."));
        else
            ImGui.TextColored(Theme.Warn, cfg.OtherModFollow
                ? "None of the dances below is turned on in Penumbra. Click \"Set up\" on the one you want."
                : "Pick a dance below with \"Use\", or \"Set up\" to also turn it on in Penumbra.");
        if (otherModResult.Length > 0) ImGui.TextDisabled(otherModResult);
        ImGui.PopTextWrapPos();

        // Filter
        ImGui.SetNextItemWidth(200 * scale);
        ImGui.InputTextWithHint("##otherFilter", "Search dances or emotes", ref otherModFilter, 64);
        ImGui.SameLine();
        var effects = cfg.OtherModShowEffects;
        var effectCount = scan!.Songs.Count(d => !d.IsMusic);
        if (ImGui.Checkbox($"Show sound effects too ({effectCount})", ref effects)) { cfg.OtherModShowEffects = effects; changed = true; }
        Ui.Help("Some dances play a long sound effect (voices, noises) instead of music. Songs can go there too: Tundy builds them "
                + "in that format (mono). They're hidden unless you need them.");

        var rows = scan.Songs
            .Where(d => cfg.OtherModShowEffects || d.IsMusic)
            .Where(d => otherModFilter.Length == 0
                        || d.Label.Contains(otherModFilter, StringComparison.OrdinalIgnoreCase)
                        || (d.Emote?.Command.Contains(otherModFilter, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (d.Emote?.Name.Contains(otherModFilter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(d => d.IsMusic).ThenBy(d => d.Group).ThenBy(d => d.Option)
            .ToList();

        var rowH = ImGui.GetFrameHeight() + ImGui.GetStyle().CellPadding.Y * 2;
        var height = Math.Clamp(rows.Count, 3, 10) * rowH + ImGui.GetFrameHeightWithSpacing();
        if (ImGui.BeginTable("otherDances", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable,
                new Vector2(0, height)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Dance", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Emote", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Song file", ImGuiTableColumnFlags.WidthStretch, 2);
            ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 80 * scale);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 110 * scale);
            ImGui.TableHeadersRow();

            foreach (var d in rows)
            {
                ImGui.PushID(d.Key);
                ImGui.TableNextRow();
                var isNow = music.CurrentDance?.Key == d.Key;
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                if (isNow) ImGui.TextColored(Theme.Good, d.Option);
                else ImGui.TextUnformatted(d.Option);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{d.Option}\nin \"{d.Group}\"");
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                if (d.Emote is { } e) { ImGui.TextUnformatted(e.Command); Ui.Tooltip(e.Name); }
                else { ImGui.TextDisabled("unknown"); Ui.Tooltip("Its animation isn't one of the game's emotes; set the emote with \"by hand\"."); }
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(d.Route);
                Ui.Tooltip($"{d.Route}\n{d.FileBytes / 1024} KB in the mod");
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                if (d.IsMusic) ImGui.TextUnformatted("music");
                else ImGui.TextColored(Theme.Warn, "effect");
                Ui.Tooltip(d.IsMusic ? "Streamed music, like the Mega Music Mod's songs." : "A sound effect: songs are built as mono sound effects to match.");
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("Use"))
                {
                    cfg.OtherModDance = d.Key;
                    cfg.OtherModFollow = false;
                    cfg.OtherModByHand = false;
                    music.Recheck();
                    otherModResult = $"Using \"{d.Option}\".";
                    changed = true;
                }
                Ui.Tooltip("Always use this dance (it still has to be on in Penumbra to play: \"Set up\" does that).");
                ImGui.SameLine();
                if (ImGui.SmallButton("Set up"))
                {
                    cfg.OtherModByHand = false;
                    otherModResult = music.SetUpDance(d);
                    changed = true;
                }
                Ui.Tooltip("Turn the mod and this dance on in your Penumbra collection, and use it.");
                ImGui.PopID();
            }
            if (rows.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled(otherModFilter.Length > 0 ? "No dance matches the search." : "Only sound effects found: tick \"Show sound effects too\".");
            }
            ImGui.EndTable();
        }
        return changed;
    }

    /// <summary>
    /// The song file(s) and emote set by hand: the mod's sound files (most used first) to tick, and the emote. Used
    /// for the Mega Music Mod's advanced settings, the "by hand" choice, and mods without a dance Tundy can find.
    /// Returns true if a setting changed.
    /// </summary>
    private bool DrawManualSlot()
    {
        var cfg = plugin.Config;
        var music = plugin.Music;
        var changed = false;

        ImGui.TextUnformatted($"Song file(s): {string.Join(", ", music.Routes)}");
        Ui.Help("The game sound file the dance plays. For the Mega Music Mod it's sound/lolo.scd.");
        ImGui.SameLine();
        if (ImGui.SmallButton("List the mod's sound files")) scannedRoutes = music.ScanRoutes();
        Ui.Tooltip("Lists the sound files this mod uses, most used first. The song file is usually the top one.");
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
                    music.Recheck();
                    changed = true;
                }
            }
            ImGui.EndChild();
        }
        if (music.IsRecommended && !cfg.QueueScdRoutes.Contains(MegaMusicMod.RecommendedRoute, StringComparer.OrdinalIgnoreCase))
        {
            if (ImGui.SmallButton($"Use {MegaMusicMod.RecommendedRoute}"))
            {
                cfg.QueueScdRoutes = new List<string> { MegaMusicMod.RecommendedRoute };
                music.Recheck();
                changed = true;
            }
        }

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
            ImGui.TextColored(Theme.Warn, $"That isn't a plain emote command; {MegaMusicMod.RecommendedEmote} is used instead.");
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

    private bool openHowItPlays;

    private void OpenHowItPlays()
    {
        page = Page.Sharing;
        openHowItPlays = true;
    }

    private enum StepState { Done, Todo, Failed }

    /// <summary>One setup step: a tick, circle or cross, its name, and a line under it saying where it stands.</summary>
    private static void Step(StepState state, string title, string detail)
    {
        var (color, icon) = state switch
        {
            StepState.Done => (Theme.Good, FontAwesomeIcon.CheckCircle),
            StepState.Failed => (Theme.Bad, FontAwesomeIcon.TimesCircle),
            _ => (Theme.Warn, FontAwesomeIcon.Circle),
        };
        ImGui.Spacing();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(color, icon.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextUnformatted(title);
        ImGui.TextColored(Theme.Muted, detail);
        ImGui.EndGroup();
    }

    /// <summary>Controls that belong to the step above, lined up under its text.</summary>
    private static void StepBody(Action draw)
    {
        var indent = ImGui.GetFrameHeight();
        ImGui.Indent(indent);
        draw();
        ImGui.Unindent(indent);
    }

    // ------------------------------------------------------------------ Queue

    private void DrawQueueTab()
    {
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
        ImGui.TextDisabled($"{player.PendingCount} waiting{TotalLength(queue.Select(q => q.Request))} · limit {plugin.Config.MaxQueueLength} for tell requests");

        var rowH = ImGui.GetFrameHeight();
        var actionsW = (rowH + ImGui.GetStyle().ItemSpacing.X) * 4;
        var rows = Math.Clamp(queue.Length + (downloading != null ? 1 : 0), 3, 12) + 1;
        var tableH = rows * (rowH + ImGui.GetStyle().CellPadding.Y * 2) + 4;

        if (ImGui.BeginTable("queue", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, tableH)))
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
                if (isNext) ImGui.TextColored(Theme.Info, (i + 1).ToString());
                else ImGui.TextDisabled((i + 1).ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.Display);
                var hover = ImGui.IsItemHovered();
                var extra = string.Join(" · ", new[] { Ui.Length(r.KnownSeconds), isNext ? (ready ? "ready" : "getting it ready...") : "" }
                    .Where(s => s.Length > 0));
                if (extra.Length > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(extra);
                    hover |= ImGui.IsItemHovered();
                }
                if (hover) ImGui.SetTooltip($"{r.Url}\nRequested {Ui.Ago(r.RequestedAt)}"
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

        DrawFailedSection();

        ImGui.Spacing();
        DrawAddLink("queue");

        if (Ui.Group($"History ({player.HistorySnapshot().Length} played)###history"))
            DrawHistoryTab();
        if (Ui.Group("Requests log###requestslog"))
            DrawRequestsTab();
    }

    /// <summary>Songs and playlists that couldn't be played (only shown when there are any), with Retry.</summary>
    private void DrawFailedSection()
    {
        var list = plugin.FailedSnapshot();
        if (list.Length == 0) return;

        ImGui.Spacing();
        Ui.BeginCard();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Warn, $"COULDN'T PLAY ({list.Length})");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear")) plugin.ClearFailed();
        Ui.Tooltip("Forget all of these. Nothing is sent to anyone.");

        foreach (var f in list)
        {
            ImGui.PushID(f.Id.ToString());
            ImGui.Separator();
            ImGui.TextUnformatted(f.Display);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{f.Url}\nClick to copy the link");
            if (ImGui.IsItemClicked()) ImGui.SetClipboardText(f.Url);
            ImGui.TextColored(Theme.Muted, $"from {f.Sender} · {Ui.Ago(f.At)} · {f.Reason}");
            if (f.CanRetry)
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Redo, "Retry")) plugin.Retry(f.Id);
                Ui.Tooltip(f.Playlist != null ? "Read the playlist again." : "Try again: it goes in as the next song, still for the same person.");
                ImGui.SameLine();
            }
            else
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Muted, "Can't be fixed by retrying.");
                ImGui.SameLine();
            }
            if (Ui.IconButton("dismiss", FontAwesomeIcon.Times, "Remove from this list")) plugin.DismissFailed(f.Id);
            ImGui.PopID();
        }
        Ui.EndCard();
    }

    /// <summary>On Now playing: the newest song that failed and can be retried, with a Retry button.</summary>
    private void DrawFailedNotice()
    {
        if (plugin.LatestRetryable() is not { } f) return;
        var count = plugin.FailedSnapshot().Length;
        Ui.BeginCard();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(Theme.Warn, FontAwesomeIcon.ExclamationTriangle.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextUnformatted(count == 1 ? $"{f.Sender}'s song couldn't play: {f.Reason}." : $"{count} songs couldn't play. Newest: {f.Sender}'s, {f.Reason}.");
        ImGui.PushID("failedNotice");
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Redo, "Retry")) plugin.Retry(f.Id);
        Ui.Tooltip($"Try \"{f.Display}\" again as the next song.");
        if (count > 1)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("See all")) page = Page.Queue;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Dismiss")) plugin.DismissFailed(f.Id);
        ImGui.PopID();
        Ui.EndCard();
    }

    private void SpecialRow(string tag, SongRequest r, string state, bool allowRemove)
    {
        ImGui.PushID("special" + r.Id);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(Theme.Info, tag);
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

        Ui.BeginCard();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Warn, $"WAITING FOR YOUR APPROVAL ({pending.Length})");
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
                if (p.Seconds is > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"· {Ui.Length(p.Seconds)}");
                }
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
        Ui.EndCard();
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
                new Vector2(0, 240 * ImGuiHelpers.GlobalScale)))
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
                new Vector2(0, 240 * ImGuiHelpers.GlobalScale)))
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
            if (e.FailedId is { } fid && plugin.FailedById(fid) is { CanRetry: true })
            {
                if (Ui.IconButton("retry", FontAwesomeIcon.Redo, "Retry: it goes in as the next song, still for the same person."))
                    plugin.Retry(fid);
                ImGui.SameLine();
            }
            ImGui.TextColored(e.Accepted ? Theme.Good : Theme.Bad, e.Outcome);
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
            if (!a.Contains(' ') && !Plugin.IsReservedCommand(a)
                && !cfg.QueueAliases.Contains(a, StringComparer.OrdinalIgnoreCase))
            {
                cfg.QueueAliases.Add(a);
                changed = true;
            }
            aliasInput = string.Empty;
        }
        Ui.Help("One word, no spaces, e.g. !play. !skip and !skipall are always the skip commands.");

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
                ImGui.TextColored(Theme.Warn, $"{cmdText} (also a request command, so it doesn't shuffle)");
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
            if (!s.Contains(' ') && !Plugin.IsReservedCommand(s)
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

        if (changed) plugin.SaveConfig();
    }

    // ------------------------------------------------------------------ Settings

    private void DrawSettingsTab()
    {
        var cfg = plugin.Config;
        var changed = false;

        if (Ui.Group("Look"))
        {
            string[] looks = { "Pastel purple", "Minimal", "Dalamud's own look" };
            var look = Math.Clamp((int)cfg.ThemeChoice, 0, looks.Length - 1);
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo("Theme", ref look, looks, looks.Length)) { cfg.ThemeChoice = (ThemeStyle)look; changed = true; }
            Ui.Help("Pastel purple: dark plum with lavender, mint and pink. Minimal: near-black and greys, with colour only on small "
                    + "status dots. Dalamud's own look: Dalamud's normal colours. Only Tundy's windows change.");
            var covers = cfg.ShowCoverArt;
            if (ImGui.Checkbox("Show song covers", ref covers)) { cfg.ShowCoverArt = covers; changed = true; }
            Ui.Help("The song's YouTube thumbnail, loaded from YouTube's image server (i.ytimg.com) for the song that's playing.");
            if (ImGui.Button("Open the welcome guide again")) OpenGuide();
            Ui.Tooltip("Four short steps: who can send songs, who hears them, a few extras, and a first song.");
        }

        if (Ui.Group("Requests", true))
        {
        var enabled = cfg.Enabled;
        if (ImGui.Checkbox($"Accept song request tells ({string.Join(", ", plugin.RequestCommands)})", ref enabled)) { cfg.Enabled = enabled; changed = true; }
        Ui.Help("Change the commands and who can request in the People tab.");

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

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var plCd = cfg.PlaylistCooldownSeconds / 60;
        if (ImGui.SliderInt("Playlist cooldown per person (minutes)", ref plCd, 0, 120)) { cfg.PlaylistCooldownSeconds = plCd * 60; changed = true; }
        Ui.Help("How long one person has to wait between playlist requests (also with shuffle commands), on top of the cooldown above. "
                + "With replies on, they're told how long is left. Playlists you add yourself or approve don't count.");

        }

        if (Ui.Group("Song as your title (Honorific)"))
        {
            var hon = plugin.Honorific;
            var available = hon.Available;
            ImGui.PushTextWrapPos();
            ImGui.TextColored(available ? Theme.Good : Theme.Warn, available ? "Honorific is installed." : "Honorific isn't installed (or isn't on).");
            ImGui.PopTextWrapPos();

            var on = cfg.HonorificTitle;
            if (ImGui.Checkbox("Show the song that's playing as my title", ref on)) { cfg.HonorificTitle = on; changed = true; }
            Ui.Help("Your title becomes the song while it plays. When the music stops, your own Honorific title comes back; "
                    + "Honorific's settings aren't changed. Lightless pairs who also have Honorific see it too, a few seconds later.");
            if (!on) ImGui.BeginDisabled();
            ImGui.TextUnformatted("The title shows:");
            var show = Math.Clamp(cfg.HonorificShow, 0, 2);
            if (ImGui.RadioButton("The song's name", ref show, HonorificTitle.ShowSong)
                | ImGui.RadioButton("The song's name, then the artist", ref show, HonorificTitle.ShowSongThenArtist)
                | ImGui.RadioButton("The song's name and the artist, taking turns", ref show, HonorificTitle.ShowSwap))
            {
                cfg.HonorificShow = show;
                changed = true;
            }
            Ui.Help("Titles can be at most 32 characters, so longer names are cut short. Taking turns gives each one the full 32.");
            if (show == HonorificTitle.ShowSwap)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
                var swap = Math.Clamp(cfg.HonorificSwapSeconds, 5, 60);
                if (ImGui.SliderInt("Switch every (seconds)", ref swap, 5, 60)) { cfg.HonorificSwapSeconds = swap; changed = true; }
                Ui.Help("You see every switch right away. Honorific passes your title on to other plugins at most once every "
                        + "5 seconds, and Lightless then sends it, so your pairs may see the switches later or skip one. "
                        + "10 seconds or more looks steadier for them.");
                ImGui.Unindent();
            }
            var above = cfg.HonorificAbove;
            if (ImGui.Checkbox("Above my name", ref above)) { cfg.HonorificAbove = above; changed = true; }
            var onlyShared = cfg.HonorificOnlyWhenSharing;
            if (ImGui.Checkbox("Only while the song is shared with Lightless pairs", ref onlyShared)) { cfg.HonorificOnlyWhenSharing = onlyShared; changed = true; }
            Ui.Help("Off: also when the song plays only for you.");

            var useColor = cfg.HonorificUseColor;
            if (ImGui.Checkbox("Color##hcol", ref useColor)) { cfg.HonorificUseColor = useColor; changed = true; }
            ImGui.SameLine();
            var color = cfg.HonorificColor;
            if (ImGui.ColorEdit3("##hcolor", ref color, ImGuiColorEditFlags.NoInputs)) { cfg.HonorificColor = color; changed = true; }
            ImGui.SameLine(0, 18 * ImGuiHelpers.GlobalScale);
            var useGlow = cfg.HonorificUseGlow;
            if (ImGui.Checkbox("Glow##hglow", ref useGlow)) { cfg.HonorificUseGlow = useGlow; changed = true; }
            ImGui.SameLine();
            var glow = cfg.HonorificGlow;
            if (ImGui.ColorEdit3("##hglowc", ref glow, ImGuiColorEditFlags.NoInputs)) { cfg.HonorificGlow = glow; changed = true; }

            var sample = plugin.Player.Current?.Title ?? "Kid Francescoli - Moon (And It Went Like)";
            ImGui.TextDisabled(cfg.HonorificShow == HonorificTitle.ShowSwap && HonorificTitle.HasArtist(sample)
                ? $"Preview: 《{hon.Format(sample)}》, then 《{hon.Format(sample, artistTurn: true)}》"
                : $"Preview: 《{hon.Format(sample)}》");
            if (hon.Status.Length > 0) ImGui.TextDisabled(hon.Status);
            if (!on) ImGui.EndDisabled();
        }

        if (Ui.Group("Jukebox terminal"))
        {
            var jukebox = cfg.AllowJukebox;
            if (ImGui.Checkbox("Let other Tundy players send me songs from their jukebox terminal", ref jukebox)) { cfg.AllowJukebox = jukebox; changed = true; }
            Ui.Help("Players who also run Tundy can right-click you, open their jukebox terminal, see what's playing and send "
                    + "songs or whole playlists to your queue. The same rules as tell requests apply: who can request, approval, "
                    + "the max songs per playlist and the playlist cooldown. Off: their terminal says your jukebox is off.");
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Theme.Muted, "Your own terminal: right-click another player → \"Tundy: open jukebox terminal\", or target them and "
                                           + "type /tundy jukebox. It only links to players who run Tundy too. The two Tundys talk through "
                                           + "tells that are hidden from both chats.");
            ImGui.PopTextWrapPos();
            if (ImGui.Button("Open my jukebox terminal")) plugin.OpenJukebox(null, null);
            Ui.Tooltip("Make and edit your playlists. To send them, open it on a player instead.");
        }

        if (Ui.Group("Announce songs in chat"))
        {
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

        }

        if (Ui.Group("Skipping with !skip and !skipall"))
        {
        string[] skipModes = { "Nobody (!skip is off)", "Only the person who requested the song", "Anyone who's allowed to request songs" };
        var skipMode = Math.Clamp(cfg.SkipPermission, 0, 2);
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("Who can skip", ref skipMode, skipModes, skipModes.Length)) { cfg.SkipPermission = skipMode; changed = true; }
        Ui.Help("People send you \"/tell <you> !skip\" to skip the song that's playing. "
                + "Blocked people can never skip, and one person can skip at most once every 10 seconds. "
                + "With \"Tell people where their song is in the queue\" on, they also get a short reply.");

        var skipAll = cfg.AllowSkipAll;
        if (ImGui.Checkbox("Let people remove all their own songs with !skipall", ref skipAll)) { cfg.AllowSkipAll = skipAll; changed = true; }
        Ui.Help("\"/tell <you> !skipall\" removes only that person's songs: the one playing (if it's theirs), their songs "
                + "waiting in the queue (playlist songs too) and their requests waiting for your approval. Everyone else's "
                + "songs stay as they are, and songs you added yourself never count. Works even when !skip is set to Nobody. "
                + "Same 10-second wait as !skip; blocked people can't use it.");

        }

        if (Ui.Group("Reply to requesters"))
        {
        var reply = cfg.ReplyQueuePosition;
        if (ImGui.Checkbox("Tell people where their song is in the queue", ref reply)) { cfg.ReplyQueuePosition = reply; changed = true; }
        Ui.Help("After a request is accepted, sends that person a tell like: \"Your song is in the queue. 3 songs play before it.\" "
                + "It uses /tell Name@World to reach exactly that person; /r could reach whoever messaged you last instead.");
        var why = cfg.ReplyWhyRejected;
        if (ImGui.Checkbox("Tell people why their song can't be played", ref why)) { cfg.ReplyWhyRejected = why; changed = true; }
        Ui.Help("When a video can never play (private, removed, age-restricted, not available in this region, too long, "
                + "a live stream...), sends that person a tell like: \"Sorry, your song can't be played: it's a private video. "
                + "Try another link.\" On even when the queue position replies above are off.");
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled("Also tells people when their request waits for your approval, was approved or rejected, or is already in the queue.");
        if (reply) ImGui.TextDisabled(plugin.Replier.LastResult);
        ImGui.PopTextWrapPos();

        }

        if (Ui.Group("Failed downloads"))
        {
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

        }

        if (Ui.Group("Playlists"))
        {
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

        }

        if (Ui.Group("Playback"))
        {
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


        }

        if (Ui.Group("Now playing window"))
        {
        var showMini = cfg.ShowMiniPlayer;
        if (ImGui.Checkbox("Show the \"Now playing\" window (song name and time)", ref showMini)) plugin.SetMiniPlayer(showMini);
        var lockMini = cfg.LockMiniPlayer;
        if (ImGui.Checkbox("Lock its position", ref lockMini)) { cfg.LockMiniPlayer = lockMini; changed = true; }
        var compact = cfg.CompactMiniPlayer;
        if (ImGui.Checkbox("Compact (cover, name, time bar and the next song)", ref compact)) { cfg.CompactMiniPlayer = compact; changed = true; }
        Ui.Help("Also: right-click the window.");

        }

        if (Ui.Group("Download tools"))
        {
        if (plugin.YtDlp.IsInstalled)
            ImGui.TextColored(Theme.Good, "Installed");
        else
            ImGui.TextColored(Theme.Warn, "Not installed yet (downloads automatically)");
        ImGui.TextDisabled(plugin.YtDlp.ExePath);
        ImGui.TextDisabled(cfg.LastYtDlpUpdateUtc == DateTime.MinValue
            ? "Updates itself once a day."
            : $"Updates itself once a day. Last update: {Ui.Ago(cfg.LastYtDlpUpdateUtc.ToLocalTime())}.");
        if (plugin.YtDlp.DenoPath is { } deno)
            ImGui.TextColored(Theme.Good, $"YouTube helper (deno): found ({deno})");
        else
            ImGui.TextColored(Theme.Warn, "YouTube helper (deno): not found yet (downloads automatically)");
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
            ImGui.TextColored(Theme.Info, ff.Status);
        else if (ff.CurrentPath == null)
            ImGui.TextColored(Theme.Warn, "ffmpeg: not installed yet (downloads with the first music mod song or import)");
        else if (ff.UsingSoundysCopy)
            ImGui.TextColored(Theme.Good, $"ffmpeg: using Soundy's copy ({ff.CurrentPath})");
        else
            ImGui.TextColored(Theme.Good, $"ffmpeg: installed ({ff.CurrentPath})");
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

        }

        if (Ui.Group("Clean up"))
        {
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

        }

        if (Ui.Group("Chat commands"))
        {
        ImGui.TextDisabled("/tundy  ·  mini  ·  jukebox  ·  play <link>  ·  skip  ·  stop  ·  retry  ·  approve [all]  ·  import  ·  on  ·  off  ·  share  ·  mute  ·  vol <0-100>");

        }

        if (changed)
            plugin.SaveConfig();
    }
}
