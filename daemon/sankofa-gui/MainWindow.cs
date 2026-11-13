using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Sankofa.Gui;

/// <summary>
/// The window: the four pictures (one per platform look, any can be opened anywhere), the
/// counts, and a side panel for what was clicked with the command it stands for. It reads the
/// ledger files, as the terminal dashboard does, and never writes.
/// </summary>
sealed class MainWindow : Window
{
    readonly Board board;
    readonly BoardFiles files;
    readonly Func<DateTimeOffset> clock;
    readonly SceneView[] views = [new ConstellationView(), new MapView(), new HeatmapView(), new RadarView()];
    readonly Border stage = new();
    readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    readonly TextBlock status = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    readonly StackPanel side = new() { Spacing = 12, Margin = new Thickness(16) };
    readonly Border top = new(), sideBox = new();
    readonly Stopwatch sw = Stopwatch.StartNew();
    readonly bool motion;
    Look look;
    Palette pal;
    Picture picture;
    Scene scene = null!;
    Pick? picked;
    DateTime themeStamp;

    public MainWindow(Board board, BoardFiles files, Look look, Picture? picture, bool motion, Func<DateTimeOffset> clock, double? still = null)
    {
        this.board = board; this.files = files; this.clock = clock; this.look = look; this.motion = motion;
        pal = Palette.For(look);
        this.picture = picture ?? Palette.Opens(look);
        Title = "Sankofa";
        Width = 1280; Height = 800; MinWidth = 860; MinHeight = 560;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,*") };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(14, 8) };
        var name = new TextBlock { Text = "sankofa", FontWeight = FontWeight.Bold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        bar.Children.Add(name);
        Grid.SetColumn(tabs, 1); bar.Children.Add(tabs);
        Grid.SetColumn(status, 2); bar.Children.Add(status);
        top.Child = bar;
        Grid.SetColumnSpan(top, 2); grid.Children.Add(top);
        Grid.SetRow(stage, 1); grid.Children.Add(stage);
        sideBox.Width = 320;
        sideBox.Child = new ScrollViewer { Content = side, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(sideBox, 1); Grid.SetColumn(sideBox, 1); grid.Children.Add(sideBox);
        Content = grid;

        foreach (var v in views)
        {
            v.Motion = this.motion;
            v.T = still ?? 0;
            v.Picked += p => { picked = p; Refresh(false); };
        }
        if (look == Look.Omarchy && File.Exists(Palette.OmarchyTheme)) themeStamp = File.GetLastWriteTimeUtc(Palette.OmarchyTheme);

        KeyDown += (_, e) =>
        {
            if (e.Key is >= Key.D1 and <= Key.D4) { Show((Picture)(e.Key - Key.D1)); e.Handled = true; }
            else if (e.Key == Key.Q && e.KeyModifiers.HasFlag(KeyModifiers.Control)) Close();
            else if (e.Key == Key.Escape) { picked = null; Refresh(false); }
        };

        Apply();
        Refresh(false);
        if (still is null)
        {
            DispatcherTimer.Run(() => { Poll(); return true; }, TimeSpan.FromSeconds(1));
            if (this.motion)
                DispatcherTimer.Run(() => { var v = views[(int)this.picture]; v.T = sw.Elapsed.TotalSeconds; v.InvalidateVisual(); return true; },
                    TimeSpan.FromMilliseconds(33));
        }
    }

    void Poll()
    {
        if (look == Look.Omarchy && File.Exists(Palette.OmarchyTheme) && File.GetLastWriteTimeUtc(Palette.OmarchyTheme) is var at && at != themeStamp)
        {
            themeStamp = at; pal = Palette.Omarchy(); Apply();
        }
        files.Poll();
        Refresh(true);
    }

    void Show(Picture p) { picture = p; Apply(); Refresh(false); }

    /// <summary>Colours and the tab row, after a look or picture change.</summary>
    void Apply()
    {
        RequestedThemeVariant = pal.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Background = B(pal.Bg);
        Foreground = B(pal.Fg);
        FontFamily = Palette.Family(pal.Font);
        top.Background = B(pal.Panel);
        top.BorderBrush = B(pal.Border); top.BorderThickness = new Thickness(0, 0, 0, 1);
        sideBox.Background = B(pal.Panel);
        sideBox.BorderBrush = B(pal.Border); sideBox.BorderThickness = new Thickness(1, 0, 0, 0);
        status.Foreground = B(pal.Dim);
        tabs.Children.Clear();
        foreach (var p in Enum.GetValues<Picture>())
        {
            var on = p == picture;
            var b = new Button
            {
                Content = $"{(int)p + 1}  {views[(int)p].Title}", FontSize = 13, Padding = new Thickness(12, 5),
                Background = on ? B(pal.Accent) : Brushes.Transparent, Foreground = B(on ? pal.OnAccent : pal.Fg),
                CornerRadius = new CornerRadius(Math.Min(pal.Radius, 6)), BorderThickness = new Thickness(0),
            };
            ToolTip.SetTip(b, views[(int)p].Hint);
            b.Click += (_, _) => Show(p);
            tabs.Children.Add(b);
        }
        foreach (var v in views) v.P = pal;
        stage.Child = views[(int)picture];
    }

    static IBrush B(Color c, double a = 1) => new SolidColorBrush(c, a);

    TextBlock T(string text, double size = 13, Color? col = null, bool bold = false, bool mono = false, bool wrap = false) => new()
    {
        Text = text, FontSize = size, Foreground = B(col ?? pal.Fg), FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        FontFamily = Palette.Family(mono ? pal.Mono : pal.Font),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    Border Card(Control child, Color? edge = null) => new()
    {
        Child = child, Background = B(pal.Raised), BorderBrush = B(edge ?? pal.Border), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(pal.Radius), Padding = new Thickness(12, 10),
    };

    /// <summary>Fold the ledger again and redraw: on every poll (ages move even with no new event) and on a click.</summary>
    void Refresh(bool polled)
    {
        var now = clock();
        scene = Scene.Of(board, now);
        // a picked row that is gone (done, picked up) is let go
        if (picked is not null && polled && !AllPicks().Any(p => p.Key == picked.Key)) picked = null;
        var v = views[(int)picture];
        v.Scene = scene; v.Selected = picked?.Key; v.InvalidateVisual();

        var missing = files.Ledgers.Count(l => !File.Exists(l));
        status.Text = $"{scene.Repos.Count} repo{(scene.Repos.Count == 1 ? "" : "s")} · {scene.Events} events · last {Scene.Ago(scene.LastAge)}"
            + (missing > 0 ? $" · {missing} ledger{(missing == 1 ? "" : "s")} missing" : "") + " · following";

        side.Children.Clear();
        var counts = new UniformGrid { Columns = 2, Rows = 2 };
        void Count(string label, int n, Color col)
        {
            var s = new StackPanel { Spacing = 2 };
            s.Children.Add(T(label, 12, col));
            s.Children.Add(T(n.ToString(), 26, n > 0 ? col : pal.Dim, bold: true));
            var card = Card(s); card.Margin = new Thickness(0, 0, 6, 6);
            counts.Children.Add(card);
        }
        Count("Waiting on you", scene.Waiting.Count, scene.Waiting.Count > 0 ? pal.Red : pal.Dim);
        Count("Open", scene.Open.Count, pal.Fg);
        Count("Leases", scene.Leases, pal.Green);
        Count("Alerts", scene.Alerts, pal.Cyan);
        side.Children.Add(counts);

        side.Children.Add(T(picked is null ? "Click anything in the picture" : "Selected", 11, pal.Dim, bold: true));
        if (picked is { } p)
        {
            var s = new StackPanel { Spacing = 6 };
            s.Children.Add(T(p.Title, 15, pal.Of(p.Kind), bold: true, wrap: true));
            var meta = string.Join(" · ", new[] { Palette.Describe(p.Kind), p.Repo.Length > 0 ? Scene.Name(p.Repo) : "", p.Who, Scene.Ago(Scene.Age(p.Ts, now)) }.Where(x => x.Length > 0));
            s.Children.Add(T(meta, 12, pal.Dim, wrap: true));
            s.Children.Add(T(p.Text, 13, wrap: true));
            if (p.Command is { } cmd)
            {
                s.Children.Add(Card(T(cmd, 12, mono: true, wrap: true)));
                var copy = new Button { Content = "Copy command", FontSize = 12, Padding = new Thickness(10, 4) };
                copy.Click += async (_, _) =>
                {
                    if (GetTopLevel(this)?.Clipboard is { } clip) await clip.SetTextAsync(cmd);
                    copy.Content = "Copied";
                };
                s.Children.Add(copy);
            }
            side.Children.Add(Card(s, pal.Of(p.Kind)));
        }

        if (scene.Waiting.Count > 0)
        {
            side.Children.Add(T("Waiting on you", 11, pal.Dim, bold: true));
            foreach (var w in scene.Waiting.Take(8))
            {
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(T($"{w.Title}  ·  {Scene.Ago(Scene.Age(w.Ts, now))}", 12, pal.Of(w.Kind), bold: true));
                row.Children.Add(T(w.Text, 12));
                var b = new Button
                {
                    Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = B(pal.Raised), Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(pal.Radius),
                };
                var pick = w;
                b.Click += (_, _) => { picked = pick; Refresh(false); };
                side.Children.Add(b);
            }
        }
        if (scene.Open.Count > 0)
        {
            side.Children.Add(T("Open", 11, pal.Dim, bold: true));
            foreach (var o in scene.Open.Take(6))
                side.Children.Add(T($"{o.Title.Split(' ')[0]}  {o.Text}", 12));
        }
        side.Children.Add(T("1–4 switch pictures · Esc clears · Ctrl+Q quits", 11, pal.Dim, wrap: true));
    }

    IEnumerable<Pick> AllPicks() =>
        scene.Waiting.Concat(scene.Open).Concat(scene.Nodes.Select(n => n.Pick)).Concat(scene.Devices.Select(d => d.Pick))
            .Concat(scene.Tiles.Select(t => t.Pick)).Concat(scene.Blips.Select(b => b.Pick));

    /// <summary>For a render: pick a row by its id (e6) or key, as if clicked.</summary>
    public void Select(string id)
    {
        picked = AllPicks().FirstOrDefault(p => p.Key == id || p.Key.EndsWith("/" + id, StringComparison.Ordinal));
        Refresh(false);
    }
}
