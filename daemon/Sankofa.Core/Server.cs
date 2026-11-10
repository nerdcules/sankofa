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
        try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (UnauthorizedAccessException) { }
        if (File.Exists(socketPath))
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { probe.Connect(Endpoint(socketPath)); throw new SankofaException($"another sankofad is listening on {socketPath}"); }
            catch (SocketException) { File.Delete(socketPath); }
        }
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        s.Bind(Endpoint(socketPath));
        File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

    async Task FollowAsync(Request req, StreamWriter w, CancellationToken ct)
    {
        var ch = Channel.CreateUnbounded<Entry>(new UnboundedChannelOptions { SingleReader = true });
        string? repo = req.Repo is null ? null : Store.RepoKey(req.Repo);
        followers[ch] = repo;   // register first so nothing falls between backlog and live
        try
        {
            long last = req.Since ?? long.MaxValue;
            if (req.Since is { } since)
                foreach (var e in store.EventsSince(since, repo))
                {
                    await w.WriteLineAsync(JsonSerializer.Serialize(e, Json.Wire.Entry));
                    last = e.Seq ?? last;
                }
            await w.FlushAsync();
            await foreach (var e in ch.Reader.ReadAllAsync(ct))
            {
                if (req.Since is not null && e.Seq <= last) continue;
                await w.WriteLineAsync(JsonSerializer.Serialize(e, Json.Wire.Entry));
                await w.FlushAsync();
            }
        }
        finally { followers.TryRemove(ch, out _); }
    }

    public Response Dispatch(Request r)
    {
        try
        {
            string Repo() => r.Repo ?? throw new SankofaException("repo is required");
            switch (r.Verb)
            {
                case "ping":
                    return new Response { Version = Store.Version };
                case "append":
                {
                    var e = store.Append(Repo(), r.Type ?? "", r.Text ?? "", r.Session, r.Agent, r.Ref, r.Wt, r.Nonce);
                    return new Response { Id = e.Id, Entry = e };
                }
                case "done":
                {
                    var e = store.Done(Repo(), r.Ref ?? throw new SankofaException("done needs an id"), r.Text, r.Session, r.Agent, r.Wt, r.Nonce);
                    return new Response { Id = e.Id, Entry = e };
                }
                case "claim":
                {
                    var c = store.Claim(Repo(), r.Glob ?? throw new SankofaException("claim needs a glob"), r.Session, r.Agent, r.Ttl, r.Wt);
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
                default:
                    return Response.Fail($"unknown verb '{r.Verb}'");
            }
        }
        catch (SankofaException ex) { return Response.Fail(ex.Message); }
        catch (Exception ex) { return Response.Fail($"internal error: {ex.Message}"); }
    }
}
