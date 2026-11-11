using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sankofa;

/// <summary>
/// Device tokens for a hub: a file of "&lt;sha256 of the token, hex&gt; &lt;device&gt;" lines (only
/// hashes are kept), read again when it changes. A device is a machine (desktop, laptop, a cloud
/// environment), not a person: sankofa is one technician, many sessions.
/// </summary>
public sealed class Tokens(string path)
{
    readonly Lock gate = new();
    DateTime loaded = DateTime.MinValue;
    Dictionary<string, string> byHash = [];

    public string Path => path;

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewToken() =>
        "sk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The device a token belongs to, or null.</summary>
    public string? Device(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (gate)
        {
            var mtime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (mtime != loaded)
            {
                loaded = mtime;
                byHash = File.Exists(path)
                    ? File.ReadAllLines(path).Select(l => l.Split(' ', 2, StringSplitOptions.TrimEntries))
                          .Where(p => p.Length == 2 && p[0].Length == 64 && !p[0].StartsWith('#'))
                          .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Last()[1])
                    : [];
            }
            var h = Hash(token);
            foreach (var (k, d) in byHash)
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(k), Encoding.ASCII.GetBytes(h))) return d;
            return null;
        }
    }

    public List<string> Devices()
    {
        Device("-");   // load
        lock (gate) return byHash.Values.Distinct().Order().ToList();
    }

    /// <summary>Make a token for a device; the token is shown once, only its hash is kept.</summary>
    public string Add(string device)
    {
        if (!Regex.IsMatch(device, "^[A-Za-z0-9._-]{1,64}$")) throw new SankofaException("device names are letters, digits, . _ - (up to 64)");
        var t = NewToken();
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        lock (gate)
        {
            File.AppendAllText(path, $"{Hash(t)} {device}\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return t;
    }

    /// <summary>Remove every token of a device. Returns how many went.</summary>
    public int Revoke(string device)
    {
        lock (gate)
        {
            if (!File.Exists(path)) return 0;
            var lines = File.ReadAllLines(path);
            var keep = lines.Where(l => l.Split(' ', 2, StringSplitOptions.TrimEntries) is var p && !(p.Length == 2 && p[1] == device)).ToList();
            File.WriteAllLines(path + ".tmp", keep);
            File.SetUnixFileMode(path + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(path + ".tmp", path, overwrite: true);
            return lines.Length - keep.Count;
        }
    }
}

/// <summary>
/// The hub: one authoritative ledger for many devices, the same store behind an HTTP listener.
/// Meant for an always-on box on a private network (Tailscale, WireGuard); it speaks plain HTTP
/// and relies on that network for encryption. Every request carries a device token.
///
///   POST /v1/rpc      the socket protocol's request and response, as JSON; with "since", the
///                     answer also carries the repo's events after it ("tail")
///   GET  /v1/events   ?repo=&amp;since=&amp;follow=1: events as JSON lines, live with follow
///   POST /mcp         MCP streamable HTTP (JSON answers), for clients that cannot run sankofad
///   GET  /healthz     no token needed
///
/// Repos are named, not pathed: devices keep the same repo in different places, so each names
/// it (by default after its git remote, like github.com/you/repo). The hub keeps each repo's
/// ledger under its data directory, mirrored to JSONL like any other.
/// </summary>
public sealed partial class Hub
{
    static readonly HashSet<string> Verbs =
        ["ping", "append", "done", "claim", "release", "ask", "check", "finish", "leases", "open", "last", "events", "sync", "import", "record"];

    readonly Store store;
    readonly Server server;
    readonly Tokens tokens;
    readonly string reposDir;
    readonly HttpListener listener = new();
    readonly HubMcp mcp;
    readonly TextWriter log;

    public Hub(Store store, Server server, Tokens tokens, string dataDir, IEnumerable<string> prefixes, TextWriter? log = null)
    {
        this.store = store; this.server = server; this.tokens = tokens;
        this.log = log ?? Console.Error;
        reposDir = System.IO.Path.Combine(dataDir, "repos");
        Directory.CreateDirectory(reposDir);
        foreach (var p in prefixes) listener.Prefixes.Add(p.EndsWith('/') ? p : p + "/");
        mcp = new HubMcp(this);
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._-]*(/[A-Za-z0-9_][A-Za-z0-9._-]*)*$")]
    private static partial Regex RepoIdRx();

    /// <summary>The hub-side directory of a named repo, made on first use.</summary>
    public string RepoDir(string id)
    {
        if (id.Length > 200 || !RepoIdRx().IsMatch(id) || id.Split('/').Any(s => s is "." or ".."))
            throw new SankofaException($"bad repo name '{id}' (use letters, digits, . _ - and /, like github.com/you/repo)");
        var dir = System.IO.Path.Combine(reposDir, id);
        var s = System.IO.Path.Combine(dir, ".sankofa");
        if (!Directory.Exists(s)) { Directory.CreateDirectory(s); File.AppendAllText(System.IO.Path.Combine(s, "ledger.jsonl"), ""); }
        return dir;
    }

    string? RepoName(string? path) =>
        path is null ? null : System.IO.Path.GetRelativePath(reposDir, path) is var r && !r.StartsWith("..", StringComparison.Ordinal) ? r : path;

    /// <summary>An empty line on every quiet event stream this often.</summary>
    public TimeSpan Keepalive { get; init; } = TimeSpan.FromSeconds(15);

    public void Start() => listener.Start();

    public async Task RunAsync(CancellationToken ct)
    {
        if (!listener.IsListening) listener.Start();
        using var reg = ct.Register(() => { try { listener.Stop(); } catch (ObjectDisposedException) { } });
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (HttpListenerException) { break; }
            _ = Task.Run(() => HandleAsync(ctx, ct), ct);
        }
        try { listener.Close(); } catch (ObjectDisposedException) { }
    }

    internal void Log(string line) { lock (log) { log.WriteLine($"sankofa hub: {line}"); log.Flush(); } }

    async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            var path = req.Url?.AbsolutePath ?? "/";
            if (path == "/healthz") { await Reply(res, 200, "text/plain", $"sankofa hub {Store.Version}\n"); return; }
            var auth = req.Headers["Authorization"];
            var token = auth is not null && auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..].Trim() : null;
            var device = tokens.Device(token);
            if (device is null)
            {
                Log($"rejected {req.RemoteEndPoint} {req.HttpMethod} {path}: {(token is null ? "no token" : "unknown token")}");
                res.Headers["WWW-Authenticate"] = "Bearer";
                await Reply(res, 401, "application/json", """{"ok":false,"error":"a valid device token is required"}""");
                return;
            }
            switch (path)
            {
                case "/v1/rpc" when req.HttpMethod == "POST":
                {
                    string body;
                    using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync(ct);
                    Request? rq = null;
                    try { rq = JsonSerializer.Deserialize(body, Json.Wire.Request); } catch (JsonException) { }
                    var resp = rq is null ? Response.Fail("request is not JSON") : Rpc(rq, device);
                    await Reply(res, 200, "application/json", JsonSerializer.Serialize(resp, Json.Wire.Response));
                    return;
                }
                case "/v1/events" when req.HttpMethod == "GET":
                    await Events(ctx, device, ct);
                    return;
                case "/mcp":
                    await mcp.HandleAsync(ctx, device, ct);
                    return;
                default:
                    await Reply(res, 404, "application/json", """{"ok":false,"error":"not found"}""");
                    return;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"internal error: {ex.Message}");
            try { await Reply(res, 500, "application/json", """{"ok":false,"error":"internal error"}"""); } catch (Exception) { }
        }
        finally { try { res.Close(); } catch (Exception) { } }
    }

    /// <summary>One request, as the named device. Public for tests and the MCP tools.</summary>
    public Response Rpc(Request rq, string device)
    {
        if (!Verbs.Contains(rq.Verb)) return Response.Fail($"verb '{rq.Verb}' is not served by a hub");
        try
        {
            if (rq.Repo is not null) rq.Repo = RepoDir(rq.Repo);
            else if (rq.Verb is not ("ping" or "finish" or "events")) return Response.Fail("repo is required (the hub name of the repo)");
        }
        catch (SankofaException ex) { return Response.Fail(ex.Message); }
        if (rq.Verb == "import")
            foreach (var e in rq.Entries ?? []) e.Device = device;
        // an id the device made up while offline, used after it was renumbered
        if (rq.Ref is { Length: > 1 } r && r[0] == 'q' && rq.Repo is not null && store.ImportedId(rq.Repo, device, r) is { } mapped)
            rq.Ref = mapped;
        Response resp;
        using (Store.Stamp(device, rq.Client)) resp = server.Dispatch(rq);
        resp.Device = device;
        if (resp.Ok && rq.Since is { } since && rq.Repo is not null && rq.Verb != "events")
        {
            var (tail, more) = store.Tail(rq.Repo, since);
            resp.Tail = tail; resp.More = more ? true : null;
        }
        // entries keep the hub-side repo path: devices go by the repo name they asked with
        return resp;
    }

    async Task Events(HttpListenerContext ctx, string device, CancellationToken ct)
    {
        var q = ctx.Request.QueryString;
        string? repo;
        try { repo = q["repo"] is { Length: > 0 } id ? RepoDir(id) : null; }
        catch (SankofaException ex) { await Reply(ctx.Response, 400, "application/json", JsonSerializer.Serialize(Response.Fail(ex.Message), Json.Wire.Response)); return; }
        long? since = long.TryParse(q["since"], out var s) ? s : null;
        var res = ctx.Response;
        res.StatusCode = 200;
        res.ContentType = "application/x-ndjson";
        res.SendChunked = true;
        await using var w = new StreamWriter(res.OutputStream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
        // send the headers now (an empty line, which readers skip): with nothing to replay a follower
        // would otherwise see no answer at all until the next event
        await w.WriteLineAsync();
        await w.FlushAsync(ct);
        if (q["follow"] is "1" or "true")
        {
            await server.FollowAsync(new Request { Verb = "events", Repo = repo, Since = since ?? 0, Follow = true }, w, ct, Keepalive);
            return;
        }
        foreach (var e in store.EventsSince(since ?? 0, repo))
        {
            e.Repo = RepoName(e.Repo);
            await w.WriteLineAsync(JsonSerializer.Serialize(e, Json.Wire.Entry));
        }
        await w.FlushAsync(ct);
    }

    static async Task Reply(HttpListenerResponse res, int code, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = code;
        res.ContentType = type;
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }
}
