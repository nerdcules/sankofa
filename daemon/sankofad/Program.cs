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
//   sankofad bench                         startup and ask round-trip timings, as JSON
//   sankofad version

static string Opt(string[] args, string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

static string DefaultSocket()
{
    var env = Environment.GetEnvironmentVariable("SANKOFA_SOCKET");
    if (!string.IsNullOrEmpty(env)) return env;
    var run = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
    return string.IsNullOrEmpty(run) ? $"/tmp/sankofa-{Environment.UserName}/sankofad.sock" : Path.Combine(run, "sankofa", "sankofad.sock");
}

static string DefaultDb()
{
    var env = Environment.GetEnvironmentVariable("SANKOFA_DB");
    if (!string.IsNullOrEmpty(env)) return env;
    var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    if (string.IsNullOrEmpty(data)) data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    return Path.Combine(data, "sankofa", "sankofa.db");
}

var cmd = args.Length > 0 ? args[0] : "help";
switch (cmd)
{
    case "serve": return Serve(Opt(args, "--socket", DefaultSocket()), Opt(args, "--db", DefaultDb()));
    case "send": return Send(Opt(args, "--socket", DefaultSocket()));
    case "bench": return Bench.Run();
    case "version" or "--version" or "-V": Console.WriteLine($"sankofad {Store.Version}"); return 0;
    default:
        Console.Error.WriteLine("usage: sankofad serve [--socket P] [--db P] | send [--socket P] | bench | version");
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
            daemon.Kill(); daemon.WaitForExit();
            Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{{\"startup_ms\":{{\"p50\":{0:F2},\"max\":{1:F2}}},\"ask_socket_ms\":{{\"p50\":{2:F3},\"p99\":{3:F3}}},\"ask_process_ms\":{{\"p50\":{4:F2},\"p99\":{5:F2}}}}}",
                P(startups, 50), startups.Max(), P(inproc, 50), P(inproc, 99), P(perProcess, 50), P(perProcess, 99)));
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
