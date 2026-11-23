using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Config;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Components;

namespace Tundy;

/// <summary>Widgets used by the control panel.</summary>
public static class Ui
{
    public static string Time(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string Ago(DateTime when)
    {
        var d = DateTime.Now - when;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return when.ToString("MMM d HH:mm");
    }

    public static bool IconButton(string id, FontAwesomeIcon icon, string tooltip, bool disabled = false)
    {
        ImGui.PushID(id);
        if (disabled) ImGui.BeginDisabled();
        var clicked = ImGuiComponents.IconButton(icon);
        if (disabled) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        ImGui.PopID();
        return clicked && !disabled;
    }

    /// <summary>Section heading with a line under it.</summary>
    public static void Section(string text)
    {
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextColored(Theme.Heading, text);
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>Small caps-style label above a card's content.</summary>
    public static void Label(string text) => ImGui.TextColored(Theme.Heading, text);

    /// <summary>Settings group you can fold away. Returns true while it's open.</summary>
    public static bool Group(string text, bool openByDefault = false)
    {
        ImGui.Spacing();
        return ImGui.CollapsingHeader(text, openByDefault ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
    }

    /// <summary>Rounded, coloured status button. Returns true when clicked.</summary>
    public static bool Pill(string label, Vector4 color, string tooltip)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 12f * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 3) * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleColor(ImGuiCol.Button, color with { W = 0.22f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color with { W = 0.38f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color with { W = 0.55f });
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        var clicked = ImGui.Button(label);
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(2);
        Tooltip(tooltip);
        return clicked;
    }

    /// <summary>Puts the next item on this line if it fits, otherwise on the next line.</summary>
    public static void SameLineIfFits(float nextWidth)
    {
        ImGui.SameLine();
        if (ImGui.GetCursorPosX() + nextWidth > ImGui.GetWindowContentRegionMax().X)
            ImGui.NewLine();
    }

    private static readonly System.Collections.Generic.Stack<Vector2> CardStarts = new();

    /// <summary>
    /// Starts a card: a rounded panel behind everything up to <see cref="EndCard"/>. It grows with its content
    /// (tables inside need ScrollY so they draw in their own child window).
    /// </summary>
    public static void BeginCard()
    {
        var pad = 10 * ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        CardStarts.Push(ImGui.GetCursorScreenPos());
        ImGui.Dummy(new Vector2(0, pad - ImGui.GetStyle().ItemSpacing.Y));
        ImGui.Indent(pad);
        ImGui.BeginGroup();
        // Leave room for the right padding so wrapped text stays inside the card.
        ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - pad);
    }

    public static void EndCard()
    {
        var pad = 10 * ImGuiHelpers.GlobalScale;
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        ImGui.Unindent(pad);
        var bottom = ImGui.GetItemRectMax().Y + pad;
        var start = CardStarts.Pop();
        var right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSetCurrent(0);
        dl.AddRectFilled(start, new Vector2(right, bottom), ImGui.GetColorU32(Theme.Card), 8 * ImGuiHelpers.GlobalScale);
        dl.ChannelsMerge();
        ImGui.Dummy(new Vector2(0, pad));
        ImGui.Spacing();
    }

    public static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }

    /// <summary>Help marker "(?)" with a tooltip.</summary>
    public static void Help(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        Tooltip(text);
    }

    public static readonly string[] PermissionNames = { "Needs approval", "Auto-approve", "Blocked" };

    /// <summary>Sender name you can right-click to set their permission or copy it.</summary>
    public static void SenderCell(Plugin plugin, string name, string uniqueId)
    {
        ImGui.PushID(uniqueId);
        var rule = name == "You" ? null : plugin.RuleFor(name);
        var blocked = rule?.Permission == RequestPermission.Blocked;
        if (blocked) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        ImGui.Selectable(name);
        if (blocked) ImGui.PopStyleColor();
        if (name != "You")
            Tooltip(rule != null ? $"{PermissionNames[(int)rule.Permission]} · right-click for options" : "Right-click for options");

        if (name != "You" && ImGui.BeginPopupContextItem("sender"))
        {
            ImGui.TextDisabled(name);
            ImGui.Separator();
            if (rule?.Permission != RequestPermission.AutoApprove && ImGui.MenuItem("Always approve their requests"))
                plugin.SetRule(name, RequestPermission.AutoApprove);
            if (rule?.Permission != RequestPermission.PreQueue && ImGui.MenuItem("Ask me before playing their requests"))
                plugin.SetRule(name, RequestPermission.PreQueue);
            if (!blocked && ImGui.MenuItem("Block this person"))
                plugin.SetRule(name, RequestPermission.Blocked);
            if (rule != null && ImGui.MenuItem(blocked ? "Unblock (use the general setting)" : "Use the general setting"))
                plugin.RemoveRule(name);

            if (ImGui.MenuItem("Copy name"))
                ImGui.SetClipboardText(name);
            ImGui.EndPopup();
        }
        ImGui.PopID();
    }
}

/// <summary>Skip/stop, seek bar and volume. One instance per window (holds drag state).</summary>
public sealed class PlayerControls
{
    private readonly Plugin plugin;
    private bool seeking;
    private float seekValue;

    public PlayerControls(Plugin plugin) => this.plugin = plugin;

    public void DrawSeekBar(float width)
    {
        var player = plugin.Player;
        var dur = (float)player.Duration.TotalSeconds;
        var hasTrack = player.Current != null && dur > 0 && !player.CurrentIsShared;

        var pos = seeking ? seekValue : (float)player.Position.TotalSeconds;
        ImGui.SetNextItemWidth(width);
        if (!hasTrack) ImGui.BeginDisabled();
        if (ImGui.SliderFloat("##seek", ref pos, 0, Math.Max(dur, 1), hasTrack ? $"{Ui.Time(pos)} / {Ui.Time(dur)}" : "--:-- / --:--"))
            seekValue = pos;
        if (!hasTrack) ImGui.EndDisabled();

        seeking = ImGui.IsItemActive();
        if (ImGui.IsItemDeactivatedAfterEdit())
            player.Seek(TimeSpan.FromSeconds(seekValue));
        if (hasTrack) Ui.Tooltip("Drag to jump to a different part of the song");
        else if (player.CurrentIsShared && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Seeking is off while a song is shared: it can't move the in-game copy other players hear.");
    }

    public void DrawButtons()
    {
        var player = plugin.Player;
        var playing = player.Current != null;
        var active = playing || player.WaitingForSync;

        if (Ui.IconButton("skip", FontAwesomeIcon.StepForward, "Skip to the next song", !active))
            player.Skip();
        ImGui.SameLine();
        if (Ui.IconButton("stop", FontAwesomeIcon.Stop, "Stop and clear the whole queue", !active && player.PendingCount == 0))
            player.Stop();
    }

    /// <summary>
    /// True when the song plays inside the game (through the music mod or an emote) instead of
    /// Tundy's own player. Then the volume bar controls the game's "Self" sound volume, because the song is a
    /// sound your character makes.
    /// </summary>
    private bool SongPlaysInGame => plugin.SongPlaysInGame;

    public void DrawVolume(float sliderWidth)
    {
        if (SongPlaysInGame)
        {
            DrawGameVolume(sliderWidth);
            return;
        }

        var cfg = plugin.Config;
        if (Ui.IconButton("mute", cfg.Muted ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp, cfg.Muted ? "Unmute" : "Mute"))
        {
            cfg.Muted = !cfg.Muted;
            plugin.Player.ApplyVolume();
            plugin.SaveConfig();
        }
        ImGui.SameLine();
        var vol = (int)MathF.Round(cfg.Volume * 100);
        ImGui.SetNextItemWidth(sliderWidth);
        if (ImGui.SliderInt("##vol", ref vol, 0, 100, cfg.Muted ? "Muted" : "Volume %d%%"))
        {
            cfg.Volume = vol / 100f;
            if (cfg.Muted) cfg.Muted = false;
            plugin.Player.ApplyVolume();
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            plugin.SaveConfig();
    }

    /// <summary>Volume bar for in-game songs: the game's System Configuration → Sound Settings → "Self" volume.</summary>
    private void DrawGameVolume(float sliderWidth)
    {
        var cfg = plugin.Config;
        if (!Plugin.GameConfig.TryGet(SystemConfigOption.SoundPlayer, out uint current))
        {
            ImGui.TextDisabled("Game volume unavailable");
            return;
        }

        const string why = "The song plays from your character in the game, so this is the game's \"Self\" sound volume "
                           + "(System Configuration → Sound Settings). It changes how loud the song is for you; "
                           + "other players hear it at their own settings.";

        var muted = current == 0;
        if (Ui.IconButton("gmute", muted ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp, muted ? "Unmute the song" : "Mute the song"))
        {
            if (muted)
            {
                Plugin.GameConfig.Set(SystemConfigOption.SoundPlayer, cfg.SavedSelfVolume is > 0 and <= 100 ? cfg.SavedSelfVolume : 100u);
            }
            else
            {
                cfg.SavedSelfVolume = current;
                Plugin.GameConfig.Set(SystemConfigOption.SoundPlayer, 0u);
                plugin.SaveConfig();
            }
        }
        ImGui.SameLine();

        var vol = (int)Math.Min(current, 100u);
        ImGui.SetNextItemWidth(sliderWidth);
        if (ImGui.SliderInt("##gvol", ref vol, 0, 100, vol == 0 ? "Song muted" : "Song %d%%"))
            Plugin.GameConfig.Set(SystemConfigOption.SoundPlayer, (uint)vol);
        Ui.Tooltip(why);
    }
}
