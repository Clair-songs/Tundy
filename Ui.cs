using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
        if (Theme.IsMinimal)
        {
            // Minimal: plain grey text with a small coloured dot in front, still clickable.
            var s = ImGuiHelpers.GlobalScale;
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 3) * s);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.05f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.08f));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
            var hit = ImGui.Button("   " + label + "##" + label);
            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar(2);
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(min.X + 8 * s, (min.Y + max.Y) / 2), 3 * s, ImGui.GetColorU32(color));
            Tooltip(tooltip);
            return hit;
        }

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

    /// <summary>
    /// A drop-down with a search box at the top: type to narrow the list, Enter picks the first match. The search
    /// box gets the keyboard as soon as the list opens. Returns true when something was picked.
    /// </summary>
    /// <param name="items">(key, label) pairs; the key is what's returned in <paramref name="picked"/>.</param>
    /// <param name="filter">The search text, kept by the caller between frames.</param>
    public static bool SearchCombo(string label, string preview, string? selectedKey,
                                   System.Collections.Generic.IEnumerable<(string Key, string Label)> items,
                                   ref string filter, out string? picked, string hint = "Search")
    {
        picked = null;
        if (!ImGui.BeginCombo(label, preview, ImGuiComboFlags.HeightLargest)) return false;

        if (ImGui.IsWindowAppearing())
        {
            filter = string.Empty;
            ImGui.SetKeyboardFocusHere();
        }
        ImGui.SetNextItemWidth(-1);
        var enter = ImGui.InputTextWithHint("##search", hint, ref filter, 64, ImGuiInputTextFlags.EnterReturnsTrue);

        var text = filter.Trim();
        var matches = items.Where(i => text.Length == 0 || i.Label.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (enter && matches.Count > 0)
        {
            picked = matches[0].Key;
            ImGui.CloseCurrentPopup();
        }

        var line = ImGui.GetTextLineHeightWithSpacing();
        ImGui.BeginChild("##results", new Vector2(0, Math.Clamp(matches.Count, 1, 14) * line + 4), false);
        foreach (var (key, itemLabel) in matches)
        {
            if (ImGui.Selectable($"{itemLabel}##{key}", key == selectedKey))
            {
                picked = key;
                ImGui.CloseCurrentPopup();
            }
            if (key == selectedKey && ImGui.IsWindowAppearing()) ImGui.SetScrollHereY();
        }
        if (matches.Count == 0) ImGui.TextDisabled(text.Length > 0 ? "Nothing matches." : "Nothing to pick.");
        ImGui.EndChild();
        ImGui.EndCombo();
        return picked != null;
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

    public static void EndCard() => EndCard(Theme.Card, null);

    /// <param name="border">Optional outline colour (used by the problems banner).</param>
    public static void EndCard(Vector4 background, Vector4? border)
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
        var rounding = (Theme.IsMinimal ? 4 : 8) * ImGuiHelpers.GlobalScale;
        if (Theme.IsMinimal && border == null)
        {
            // Minimal: no panel, just a thin line above the section.
            dl.AddLine(start, new Vector2(right, start.Y), ImGui.GetColorU32(Theme.Line), 1f);
        }
        else
        {
            dl.AddRectFilled(start, new Vector2(right, bottom), ImGui.GetColorU32(background), rounding);
            if (border is { } b) dl.AddRect(start, new Vector2(right, bottom), ImGui.GetColorU32(b), rounding);
        }
        dl.ChannelsMerge();
        ImGui.Dummy(new Vector2(0, pad));
        ImGui.Spacing();
    }

    /// <summary>
    /// Text on the current line, pushed against the right edge (minus <paramref name="margin"/>). Never wraps:
    /// inside a card the wrap point is right there, so it would otherwise break into one piece per line.
    /// </summary>
    public static void TextRight(string text, Vector4 color, float margin)
    {
        ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(text).X - margin);
        ImGui.PushTextWrapPos(-1f);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    /// <summary>"3:45", or "" when unknown.</summary>
    public static string Length(int? seconds) => seconds is > 0 ? Time(seconds.Value) : "";

    /// <summary>"about 38 min" / "about 1 h 20 min".</summary>
    public static string About(int seconds)
    {
        var minutes = (int)Math.Round(seconds / 60.0);
        if (minutes < 1) return "under a minute";
        return minutes < 60 ? $"about {minutes} min" : $"about {minutes / 60} h {minutes % 60} min";
    }

    /// <summary>Cuts text to fit <paramref name="width"/>, ending in "...".</summary>
    public static string Ellipsize(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width) return text;
        const string dots = "...";
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid] + dots).X <= width) lo = mid;
            else hi = mid - 1;
        }
        return text[..lo].TrimEnd() + dots;
    }

    /// <summary>
    /// The song's cover as a rounded square (the middle of its YouTube thumbnail), or a record icon while there's
    /// none. Takes <paramref name="size"/> x <paramref name="size"/> of space.
    /// </summary>
    public static void Cover(Plugin plugin, string? videoId, float size)
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(size);
        var rounding = 6 * ImGuiHelpers.GlobalScale;
        var tex = plugin.Config.ShowCoverArt ? plugin.Covers.Get(videoId) : null;
        if (tex != null)
        {
            // Thumbnails are 16:9: show the middle square.
            var aspect = tex.Width > 0 ? (float)tex.Height / tex.Width : 9f / 16f;
            var side = (1 - aspect) / 2;
            dl.AddImageRounded(tex.Handle, min, max, new Vector2(side, 0), new Vector2(1 - side, 1), 0xFFFFFFFF, rounding);
        }
        else
        {
            dl.AddRectFilled(min, max, ImGui.GetColorU32(Theme.CoverFill), rounding);
            ImGui.PushFont(UiBuilder.IconFont);
            var icon = FontAwesomeIcon.Music.ToIconString();
            var iconSize = ImGui.CalcTextSize(icon);
            dl.AddText(min + (new Vector2(size) - iconSize) / 2, ImGui.GetColorU32(Theme.CoverIcon), icon);
            ImGui.PopFont();
        }
        ImGui.Dummy(new Vector2(size));
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
}
