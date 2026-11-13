// sankofa-gui: the dashboard in a window, on Windows, macOS and Linux. Four pictures of one
// ledger (a session constellation, a device map, a lease heatmap, a radar); each platform opens
// with the one drawn for it, and any can be switched to.
//
//   sankofa-gui [open] [--look L] [--picture P] [--still] [repo...]
//   sankofa-gui render --out file.png [--look L] [--picture P] [--now TS] [--size WxH] [--at SECONDS] [--select ID] [repo...]
//   sankofa-gui scene [--now TS] [repo...]      the scene as text (what the tests read)
//   sankofa-gui version
//
// L: windows | mac | breeze | omarchy (default: this platform's). P: constellation | map |
// heatmap | radar (default: the look's own). Repos default to the one you are in, else the ones
// `sankofa watch add` registered. Like the terminal dashboard it follows the ledger files, so
// it needs no daemon and works for hub replicas.
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Sankofa;
using Sankofa.Gui;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var argv = args.ToList();
        var command = argv.Count > 0 && argv[0] is "open" or "render" or "scene" or "version" or "--version" or "help" or "--help" or "-h" ? argv[0] : "open";
        if (argv.Count > 0 && argv[0] == command) argv.RemoveAt(0);
        string? registry = null, outFile = null, select = null;
        Look? look = null; Picture? picture = null;
        DateTimeOffset? now = null;
        int width = 1280, height = 800;
        double at = 1.3;
        var still = Environment.GetEnvironmentVariable("SANKOFA_GUI_MOTION") == "0";
        var repos = new List<string>();
        for (var i = 0; i < argv.Count; i++)
        {
            string Next() => i + 1 < argv.Count ? argv[++i] : throw new ArgumentException($"{argv[i]} needs a value");
            try
            {
                switch (argv[i])
                {
                    case "--registry": registry = Next(); break;
                    case "--out": outFile = Next(); break;
                    case "--select": select = Next(); break;
                    case "--look": look = Enum.Parse<Look>(Next(), true); break;
                    case "--picture": picture = Enum.Parse<Picture>(Next(), true); break;
                    case "--now": now = DateTimeOffset.Parse(Next(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal); break;
                    case "--at": at = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--still": still = true; break;
                    case "--size":
                        var wh = Next().Split('x');
                        width = Math.Clamp(int.Parse(wh[0], CultureInfo.InvariantCulture), 64, 8192);
                        height = Math.Clamp(int.Parse(wh[1], CultureInfo.InvariantCulture), 64, 8192);
                        break;
                    default: repos.Add(argv[i]); break;
                }
            }
            catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException)
            {
                Console.Error.WriteLine($"sankofa-gui: {e.Message}");
                return 2;
            }
        }

        switch (command)
        {
            case "version" or "--version":
                Console.WriteLine($"sankofa-gui {Store.Version}");
                return 0;
            case "help" or "--help" or "-h":
                Console.WriteLine("usage: sankofa-gui [open] [--look windows|mac|breeze|omarchy] [--picture constellation|map|heatmap|radar] [--still] [repo...]");
                Console.WriteLine("       sankofa-gui render --out file.png [--look L] [--picture P] [--now TS] [--size WxH] [--at S] [--select ID] [repo...]");
                Console.WriteLine("       sankofa-gui scene [--now TS] [repo...] | version");
                return 0;
        }

        if (repos.Count == 0)
        {
            if (BoardFiles.FindRoot(Environment.CurrentDirectory) is { } here) repos.Add(here);
            else repos.AddRange(BoardFiles.Watched(registry));
        }
        var board = new Board();
        var files = new BoardFiles(board);
        foreach (var r in repos) files.Follow(r);
        files.Poll();
        Func<DateTimeOffset> clock = now is { } fixedNow ? () => fixedNow : () => DateTimeOffset.UtcNow;

        if (command == "scene")
        {
            Console.Out.Write(Scene.Of(board, clock()).Text());
            return 0;
        }
        if (repos.Count == 0)
        {
            Console.Error.WriteLine("sankofa-gui: no repo here and none watched (cd into one, name one, or sankofa watch add)");
            return 1;
        }
        var chosen = look ?? Palette.Detect();

        if (command == "render")
        {
            if (outFile is null) { Console.Error.WriteLine("sankofa-gui: render needs --out file.png"); return 2; }
            AppBuilder.Configure<App>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            var w = new MainWindow(board, files, chosen, picture, !still, clock, at) { Width = width, Height = height };
            if (select is not null) w.Select(select);
            w.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var frame = w.CaptureRenderedFrame();
            if (frame is null) { Console.Error.WriteLine("sankofa-gui: nothing was rendered"); return 1; }
#pragma warning disable CS0618 // the PNG default is what a render wants
            frame.Save(outFile);
#pragma warning restore CS0618
            Console.WriteLine(outFile);
            return 0;
        }

        App.Make = () => new MainWindow(board, files, chosen, picture, !still, clock);
        return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

sealed class App : Application
{
    public static Func<Window>? Make;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Make is not null)
            desktop.MainWindow = Make();
        base.OnFrameworkInitializationCompleted();
    }
}
