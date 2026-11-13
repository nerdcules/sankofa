using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Sankofa.Cli;

/// <summary>
/// The desktop notifier (what the sankofa-watch service runs): toasts for new ledger entries in
/// every registered repo. `watch serve` runs forever; `watch once` scans once (tests, cron,
/// catch-up). A port of the 0.2 shell watcher: the registry is $XDG_CONFIG_HOME/sankofa/watched
/// (one repo root per line), offsets in $XDG_STATE_HOME/sankofa/offsets keep every entry toasted
/// once and catch up on entries written while the service was down. Toasts go through
/// notify-send on Linux, osascript on macOS and a PowerShell toast on Windows.
/// Env: SANKOFA_TOAST_TYPES (default "awaiting handoff overlap landed"), SANKOFA_TOAST_BURST (5),
/// SANKOFA_WATCH_TIMEOUT (30 s between rescans if the file watcher misses a wakeup).
/// </summary>
public static class Watch
{
    static readonly string Conf = Path.Combine(Defaults.Xdg("XDG_CONFIG_HOME", ".config"), "sankofa");
    static readonly string State = Path.Combine(Defaults.Xdg("XDG_STATE_HOME", ".local", "state"), "sankofa");
    static string Registry => Path.Combine(Conf, "watched");
    static string Offsets => Path.Combine(State, "offsets");
    static readonly HashSet<string> Types = (Env.Get("SANKOFA_TOAST_TYPES") ?? "awaiting handoff overlap landed")
        .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    static readonly int Burst = int.TryParse(Env.Get("SANKOFA_TOAST_BURST"), out var b) ? b : 5;
    static readonly double TimeoutSecs = double.TryParse(Env.Get("SANKOFA_WATCH_TIMEOUT"), System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 30;
    static readonly string Me = Env.ThisDevice;

    public static int Run(string[] a)
    {
        Directory.CreateDirectory(Conf); Directory.CreateDirectory(State);
        if (!File.Exists(Registry)) File.WriteAllText(Registry, "");
        if (!File.Exists(Offsets)) File.WriteAllText(Offsets, "");
        switch (a.Length > 0 ? a[0] : "")
        {
            case "once": Scan(); return 0;
            case "serve": return Serve();
            default: throw new Exit("watch serve|once");
        }
    }

    // ------------------------------------------------------------------ offsets

    static Dictionary<string, long> ReadOffsets()
    {
        var d = new Dictionary<string, long>();
        if (!File.Exists(Offsets)) return d;
        foreach (var line in File.ReadLines(Offsets))
        {
            var i = line.LastIndexOf('\t');
            if (i > 0 && long.TryParse(line[(i + 1)..], out var n)) d[line[..i]] = n;
        }
        return d;
    }

    static void SetOffset(string root, long n)
    {
        var d = ReadOffsets();
        d[root] = n;
        var tmp = Offsets + ".tmp." + Environment.ProcessId;
        File.WriteAllText(tmp, string.Concat(d.Select(kv => $"{kv.Key}\t{kv.Value}\n")));
        File.Move(tmp, Offsets, overwrite: true);
    }

    static List<string> Registered() =>
        File.Exists(Registry) ? File.ReadAllLines(Registry).Where(l => l.Length > 0).ToList() : [];

    // ------------------------------------------------------------------ scanning

    static void Scan()
    {
        foreach (var r in Registered())
        {
            try { ScanRepo(r); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Out.Err($"sankofa-watch: {r}: {ex.Message}"); }
        }
    }

    static void ScanRepo(string root)
    {
        var led = Path.Combine(root, ".sankofa", "ledger.jsonl");
        if (!File.Exists(led)) return;
        var lines = File.ReadAllLines(led);
        long n = CountNewlines(led);
        var off = ReadOffsets();
        if (!off.TryGetValue(root, out var o)) { SetOffset(root, n); return; }   // first sight: no backlog
        if (n == o) return;
        if (n < o) { SetOffset(root, n); return; }                                 // ledger was replaced
        var name = Path.GetFileName(root.TrimEnd('/', '\\'));
        var fresh = new List<Entry>();
        foreach (var line in lines.Skip((int)Math.Min(o, lines.Length)).Take((int)(n - o)))
        {
            if (line.Length == 0) continue;
            Entry? e = null;
            try { e = JsonSerializer.Deserialize(line, Json.Wire.Entry); } catch (JsonException) { }
            if (e is null || !Types.Contains(e.Type)) continue;
            // a handoff is for the others: not the device (hub) or the CLI (no hub) that wrote it
            if (e.Type == "handoff" && !((e.Device ?? "") != Me && (e.Device is not null || e.Session == "sankofad"))) continue;
            fresh.Add(e);
        }
        int k = fresh.Count;
        if (k > Burst)
        {
            var body = string.Join(", ", fresh.GroupBy(e => e.Type).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}"));
            var last = fresh.LastOrDefault(e => e.Type == "awaiting")?.Text;
            if (last is { Length: > 0 }) body += "\nlatest awaiting: " + Flat(last);
            Toast(last is { Length: > 0 } ? "critical" : "normal", $"{name}: {k} new entries", body);
        }
        else
        {
            foreach (var e in fresh)
            {
                var text = Flat(e.Text);
                var branch = e.Branch is { Length: > 0 } ? e.Branch[(e.Branch.IndexOf('/') + 1)..] : "-";
                switch (e.Type)
                {
                    case "awaiting": Toast("critical", $"{name}: awaiting you", text); break;
                    case "handoff": Toast("critical", $"{name}: handed off to you", $"{text}\nsankofa pickup {branch}"); break;
                    case "overlap": Toast("normal", $"{name}: two worktrees changed one path", text); break;
                    case "landed": Toast("normal", $"{name}: work landed on the remote", text); break;
                    default: Toast("normal", $"{name}: {e.Type}", $"[{e.Agent}] {text}"); break;
                }
            }
        }
        SetOffset(root, n);
    }

    static string Flat(string s) => Out.Printable(s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '));

    static long CountNewlines(string path)
    {
        long n = 0;
        using var f = File.OpenRead(path);
        var buf = new byte[65536];
        int k;
        while ((k = f.Read(buf, 0, buf.Length)) > 0) for (int i = 0; i < k; i++) if (buf[i] == (byte)'\n') n++;
        return n;
    }

    // ------------------------------------------------------------------ toasts

    /// <summary>urgency: critical or normal. Text written by someone else is printable by now.</summary>
    static void Toast(string urgency, string title, string body)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) ToastMac(title, body);
            else if (OperatingSystem.IsWindows()) ToastWindows(title, body);
            else ToastLinux(urgency, title, body);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Out.Err($"sankofa-watch: toast failed: {title} ({ex.Message})");
        }
    }

    static void ToastLinux(string urgency, string title, string body)
    {
        // -- ends the options, so a title or body starting with a dash is text, not a flag
        var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false };
        foreach (var x in new[] { "-a", "sankofa", "-u", urgency, "--", title, body }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi);
        if (p is null) { Out.Err($"sankofa-watch: notify-send failed: {title}"); return; }
        p.WaitForExit();
        if (p.ExitCode != 0) Out.Err($"sankofa-watch: notify-send failed: {title}");
    }

    static void ToastMac(string title, string body)
    {
        static string Q(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var psi = new ProcessStartInfo("osascript") { UseShellExecute = false };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add($"display notification \"{Q(body)}\" with title \"sankofa\" subtitle \"{Q(title)}\"");
        using var p = Process.Start(psi);
        p?.WaitForExit();
    }

    static void ToastWindows(string title, string body)
    {
        // the text travels in the environment, never in the script, so nothing in it is PowerShell
        const string script = """
            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
            $x = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
            $t = $x.GetElementsByTagName('text')
            $t.Item(0).AppendChild($x.CreateTextNode($env:SANKOFA_TOAST_TITLE)) | Out-Null
            $t.Item(1).AppendChild($x.CreateTextNode($env:SANKOFA_TOAST_BODY)) | Out-Null
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('sankofa').Show([Windows.UI.Notifications.ToastNotification]::new($x))
            """;
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }) psi.ArgumentList.Add(x);
        psi.Environment["SANKOFA_TOAST_TITLE"] = title;
        psi.Environment["SANKOFA_TOAST_BODY"] = body;
        using var p = Process.Start(psi);
        p?.WaitForExit();
    }

    // ------------------------------------------------------------------ the service

    static int Serve()
    {
        using var stop = new CancellationTokenSource();
        using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        using var intr = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });
        using var woke = new AutoResetEvent(false);
        var watchers = new Dictionary<string, FileSystemWatcher>();
        FileSystemWatcher? reg = null;
        try
        {
            reg = Watcher(Conf, "watched", woke);
            while (!stop.IsCancellationRequested)
            {
                Scan();
                // wake only for the ledgers and the registry; other files in .sankofa/ (the lock file)
                // would wake the scan before the ledger line lands
                var want = Registered().Select(r => Path.Combine(r, ".sankofa")).Where(Directory.Exists).ToHashSet();
                foreach (var gone in watchers.Keys.Where(k => !want.Contains(k)).ToList()) { watchers[gone].Dispose(); watchers.Remove(gone); }
                foreach (var d in want.Where(d => !watchers.ContainsKey(d)))
                {
                    try { watchers[d] = Watcher(d, "ledger.jsonl", woke); }
                    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { Out.Err($"sankofa-watch: cannot watch {d}: {ex.Message}"); }
                }
                WaitHandle.WaitAny([woke, stop.Token.WaitHandle], TimeSpan.FromSeconds(TimeoutSecs));
            }
            return 0;
        }
        finally
        {
            reg?.Dispose();
            foreach (var w in watchers.Values) w.Dispose();
        }
    }

    static FileSystemWatcher Watcher(string dir, string file, AutoResetEvent woke)
    {
        var w = new FileSystemWatcher(dir, file) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
        FileSystemEventHandler on = (_, _) => woke.Set();
        w.Changed += on; w.Created += on; w.Deleted += on;
        w.Renamed += (_, _) => woke.Set();
        w.Error += (_, _) => woke.Set();
        w.EnableRaisingEvents = true;
        return w;
    }
}
