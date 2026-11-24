using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;

namespace Tundy;

/// <summary>Tundy's looks. The number is what's saved in the settings.</summary>
public enum ThemeStyle
{
    /// <summary>Dark plum, lavender, mint, pink and soft red (the default).</summary>
    Pastel = 0,

    /// <summary>Near-black and greys, white for what's selected, colour only on small status dots.</summary>
    Minimal = 1,

    /// <summary>Dalamud's own colours, untouched.</summary>
    Dalamud = 2,
}

/// <summary>
/// The looks for Tundy's own windows. Only pushed while a Tundy window draws, so other plugins keep their look.
/// The meaning colours (Good, Bad, ...) and the few drawing colours below follow whichever look is on; with
/// Dalamud's own look they give Dalamud's colours.
/// </summary>
public static class Theme
{
    private static Vector4 Rgb(int hex, float a = 1f) =>
        new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, a);

    // ---- Pastel palette
    public static readonly Vector4 Plum = Rgb(0x211A2D, 0.97f);
    public static readonly Vector4 PlumDark = Rgb(0x1A1424);
    public static readonly Vector4 PlumCard = Rgb(0x2C2340);
    public static readonly Vector4 PlumFrame = Rgb(0x372B50);
    public static readonly Vector4 PlumLine = Rgb(0x4B3C66, 0.7f);
    public static readonly Vector4 LavenderDeep = Rgb(0x5B4891);
    public static readonly Vector4 LavenderMid = Rgb(0x7A63C2);
    public static readonly Vector4 LavenderLight = Rgb(0xC6B5F7);
    public static readonly Vector4 PastelText = Rgb(0xEEE7F8);
    public static readonly Vector4 PastelMuted = Rgb(0xA394BC);
    public static readonly Vector4 MintColor = Rgb(0x8EE3C1);
    public static readonly Vector4 PinkColor = Rgb(0xF4A9D2);
    public static readonly Vector4 SoftRedColor = Rgb(0xF2899A);
    public static readonly Vector4 PeachColor = Rgb(0xF6C98C);

    // ---- Minimal palette
    public static readonly Vector4 Ink = Rgb(0x16171A, 0.97f);
    public static readonly Vector4 InkDark = Rgb(0x121315);
    public static readonly Vector4 InkRaised = Rgb(0x1C1D21);
    public static readonly Vector4 InkFrame = Rgb(0x24262B);
    public static readonly Vector4 InkLine = Rgb(0x2A2C31);
    public static readonly Vector4 InkOutline = Rgb(0x3A3D44);
    public static readonly Vector4 MinimalText = Rgb(0xE6E6E6);
    public static readonly Vector4 MinimalMuted = Rgb(0x8B8E96);
    public static readonly Vector4 MinimalSoft = Rgb(0xC9CBD0);
    public static readonly Vector4 MinimalGreen = Rgb(0x7FBF9A);
    public static readonly Vector4 MinimalRed = Rgb(0xD98A8A);
    public static readonly Vector4 MinimalAmber = Rgb(0xD9B27A);

    /// <summary>The look of the Tundy window drawing right now (Dalamud's outside Tundy's windows).</summary>
    public static ThemeStyle Current { get; private set; } = ThemeStyle.Dalamud;

    /// <summary>True while a Tundy window draws with one of Tundy's own looks.</summary>
    public static bool Active => Current != ThemeStyle.Dalamud;

    public static bool IsMinimal => Current == ThemeStyle.Minimal;

    private static Vector4 Pick(Vector4 pastel, Vector4 minimal, Vector4 dalamud) =>
        Current switch { ThemeStyle.Pastel => pastel, ThemeStyle.Minimal => minimal, _ => dalamud };

    // Meaning colours
    public static Vector4 Good => Pick(MintColor, MinimalGreen, ImGuiColors.HealerGreen);
    public static Vector4 Bad => Pick(SoftRedColor, MinimalRed, ImGuiColors.DalamudRed);
    public static Vector4 Warn => Pick(PeachColor, MinimalAmber, ImGuiColors.DalamudOrange);
    public static Vector4 Info => Pick(LavenderLight, MinimalText, ImGuiColors.ParsedBlue);
    public static Vector4 Accent => Pick(PinkColor, MinimalSoft, ImGuiColors.ParsedBlue);
    public static Vector4 Muted => Pick(PastelMuted, MinimalMuted, ImGuiColors.DalamudGrey3);
    public static Vector4 Heading => Pick(LavenderLight, MinimalMuted, ImGuiColors.DalamudGrey);
    public static Vector4 Card => Pick(PlumCard, new Vector4(0, 0, 0, 0), new Vector4(1, 1, 1, 0.04f));

    // Drawing colours
    public static Vector4 TextLight => Pick(PastelText, MinimalText, ImGuiColors.DalamudWhite);
    public static Vector4 TextMuted => Pick(PastelMuted, MinimalMuted, ImGuiColors.DalamudGrey3);
    public static Vector4 Line => Pick(PlumLine, InkLine, new Vector4(1, 1, 1, 0.12f));

    /// <summary>Background of the open tab, and of the others (Minimal draws tabs as text instead).</summary>
    public static Vector4 TabOn => Pick(LavenderDeep, new Vector4(0, 0, 0, 0), ImGuiColors.ParsedBlue with { W = 0.5f });
    public static Vector4 TabOff => Pick(PlumCard, new Vector4(0, 0, 0, 0), new Vector4(1, 1, 1, 0.06f));

    /// <summary>Time bars and the welcome guide's step bar.</summary>
    public static Vector4 Bar => Pick(LavenderLight with { W = 0.85f }, MinimalText, ImGuiColors.ParsedBlue);

    /// <summary>The cover placeholder square and its icon.</summary>
    public static Vector4 CoverFill => Pick(LavenderDeep, InkFrame, new Vector4(1, 1, 1, 0.08f));
    public static Vector4 CoverIcon => Pick(LavenderLight, Rgb(0x5F626A), ImGuiColors.DalamudGrey3);

    /// <summary>The main button of a screen (Next, Finish).</summary>
    public static Vector4 PrimaryButton => Pick(LavenderMid, Rgb(0x33363D), ImGuiColors.ParsedBlue with { W = 0.6f });

    private static readonly (ImGuiCol Col, Vector4 Color)[] PastelColors =
    {
        (ImGuiCol.Text, PastelText),
        (ImGuiCol.TextDisabled, PastelMuted),
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

    private static readonly (ImGuiCol Col, Vector4 Color)[] MinimalColors =
    {
        (ImGuiCol.Text, MinimalText),
        (ImGuiCol.TextDisabled, MinimalMuted),
        (ImGuiCol.WindowBg, Ink),
        (ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0)),
        (ImGuiCol.PopupBg, Rgb(0x1C1D21, 0.98f)),
        (ImGuiCol.Border, InkOutline),
        (ImGuiCol.FrameBg, InkFrame),
        (ImGuiCol.FrameBgHovered, Rgb(0x2C2E34)),
        (ImGuiCol.FrameBgActive, Rgb(0x33363D)),
        (ImGuiCol.TitleBg, InkDark),
        (ImGuiCol.TitleBgActive, InkRaised),
        (ImGuiCol.TitleBgCollapsed, InkDark),
        (ImGuiCol.ScrollbarBg, new Vector4(0, 0, 0, 0.1f)),
        (ImGuiCol.ScrollbarGrab, Rgb(0x2E3036)),
        (ImGuiCol.ScrollbarGrabHovered, InkOutline),
        (ImGuiCol.ScrollbarGrabActive, Rgb(0x4A4D55)),
        (ImGuiCol.CheckMark, MinimalText),
        (ImGuiCol.SliderGrab, MinimalSoft),
        (ImGuiCol.SliderGrabActive, Rgb(0xFFFFFF)),
        (ImGuiCol.Button, InkRaised),
        (ImGuiCol.ButtonHovered, InkLine),
        (ImGuiCol.ButtonActive, Rgb(0x33363D)),
        (ImGuiCol.Header, InkFrame),
        (ImGuiCol.HeaderHovered, Rgb(0x2C2E34)),
        (ImGuiCol.HeaderActive, Rgb(0x33363D)),
        (ImGuiCol.Separator, InkLine),
        (ImGuiCol.SeparatorHovered, Rgb(0x4A4D55)),
        (ImGuiCol.SeparatorActive, MinimalText),
        (ImGuiCol.ResizeGrip, Rgb(0x2A2C31, 0.4f)),
        (ImGuiCol.ResizeGripHovered, Rgb(0x4A4D55)),
        (ImGuiCol.ResizeGripActive, MinimalText),
        (ImGuiCol.Tab, InkRaised),
        (ImGuiCol.TabHovered, InkLine),
        (ImGuiCol.TabActive, Rgb(0x33363D)),
        (ImGuiCol.PlotHistogram, MinimalText),
        (ImGuiCol.TableHeaderBg, InkRaised),
        (ImGuiCol.TableBorderStrong, InkLine),
        (ImGuiCol.TableBorderLight, Rgb(0x232529)),
        (ImGuiCol.TableRowBg, new Vector4(0, 0, 0, 0)),
        (ImGuiCol.TableRowBgAlt, new Vector4(1, 1, 1, 0.02f)),
        (ImGuiCol.TextSelectedBg, new Vector4(1, 1, 1, 0.15f)),
    };

    // Same style settings in the same order for both looks, so Pop always pops the same number.
    private static readonly (ImGuiStyleVar Var, float Pastel, float Minimal)[] Shapes =
    {
        (ImGuiStyleVar.WindowRounding, 10f, 6f),
        (ImGuiStyleVar.ChildRounding, 8f, 4f),
        (ImGuiStyleVar.FrameRounding, 6f, 4f),
        (ImGuiStyleVar.PopupRounding, 8f, 4f),
        (ImGuiStyleVar.GrabRounding, 6f, 3f),
        (ImGuiStyleVar.TabRounding, 6f, 4f),
        (ImGuiStyleVar.ScrollbarRounding, 8f, 4f),
        (ImGuiStyleVar.FrameBorderSize, 0f, 1f), // Minimal: outlined buttons and boxes
    };

    private static int depth;
    private static bool pushed;
    private static int pushedColors;

    /// <summary>Call from a window's PreDraw. Pairs with <see cref="Pop"/> in PostDraw.</summary>
    public static void Push(ThemeStyle style)
    {
        depth++;
        if (depth > 1) return; // already on (a window inside another window's draw)
        Current = style;
        pushed = style != ThemeStyle.Dalamud;
        if (!pushed) return;
        var minimal = style == ThemeStyle.Minimal;
        var colors = minimal ? MinimalColors : PastelColors;
        foreach (var (col, color) in colors) ImGui.PushStyleColor(col, color);
        pushedColors = colors.Length;
        foreach (var (v, pastel, min) in Shapes) ImGui.PushStyleVar(v, minimal ? min : pastel);
    }

    public static void Pop()
    {
        if (depth == 0) return;
        depth--;
        if (depth > 0) return;
        if (pushed)
        {
            ImGui.PopStyleVar(Shapes.Length);
            ImGui.PopStyleColor(pushedColors);
        }
        pushed = false;
        Current = ThemeStyle.Dalamud;
    }
}
