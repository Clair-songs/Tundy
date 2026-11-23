using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace Tundy;

/// <summary>Small window you can leave on screen: the song's name and how far into it you are.</summary>
public sealed class MiniPlayerWindow : Window
{
    private const float Width = 260;
    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize;

    private readonly Plugin plugin;

    public MiniPlayerWindow(Plugin plugin) : base("Now playing###TundyMini", BaseFlags)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false; // Esc shouldn't close the overlay
    }

    public override void PreDraw()
    {
        Flags = plugin.Config.LockMiniPlayer ? BaseFlags | ImGuiWindowFlags.NoMove : BaseFlags;
        Theme.Push(plugin.Config.PastelTheme);
    }

    public override void PostDraw() => Theme.Pop();

    public override void OnClose() => plugin.SetMiniPlayer(false);

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = Width * scale;
        var np = plugin.NowPlaying();

        // Song name
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        if (np.Title != null)
            ImGui.TextUnformatted(np.Title);
        else
            ImGui.TextDisabled(np.Source == "Syncing" ? "Getting the next song ready..." : "Nothing playing");
        ImGui.PopTextWrapPos();

        // Time bar, with skip and the panel button on the same line
        var elapsed = np.Elapsed;
        var total = np.Duration;
        var fraction = elapsed is { } e && total is { } t && t > TimeSpan.Zero
            ? (float)Math.Clamp(e.TotalSeconds / t.TotalSeconds, 0, 1)
            : 0f;
        var label = np.Title == null ? "" : $"{Time(elapsed)} / {Time(total)}";
        var buttons = (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X) * 2;

        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Theme.Active ? Theme.LavenderLight with { W = 0.85f } : Theme.Info);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f * scale);
        ImGui.ProgressBar(fraction, new Vector2(width - buttons, ImGui.GetFrameHeight()), label);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        if (np.Title != null && elapsed == null && ImGui.IsItemHovered())
            ImGui.SetTooltip("The start time isn't known yet: it starts counting when the emote plays the song.");

        ImGui.SameLine();
        if (Ui.IconButton("skip", FontAwesomeIcon.StepForward, "Skip to the next song", !plugin.CanSkip))
            plugin.Skip();
        ImGui.SameLine();
        if (Ui.IconButton("open", FontAwesomeIcon.List, "Open the Tundy control panel"))
            plugin.ToggleMainWindow();

        // Who asked for it and where it plays
        var where = np.Source switch
        {
            "Music mod" => "via the music mod",
            "Lightless" => "shared via Lightless",
            "Syncing" => "syncing through Lightless",
            _ => "Tundy player",
        };
        ImGui.TextColored(Theme.Muted, np.Requester != null ? $"from {np.Requester} · {where}" : where);
    }

    private static string Time(TimeSpan? t) => t is { } v ? Ui.Time(v.TotalSeconds) : "-:--";
}
