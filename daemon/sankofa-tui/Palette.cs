using Terminal.Gui.Drawing;
using Attr = Terminal.Gui.Drawing.Attribute;

namespace Sankofa.Tui;

/// <summary>
/// Colour says what kind of thing a row is before it is read: red waits on you, magenta is a
/// handoff, green is held, amber may be half-finished, cyan landed, yellow overlapped. The 16
/// ANSI colours only, so it follows the terminal's own theme instead of fighting it.
/// </summary>
static class Palette
{
    public static readonly ColorName16 Gold = ColorName16.BrightYellow;   // the sankofa, as on adinkra cloth

    /// <summary>The border colour of each panel.</summary>
    public static ColorName16 Accent(string panel) => panel switch
    {
        "Waiting on you" => ColorName16.BrightRed,
        "Open" => ColorName16.Yellow,
        "Leases" => ColorName16.Green,
        "Last word" => ColorName16.Cyan,
        "Alerts" => ColorName16.BrightBlue,
        _ => ColorName16.White,
    };

    /// <summary>The text colour of one row.</summary>
    public static ColorName16 Row(object row) => row switch
    {
        Board.Item { Type: "handoff" } => ColorName16.BrightMagenta,
        Board.Item { Type: "awaiting" } => ColorName16.BrightRed,
        Board.Item => ColorName16.White,
        Board.LeaseRow { State: "held" } => ColorName16.BrightGreen,
        Board.LeaseRow => ColorName16.Yellow,
        Board.Word => ColorName16.Gray,
        Board.Alert { Type: "overlap" } => ColorName16.BrightYellow,
        Board.Alert { Type: "landed" } => ColorName16.BrightCyan,
        Board.Alert { Type: "flag" } => ColorName16.BrightRed,
        Board.Alert { Type: "contend" } => ColorName16.BrightMagenta,
        _ => ColorName16.Gray,
    };

    public static Attr On(ColorName16 fg, Color bg, TextStyle style = TextStyle.None) => new(new Color(fg), bg, style);

    /// <summary>A copy of a view's scheme with its normal colours changed (the focus colours stay).</summary>
    public static Scheme With(Scheme s, ColorName16 fg, TextStyle style = TextStyle.None) =>
        new(s) { Normal = On(fg, s.Normal.Background, style) };
}
