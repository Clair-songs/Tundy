using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;

namespace Tundy;

/// <summary>
/// The pastel purple look for Tundy's own windows (dark plum, lavender, mint, pink, soft red). Only pushed while a
/// Tundy window draws, so other plugins keep their look. With the theme off, the same names give Dalamud's colors.
/// </summary>
public static class Theme
{
    private static Vector4 Rgb(int hex, float a = 1f) =>
        new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, a);

    // Palette
    public static readonly Vector4 Plum = Rgb(0x211A2D, 0.97f);
    public static readonly Vector4 PlumDark = Rgb(0x1A1424);
    public static readonly Vector4 PlumCard = Rgb(0x2C2340);
    public static readonly Vector4 PlumFrame = Rgb(0x372B50);
    public static readonly Vector4 PlumLine = Rgb(0x4B3C66, 0.7f);
    public static readonly Vector4 LavenderDeep = Rgb(0x5B4891);
    public static readonly Vector4 LavenderMid = Rgb(0x7A63C2);
    public static readonly Vector4 LavenderLight = Rgb(0xC6B5F7);
    public static readonly Vector4 TextLight = Rgb(0xEEE7F8);
    public static readonly Vector4 TextMuted = Rgb(0xA394BC);
    public static readonly Vector4 MintColor = Rgb(0x8EE3C1);
    public static readonly Vector4 PinkColor = Rgb(0xF4A9D2);
    public static readonly Vector4 SoftRedColor = Rgb(0xF2899A);
    public static readonly Vector4 PeachColor = Rgb(0xF6C98C);

    /// <summary>True while a Tundy window draws with the pastel theme.</summary>
    public static bool Active { get; private set; }

    // Meaning colors: pastel while the theme is on, Dalamud's otherwise.
    public static Vector4 Good => Active ? MintColor : ImGuiColors.HealerGreen;
    public static Vector4 Bad => Active ? SoftRedColor : ImGuiColors.DalamudRed;
    public static Vector4 Warn => Active ? PeachColor : ImGuiColors.DalamudOrange;
    public static Vector4 Info => Active ? LavenderLight : ImGuiColors.ParsedBlue;
    public static Vector4 Accent => Active ? PinkColor : ImGuiColors.ParsedBlue;
    public static Vector4 Muted => Active ? TextMuted : ImGuiColors.DalamudGrey3;
    public static Vector4 Heading => Active ? LavenderLight : ImGuiColors.DalamudGrey;
    public static Vector4 Card => Active ? PlumCard : new Vector4(1, 1, 1, 0.04f);

    private static readonly (ImGuiCol Col, Vector4 Color)[] Colors =
    {
        (ImGuiCol.Text, TextLight),
        (ImGuiCol.TextDisabled, TextMuted),
        (ImGuiCol.WindowBg, Plum),
        (ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0)),
        (ImGuiCol.PopupBg, Rgb(0x2A2140, 0.98f)),
        (ImGuiCol.Border, PlumLine),
        (ImGuiCol.FrameBg, PlumFrame),
        (ImGuiCol.FrameBgHovered, Rgb(0x433562)),
        (ImGuiCol.FrameBgActive, Rgb(0x4E3E73)),
        (ImGuiCol.TitleBg, PlumDark),
        (ImGuiCol.TitleBgActive, Rgb(0x3B2D5A)),
        (ImGuiCol.TitleBgCollapsed, PlumDark),
        (ImGuiCol.ScrollbarBg, new Vector4(0, 0, 0, 0.15f)),
        (ImGuiCol.ScrollbarGrab, Rgb(0x4B3C66)),
        (ImGuiCol.ScrollbarGrabHovered, LavenderDeep),
        (ImGuiCol.ScrollbarGrabActive, LavenderMid),
        (ImGuiCol.CheckMark, LavenderLight),
        (ImGuiCol.SliderGrab, Rgb(0xA990F0)),
        (ImGuiCol.SliderGrabActive, LavenderLight),
        (ImGuiCol.Button, Rgb(0x47396B)),
        (ImGuiCol.ButtonHovered, Rgb(0x6650A6)),
        (ImGuiCol.ButtonActive, LavenderMid),
        (ImGuiCol.Header, Rgb(0x3E3160)),
        (ImGuiCol.HeaderHovered, Rgb(0x56448A)),
        (ImGuiCol.HeaderActive, Rgb(0x6A56A8)),
        (ImGuiCol.Separator, PlumLine),
        (ImGuiCol.SeparatorHovered, LavenderMid),
        (ImGuiCol.SeparatorActive, LavenderLight),
        (ImGuiCol.ResizeGrip, Rgb(0x5B4891, 0.4f)),
        (ImGuiCol.ResizeGripHovered, LavenderMid),
        (ImGuiCol.ResizeGripActive, LavenderLight),
        (ImGuiCol.Tab, Rgb(0x2E2444)),
        (ImGuiCol.TabHovered, Rgb(0x6650A6)),
        (ImGuiCol.TabActive, LavenderDeep),
        (ImGuiCol.PlotHistogram, Rgb(0xB49CF5)),
        (ImGuiCol.TableHeaderBg, Rgb(0x30264A)),
        (ImGuiCol.TableBorderStrong, PlumLine),
        (ImGuiCol.TableBorderLight, Rgb(0x3E3157, 0.6f)),
        (ImGuiCol.TableRowBg, new Vector4(0, 0, 0, 0)),
        (ImGuiCol.TableRowBgAlt, Rgb(0x3A2E55, 0.35f)),
        (ImGuiCol.TextSelectedBg, Rgb(0x7A63C2, 0.45f)),
    };

    private static readonly (ImGuiStyleVar Var, float Value)[] Rounding =
    {
        (ImGuiStyleVar.WindowRounding, 10f),
        (ImGuiStyleVar.ChildRounding, 8f),
        (ImGuiStyleVar.FrameRounding, 6f),
        (ImGuiStyleVar.PopupRounding, 8f),
        (ImGuiStyleVar.GrabRounding, 6f),
        (ImGuiStyleVar.TabRounding, 6f),
        (ImGuiStyleVar.ScrollbarRounding, 8f),
    };

    private static int depth;
    private static bool pushed;

    /// <summary>Call from a window's PreDraw. Pairs with <see cref="Pop"/> in PostDraw.</summary>
    public static void Push(bool enabled)
    {
        depth++;
        if (depth > 1) return; // already on (a window inside another window's draw)
        pushed = enabled;
        Active = enabled;
        if (!enabled) return;
        foreach (var (col, color) in Colors) ImGui.PushStyleColor(col, color);
        foreach (var (v, value) in Rounding) ImGui.PushStyleVar(v, value);
    }

    public static void Pop()
    {
        if (depth == 0) return;
        depth--;
        if (depth > 0) return;
        if (pushed)
        {
            ImGui.PopStyleVar(Rounding.Length);
            ImGui.PopStyleColor(Colors.Length);
        }
        pushed = false;
        Active = false;
    }
}
