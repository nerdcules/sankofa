using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Sankofa;

// sankofad: the sankofa daemon, and the tiny client the shell CLI uses to reach it.
//   sankofad serve [--socket P] [--db P]   run the daemon
//   sankofad send  [--socket P]            one JSON request on stdin -> response(s) on stdout
//                                          exit 0 ok, 1 refused, 3 daemon unreachable (nothing sent),
//                                          4 sent but no answer (the write may or may not have happened)
//   sankofad gate claude-code|codex        pre-edit hook: JSON payload on stdin, deny JSON on stdout
//   sankofad gate git-pre-commit           refuse staged paths someone else holds (exit 1)
//   sankofad mcp                           MCP server on stdio: the CLI's verbs as tools
//   sankofad hub serve --listen URL [--db P] [--data DIR] [--tokens F]
//                                          the hub: one ledger for many devices, over HTTP
//   sankofad hub token add|revoke <device> | list [--tokens F]
//   sankofad bench                         startup and ask round-trip timings, as JSON
//   sankofad version

static string Opt(string[] args, string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var cmd = args.Length > 0 ? args[0] : "help";
switch (cmd)
{
    case "serve": return Serve(Opt(args, "--socket", Defaults.Socket()), Opt(args, "--db", Defaults.Db()));
    case "send": return Send(Opt(args, "--socket", Defaults.Socket()));
    case "bench": return Bench.Run();
    case "mcp" or "--mcp": return SiblingExec.Run("sankofa-mcp", args[1..]);
    case "hub": return SiblingExec.Run("sankofa-hub", args[1..]);
    case "gate" when args.Length > 1 && args[1] == "git-pre-commit": return Gate.PreCommit();
    case "gate" when args.Length > 1:
        try { return Gate.PreEdit(args[1]); }
        catch (SankofaException ex) { Console.Error.WriteLine($"sankofad gate: {ex.Message}"); return 1; }
    case "version" or "--version" or "-V": Console.WriteLine($"sankofad {Store.Version}"); return 0;
    default:
        Console.Error.WriteLine("usage: sankofad serve [--socket P] [--db P] | send [--socket P] | gate claude-code|codex|git-pre-commit | mcp | hub serve|token | bench | version");
        return cmd is "help" or "-h" or "--help" ? 0 : 2;
}

static int Serve(string socket, string dbPath)
{
    using var cts = new CancellationTokenSource();
    using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });
    using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; cts.Cancel(); });
    try
    {
        using var store = new Store(dbPath);
        var server = new Server(store, socket);
        var listener = server.Bind();
        // 0.6: overlap detection across worktrees, and the remote bridge
        using var overlaps = new Overlaps(store) { Period = TimeSpan.FromSeconds(Defaults.Seconds("SANKOFA_RESCAN", 30)) };
        using var bridge = new Bridge(store)
        {
            Tick = TimeSpan.FromSeconds(Defaults.Seconds("SANKOFA_BRIDGE_TICK", 5)),
            RetryAfter = TimeSpan.FromSeconds(Defaults.Seconds("SANKOFA_BRIDGE_RETRY", 30)),
        };
        server.Overlaps = overlaps; server.Bridge = bridge;
        // 0.7: repos attached to a hub are served from there (hub.conf: url, token, device)
        using var link = new HubLink(store, Defaults.HubConf())
        {
            Timeout = TimeSpan.FromSeconds(Defaults.Seconds("SANKOFA_HUB_TIMEOUT", 3)),
            GateTimeout = TimeSpan.FromMilliseconds(Defaults.Seconds("SANKOFA_HUB_GATE_MS", 150)),
            Breaker = TimeSpan.FromSeconds(Defaults.Seconds("SANKOFA_HUB_BREAKER", 2)),
            MaxBackoff = TimeSpan.FromSeconds(Defaults.Seconds("SANKOFA_HUB_RETRY_MAX", 10)),
            StreamSilence = TimeSpan.FromSeconds(Defaults.Seconds("SANKOFA_HUB_SILENCE", 45)),
        };
        server.Link = link;
        // 0.8.1: ended leases whose paths git shows clean lose their half-finished mark
        using var settle = new Settle(store, server.Dispatch, link) { Period = TimeSpan.FromSeconds(Defaults.Seconds("SANKOFA_SETTLE", 60)) };
        server.Settle = settle;
        link.Start(); overlaps.Start(); bridge.Start(); settle.Start();
        Console.Error.WriteLine($"sankofad {Store.Version}: listening on {socket}, db {dbPath}");
        server.RunAsync(listener, cts.Token).GetAwaiter().GetResult();
        return 0;
    }
    catch (SankofaException ex) { Console.Error.WriteLine($"sankofad: {ex.Message}"); return 1; }
    catch (SqliteException ex) { Console.Error.WriteLine($"sankofad: database: {ex.Message}"); return 1; }
}

static int Send(string socket)
{
    var request = Console.In.ReadToEnd().Trim();
    if (request.Length == 0) { Console.Error.WriteLine("sankofad send: empty request"); return 2; }
    bool follow = request.Contains("\"follow\":true", StringComparison.Ordinal);
    using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    try { s.Connect(Server.Endpoint(socket)); }
    catch (SocketException) { Console.Error.WriteLine($"sankofad: no daemon at {socket}"); return 3; }
    catch (SankofaException ex) { Console.Error.WriteLine($"sankofad: {ex.Message}"); return 3; }
    using var stream = new NetworkStream(s, ownsSocket: false);
    using var reader = new StreamReader(stream, new UTF8Encoding(false));
    try { stream.Write(Encoding.UTF8.GetBytes(request + "\n")); }
    catch (IOException) { Console.Error.WriteLine("sankofad: daemon went away while sending"); return 4; }
    using var stdout = Console.OpenStandardOutput();
    if (follow)
    {
        string? l;
        while ((l = reader.ReadLine()) is not null) { stdout.Write(Encoding.UTF8.GetBytes(l + "\n")); stdout.Flush(); }
        return 0;
    }
    string? line;
    try { line = reader.ReadLine(); } catch (IOException) { line = null; }
    if (line is null) { Console.Error.WriteLine("sankofad: daemon closed the connection before answering"); return 4; }
    stdout.Write(Encoding.UTF8.GetBytes(line + "\n"));
    return line.StartsWith("{\"ok\":true", StringComparison.Ordinal) ? 0 : 1;
}

/// <summary>`sankofad mcp` becomes sankofa-mcp, `sankofad hub` sankofa-hub (execv: same process,
/// same stdio, no relay). They are separate binaries so the per-edit gate stays small and fast.</summary>
static partial class SiblingExec
{
    [System.Runtime.InteropServices.LibraryImport("libc", SetLastError = true, StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int execv(string path, string?[] argv);

    public static int Run(string name, string[] rest)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var self = Environment.ProcessPath is { } p ? new FileInfo(p).ResolveLinkTarget(true)?.FullName ?? p : "";
        var sibling = Path.Combine(Path.GetDirectoryName(self) ?? ".", exe);
        var target = File.Exists(sibling) ? sibling
                   : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
        if (target is null) { Console.Error.WriteLine($"sankofad: {name} not found next to sankofad or on PATH"); return 1; }
        if (OperatingSystem.IsWindows())
        {
            // no execv there: the sibling runs as a child with this process's stdio
            var psi = new ProcessStartInfo(target) { UseShellExecute = false };
            foreach (var a in rest) psi.ArgumentList.Add(a);
            using var child = Process.Start(psi);
            if (child is null) { Console.Error.WriteLine($"sankofad: could not run {target}"); return 1; }
            child.WaitForExit();
            return child.ExitCode;
        }
        execv(target, [target, .. rest, null]);
        Console.Error.WriteLine($"sankofad: could not run {target} (errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
        return 1;
    }
}

static class Bench
{
    /// <summary>Startup: exec to first answered ping, fresh database. Ask: persistent connection and one process per ask.</summary>
    public static int Run()
    {
        var self = Environment.ProcessPath!;
        var dir = Directory.CreateTempSubdirectory("sankofad-bench-").FullName;
        try
        {
            var sock = Path.Combine(dir, "s.sock");
            var startups = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                var db = Path.Combine(dir, $"start{i}.db");
                var sw = Stopwatch.StartNew();
                using var p = Process.Start(new ProcessStartInfo(self, ["serve", "--socket", sock, "--db", db]) { RedirectStandardError = true })!;
                while (!Ping(sock)) Thread.SpinWait(200);
                startups.Add(sw.Elapsed.TotalMilliseconds);
                p.Kill(); p.WaitForExit();
                File.Delete(sock);
            }

            var repo = Path.Combine(dir, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".sankofa"));
            File.WriteAllText(Path.Combine(repo, ".sankofa", "ledger.jsonl"), "");
            using var daemon = Process.Start(new ProcessStartInfo(self, ["serve", "--socket", sock, "--db", Path.Combine(dir, "ask.db")]) { RedirectStandardError = true })!;
            while (!Ping(sock)) Thread.SpinWait(200);
            var esc = JsonEncodedText.Encode(repo).ToString();
            Call(sock, $$"""{"verb":"claim","repo":"{{esc}}","glob":"src/**","session":"bench","ttl":3600}""");

            var inproc = new List<double>();
            using (var s = Connect(sock))
            using (var ns = new NetworkStream(s))
            using (var r = new StreamReader(ns))
            {
                var req = Encoding.UTF8.GetBytes($$"""{"verb":"ask","repo":"{{esc}}","path":"src/main.c"}""" + "\n");
                for (int i = 0; i < 1100; i++)
                {
                    var sw = Stopwatch.StartNew();
                    ns.Write(req);
                    r.ReadLine();
                    if (i >= 100) inproc.Add(sw.Elapsed.TotalMilliseconds);   // first 100 warm up
                }
            }
            var perProcess = new List<double>();
            for (int i = 0; i < 60; i++)
            {
                var sw = Stopwatch.StartNew();
                using var c = Process.Start(new ProcessStartInfo(self, ["send", "--socket", sock])
                    { RedirectStandardInput = true, RedirectStandardOutput = true })!;
                c.StandardInput.Write($$"""{"verb":"ask","repo":"{{esc}}","path":"src/main.c"}""");
                c.StandardInput.Close();
                c.StandardOutput.ReadToEnd();
                c.WaitForExit();
                if (i >= 10) perProcess.Add(sw.Elapsed.TotalMilliseconds);
            }
            // the pre-edit gate as Claude Code runs it: one process per edit, holder already has the lease
            var gate = new List<double>();
            var payload = "{\"session_id\":\"bench\",\"cwd\":\"" + esc + "\",\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\""
                        + JsonEncodedText.Encode(Path.Combine(repo, "src", "main.c")) + "\"}}";
            var genv = new Dictionary<string, string> { ["SANKOFA_SOCKET"] = sock };
            for (int i = 0; i < 60; i++)
            {
                var psi = new ProcessStartInfo(self, ["gate", "claude-code"]) { RedirectStandardInput = true, RedirectStandardOutput = true };
                foreach (var (k, v) in genv) psi.Environment[k] = v;
                var sw = Stopwatch.StartNew();
                using var c = Process.Start(psi)!;
                c.StandardInput.Write(payload);
                c.StandardInput.Close();
                c.StandardOutput.ReadToEnd();
                c.WaitForExit();
                if (i >= 10) gate.Add(sw.Elapsed.TotalMilliseconds);
            }
            daemon.Kill(); daemon.WaitForExit();
            Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{{\"startup_ms\":{{\"p50\":{0:F2},\"max\":{1:F2}}},\"ask_socket_ms\":{{\"p50\":{2:F3},\"p99\":{3:F3}}},\"ask_process_ms\":{{\"p50\":{4:F2},\"p99\":{5:F2}}},\"gate_process_ms\":{{\"p50\":{6:F2},\"p99\":{7:F2}}}}}",
                P(startups, 50), startups.Max(), P(inproc, 50), P(inproc, 99), P(perProcess, 50), P(perProcess, 99), P(gate, 50), P(gate, 99)));
            return 0;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static double P(List<double> xs, int pct)
    {
        var s = xs.Order().ToList();
        return s[Math.Min(s.Count - 1, (int)Math.Ceiling(pct / 100.0 * s.Count) - 1)];
    }

    static Socket Connect(string sock)
    {
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        s.Connect(new UnixDomainSocketEndPoint(sock));
        return s;
    }

    static string? Call(string sock, string json)
    {
        using var s = Connect(sock);
        using var ns = new NetworkStream(s);
        ns.Write(Encoding.UTF8.GetBytes(json + "\n"));
        return new StreamReader(ns).ReadLine();
    }

    static bool Ping(string sock)
    {
        try { return Call(sock, """{"verb":"ping"}""")?.StartsWith("{\"ok\":true", StringComparison.Ordinal) == true; }
        catch (SocketException) { return false; }
        catch (IOException) { return false; }
    }
}
