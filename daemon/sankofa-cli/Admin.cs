using System.Text.RegularExpressions;

namespace Sankofa.Cli;

/// <summary>gate, trees, bridge, hub, watch add/list/rm, dash, setup.</summary>
public static class Admin
{
    // ------------------------------------------------------------------ gate

    const string GateMark = "# sankofa pre-commit gate";

    /// <summary>hooks/git-pre-commit, for a machine where the checkout and the package are both absent.</summary>
    const string GateScript = """
#!/bin/sh
# sankofa pre-commit gate
# Refuses a commit that touches a path another session or subagent has leased, or when
# sankofad is not answering. The universal gate: it works for every client, with or without
# hooks of its own. Override once with SANKOFA_GATE=0 or git commit --no-verify.
[ "${SANKOFA_GATE:-1}" = 0 ] && exit 0
d=${SANKOFA_DAEMON_BIN:-$(command -v sankofad)} || { echo "sankofa: sankofad not found; commit gate skipped" >&2; exit 0; }
exec "$d" gate git-pre-commit

""";

    static bool HasMark(string path) => File.Exists(path) && File.ReadAllText(path).Contains(GateMark, StringComparison.Ordinal);

    public static int Gate(string[] a)
    {
        var sub = a.Length > 0 ? a[0] : "status";
        var d = a.Length > 1 ? a[1] : ".";
        var r = Git.Run(d, "rev-parse", "--git-path", "hooks/pre-commit");
        if (!r.Ok || !Directory.Exists(d)) throw new Exit($"{d} is not in a git repository");
        var hook = r.Out.Trim();
        if (!Path.IsPathRooted(hook)) hook = Path.Combine(Path.GetFullPath(d), hook);
        hook = Path.GetFullPath(hook);
        switch (sub)
        {
            case "install":
                if (File.Exists(hook) && !HasMark(hook))
                    throw new Exit($"{hook} exists and is not sankofa's; add this line to it instead: sankofad gate git-pre-commit || exit 1");
                Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
                var src = Env.HookFile("git-pre-commit");
                if (src is not null) File.Copy(src, hook, overwrite: true); else File.WriteAllText(hook, GateScript);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                Out.Line($"sankofa: pre-commit gate installed at {hook}");
                return 0;
            case "uninstall":
                if (!HasMark(hook)) throw new Exit($"no sankofa gate at {hook}");
                File.Delete(hook);
                Out.Line($"sankofa: pre-commit gate removed from {hook}");
                return 0;
            case "status":
                if (HasMark(hook)) { Out.Line($"pre-commit gate: installed ({hook})"); return 0; }
                Out.Line("pre-commit gate: not installed");
                return 1;
            default: throw new Exit("gate install|uninstall|status [dir]");
        }
    }

    // ------------------------------------------------------------------ watch registry

    static string WatchRegPath()
    {
        var conf = Env.Get("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(conf, "sankofa", "watched");
    }

    static List<string> ReadReg(string reg) => File.Exists(reg) ? File.ReadAllLines(reg).Where(l => l.Length > 0).ToList() : [];

    /// <summary>Offer the desktop toasts the first time a repo starts producing overlap or landed events.</summary>
    static void WatchHint(Ctx c)
    {
        if (ReadReg(WatchRegPath()).Contains(c.Root)) return;
        Out.Line($"sankofa: to see these on the desktop: sankofa watch add {c.Root}");
    }

    /// <summary>cd -P: the directory with symbolic links resolved.</summary>
    static string RealDir(string d)
    {
        var full = Path.GetFullPath(d);
        try
        {
            var parts = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var cur = Path.GetPathRoot(full) ?? "/";
            foreach (var p in parts)
            {
                var next = Path.Combine(cur, p);
                var info = new DirectoryInfo(next);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } t) next = t.FullName;
                cur = next;
            }
            return cur.Length > 1 ? cur.TrimEnd(Path.DirectorySeparatorChar) : cur;
        }
        catch (IOException) { return full; }
    }

    public static int WatchReg(string[] a)
    {
        var reg = WatchRegPath();
        Directory.CreateDirectory(Path.GetDirectoryName(reg)!);
        if (!File.Exists(reg)) File.WriteAllText(reg, "");
        var sub = a.Length > 0 ? a[0] : "list";
        var rest = a.Length > 0 ? a[1..] : [];
        switch (sub)
        {
            case "add":
            {
                var d = rest.Length > 0 ? rest[0] : ".";
                if (!Directory.Exists(d)) throw new Exit($"no such directory: {d}");
                var r = Defaults.FindRoot(RealDir(d)) ?? throw new Exit($"no .sankofa/ in {d} or above; run 'sankofa init' there first");
                if (ReadReg(reg).Contains(r)) { Out.Line($"sankofa: already watching {r}"); return 0; }
                File.AppendAllText(reg, r + "\n");
                Out.Line($"sankofa: watching {r}");
                return 0;
            }
            case "list":
            {
                var list = ReadReg(reg);
                if (list.Count == 0) { Out.Line("no watched repos"); return 0; }
                foreach (var r in list)
                    Out.Line(File.Exists(Path.Combine(r, ".sankofa", "ledger.jsonl")) ? r : $"{r}  (ledger missing)");
                return 0;
            }
            case "rm":
            {
                var d = rest.Length > 0 ? rest[0] : ".";
                var r = (Directory.Exists(d) ? Defaults.FindRoot(RealDir(d)) : null) ?? d.TrimEnd('/');
                var list = ReadReg(reg);
                if (!list.Contains(r)) throw new Exit($"not watching {r}");
                var tmp = reg + ".tmp." + Environment.ProcessId;
                File.WriteAllText(tmp, string.Concat(list.Where(x => x != r).Select(x => x + "\n")));
                File.Move(tmp, reg, overwrite: true);
                Out.Line($"sankofa: stopped watching {r}");
                return 0;
            }
            default: throw new Exit("watch add|list|rm");
        }
    }

    // ------------------------------------------------------------------ trees

    static void PrintTrees(Response r)
    {
        foreach (var t in r.Trees ?? [])
            Out.Line(Out.Printable($"{t.Kind,-9} {t.Branch,-24} {t.Touched,4} changed  {t.Path}"));
    }

    static string AbsDir(string d)
    {
        if (!Directory.Exists(d)) throw new Exit($"no such directory: {d}");
        return RealDir(d);
    }

    public static int Trees(string[] a)
    {
        var c = Ctx.Need();
        var sub = a.Length > 0 ? a[0] : "list";
        var rest = a.Length > 0 ? a[1..] : [];
        switch (sub)
        {
            case "list": PrintTrees(Daemon.Need(new Request { Verb = "trees", Repo = c.Root })); return 0;
            case "on": PrintTrees(Daemon.Need(new Request { Verb = "trees-add", Repo = c.Root })); WatchHint(c); return 0;
            case "add":
            {
                if (rest.Length < 1) throw new Exit("trees add <dir>...");
                var paths = rest.Select(AbsDir).ToList();
                PrintTrees(Daemon.Need(new Request { Verb = "trees-add", Repo = c.Root, Paths = paths }));
                WatchHint(c);
                return 0;
            }
            case "rm":
            {
                if (rest.Length < 1) throw new Exit("trees rm <dir>...");
                var paths = rest.Select(d => Directory.Exists(d) ? RealDir(d) : d).ToList();
                Daemon.Need(new Request { Verb = "trees-rm", Repo = c.Root, Paths = paths });
                Out.Line($"sankofa: stopped watching {string.Join(' ', rest)}");
                return 0;
            }
            case "off":
                Daemon.Need(new Request { Verb = "trees-rm", Repo = c.Root });
                Out.Line($"sankofa: no longer watching trees of {c.Root}");
                return 0;
            case "scan":
            {
                var r = Daemon.Need(new Request { Verb = "rescan", Repo = c.Root });
                var entries = r.Entries ?? [];
                if (entries.Count == 0) { Out.Line("no new overlaps"); return 0; }
                foreach (var e in entries) Out.Line(Ledger.Render(e));
                return 0;
            }
            default: throw new Exit("trees on|add <dir>...|list|scan|rm <dir>...|off");
        }
    }

    // ------------------------------------------------------------------ bridge

    static void PrintBridge(Response r)
    {
        if (r.State == "off" || r.Bridge is null) { Out.Line("bridge off"); return; }
        var b = r.Bridge;
        var s = $"bridge on  every {b.Interval} s, {b.Refs} remote branches";
        if (b.LastOk is not null) s += $"\n  last fetch ok   {b.LastOk}";
        if (b.LastError is not null) s += $"\n  last fetch FAILED {b.LastFetch}: {b.LastError}";
        Out.Line(Out.Printable(s));
    }

    public static int Bridge(string[] a)
    {
        var c = Ctx.Need();
        var sub = a.Length > 0 ? a[0] : "status";
        var rest = a.Length > 0 ? a[1..] : [];
        switch (sub)
        {
            case "status": PrintBridge(Daemon.Need(new Request { Verb = "bridge", Repo = c.Root })); return 0;
            case "on":
            {
                var iv = Env.TtlSecs(rest.Length > 0 ? rest[0] : "5m");
                PrintBridge(Daemon.Need(new Request { Verb = "bridge", Repo = c.Root, On = true, Interval = iv }));
                WatchHint(c);
                return 0;
            }
            case "off": PrintBridge(Daemon.Need(new Request { Verb = "bridge", Repo = c.Root, On = false })); return 0;
            case "fetch":
            {
                // the daemon's own loop may be fetching this repo right now (it fetches every bridged repo
                // at start): "fetch now" then means wait for that one and ask again
                Response r;
                for (int attempt = 0; ; attempt++)
                {
                    var ans = Daemon.Call(new Request { Verb = "fetch", Repo = c.Root });
                    if (ans.Rc == 0) { r = ans.Resp!; break; }
                    if (ans.Rc == 3) throw new Exit($"sankofad unreachable at {Daemon.Socket}; trees and the bridge run in the daemon");
                    if (ans.Rc == 1 && attempt < 50 && ans.Error.Contains("already running", StringComparison.Ordinal)) { Thread.Sleep(100); continue; }
                    throw new Exit(ans.Error);
                }
                var entries = r.Entries ?? [];
                if (entries.Count == 0) Out.Line("nothing new landed");
                else foreach (var e in entries) Out.Line(Ledger.Render(e));
                if (r.Bridge?.LastError is { } err) Out.Err(Out.Printable($"sankofa: fetch failed: {err}"));
                return 0;
            }
            default: throw new Exit("bridge on [interval]|off|status|fetch");
        }
    }

    // ------------------------------------------------------------------ hub

    /// <summary>A repo's name on the hub: its git remote without scheme, user, port and .git.</summary>
    static string RepoName(Ctx c)
    {
        var r = Git.Run(c.Root, "remote", "get-url", "origin");
        var u = r.Ok ? r.Out.Trim() : "";
        if (u.Length == 0) return Path.GetFileName(c.Root.TrimEnd('/', '\\'));
        u = Regex.Replace(u, "^[a-z+]+://", "");
        u = Regex.Replace(u, "^[^@/]+@", "");
        u = Regex.Replace(u, "^([^/:]+):([^0-9/])", "$1/$2");
        u = Regex.Replace(u, ":[0-9]+/", "/");
        u = Regex.Replace(u, @"\.git/?$", "");
        u = Regex.Replace(u, "/+$", "");
        u = Regex.Replace(u, "^/+", "");
        u = Regex.Replace(u, "[^A-Za-z0-9._/-]", "-");
        return u;
    }

    public static int Hub(string[] a)
    {
        var sub = a.Length > 0 ? a[0] : "status";
        var rest = a.Length > 0 ? a[1..] : [];
        switch (sub)
        {
            case "join":
            {
                if (rest.Length < 2) throw new Exit("hub join <url> <token> [device]");
                var url = rest[0].TrimEnd('/');
                var token = rest[1];
                var dev = rest.Length > 2 ? rest[2] : Environment.MachineName;
                var conf = Env.HubConf;
                Directory.CreateDirectory(Path.GetDirectoryName(conf)!);
                File.WriteAllText(conf, $"url={url}\ntoken={token}\ndevice={dev}\n");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(conf, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var code = "000";
                try
                {
                    using var http = new MiniHttp(TimeSpan.FromSeconds(5));
                    code = http.Post(new Uri(url + "/v1/rpc"), token, "{\"verb\":\"ping\"}", TimeSpan.FromSeconds(5)).Status.ToString();
                }
                catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or UriFormatException or SankofaException) { }
                switch (code)
                {
                    case "200": Out.Line($"sankofa: joined {url} as {dev}"); return 0;
                    case "401": throw new Exit($"the hub at {url} refused that token (saved anyway in {conf})");
                    default: Out.Line($"sankofa: saved {conf}; the hub at {url} did not answer ({code}); writes queue until it does"); return 0;
                }
            }
            case "on":
            {
                var c = Ctx.Need();
                if (!File.Exists(Env.HubConf)) throw new Exit("this device has no hub yet: sankofa hub join <url> <token>");
                var name = rest.Length > 0 ? rest[0] : c.Cfg("hub_repo", "");
                if (name.Length == 0) name = RepoName(c);
                c.SetCfg("hub_repo", name);
                var ans = Daemon.Call(new Request { Verb = "hub-attach", Repo = c.Root });
                switch (ans.Rc)
                {
                    case 0:
                        var st = ans.Resp!.State ?? "";
                        Out.Line(st switch
                        {
                            "seeded" => $"sankofa: {c.Root} is {name} on the hub; this device's ledger became its first events",
                            "joined" => $"sankofa: {c.Root} is {name} on the hub; the ledger is now the hub's (yours is kept in .sankofa/ledger.pre-hub.jsonl)",
                            _ => $"sankofa: {c.Root} is {name} on the hub ({st})",
                        });
                        return 0;
                    case 3: c.SetCfg("hub_repo", ""); throw new Exit($"sankofad unreachable at {Daemon.Socket}; hub on needs it");
                    default: c.SetCfg("hub_repo", ""); throw new Exit(ans.Error);
                }
            }
            case "off":
            {
                var c = Ctx.Need();
                Daemon.Need(new Request { Verb = "hub-detach", Repo = c.Root });
                c.SetCfg("hub_repo", "");
                Out.Line($"sankofa: {c.Root} is local again; the hub replica is kept in .sankofa/ledger.hub.jsonl");
                return 0;
            }
            case "status":
            {
                var c = Ctx.Need();
                var r = Daemon.Need(new Request { Verb = "hub-status", Repo = c.Root });
                if (r.State == "off")
                    Out.Line("hub off" + (r.Url is not null ? $" (this device: {r.Device} on {r.Url})" : ""));
                else
                {
                    var s = $"hub {r.State}  {r.Id} on {r.Url} as {r.Device}, at event {r.Cursor}";
                    if ((r.Queued ?? 0) > 0) s += $", {r.Queued} bytes queued for the hub";
                    Out.Line(Out.Printable(s));
                }
                return 0;
            }
            default: throw new Exit("hub join <url> <token> [device] | on [name] | off | status");
        }
    }

    // ------------------------------------------------------------------ dash, setup

    public static int Dash(string[] a)
    {
        string name;
        if (a.Length > 0 && a[0] == "--gui") { name = "sankofa-gui"; a = a[1..]; }
        else name = "sankofa-tui";
        var bin = Env.Sibling(name) ?? throw new Exit($"{name} is not built (make build)");
        return Env.RunInherit(bin, a);
    }

    public static int Setup(string[] a) => Sankofa.Cli.Setup.Run(a);
}
