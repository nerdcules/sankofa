using System.Text.RegularExpressions;
using Avalonia.Media;

namespace Sankofa.Gui;

/// <summary>The look of each platform, and the picture each opens with.</summary>
enum Look { Windows, Mac, Breeze, Omarchy }
enum Picture { Constellation, Map, Heatmap, Radar }

sealed record Palette(
    Look Look, bool Dark, Color Bg, Color Panel, Color Raised, Color Border, Color Fg, Color Dim, Color Accent,
    Color Red, Color Magenta, Color Green, Color Amber, Color Cyan, Color Purple, Color Blue,
    string? Font, string Mono, double Radius)
{
    public Color OnAccent => Luminance(Accent) > .179 ? Color.Parse("#14141A") : Color.Parse("#FFFFFF");

    static double Luminance(Color c)
    {
        static double L(byte v) { var s = v / 255.0; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        return .2126 * L(c.R) + .7152 * L(c.G) + .0722 * L(c.B);
    }

    public Color Of(Kind k) => k switch
    {
        Kind.Waiting or Kind.Contested or Kind.Flag => Red,
        Kind.Handoff => Magenta,
        Kind.Held => Green,
        Kind.Half or Kind.Overlap => Amber,
        Kind.Landed => Cyan,
        Kind.Queued => Purple,
        Kind.Todo => Dim,
        _ => Blue,
    };

    public static string Describe(Kind k) => k switch
    {
        Kind.Waiting => "waits on you", Kind.Handoff => "handoff", Kind.Todo => "open", Kind.Held => "held",
        Kind.Half => "may be half-finished", Kind.Queued => "queued offline", Kind.Contested => "contested",
        Kind.Landed => "landed", Kind.Overlap => "overlap", Kind.Flag => "flag", _ => "last word",
    };

    public static Picture Opens(Look l) => l switch
    {
        Look.Windows => Picture.Constellation, Look.Mac => Picture.Map, Look.Breeze => Picture.Heatmap, _ => Picture.Radar,
    };

    static Color C(string hex) => Color.Parse(hex);

    public static Palette Windows() => new(Look.Windows, false,
        C("#EEF0F4"), C("#F9FAFB"), C("#FFFFFF"), C("#DDE1E7"), C("#1B1B1F"), C("#5C6370"), C("#005FB8"),
        C("#C42B1C"), C("#B4009E"), C("#0F7B3D"), C("#B35900"), C("#00727F"), C("#6B3FA0"), C("#005FB8"),
        "Segoe UI Variable Text, Segoe UI", "Cascadia Mono, Consolas", 8);

    public static Palette Mac() => new(Look.Mac, true,
        C("#1E1E22"), C("#222227"), C("#2C2C33"), C("#38383F"), C("#ECECF1"), C("#A0A0AB"), C("#4DA3FF"),
        C("#FF6961"), C("#DA8FFF"), C("#32D74B"), C("#FFB454"), C("#64D2FF"), C("#B48CFF"), C("#4DA3FF"),
        null, "SF Mono, Menlo", 10);

    public static Palette Breeze() => new(Look.Breeze, false,
        C("#EFF0F1"), C("#FCFCFC"), C("#FFFFFF"), C("#C9CBCD"), C("#232629"), C("#4D5257"), C("#2980B9"),
        C("#DA4453"), C("#A93CB4"), C("#1E8449"), C("#C65F00"), C("#1D7FB8"), C("#8E44AD"), C("#2980B9"),
        "Noto Sans", "Hack, Noto Sans Mono, DejaVu Sans Mono", 4);

    /// <summary>Tokyo Night, for an Omarchy whose theme cannot be read.</summary>
    static readonly Dictionary<string, string> Night = new()
    {
        ["background"] = "#1A1B26", ["dark_background"] = "#16161E", ["lighter_background"] = "#1F2335",
        ["foreground"] = "#C0CAF5", ["dark_foreground"] = "#565F89", ["light_foreground"] = "#9AA5CE",
        ["accent"] = "#7AA2F7", ["selection"] = "#2F3549", ["red"] = "#F7768E", ["magenta"] = "#BB9AF7",
        ["green"] = "#9ECE6A", ["yellow"] = "#E0AF68", ["orange"] = "#FF9E64", ["cyan"] = "#7DCFFF", ["blue"] = "#7AA2F7",
    };

    public static string OmarchyTheme =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/state/omarchy/current/theme/colors.toml");

    /// <summary>Omarchy's current theme (colors.toml), read again whenever it changes.</summary>
    public static Palette Omarchy(string? file = null)
    {
        var v = new Dictionary<string, string>(Night);
        try
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file ?? OmarchyTheme), "(?m)^\\s*([a-z_]+)\\s*=\\s*[\"'](#[0-9a-fA-F]{6})[\"']"))
                v[m.Groups[1].Value] = m.Groups[2].Value;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var bg = C(v["background"]);
        var dark = (bg.R + bg.G + bg.B) / 3 < 128;
        return new(Look.Omarchy, dark, C(v["dark_background"]), bg, C(v["lighter_background"]), C(v["selection"]),
            C(v["foreground"]), C(v["light_foreground"]), C(v["accent"]),
            C(v["red"]), C(v["magenta"]), C(v["green"]), C(v["orange"]), C(v["cyan"]), C(v["blue"]), C(v["blue"]),
            "JetBrainsMono Nerd Font, JetBrains Mono, monospace", "JetBrainsMono Nerd Font, JetBrains Mono, monospace", 0);
    }

    static readonly Dictionary<string, FontFamily> fonts = [];

    /// <summary>The first installed family of a comma list ("Segoe UI Variable Text, Segoe UI"), else
    /// the platform default: a list whose first name is missing would otherwise fall back as a whole.</summary>
    public static FontFamily Family(string? list)
    {
        if (list is null) return FontFamily.Default;
        if (fonts.TryGetValue(list, out var f)) return f;
        var installed = FontManager.Current.SystemFonts.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = list.Split(',').Select(n => n.Trim()).FirstOrDefault(installed.Contains);
        return fonts[list] = name is null ? FontFamily.Default : new FontFamily(name);
    }

    public static bool OnOmarchy => File.Exists(OmarchyTheme);

    public static Look Detect() =>
        OperatingSystem.IsWindows() ? Look.Windows : OperatingSystem.IsMacOS() ? Look.Mac : OnOmarchy ? Look.Omarchy : Look.Breeze;

    public static Palette For(Look l) => l switch
    {
        Look.Windows => Windows(), Look.Mac => Mac(), Look.Breeze => Breeze(), _ => Omarchy(),
    };
}
