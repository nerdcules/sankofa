using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sankofa.Gui;

/// <summary>
/// The About card: the logo, the name as a wide wordmark, what sankofa is and whose idea it
/// was. Its edge, wordmark and logo keep the Fathom brand's cyan and violet in every look; the
/// card itself takes the look's panel and text colours. Drawn from vectors, so it needs no
/// image file and stays sharp at any scale.
/// </summary>
static class About
{
    const string Repository = "github.com/nerdcules/sankofa";
    static readonly Color Cyan = Color.Parse("#4cc9f0"), Violet = Color.Parse("#9d8cff");

    /// <summary>The dimmed layer with the card on it; a click outside the card calls close.</summary>
    public static Control Layer(Palette pal, Action close, Action<string> open)
    {
        var column = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        column.Children.Add(new Viewbox { Width = 88, Height = 88, HorizontalAlignment = HorizontalAlignment.Center, Child = Logo() });
        column.Children.Add(new TextBlock
        {
            Text = "SANKOFA", FontSize = 30, FontWeight = FontWeight.Medium, LetterSpacing = 9, Foreground = new SolidColorBrush(Cyan),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(9, 4, 0, 0),
        });
        column.Children.Add(Line($"Version {Store.Version}", 12, pal.Dim));
        column.Children.Add(Line("A handoff ledger that lets many AI coding sessions work on one codebase without stepping on each other.", 14, pal.Fg));
        column.Children.Add(new Border
        {
            Height = 1, Width = 240, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Cyan, 0.3), new GradientStop(Violet, 0.7), new GradientStop(Colors.Transparent, 1) },
            },
        });
        var credit = Line("An idea by Alan Seunarayan, developed with AI", 13, pal.Fg);
        credit.FontWeight = FontWeight.SemiBold;
        column.Children.Add(credit);
        column.Children.Add(Line("MIT licence", 12, pal.Dim));
        var link = Line(Repository, 12, Cyan);
        link.Cursor = new Cursor(StandardCursorType.Hand);
        link.PointerPressed += (_, e) => { e.Handled = true; open("https://" + Repository); };
        column.Children.Add(link);
        var hint = Line("Press Esc or click outside to close", 11, pal.Dim);
        hint.Margin = new Thickness(0, 6, 0, 0);
        column.Children.Add(hint);

        var card = new Border
        {
            Width = 440, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(pal.Panel), BorderThickness = new Thickness(1.5),
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Cyan, 0), new GradientStop(Violet, 1) },
            },
            CornerRadius = new CornerRadius(Math.Max(pal.Radius, 8)), Padding = new Thickness(28, 30), Child = column,
        };
        card.PointerPressed += (_, e) => e.Handled = true; // a click on the card keeps it open

        var layer = new Border { Background = new SolidColorBrush(Colors.Black, 0.55), Child = card };
        layer.PointerPressed += (_, _) => close();
        return layer;
    }

    static TextBlock Line(string text, double size, Color color) => new()
    {
        Text = text, FontSize = size, Foreground = new SolidColorBrush(color), TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    /// <summary>docs/sankofa.svg as Avalonia shapes on its 64×64 grid: the bird looking back.</summary>
    static Control Logo()
    {
        var canvas = new Canvas { Width = 64, Height = 64 };
        canvas.Children.Add(new Rectangle
        {
            Width = 64, Height = 64, RadiusX = 14, RadiusY = 14,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.4, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#16294a"), 0), new GradientStop(Color.Parse("#0a1020"), 1) },
            },
        });
        var edge = new Rectangle { Width = 62, Height = 62, RadiusX = 13, RadiusY = 13, StrokeThickness = 1.6, Stroke = Gradient() };
        Canvas.SetLeft(edge, 1); Canvas.SetTop(edge, 1);
        canvas.Children.Add(edge);

        string[] bird =
        [
            "M16,42 C16,28 30,20 44,24 C52,26 54,34 50,40",
            "M16,42 C24,48 40,48 50,40",
            "M44,24 C42,16 34,12 28,15",
            "M28,15 L22,17 L28,19",
            "M24,47 L22,55",
            "M36,48 L36,55",
        ];
        foreach (var (brush, width) in new (IBrush, double)[] { (new SolidColorBrush(Cyan, 0.18), 7), (Gradient(absolute: true), 3) })
            foreach (var d in bird)
                canvas.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse(d), Stroke = brush, StrokeThickness = width,
                    StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                });
        canvas.Children.Add(Dot(22, 31, 4.2, null, new SolidColorBrush(Violet), 2.6));
        canvas.Children.Add(Dot(33, 17.5, 1.6, new SolidColorBrush(Cyan), null, 0));
        return canvas;
    }

    static Ellipse Dot(double cx, double cy, double r, IBrush? fill, IBrush? stroke, double width)
    {
        var e = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = fill, Stroke = stroke, StrokeThickness = width };
        Canvas.SetLeft(e, cx - r); Canvas.SetTop(e, cy - r);
        return e;
    }

    // Cyan to violet, top left to bottom right: across each shape, or across the whole 64×64
    // logo for the bird's strokes so they shade as one drawing, as the SVG does.
    static LinearGradientBrush Gradient(bool absolute = false) => new()
    {
        StartPoint = absolute ? new RelativePoint(0, 0, RelativeUnit.Absolute) : new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = absolute ? new RelativePoint(64, 64, RelativeUnit.Absolute) : new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Cyan, 0), new GradientStop(Violet, 1) },
    };
}
