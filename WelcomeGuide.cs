using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;

namespace Tundy;

/// <summary>
/// The welcome guide: four short steps shown in the control panel the first time it opens on a new setup (and
/// again from Settings → Look). Who can send songs, who hears them, a few extras, then a first song.
/// </summary>
public sealed partial class MainWindow
{
    private bool guideOpen;      // reopened from Settings
    private int guideStep;
    private string guideCommand = string.Empty;
    private string guideCommandError = string.Empty;

    private const int GuideSteps = 4;

    private bool GuideActive => guideOpen || plugin.Config.ShowWelcome;

    /// <summary>Opens the guide again from its first step.</summary>
    private void OpenGuide()
    {
        guideOpen = true;
        guideStep = 0;
        guideCommand = string.Empty;
    }

    private void DrawGuide()
    {
        var cfg = plugin.Config;
        var scale = ImGuiHelpers.GlobalScale;

        // Header: title and Skip
        ImGui.AlignTextToFramePadding();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(Theme.Accent, FontAwesomeIcon.Music.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextColored(Theme.Heading, "Welcome to Tundy");
        const string skip = "Skip";
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(skip).X - ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.SmallButton(skip)) CloseGuide(finished: false);
        Ui.Tooltip(cfg.Enabled
            ? "Close the guide. You can open it again in Settings → Look."
            : "Close the guide. Requests stay paused until you click \"Requests paused\" at the top. You can open the guide again in Settings → Look.");

        // Step bar
        ImGui.Spacing();
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var gap = 6 * scale;
        var seg = (width - gap * (GuideSteps - 1)) / GuideSteps;
        for (var i = 0; i < GuideSteps; i++)
        {
            var min = start + new Vector2(i * (seg + gap), 0);
            var color = i <= guideStep ? (Theme.Active ? Theme.LavenderLight : Theme.Info) : new Vector4(1, 1, 1, 0.1f);
            dl.AddRectFilled(min, min + new Vector2(seg, 4 * scale), ImGui.GetColorU32(color), 2 * scale);
        }
        ImGui.Dummy(new Vector2(width, 4 * scale));
        ImGui.TextDisabled($"Step {guideStep + 1} of {GuideSteps}");
        ImGui.Spacing();

        // The step, with the Back/Next row kept at the bottom
        var navH = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        ImGui.BeginChild("guideStep", new Vector2(0, -navH), false);
        ImGui.PushTextWrapPos();
        switch (guideStep)
        {
            case 0: DrawGuideWho(); break;
            case 1: DrawGuideSharing(); break;
            case 2: DrawGuideExtras(); break;
            default: DrawGuideTry(); break;
        }
        ImGui.PopTextWrapPos();
        ImGui.EndChild();

        ImGui.Separator();
        if (guideStep > 0 && ImGui.Button("Back")) guideStep--;
        var last = guideStep == GuideSteps - 1;
        var next = last ? (cfg.Enabled ? "Finish" : "Finish and accept requests") : "Next";
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(next).X - ImGui.GetStyle().FramePadding.X * 2);
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.Active ? Theme.LavenderMid : Theme.Info with { W = 0.6f });
        var go = ImGui.Button(next);
        ImGui.PopStyleColor();
        if (go)
        {
            if (last) CloseGuide(finished: true);
            else if (LeaveGuideStep()) guideStep++;
        }
    }

    private static void GuideTitle(Plugin plugin, string text)
    {
        using (plugin.TitleFont.Push()) ImGui.TextUnformatted(text);
    }

    /// <summary>An option with a line of explanation under it, lined up with the option's text.</summary>
    private static bool GuideRadio(string label, string explanation, ref int value, int option)
    {
        var picked = ImGui.RadioButton(label, ref value, option);
        ImGui.Indent(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.TextColored(Theme.Muted, explanation);
        ImGui.Unindent(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.Spacing();
        return picked;
    }

    // ---- 1. Who can send songs

    private void DrawGuideWho()
    {
        var cfg = plugin.Config;
        if (guideCommand.Length == 0) guideCommand = plugin.RequestCommands[0];

        GuideTitle(plugin, "Who can send you songs?");
        ImGui.TextColored(Theme.Muted, $"People send \"/tell <you> {guideCommand.Trim()} <YouTube or Spotify link>\". You can change all of this later in People.");
        ImGui.Spacing();

        var mode = (int)cfg.QueueAccessMode;
        var changed = GuideRadio("Anyone, straight into the queue", "Fastest, but anyone who sends you a link plays it.", ref mode, (int)AccessMode.EveryoneAutoApprove);
        changed |= GuideRadio("Anyone, but I approve each request (recommended)", "Requests wait on Now playing until you click ✓.", ref mode, (int)AccessMode.EveryonePreQueue);
        changed |= GuideRadio("Only the players I list", "You add them in the People tab. Everyone else is ignored.", ref mode, (int)AccessMode.ListedOnly);
        if (changed)
        {
            cfg.QueueAccessMode = (AccessMode)mode;
            plugin.SaveConfig();
        }

        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Request command");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##guideCmd", ref guideCommand, 24);
        Ui.Help("One word, no spaces, like !q or !play. You can add more commands later in People.");
        if (guideCommandError.Length > 0) ImGui.TextColored(Theme.Bad, guideCommandError);
    }

    /// <summary>Checks and saves what the step asks for. False keeps the guide on this step.</summary>
    private bool LeaveGuideStep()
    {
        var cfg = plugin.Config;
        if (guideStep == 0)
        {
            var command = guideCommand.Trim();
            if (command.Length == 0 || command.Contains(' ') || command.Equals("!skip", StringComparison.OrdinalIgnoreCase))
            {
                guideCommandError = "The command has to be one word without spaces, and not !skip.";
                return false;
            }
            guideCommandError = string.Empty;
            // The chosen command goes first; any others stay.
            cfg.QueueAliases = new[] { command }.Concat(cfg.QueueAliases)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            plugin.SaveConfig();
        }
        else if (guideStep == 1 && cfg.ShareViaLightless && cfg.ShareTarget == 0
                 && plugin.Ffmpeg.CurrentPath == null && !plugin.Ffmpeg.Downloading)
        {
            // Songs go through the music mod: it needs ffmpeg, so start that download now.
            _ = plugin.Ffmpeg.DownloadAsync(default).ContinueWith(_ => { });
        }
        return true;
    }

    // ---- 2. Who hears the music

    private void DrawGuideSharing()
    {
        var cfg = plugin.Config;
        GuideTitle(plugin, "Who hears the music?");
        ImGui.Spacing();

        var share = cfg.ShareViaLightless ? 1 : 0;
        var changed = GuideRadio("Just me", "Songs play in Tundy's own player. Nothing else is needed.", ref share, 0);
        changed |= GuideRadio("Me and my Lightless pairs nearby",
            "Songs play from your character in the game through the Mega Music Mod's DJ dance, and Lightless Sync sends them "
            + "to the people you're paired with. They only need Lightless.", ref share, 1);
        if (changed)
        {
            cfg.ShareViaLightless = share == 1;
            plugin.SaveConfig();
        }

        if (share == 1)
        {
            Ui.BeginCard();
            DrawSetupSteps(inGuide: true);
            Ui.EndCard();
            ImGui.TextColored(Theme.Muted,
                "The first time a song is shared, your character does the emote once with its normal sound so Lightless learns "
                + "the file. That's expected. Each song then waits a few seconds so your pairs can download it.");
        }
    }

    // ---- 3. Extras

    private void DrawGuideExtras()
    {
        var cfg = plugin.Config;
        var changed = false;
        GuideTitle(plugin, "A few extras");
        ImGui.TextColored(Theme.Muted, "All optional, all in Settings later.");
        ImGui.Spacing();

        var announce = cfg.AnnounceSongs;
        if (ImGui.Checkbox("Announce each song in chat", ref announce)) { cfg.AnnounceSongs = announce; changed = true; }
        GuideNote($"Your character posts \"Now playing: <song> - requested by <name>\" in {cfg.AnnounceChannel} when a song starts.");

        var reply = cfg.ReplyQueuePosition;
        if (ImGui.Checkbox("Tell people where their song is in the queue", ref reply)) { cfg.ReplyQueuePosition = reply; changed = true; }
        GuideNote("A short /tell back, like \"Your song is up next!\", and how long to wait before another playlist.");

        var available = plugin.Honorific.Available;
        if (!available) ImGui.BeginDisabled();
        var title = cfg.HonorificTitle && available;
        if (ImGui.Checkbox("Show the song as my title", ref title)) { cfg.HonorificTitle = title; changed = true; }
        if (!available) ImGui.EndDisabled();
        GuideNote(available
            ? "Through the Honorific plugin: 《♪ Song name》 under your name while it plays. Your own title comes back after."
            : "Needs the Honorific plugin, which isn't installed.");

        if (changed) plugin.SaveConfig();
    }

    private static void GuideNote(string text)
    {
        ImGui.Indent(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.TextColored(Theme.Muted, text);
        ImGui.Unindent(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.Spacing();
    }

    // ---- 4. Try it

    private void DrawGuideTry()
    {
        GuideTitle(plugin, "You're set. Try it!");
        ImGui.Spacing();
        DrawAddLink("guide");

        ImGui.Spacing();
        Ui.Label("TELL YOUR FRIENDS");
        var line = $"/tell {YourName()} {plugin.RequestCommands[0]} <YouTube or Spotify link>";
        ImGui.TextUnformatted(line);
        ImGui.SameLine();
        if (Ui.IconButton("copyLine", FontAwesomeIcon.Copy, "Copy")) ImGui.SetClipboardText(line);

        if (plugin.ImportNotes.Count > 0)
        {
            ImGui.Spacing();
            foreach (var note in plugin.ImportNotes) ImGui.TextColored(Theme.Muted, note);
        }

        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, plugin.Config.Enabled
            ? "Song requests are on."
            : "Song requests start when you click Finish. Until then, only links you paste yourself play.");
    }

    /// <summary>"Firstname Lastname@World" for the line to share, or a placeholder when not logged in.</summary>
    private static string YourName()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return "<your name>";
        var world = me.HomeWorld.ValueNullable?.Name.ExtractText();
        return string.IsNullOrEmpty(world) ? me.Name.TextValue : $"{me.Name.TextValue}@{world}";
    }

    private void CloseGuide(bool finished)
    {
        var cfg = plugin.Config;
        var firstTime = cfg.ShowWelcome;
        cfg.ShowWelcome = false;
        if (finished) cfg.Enabled = true;
        plugin.SaveConfig();
        guideOpen = false;
        guideStep = 0;
        guideCommand = string.Empty;
        page = Page.NowPlaying;
        if (firstTime) plugin.StartToolDownloads(); // ffmpeg waited for the guide
        if (!finished && !cfg.Enabled)
            Plugin.Chat.Print("[Tundy] Song requests are paused. Click \"Requests paused\" at the top of /tundy to turn them on.");
    }
}
