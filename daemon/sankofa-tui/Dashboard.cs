using System.Collections.ObjectModel;
using System.Diagnostics;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Sankofa.Tui;

/// <summary>
/// The window: a menu bar, five tiled panels, a status line, in the Turbo Vision manner. Every
/// panel is a list; Enter opens the row, Space copies the command it stands for, F5 zooms the
/// panel to the whole screen. It acts only through the sankofa CLI, the same commands a person
/// would type, so what it does is in the ledger like anything else.
/// </summary>
sealed class Dashboard(Board board, BoardFiles files, Func<IEnumerable<string>> missing)
{
    sealed class Panel
    {
        public required FrameView Frame;
        public required ListView List;
        public required string Title;
        public List<object> Rows = [];
        public ObservableCollection<string> Lines = [];
        public object? Selected => List.SelectedItem is int i && i >= 0 && i < Rows.Count ? Rows[i] : null;
        /// <summary>Until when the border shows that something new arrived.</summary>
        public DateTime FlashUntil;
    }

    /// <summary>When each row was first seen, so a new one stands out for a few seconds.</summary>
    readonly Dictionary<string, DateTime> firstSeen = [];
    bool started;           // rows already there at start are not new
    Label live = null!;
    int tick;
    static readonly TimeSpan Fresh = TimeSpan.FromSeconds(6), FlashFor = TimeSpan.FromSeconds(3);

    static string RowKey(object row) => row switch
    {
        Board.Item i => $"i {i.Repo} {i.Id}",
        Board.LeaseRow l => $"l {l.Repo} {l.Id} {l.State}",
        Board.Word w => $"w {w.Repo} {w.Session} {w.Ts}",
        Board.Alert a => $"a {a.Repo} {a.Id}",
        _ => "",
    };

    IApplication app = null!;
    readonly List<Panel> panels = [];
    Panel waiting = null!, open = null!, leases = null!, words = null!, alerts = null!;
    Window top = null!;
    string? repo;           // null: every repo on the board
    Panel? zoomed;
    Snapshot snap = null!;

    public int Run()
    {
        using var a = Application.Create();
        app = a;
        app.Init();
        if (Environment.GetEnvironmentVariable("SANKOFA_TUI_SPLASH") != "0") Splash();
        using (top = new Window { Title = Title(), BorderStyle = LineStyle.None })
        {
            Build();
            Refresh();
            started = true;
            // the files every half second; ages, the live line and fading highlights every half second too
            app.AddTimeout(TimeSpan.FromMilliseconds(500), () => { files.Poll(); Refresh(); Pulse(); return true; });
            if (Environment.GetEnvironmentVariable("SANKOFA_TUI_EXIT_AFTER") is { } ms && int.TryParse(ms, out var n))
                app.AddTimeout(TimeSpan.FromMilliseconds(n), () => { app.RequestStop(); return false; });
            app.Run(top);
        }
        return 0;
    }

    string Title() => repo is null ? "sankofa" : $"sankofa: {Snapshot.Name(repo)}";

    void Build()
    {
        waiting = MakePanel("Waiting on you");
        open = MakePanel("Open");
        leases = MakePanel("Leases");
        words = MakePanel("Last word");
        alerts = MakePanel("Alerts");
        Tile();

        var menu = new MenuBar([
            new MenuBarItem("_File", [
                new MenuItem("_Refresh", "", () => { files.Poll(); Refresh(); }, Key.F5.WithCtrl),
                new MenuItem("_Quit", "", () => app.RequestStop(), Key.X.WithAlt),
            ]),
            new MenuBarItem("_Repo", RepoItems()),
            new MenuBarItem("_Item", [
                new MenuItem("_Open…", "", () => Details(Focused()), Key.Enter),
                new MenuItem("_Done…", "", () => DoneItem(Focused()?.Selected as Board.Item), Key.D),
                new MenuItem("_Pick up…", "", () => Pickup(Focused()?.Selected as Board.Item), Key.P),
                new MenuItem("_Copy command", "", () => Copy(Focused()?.Selected), Key.Space),
            ]),
            new MenuBarItem("_View", [
                new MenuItem("_Zoom panel", "", ToggleZoom, Key.F5),
                new MenuItem("_Tile panels", "", () => { zoomed = null; Tile(); }, Key.F4),
            ]),
            new MenuBarItem("_Help", [
                new MenuItem("_Keys", "", Keys, Key.F1),
                new MenuItem("_About sankofa", "", About),
            ]),
        ]);
        // top right: the dashboard is following, how much, and how long since anything happened
        live = new Label { X = Pos.AnchorEnd(), Y = 0, Text = "" };
        var status = new StatusBar([
            new Shortcut(Key.F1, "Help", Keys),
            new Shortcut(Key.Enter, "Open", null),
            new Shortcut(Key.Space, "Copy", null),
            new Shortcut(Key.F5, "Zoom", ToggleZoom),
            new Shortcut(Key.F9, "Menu", null),
            new Shortcut(Key.X.WithAlt, "Quit", () => app.RequestStop()),
        ]);
        top.Add(menu, live);
        foreach (var p in panels) top.Add(p.Frame);
        top.Add(status);
        // the panel lists would take letters as type-to-search: these keys go first, but only on
        // the dashboard itself, never while a dialog (and its text field) is up
        app.Keyboard.KeyDown += (_, k) =>
        {
            if (!ReferenceEquals(app.TopRunnableView, top) || k.Handled) return;
            if (k == Key.Q || k == Key.Esc && zoomed is null) { app.RequestStop(); k.Handled = true; }
            else if (k == Key.Esc && zoomed is not null) { ToggleZoom(); k.Handled = true; }
            else if (k.AsRune.Value == '?') { Keys(); k.Handled = true; }
            else if (k == Key.D) { DoneItem(Focused()?.Selected as Board.Item); k.Handled = true; }
            else if (k == Key.P) { Pickup(Focused()?.Selected as Board.Item); k.Handled = true; }
        };
    }

    MenuItem[] RepoItems()
    {
        var list = new List<MenuItem> { new("_All repos", "", () => SetRepo(null)) };
        var i = 1;
        foreach (var r in board.Repos())
        {
            var hot = i <= 9 ? $"_{i} " : "";
            list.Add(new MenuItem($"{hot}{Snapshot.Name(r)}", r, () => SetRepo(r)));
            i++;
        }
        return [.. list];
    }

    void SetRepo(string? r) { repo = r; top.Title = Title(); Refresh(); }

    Panel MakePanel(string title)
    {
        var list = new ListView { Width = Dim.Fill(), Height = Dim.Fill() };
        var frame = new FrameView { Title = title, CanFocus = true };
        frame.Add(list);
        var p = new Panel { Frame = frame, List = list, Title = title };
        list.SetSource(p.Lines);
        frame.SetScheme(Palette.With(frame.GetScheme(), Palette.Accent(title)));
        // each row in the colour of its kind; one that just arrived is lit up, then fades back
        list.RowRender += (_, e) =>
        {
            if (e.Row < 0 || e.Row >= p.Rows.Count || (e.Row == list.SelectedItem && list.HasFocus)) return;
            var row = p.Rows[e.Row];
            var bg = list.GetScheme().Normal.Background;
            var age = DateTime.UtcNow - firstSeen.GetValueOrDefault(RowKey(row), DateTime.MinValue);
            e.RowAttribute = age < Fresh
                ? Palette.On(Palette.Row(row), bg, age < Fresh / 2 ? TextStyle.Reverse | TextStyle.Bold : TextStyle.Bold)
                : Palette.On(Palette.Row(row), bg);
        };
        list.Accepting += (_, e) => { Details(p); e.Handled = true; };
        list.KeyDown += (_, k) =>
        {
            if (k == Key.Space) { Copy(p.Selected); k.Handled = true; }
        };
        panels.Add(p);
        return p;
    }

    /// <summary>Two columns: what waits on you and what is open on the left (what to do), who
    /// holds what, the last word of each session and the alerts on the right (what is going on).</summary>
    void Tile()
    {
        foreach (var p in panels) p.Frame.Visible = true;
        Place(waiting, 0, 1, Dim.Percent(50), Dim.Percent(40));
        Place(open, 0, Pos.Bottom(waiting.Frame), Dim.Percent(50), Dim.Fill(1));
        Place(leases, Pos.Right(waiting.Frame), 1, Dim.Fill(), Dim.Percent(35));
        Place(words, Pos.Right(waiting.Frame), Pos.Bottom(leases.Frame), Dim.Fill(), Dim.Percent(35));
        Place(alerts, Pos.Right(waiting.Frame), Pos.Bottom(words.Frame), Dim.Fill(), Dim.Fill(1));
    }

    static void Place(Panel p, Pos x, Pos y, Dim w, Dim h) { p.Frame.X = x; p.Frame.Y = y; p.Frame.Width = w; p.Frame.Height = h; }

    void ToggleZoom()
    {
        if (zoomed is not null) { zoomed = null; Tile(); return; }
        if (Focused() is not { } f) return;
        zoomed = f;
        foreach (var p in panels) p.Frame.Visible = p == f;
        Place(f, 0, 1, Dim.Fill(), Dim.Fill(1));
        f.List.SetFocus();
    }

    Panel? Focused() => panels.FirstOrDefault(p => p.Frame.HasFocus) ?? (zoomed ?? waiting);

    void Refresh()
    {
        snap = Snapshot.Of(board, repo, missing(), DateTimeOffset.UtcNow);
        Fill(waiting, snap.Waiting.Cast<object>(), o => snap.Row((Board.Item)o));
        Fill(open, snap.Open.Cast<object>(), o => snap.Row((Board.Item)o));
        Fill(leases, snap.Leases.Cast<object>(), o => snap.Row((Board.LeaseRow)o));
        Fill(words, snap.Words.Cast<object>(), o => snap.Row((Board.Word)o));
        Fill(alerts, snap.Alerts.Cast<object>(), o => snap.Row((Board.Alert)o));
        // forget rows no longer shown, so a dashboard left open for weeks does not keep every row it ever showed
        if (firstSeen.Count > 4 * panels.Sum(p => p.Rows.Count) + 1000)
        {
            var shown = panels.SelectMany(p => p.Rows).Select(RowKey).ToHashSet();
            foreach (var k in firstSeen.Keys.Where(k => !shown.Contains(k)).ToList()) firstSeen.Remove(k);
        }
        if (snap.Missing.Count > 0) alerts.Frame.Title = $"Alerts · missing ledger: {string.Join(", ", snap.Missing)}";
    }

    void Fill(Panel p, IEnumerable<object> rows, Func<object, string> line)
    {
        var keep = p.List.SelectedItem;
        p.Rows = rows.ToList();
        var now = DateTime.UtcNow;
        foreach (var r in p.Rows)
            if (firstSeen.TryAdd(RowKey(r), started ? now : DateTime.MinValue) && started) p.FlashUntil = now + FlashFor;
        var lines = p.Rows.Select(line).ToList();
        p.Frame.Title = $"{p.Title} ({p.Rows.Count})";
        p.List.SetNeedsDraw();   // the highlights fade with time, not only when the rows change
        if (lines.SequenceEqual(p.Lines)) return;
        // a new source in one step: clearing and adding row by row makes the list re-measure on
        // every row, which with thousands of rows takes longer than the half second between refreshes
        p.Lines = new ObservableCollection<string>(lines);
        p.List.SetSource(p.Lines);
        if (p.Rows.Count > 0) p.List.SelectedItem = Math.Clamp(keep ?? 0, 0, p.Rows.Count - 1);
    }

    /// <summary>Every half second: the live line's heartbeat, and borders lit while news is fresh.</summary>
    void Pulse()
    {
        tick++;
        var now = DateTime.UtcNow;
        foreach (var p in panels)
        {
            var lit = now < p.FlashUntil && tick % 2 == 0;
            p.Frame.SetScheme(Palette.With(p.Frame.GetScheme(), lit ? ColorName16.White : Palette.Accent(p.Title),
                                           lit ? TextStyle.Bold : TextStyle.None));
        }
        var last = snap.Repos.Select(r => board.LastTs(r)).DefaultIfEmpty("").Max(StringComparer.Ordinal) ?? "";
        var missingNow = snap.Missing.Count > 0;
        var dot = missingNow ? "○" : tick % 2 == 0 ? "●" : "◉";
        live.Text = $" {dot} {(missingNow ? "ledger missing" : "live")}  {snap.Repos.Count} repo{(snap.Repos.Count == 1 ? "" : "s")}" +
                    $"  {board.Events(repo)} events  last {(last.Length > 0 ? snap.Ago(last) : "never")} ";
        live.SetScheme(Palette.With(live.GetScheme(), missingNow ? ColorName16.BrightRed : tick % 2 == 0 ? ColorName16.BrightGreen : ColorName16.Green,
                                    TextStyle.Bold));
    }

    /// <summary>The sankofa, brightening out of the dark, for a moment at start (any key skips it).</summary>
    void Splash()
    {
        using var w = new Window { BorderStyle = LineStyle.None };
        var lines = Art.Bird.Split('\n');
        var bird = new Label { Text = Art.Bird, X = Pos.Center(), Y = Pos.Center() - 2, Width = lines.Max(l => l.Length), Height = lines.Length };
        var motto = new Label { Text = Art.Motto, X = Pos.Center(), Y = Pos.Bottom(bird) + 1 };
        w.Add(bird, motto);
        ColorName16[] fade = [ColorName16.Black, ColorName16.DarkGray, ColorName16.Gray, ColorName16.Yellow, Palette.Gold];
        var step = 0;
        void Paint()
        {
            var c = fade[Math.Min(step, fade.Length - 1)];
            bird.SetScheme(Palette.With(bird.GetScheme(), c, TextStyle.Bold));
            motto.SetScheme(Palette.With(motto.GetScheme(), step >= fade.Length - 1 ? ColorName16.White : ColorName16.DarkGray));
        }
        Paint();
        app.AddTimeout(TimeSpan.FromMilliseconds(110), () =>
        {
            step++;
            if (step > fade.Length + 5) { app.RequestStop(w); return false; }   // hold the gold a moment
            Paint();
            return true;
        });
        w.KeyDown += (_, k) => { app.RequestStop(w); k.Handled = true; };
        app.Run(w);
    }

    // ------------------------------------------------------------------ actions

    void Details(Panel? p)
    {
        switch (p?.Selected)
        {
            case Board.Item i:
            {
                var body = $"{i.Text}\n\n{i.Id} · {i.Type} · {Snapshot.Name(i.Repo)} · {i.Who} · {i.Ts}" +
                           (i.Branch is not null ? $"\nbranch {i.Branch}" : "");
                var act = i.Type == "handoff" ? "_Pick up" : "_Done";
                var r = MessageBox.Query(app, i.Type == "handoff" ? "Handoff" : i.Type == "awaiting" ? "Waiting on you" : "Open item",
                                         body, act, "_Copy command", "_Close");
                if (r == 0) { if (i.Type == "handoff") Pickup(i); else DoneItem(i); }
                else if (r == 1) Copy(i);
                break;
            }
            case Board.LeaseRow l:
            {
                var body = $"{l.Glob}\n\n{l.Id} · {l.State}{(l.Dirty && l.State != "held" ? " · may hold a half-finished edit" : "")}\n" +
                           $"{l.Who} · {Snapshot.Name(l.Repo)}\nclaimed {l.Claimed}\nuntil {l.Expires}";
                if (MessageBox.Query(app, "Lease", body, "_Copy paths", "_Close") == 0) Copy(l);
                break;
            }
            case Board.Word w:
                MessageBox.Query(app, $"Last word: {w.Who}", $"{w.Text}\n\n{w.Type} · {Snapshot.Name(w.Repo)} · {w.Ts}", "_Close");
                break;
            case Board.Alert a:
                MessageBox.Query(app, a.Type, $"{a.Text}\n\n{a.Id} · {Snapshot.Name(a.Repo)} · {a.Ts}", "_Close");
                break;
        }
    }

    void Copy(object? row)
    {
        var text = row switch
        {
            Board.Item i => Snapshot.Command(i),
            Board.LeaseRow l => l.Glob,
            Board.Word w => w.Text,
            Board.Alert a => a.Text,
            _ => null,
        };
        if (text is null) return;
        if (app.Clipboard?.TrySetClipboardData(text) == true) Flash($"copied: {text}");
        else MessageBox.Query(app, "Copy", $"No clipboard here; the command is:\n\n{text}", "_Close");
    }

    void Flash(string text)
    {
        var was = top.Title;
        top.Title = text;
        app.AddTimeout(TimeSpan.FromSeconds(2), () => { top.Title = was; return false; });
    }

    void DoneItem(Board.Item? i)
    {
        if (i is null || i.Type == "handoff") { if (i is not null) Pickup(i); return; }
        var text = Ask($"Done {i.Id}", i.Text, "How was it resolved?");
        if (text is null) return;
        Cli(i.Repo, ["done", i.Id, text.Length > 0 ? text : "resolved"], $"{i.Id} done");
    }

    void Pickup(Board.Item? i)
    {
        if (i is null || i.Type != "handoff") return;
        var branch = i.Branch?.Split('/', 2)[^1] ?? "?";
        if (MessageBox.Query(app, "Pick up", $"Switch {Snapshot.Name(i.Repo)} to {branch} and take this handoff?\n\n{i.Text}",
                             "_Pick up", "_Cancel") != 0) return;
        Cli(i.Repo, ["pickup", i.Id], "picked up");
    }

    /// <summary>A one-line prompt; null when cancelled.</summary>
    string? Ask(string title, string context, string prompt)
    {
        string? result = null;
        // height left to the dialog: a fixed one let the button row clip the field
        using var d = new Dialog { Title = title, Width = Dim.Percent(70), Height = Dim.Auto() };
        var ctx = new Label { Text = context.Length > 200 ? context[..200] + "…" : context, X = 1, Y = 0, Width = Dim.Fill(1), Height = 2 };
        var label = new Label { Text = prompt, X = 1, Y = 3 };
        var field = new TextField { X = 1, Y = 4, Width = Dim.Fill(1) };
        var ok = new Button { Text = "_OK", IsDefault = true };
        var cancel = new Button { Text = "_Cancel" };
        ok.Accepting += (_, e) => { result = field.Text ?? ""; app.RequestStop(d); e.Handled = true; };
        cancel.Accepting += (_, e) => { app.RequestStop(d); e.Handled = true; };
        d.Add(ctx, label, field);
        d.AddButton(cancel); d.AddButton(ok);   // the last one added is the default
        // a dialog focuses its default button when it becomes modal: give the field the focus then
        d.IsModalChanged += (_, _) => field.SetFocus();
        field.Accepting += (_, e) => { result = field.Text ?? ""; app.RequestStop(d); e.Handled = true; };
        app.Run(d);
        return result;
    }

    void Cli(string repoDir, string[] args, string success)
    {
        var psi = new ProcessStartInfo(CliPath()) { WorkingDirectory = repoDir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in args) psi.ArgumentList.Add(x);
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            var text = (o.Result + e.Result).Trim();
            if (p.ExitCode == 0) { if (text.Length > 0) MessageBox.Query(app, success, text, "_Close"); else Flash(success); }
            else MessageBox.ErrorQuery(app, $"sankofa {args[0]} failed", text.Length > 0 ? text : $"exit {p.ExitCode}", "_Close");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.ErrorQuery(app, "sankofa not found", ex.Message, "_Close");
        }
        files.Poll(); Refresh();
    }

    /// <summary>The CLI: $SANKOFA_CLI, else next to this binary (bin/out/../sankofa in a checkout), else on PATH.</summary>
    static string CliPath()
    {
        if (Environment.GetEnvironmentVariable("SANKOFA_CLI") is { Length: > 0 } c) return c;
        foreach (var p in new[] { Path.Combine(AppContext.BaseDirectory, "sankofa"), Path.Combine(AppContext.BaseDirectory, "..", "sankofa") })
            if (File.Exists(p)) return Path.GetFullPath(p);
        return "sankofa";
    }

    /// <summary>A dialog of preformatted text, kept as written (a message box centres each line).</summary>
    void Plain(string title, string text)
    {
        var lines = text.Split('\n');
        using var d = new Dialog { Title = title, Width = Dim.Auto(), Height = Dim.Auto() };
        var label = new Label { Text = text, X = 1, Y = 0, Width = lines.Max(l => l.Length), Height = lines.Length };
        var close = new Button { Text = "_Close", IsDefault = true };
        close.Accepting += (_, e) => { app.RequestStop(d); e.Handled = true; };
        d.Add(label);
        d.AddButton(close);
        app.Run(d);
    }

    void Keys() => Plain("Keys", """
        Tab / Shift+Tab   next / previous panel
        Up, Down, PgUp    move in a panel
        Enter             open the row (details, Done, Pick up)
        Space             copy the row's command (or a lease's paths)
        d                 done: resolve the item (asks how)
        p                 pick up the handoff (switches its branch)
        F5                zoom the panel to the whole screen (Esc back)
        F9 or Alt+letter  the menus
        ?  F1             this help
        q  Alt+X          quit
        """);

    void About()
    {
        using var d = new Dialog { Title = "About sankofa", Width = Dim.Auto(), Height = Dim.Auto() };
        var lines = Art.Bird.Split('\n');
        var bird = new Label { Text = Art.Bird, X = 2, Y = 1, Width = lines.Max(l => l.Length), Height = lines.Length };
        bird.SetScheme(Palette.With(bird.GetScheme(), Palette.Gold, TextStyle.Bold));
        var text = new Label { Text = $"{Art.Motto}\nsankofa-tui {Store.Version}", X = 2, Y = Pos.Bottom(bird) + 1 };
        var close = new Button { Text = "_Close", IsDefault = true };
        close.Accepting += (_, e) => { app.RequestStop(d); e.Handled = true; };
        d.Add(bird, text);
        d.AddButton(close);
        app.Run(d);
    }
}
