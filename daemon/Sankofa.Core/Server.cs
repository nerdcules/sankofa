using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Sankofa;

/// <summary>
/// Newline-delimited JSON over a Unix socket. One request per line, one response per line;
/// a connection may carry many requests. <c>events</c> with <c>follow</c> streams entries
/// (one JSON object per line) until the client goes away.
/// </summary>
public sealed class Server
{
    readonly Store store;
    readonly string socketPath;
    readonly ConcurrentDictionary<Channel<Entry>, string?> followers = new();

    /// <summary>The overlap watcher and the remote bridge, when this server runs them (sankofad serve).</summary>
    public Overlaps? Overlaps { get; set; }
    /// <summary>0.7: this device's link to a hub; repos attached to it are served from there.</summary>
    public HubLink? Link { get; set; }
    static readonly HashSet<string> HubVerbs =
        ["append", "done", "claim", "release", "ask", "check", "finish", "leases", "open", "last", "events", "sync", "record"];
    public Bridge? Bridge { get; set; }
    /// <summary>Clears dirty marks git can vouch for (0.8.1).</summary>
    public Settle? Settle { get; set; }
    Overlaps? passive;
    Overlaps Trees => Overlaps ?? (passive ??= new Overlaps(store, useInotify: false));

    public Server(Store store, string socketPath)
    {
        this.store = store;
        this.socketPath = socketPath;
        store.Appended += e =>
        {
            foreach (var (ch, repo) in followers)
                if (repo is null || repo == e.Repo) ch.Writer.TryWrite(e);
        };
    }

    /// <summary>Bind the socket. Fails if another daemon already answers on it.</summary>
    /// <summary>Unix socket paths are limited to 107 bytes on Linux.</summary>
    public static UnixDomainSocketEndPoint Endpoint(string path) =>
        System.Text.Encoding.UTF8.GetByteCount(path) < 108
            ? new UnixDomainSocketEndPoint(path)
            : throw new SankofaException($"socket path is longer than 107 bytes: {path} (set SANKOFA_SOCKET to a shorter path)");

    public Socket Bind()
    {
        var dir = Path.GetDirectoryName(socketPath)!;
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (UnauthorizedAccessException)
            {
                // someone else owns the directory (a /tmp squat on a shared host): they could swap the
                // socket for their own, so refuse rather than bind into it
                throw new SankofaException($"{dir} is not owned by this user; refusing to put the socket there (set SANKOFA_SOCKET or XDG_RUNTIME_DIR)");
            }
        }
        if (File.Exists(socketPath))
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { probe.Connect(Endpoint(socketPath)); throw new SankofaException($"another sankofad is listening on {socketPath}"); }
            catch (SocketException) { File.Delete(socketPath); }
        }
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        s.Bind(Endpoint(socketPath));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        s.Listen(128);
        return s;
    }

    public async Task RunAsync(Socket listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(ct);
                _ = Task.Run(() => HandleAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Dispose();
            try { File.Delete(socketPath); } catch (IOException) { }
        }
    }

    async Task HandleAsync(Socket client, CancellationToken ct)
    {
        using var stream = new NetworkStream(client, ownsSocket: true);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Request? req = null;
                try { req = JsonSerializer.Deserialize(line, Json.Wire.Request); } catch (JsonException) { }
                if (req is null)
                {
                    await Send(writer, Response.Fail("request is not JSON"));
                    continue;
                }
                if (req.Verb == "events" && req.Follow)
                {
                    await FollowAsync(req, writer, ct);
                    return;
                }
                await Send(writer, Dispatch(req));
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }

    static async Task Send(StreamWriter w, Response r)
    {
        await w.WriteLineAsync(JsonSerializer.Serialize(r, Json.Wire.Response));
        await w.FlushAsync();
    }

    public async Task FollowAsync(Request req, StreamWriter w, CancellationToken ct, TimeSpan? heartbeat = null)
    {
        var ch = Channel.CreateUnbounded<Entry>(new UnboundedChannelOptions { SingleReader = true });
        string? repo = req.Repo is null ? null : Store.RepoKey(req.Repo);
        followers[ch] = repo;   // register first so nothing falls between backlog and live
        try
        {
            long last = req.Since ?? long.MaxValue;
            if (req.Since is { } since)
            {
                // the whole backlog, page by page: one page and then the live channel would leave a
                // hole in a replica that was away for longer than a page
                while (true)
                {
                    var page = store.EventsSince(since, repo, Store.EventPage);
                    foreach (var e in page)
                    {
                        await w.WriteLineAsync(JsonSerializer.Serialize(e, Json.Wire.Entry));
                        last = e.Seq ?? last;
                        since = e.Seq ?? since;
                    }
                    if (page.Count < Store.EventPage) break;
                }
            }
            await w.FlushAsync();
            while (true)
            {
                Entry e;
                if (heartbeat is { } hb)
                {
                    // an empty line now and then, so a client can tell a quiet stream from a dead network
                    using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    t.CancelAfter(hb);
                    try { e = await ch.Reader.ReadAsync(t.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await w.WriteLineAsync(); await w.FlushAsync();
                        continue;
                    }
                }
                else e = await ch.Reader.ReadAsync(ct);
                if (req.Since is not null && e.Seq <= last) continue;
                await w.WriteLineAsync(JsonSerializer.Serialize(e, Json.Wire.Entry));
                await w.FlushAsync();
            }
        }
        finally { followers.TryRemove(ch, out _); }
    }

    /// <summary>The top of the working tree a path is in.</summary>
    static string TopLevel(string path)
    {
        if (!Directory.Exists(path)) throw new SankofaException($"no such directory: {path}");
        var t = Git.Run(path, "rev-parse", "--show-toplevel");
        return t.Ok ? t.Out.Trim() : throw new SankofaException($"{path} is not in a git working tree");
    }

    static string TopLevelOrSelf(string path) =>
        Directory.Exists(path) && Git.Run(path, "rev-parse", "--show-toplevel") is { Ok: true } t ? t.Out.Trim() : path;

    public Response Dispatch(Request r)
    {
        try
        {
            string Repo() => r.Repo ?? throw new SankofaException("repo is required");
            if (Link is { } link)
            {
                if (r.Repo is not null && HubVerbs.Contains(r.Verb) && link.Covers(Store.RepoKey(r.Repo))) return link.Handle(r);
                switch (r.Verb)
                {
                    case "hub-attach": return link.Attach(Repo());
                    case "hub-detach": return link.Detach(Repo());
                    case "hub-status": return link.Status(Repo());
                    case "finish" when r.Repo is null: link.FinishEverywhere(r); break;
                }
            }
            switch (r.Verb)
            {
                case "ping":
                    return new Response { Version = Store.Version };
                case "append":
                {
                    var e = store.Append(Repo(), r.Type ?? "", r.Text ?? "", r.Session, r.Agent, r.Ref, r.Wt, r.Nonce, r.Branch, r.Sha, r.Letter);
                    return new Response { Id = e.Id, Entry = e };
                }
                case "done":
                {
                    var e = store.Done(Repo(), r.Ref ?? throw new SankofaException("done needs an id"), r.Text, r.Session, r.Agent, r.Wt, r.Nonce);
                    return new Response { Id = e.Id, Entry = e };
                }
                case "claim":
                {
                    var c = store.Claim(Repo(), r.Glob ?? throw new SankofaException("claim needs a glob"), r.Session, r.Agent, r.Ttl, r.Wt, r.Inherit, r.Idle);
                    return new Response
                    {
                        Id = c.Event.Id, Granted = c.Granted, Renewed = c.Renewed, Lease = c.Lease,
                        Conflicts = c.Conflicts.Count > 0 ? c.Conflicts : null, Warnings = c.Warnings.Count > 0 ? c.Warnings : null,
                    };
                }
                case "release":
                {
                    var e = store.Release(Repo(), r.Ref ?? throw new SankofaException("release needs an id"), r.Session, r.Agent);
                    return new Response { Id = e.Id, Entry = e };
                }
                case "ask":
                {
                    var a = store.Ask(Repo(), r.Path ?? throw new SankofaException("ask needs a path"));
                    return new Response
                    {
                        State = a.State, Lease = a.Lease, Conflicts = a.Holders.Count > 1 ? a.Holders : null,
                        Warnings = a.Dirty.Count > 0 ? a.Dirty : null,
                    };
                }
                case "check":
                    return new Response
                    {
                        Hits = store.Check(Repo(), r.Paths ?? [], r.Session, r.Agent)
                            .Select(h => new Hit { Path = h.Path, Lease = h.Lease }).ToList(),
                    };
                case "finish":
                {
                    var done = store.Finish(r.Repo, r.Session ?? throw new SankofaException("finish needs a session"),
                                            r.Agent, string.IsNullOrWhiteSpace(r.Text) ? "finished" : r.Text!);
                    return new Response { Entries = done };
                }
                case "leases":
                    return new Response { Leases = store.Leases(Repo()) };
                case "open":
                    return new Response { Entries = store.Open(Repo()) };
                case "last":
                    return new Response { Entries = store.Last(Repo(), r.N ?? 5) };
                case "events":
                    return new Response { Entries = store.EventsSince(r.Since ?? 0, r.Repo) };
                case "sync":
                    store.Sync(Repo());
                    return new Response();
                case "trees":
                    return new Response { Trees = Trees.Status(Repo()) };
                case "trees-add":
                {
                    var home = Repo();
                    if (r.Paths is not { Count: > 0 }) store.TreeAdd(home, Store.AutoTrees);
                    else foreach (var p in r.Paths) store.TreeAdd(home, TopLevel(p));
                    Overlaps?.Refresh();
                    return new Response { Trees = Trees.Status(home) };
                }
                case "trees-rm":
                {
                    var home = Repo();
                    int n = 0;
                    if (r.Paths is not { Count: > 0 }) n = store.TreeRemove(home, null);
                    else foreach (var p in r.Paths) n += store.TreeRemove(home, p == Store.AutoTrees ? p : TopLevelOrSelf(p));
                    if (n == 0) throw new SankofaException("no such tree registered");
                    Overlaps?.Refresh();
                    return new Response();
                }
                case "import":
                {
                    var (map, written) = store.Import(Repo(), r.Entries ?? []);
                    return new Response { Map = map, Entries = written };
                }
                case "record":
                {
                    var e = store.Record(Repo(), r.Entry ?? throw new SankofaException("record needs an entry"));
                    return new Response { Id = e.Id, Entry = e };
                }
                case "settle":
                {
                    var s = Settle ?? new Settle(store, Dispatch, Link);
                    return new Response { Entries = r.Repo is null ? s.All() : s.Repo(r.Repo) };
                }
                case "rescan":
                    return new Response { Entries = Trees.Scan(Repo()) };
                case "bridge":
                {
                    var repo = Repo();
                    if (r.On == false)
                    {
                        if (!store.BridgeOff(repo)) throw new SankofaException("the bridge is not on for this repo");
                        return new Response { State = "off" };
                    }
                    if (r.On == true)
                    {
                        if (!Git.Run(repo, "rev-parse", "--git-dir").Ok) throw new SankofaException($"{repo} is not a git repository");
                        store.BridgeOn(repo, r.Interval);
                        (Bridge ?? new Bridge(store)).Poll(repo);   // the baseline, so the first real fetch can report
                    }
                    var b = store.Bridge(repo);
                    return new Response { State = b is null ? "off" : "on", Bridge = b };
                }
                case "fetch":
                {
                    var repo = Repo();
                    if (store.Bridge(repo) is null) throw new SankofaException("the bridge is not on for this repo (sankofa bridge on)");
                    var landed = (Bridge ?? new Bridge(store)).Poll(repo);   // waits for a poll already in flight
                    return new Response { Entries = landed, Bridge = store.Bridge(repo) };
                }
                default:
                    return Response.Fail($"unknown verb '{r.Verb}'");
            }
        }
        catch (SankofaException ex) { return Response.Fail(ex.Message); }
        catch (Exception ex) { return Response.Fail($"internal error: {ex.Message}"); }
    }
}
