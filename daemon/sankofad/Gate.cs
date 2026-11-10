using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Sankofa;

/// <summary>
/// The gates. They live at the client edge and are the only enforcement; the daemon only
/// records and answers. Pre-edit: claim every path about to be edited; deny, naming the holder,
/// when someone else has it; when the daemon does not answer within 50 ms, allow and flag.
/// Pre-commit: refuse staged paths someone else holds; refuse when the daemon is unreachable.
/// </summary>
static class Gate
{
    const int TimeoutMs = 50;

    static bool Off(string? root) =>
        Environment.GetEnvironmentVariable("SANKOFA_GATE") == "0" || (root is not null && Defaults.Config(root, "gate") == "0");

    /// <summary>A short-lived connection with a hard deadline on every send and receive.</summary>
    sealed class Conn : IDisposable
    {
        readonly Socket s;
        readonly NetworkStream ns;
        readonly StreamReader r;

        Conn(Socket s) { this.s = s; ns = new NetworkStream(s); r = new StreamReader(ns, new UTF8Encoding(false)); }

        public static Conn? Open(string sock)
        {
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                { SendTimeout = TimeoutMs, ReceiveTimeout = TimeoutMs };
            try { s.Connect(Server.Endpoint(sock)); return new Conn(s); }
            catch (Exception e) when (e is SocketException or SankofaException) { s.Dispose(); return null; }
        }

        /// <summary>null when the daemon went quiet or away.</summary>
        public Response? Ask(Request req)
        {
            try
            {
                ns.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req, Json.Wire.Request) + "\n"));
                var line = r.ReadLine();
                return line is null ? null : JsonSerializer.Deserialize(line, Json.Wire.Response);
            }
            catch (Exception e) when (e is IOException or SocketException or JsonException) { return null; }
        }

        public void Dispose() { r.Dispose(); ns.Dispose(); s.Dispose(); }
    }

    // ------------------------------------------------------------------ pre-edit

    public static int PreEdit(string client)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(Console.In.ReadToEnd()).RootElement; }
        catch (JsonException) { Console.Error.WriteLine("sankofa gate: hook input is not JSON; allowing"); return 0; }
        var intent = Adapters.For(client, root);
        if (intent is null) return 0;

        var byRepo = intent.Paths
            .Select(p => (Path: p, Root: Defaults.FindRoot(Path.GetDirectoryName(p) ?? "/")))
            .Where(x => x.Root is not null && !Off(x.Root))
            .GroupBy(x => x.Root!);
        var denials = new List<string>();
        Conn? conn = null;
        bool reachable = true;
        try
        {
            foreach (var repo in byRepo)
            {
                long ttl = Defaults.TtlSeconds(Defaults.Config(repo.Key, "lease_ttl"), Store.DefaultTtlSeconds);
                long idle = Defaults.TtlSeconds(Defaults.Config(repo.Key, "handover_idle"), Store.DefaultHandoverIdleSeconds);
                foreach (var (path, _) in repo)
                {
                    var rel = Path.GetRelativePath(repo.Key, path);
                    if (reachable) conn ??= Conn.Open(Defaults.Socket());
                    var resp = reachable && conn is not null
                        ? conn.Ask(new Request
                        {
                            Verb = "claim", Repo = repo.Key, Glob = rel, Ttl = ttl,
                            Session = intent.Session, Agent = intent.Agent, Inherit = intent.Agent != "main", Idle = idle,
                        })
                        : null;
                    if (resp is null)
                    {
                        reachable = false;
                        Flag(repo.Key, $"edit of {rel} by {intent.Session} [{intent.Agent}] not leased: sankofad unreachable",
                             intent.Session, intent.Agent);
                        continue;
                    }
                    if (!resp.Ok) { Console.Error.WriteLine($"sankofa gate: {resp.Error}; allowing {rel}"); continue; }
                    if (resp.Granted == false)
                        denials.AddRange((resp.Conflicts ?? []).Select(c =>
                            $"{rel} is leased to {c.Session} [{c.Agent}] as {c.Id} ({c.Glob}) until {c.Expires}"));
                }
            }
        }
        finally { conn?.Dispose(); }

        if (denials.Count == 0) return 0;
        var reason = "sankofa: " + string.Join("; ", denials) +
            ". Another session or subagent is editing this; do not overwrite it. Work on something else, " +
            "wait for it to finish (sankofa ask <path>), or ask the human.";
        Console.Out.Write("{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\"," +
                          "\"permissionDecisionReason\":\"" + JsonEncodedText.Encode(reason, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"}}\n");
        return 0;
    }

    /// <summary>
    /// Record an unleased edit in the repo's ledger file the way the CLI fallback does; the
    /// daemon imports it when it is back. Once per text, so a burst of edits is one flag.
    /// </summary>
    static void Flag(string root, string text, string session, string agent)
    {
        try
        {
            var path = Path.Combine(root, ".sankofa", "ledger.jsonl");
            using var held = LedgerLock.Acquire(Path.Combine(root, ".sankofa"));
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
            var tail = Encoding.UTF8.GetString(bytes, Math.Max(0, bytes.Length - 65536), Math.Min(bytes.Length, 65536));
            var e = new Entry
            {
                Ts = Store.Ts(DateTimeOffset.UtcNow), Type = "flag", Session = session, Agent = agent, Text = text,
                Nonce = $"{DateTime.UtcNow.Ticks}-{Environment.ProcessId}",
            };
            if (tail.Contains("\"text\":" + JsonSerializer.Serialize(text, Json.Wire.String), StringComparison.Ordinal)) return;
            e.Id = $"e{bytes.Count(b => b == (byte)'\n') + 1}";
            using var f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            f.Write(Encoding.UTF8.GetBytes(Json.Line(e) + "\n"));
        }
        catch (IOException ex) { Console.Error.WriteLine($"sankofa gate: could not flag: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ pre-commit

    public static int PreCommit()
    {
        var top = Git("rev-parse", "--show-toplevel")?.Trim();
        if (string.IsNullOrEmpty(top)) return 0;
        var root = Defaults.FindRoot(top);
        if (root is null || Off(root)) return 0;
        var staged = (Git("diff", "--cached", "--name-only", "-z") ?? "")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.GetRelativePath(root, Path.Combine(top, p)))
            .Where(p => !p.StartsWith("..", StringComparison.Ordinal) && !p.StartsWith(".sankofa/", StringComparison.Ordinal))
            .ToList();
        if (staged.Count == 0) return 0;
        var session = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } s ? s
                    : Environment.GetEnvironmentVariable("SANKOFA_SESSION") is { Length: > 0 } s2 ? s2 : "-";
        var agent = Environment.GetEnvironmentVariable("SANKOFA_AGENT") is { Length: > 0 } a ? a : "main";
        var sock = Defaults.Socket();

        Response? resp;
        using (var conn = Conn.Open(sock))
            resp = conn?.Ask(new Request { Verb = "check", Repo = root, Paths = staged, Session = session, Agent = agent });
        if (resp is null)
        {
            Console.Error.WriteLine($"sankofa: sankofad is not answering on {sock}, and commits are gated on it.");
            Console.Error.WriteLine("Start it (systemctl --user start sankofad), or commit anyway with SANKOFA_GATE=0 or git commit --no-verify.");
            return 1;
        }
        if (!resp.Ok) { Console.Error.WriteLine($"sankofa: {resp.Error}"); return 1; }
        var hits = resp.Hits ?? [];
        if (hits.Count == 0) return 0;
        Console.Error.WriteLine("sankofa: this commit touches paths another session or subagent holds:");
        foreach (var h in hits)
            Console.Error.WriteLine($"  {h.Path}  leased to {h.Lease.Session} [{h.Lease.Agent}] as {h.Lease.Id} ({h.Lease.Glob}) until {h.Lease.Expires}");
        Console.Error.WriteLine("Wait for the holder to finish (sankofa done <id> / release <id>), unstage those paths,");
        Console.Error.WriteLine("or commit anyway with SANKOFA_GATE=0 or git commit --no-verify.");
        return 1;
    }

    static string? Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
