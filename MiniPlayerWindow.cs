using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace Tundy;

/// <summary>Small window you can leave on screen: the song's name and how far into it you are.</summary>
public sealed class MiniPlayerWindow : Window
{
    private const float Width = 280;
    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize;

    private readonly Plugin plugin;

    public MiniPlayerWindow(Plugin plugin) : base("Now playing###TundyMini", BaseFlags)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false; // Esc shouldn't close the overlay
    }

    public override void PreDraw() =>
        Flags = plugin.Config.LockMiniPlayer ? BaseFlags | ImGuiWindowFlags.NoMove : BaseFlags;

    public override void OnClose() => plugin.SetMiniPlayer(false);

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = Width * scale;
        var np = plugin.NowPlaying();

        // Song name
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        if (np.Title != null)
        {
            ImGui.TextUnformatted(np.Title);
            if (np.Requester != null) ImGui.TextDisabled($"from {np.Requester}");
        }
        else
        {
            ImGui.TextDisabled(np.Source == "Syncing" ? "Getting the next song ready..." : "Nothing playing");
        }
        ImGui.PopTextWrapPos();

        // Time
        var elapsed = np.Elapsed;
        var total = np.Duration;
        var fraction = elapsed is { } e && total is { } t && t > TimeSpan.Zero
            ? (float)Math.Clamp(e.TotalSeconds / t.TotalSeconds, 0, 1)
            : 0f;
        var label = np.Title == null ? "" : $"{Time(elapsed)} / {Time(total)}";

        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, ImGuiColors.ParsedBlue);
        ImGui.ProgressBar(fraction, new Vector2(width, ImGui.GetTextLineHeight() * 1.2f), label);
        ImGui.PopStyleColor();
        if (np.Title != null && elapsed == null && ImGui.IsItemHovered())
            ImGui.SetTooltip("The start time isn't known yet: it starts counting when the emote plays the song.");

        // Controls: skip, then where it plays, and a button for the full panel
        if (Ui.IconButton("skip", FontAwesomeIcon.StepForward, "Skip to the next song", !plugin.CanSkip))
            plugin.Skip();
        ImGui.SameLine();

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(np.Source switch
        {
            "Music mod" => "playing via the music mod",
            "Lightless" => "shared via Lightless",
            "Syncing" => "syncing through Lightless",
            _ => "Tundy player",
        });

        ImGui.SameLine(ImGui.GetStyle().WindowPadding.X + width - ImGui.GetFrameHeight());
        if (Ui.IconButton("open", FontAwesomeIcon.List, "Open the Tundy control panel"))
            plugin.ToggleMainWindow();
    }

    private static string Time(TimeSpan? t) => t is { } v ? Ui.Time(v.TotalSeconds) : "-:--";
}
