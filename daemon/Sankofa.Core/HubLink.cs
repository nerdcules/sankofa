using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Sankofa;

/// <summary>key=value files: a repo's .sankofa/config, the device's hub.conf. Last line wins.</summary>
public static class KeyValues
{
    public static string? Get(string path, string key)
    {
        if (!File.Exists(path)) return null;
        string? v = null;
        foreach (var line in File.ReadLines(path))
            if (line.StartsWith(key + "=", StringComparison.Ordinal)) v = line[(key.Length + 1)..].Trim();
        return v;
    }
}

/// <summary>
/// A device's side of a hub. For a repo whose .sankofa/config names it on the hub (hub_repo),
/// sankofad stops being that repo's authority: every verb goes to the hub, and the repo's
/// .sankofa/ledger.jsonl becomes a replica of the hub's ledger, kept current by a live event
/// stream, so the Stop hook, the watcher and `sankofa last` read it as before.
///
/// When the hub cannot be reached, writes are allowed and flagged locally (design rule 4): they
/// are appended to the replica after the part that came from the hub, with ids of their own
/// (q...), as are writes the CLI and the gate make when sankofad itself is down. That tail is the
/// queue. When the hub answers again the queue is sent in order; the hub applies it (recording
/// contention for claims another device held meanwhile), and the replica's tail is replaced by
/// the hub's copy of those events. Only the replica is rewritten, never the hub's ledger.
/// Commits stay gated: with the hub unreachable, the commit check fails.
/// </summary>
public sealed class HubLink : IDisposable
{
    public sealed record Conf(string Url, string Token, string Device);

    readonly Store store;
    readonly string confPath;
    readonly Lock gate = new();
    readonly ConcurrentDictionary<string, Rep> reps = new();
    readonly CancellationTokenSource stop = new();
    readonly MiniHttp http;
    /// <summary>The edit gate's own connection, so a claim never waits behind a slow write.</summary>
    readonly MiniHttp fast;
    readonly MiniHttp streamer;
    Conf? conf;
    DateTime confTime = DateTime.MinValue;
    readonly ConcurrentDictionary<string, (DateTime Mtime, string? Id)> repoIds = new();
    DateTimeOffset offlineUntil = DateTimeOffset.MinValue;
    long qCounter;

    /// <summary>A write waits this long for the hub before it is queued locally.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>The pre-edit gate's claims wait only this long (the gate itself waits a little longer).</summary>
    public TimeSpan GateTimeout { get; init; } = TimeSpan.FromMilliseconds(150);
    /// <summary>After a failed call, go straight to the queue for this long (the stream keeps probing).</summary>
    public TimeSpan Breaker { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>The event stream reconnects with backoff up to this.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>The hub sends a keepalive line every 15 s; a stream silent for this long is dead
    /// (a network that drops packets without resetting the connection).</summary>
    public TimeSpan StreamSilence { get; init; } = TimeSpan.FromSeconds(45);

    sealed class Rep(string root, string hubRepo)
    {
        public readonly string Root = root;
        public string HubRepo = hubRepo;
        public long Cursor;
        /// <summary>Bytes of the replica that came from the hub; anything after is the queue.</summary>
        public long Synced;
        public readonly Lock L = new();
        public Task? Pull;
        public bool Streaming;
        public string? LastError;
        public string Ledger => Path.Combine(Root, ".sankofa", "ledger.jsonl");
        public string Dir => Path.Combine(Root, ".sankofa");
    }

    public HubLink(Store store, string confPath)
    {
        this.store = store;
        this.confPath = confPath;
        http = new MiniHttp(TimeSpan.FromSeconds(2));
        fast = new MiniHttp(TimeSpan.FromSeconds(2));
        streamer = new MiniHttp(TimeSpan.FromSeconds(5));
        store.IsReplica = Covers;
        store.RecordElsewhere = (repo, e) => Record(repo, e);
    }

    public Conf? Config()
    {
        lock (gate)
        {
            var t = File.Exists(confPath) ? File.GetLastWriteTimeUtc(confPath) : DateTime.MinValue;
            if (t != confTime)
            {
                confTime = t;
                var url = KeyValues.Get(confPath, "url");
                var token = KeyValues.Get(confPath, "token");
                conf = string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token) ? null
                     : new Conf(url.TrimEnd('/'), token, KeyValues.Get(confPath, "device") is { Length: > 0 } d ? d : Environment.MachineName);
            }
            return conf;
        }
    }

    /// <summary>The hub name of a repo, when this device has a hub and the repo is attached to it.</summary>
    public string? HubRepo(string root)
    {
        root = Store.RepoKey(root);
        var cfg = Path.Combine(root, ".sankofa", "config");
        var t = File.Exists(cfg) ? File.GetLastWriteTimeUtc(cfg) : DateTime.MinValue;
        if (repoIds.TryGetValue(root, out var c) && c.Mtime == t) return c.Id;
        var id = KeyValues.Get(cfg, "hub_repo") is { Length: > 0 } v ? v : null;
        repoIds[root] = (t, id);
        return id;
    }

    public bool Covers(string root) => Config() is not null && HubRepo(root) is not null && store.Replica(root) is not null;

    public bool Online => DateTimeOffset.UtcNow >= offlineUntil;

    /// <summary>This device's name on the hub (hub.conf), or null without a hub.</summary>
    public string? DeviceName => Config()?.Device;

    public void Start()
    {
        foreach (var r in store.Replicas()) Get(r.Repo);
    }

    Rep Get(string root)
    {
        root = Store.RepoKey(root);
        var rep = reps.GetOrAdd(root, r =>
        {
            var st = store.Replica(r) ?? throw new SankofaException($"{r} is not attached to the hub (sankofa hub on)");
            return new Rep(r, st.HubRepo) { Cursor = st.Cursor, Synced = st.Synced };
        });
        if (HubRepo(root) is { } id && id != rep.HubRepo) throw new SankofaException($"hub_repo changed to {id}; run sankofa hub off, then on");
        lock (rep.L) rep.Pull ??= Task.Run(() => PullLoop(rep));
        return rep;
    }

    void Save(Rep r) => store.ReplicaSave(new Store.ReplicaState(r.Root, r.HubRepo, r.Cursor, r.Synced));

    // ---------------------------------------------------------------- HTTP

    /// <summary>One request to the hub. Null when it did not answer in time (or at all).</summary>
    Response? Post(Request rq, TimeSpan timeout, bool gateClaim = false)
    {
        var c = Config();
        if (c is null) return null;
        try
        {
            var reply = (gateClaim ? fast : http).Post(new Uri(c.Url + "/v1/rpc"), c.Token, JsonSerializer.Serialize(rq, Json.Wire.Request), timeout);
            if (reply.Status == 401)
                return Response.Fail($"the hub refused this device's token ({c.Url}); get a new one: sankofad hub token add {c.Device}");
            if (reply.Status != 200) { Trip(); return null; }
            return JsonSerializer.Deserialize(reply.Body, Json.Wire.Response);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UriFormatException)
        {
            Trip();
            return null;
        }
    }

    void Trip() { lock (gate) offlineUntil = DateTimeOffset.UtcNow + Breaker; }

    async Task PullLoop(Rep rep)
    {
        var backoff = TimeSpan.FromMilliseconds(250);
        while (!stop.IsCancellationRequested)
        {
            var c = Config();
            if (c is null || store.Replica(rep.Root) is null) { await Delay(TimeSpan.FromSeconds(2)); continue; }
            try
            {
                var url = new Uri($"{c.Url}/v1/events?repo={Uri.EscapeDataString(rep.HubRepo)}&since={rep.Cursor}&follow=1");
                var opened = false;
                await Task.Run(() => streamer.Stream(url, c.Token, status =>
                {
                    if (status != 200) throw new IOException($"hub answered {status}");
                    opened = true;
                    if (rep.LastError is not null)
                    {
                        Console.Error.WriteLine($"sankofad: hub stream for {rep.Root} is back");
                        http.Reset(); fast.Reset();   // connections kept from before the outage are dead
                    }
                    rep.LastError = null;
                    lock (gate) offlineUntil = DateTimeOffset.MinValue;
                    rep.Streaming = true;
                    backoff = TimeSpan.FromMilliseconds(250);
                    Reconcile(rep);   // first send what was written while away
                }, line =>
                {
                    if (line.Length == 0) return;   // the hub's keepalive
                    Entry? e = null;
                    try { e = JsonSerializer.Deserialize(line, Json.Wire.Entry); } catch (JsonException) { }
                    if (e is not null) Apply(rep, [e], false);
                }, StreamSilence, stop.Token));
                if (stop.IsCancellationRequested) return;
                throw new IOException(opened ? "hub closed the event stream" : "no answer from the hub");
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                if (rep.Streaming || rep.LastError != ex.Message)
                    Console.Error.WriteLine($"sankofad: hub stream for {rep.Root}: {ex.GetType().Name}: {ex.Message}");
                rep.LastError = ex.Message;
                rep.Streaming = false;
                Trip();
                await Delay(backoff);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task Delay(TimeSpan t) { try { await Task.Delay(t, stop.Token); } catch (OperationCanceledException) { } }

    // ---------------------------------------------------------------- replica

    /// <summary>Append hub events to the replica, in order, if nothing is queued after the hub's part.</summary>
    void Apply(Rep rep, IEnumerable<Entry> entries, bool more, bool wait = true)
    {
        bool reconcile = false;
        // the gate does not wait for a reconcile in progress: the stream brings these events anyway
        if (!wait && !rep.L.TryEnter()) return;
        if (!wait) rep.L.Exit();
        lock (rep.L)
        {
            using var _ = LedgerLock.Acquire(rep.Dir);
            var len = File.Exists(rep.Ledger) ? new FileInfo(rep.Ledger).Length : 0;
            if (len != rep.Synced) reconcile = true;   // local writes are queued: reconcile brings both in order
            else
            {
                var sb = new StringBuilder();
                long cursor = rep.Cursor;
                foreach (var e in entries)
                    if (e.Seq is { } s && s > cursor) { sb.Append(Json.Line(e)).Append('\n'); cursor = s; }
                if (sb.Length > 0)
                {
                    var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                    using (var f = new FileStream(rep.Ledger, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) f.Write(bytes);
                    rep.Synced = len + bytes.Length;
                    rep.Cursor = cursor;
                    Save(rep);
                }
            }
        }
        if (reconcile) ThreadPool.QueueUserWorkItem(_ => Reconcile(rep));
        else if (more) CatchUp(rep);
    }

    /// <summary>Fetch the events after the cursor that an answer's tail did not carry.</summary>
    void CatchUp(Rep rep)
    {
        for (int i = 0; i < 100; i++)
        {
            var r = Post(new Request { Verb = "sync", Repo = rep.HubRepo, Since = rep.Cursor }, Timeout);
            if (r?.Tail is not { Count: > 0 } t) return;
            Apply(rep, t, false);
            if (r.More != true) return;
        }
    }

    /// <summary>Send the queue to the hub and replace it with the hub's copy. False if the hub is unreachable.</summary>
    bool Reconcile(Rep rep)
    {
        lock (rep.L)
        {
            List<Entry> queued;
            long end;
            using (LedgerLock.Acquire(rep.Dir))
            {
                var bytes = File.Exists(rep.Ledger) ? File.ReadAllBytes(rep.Ledger) : [];
                // only whole lines: a half-written last line stays for next time
                end = Array.LastIndexOf(bytes, (byte)'\n') + 1;
                if (end <= rep.Synced) return true;
                queued = Parse(Encoding.UTF8.GetString(bytes, (int)rep.Synced, (int)(end - rep.Synced)));
            }
            var resp = Post(new Request { Verb = "import", Repo = rep.HubRepo, Entries = queued, Since = rep.Cursor, Client = "sankofad" },
                            Timeout + TimeSpan.FromSeconds(queued.Count / 50));
            if (resp is null || !resp.Ok)
            {
                if (resp is not null) Console.Error.WriteLine($"sankofad: hub refused the queue for {rep.Root}: {resp.Error}");
                return false;
            }
            using (LedgerLock.Acquire(rep.Dir))
            {
                var bytes = File.ReadAllBytes(rep.Ledger);
                var head = bytes.AsSpan(0, (int)rep.Synced).ToArray();
                var later = bytes.AsSpan((int)end).ToArray();   // written while the queue was in flight: still queued
                var tail = new StringBuilder();
                long cursor = rep.Cursor;
                foreach (var e in resp.Tail ?? [])
                    if (e.Seq is { } s && s > cursor) { tail.Append(Json.Line(e)).Append('\n'); cursor = s; }
                var tailBytes = Encoding.UTF8.GetBytes(tail.ToString());
                var tmp = rep.Ledger + ".reconcile." + Environment.ProcessId;
                using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    f.Write(head); f.Write(tailBytes); f.Write(later);
                    f.Flush(flushToDisk: true);
                }
                File.Move(tmp, rep.Ledger, overwrite: true);
                rep.Synced = head.Length + tailBytes.Length;
                rep.Cursor = cursor;
                Save(rep);
            }
            if (resp.More == true) CatchUp(rep);
            return true;
        }
    }

    static List<Entry> Parse(string text)
    {
        var list = new List<Entry>();
        foreach (var l in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Entry? e = null;
            try { e = JsonSerializer.Deserialize(l, Json.Wire.Entry); } catch (JsonException) { }
            list.Add(e ?? new Entry { Id = $"q-bad-{list.Count}", Ts = Store.Ts(DateTimeOffset.UtcNow), Type = "flag",
                                      Session = "sankofad", Agent = "sankofad", Text = $"queued line was not JSON; kept as text: {l}" });
        }
        return list;
    }

    /// <summary>Allow-and-flag: write the event to the replica's queue, to be sent when the hub answers.</summary>
    Entry Queue(Rep rep, Entry e)
    {
        var c = Config();
        e.Id = $"q{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{Interlocked.Increment(ref qCounter) % 1000:000}";
        e.Ts = Store.Ts(DateTimeOffset.UtcNow);
        e.Device = c?.Device;
        e.Nonce ??= $"{DateTime.UtcNow.Ticks}-{Environment.ProcessId}-{qCounter}";
        using (LedgerLock.Acquire(rep.Dir))
        using (var f = new FileStream(rep.Ledger, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            f.Write(Encoding.UTF8.GetBytes(Json.Line(e) + "\n"));
        return e;
    }

    // ---------------------------------------------------------------- verbs

    static readonly HashSet<string> Writes = ["append", "done", "claim", "release", "record"];

    /// <summary>A verb for an attached repo.</summary>
    public Response Handle(Request r)
    {
        var rep = Get(r.Repo!);
        bool gateClaim = r.Client?.StartsWith("gate", StringComparison.Ordinal) == true;
        // send anything queued first, so this answer's tail lands in order; never on the edit
        // gate's path (the stream reconciles on its own), and only while the stream is up
        if (!gateClaim && rep.Streaming && File.Exists(rep.Ledger) && new FileInfo(rep.Ledger).Length > rep.Synced) Reconcile(rep);
        var timeout = gateClaim ? GateTimeout : Timeout;
        var fwd = Clone(r);
        fwd.Repo = rep.HubRepo;
        fwd.Since = r.Verb == "events" ? r.Since : rep.Cursor;
        var resp = Online ? Post(fwd, timeout, gateClaim) : null;
        if (resp is not null)
        {
            if (resp.Tail is not null) Apply(rep, resp.Tail, resp.More == true, wait: !gateClaim);
            resp.Tail = null;
            return resp;
        }
        var url = Config()?.Url ?? "(no hub configured)";
        if (r.Verb == "finish")
            return new Response { Entries = [], State = "queued", Error = null };   // leases expire on the hub on their own
        if (!Writes.Contains(r.Verb))
            return r.Verb == "ask"
                ? new Response { State = "unknown", Error = null, Ok = true, Device = Config()?.Device }
                : Response.Fail($"hub unreachable at {url}");
        var e = r.Verb switch
        {
            "append" => new Entry { Type = r.Type ?? "note", Text = r.Text ?? "", Ref = r.Ref, Wt = r.Wt },
            "done" => new Entry { Type = "done", Text = string.IsNullOrWhiteSpace(r.Text) ? "resolved" : r.Text!, Ref = r.Ref, Wt = r.Wt },
            "release" => new Entry { Type = "release", Text = $"release {r.Ref}", Ref = r.Ref },
            "claim" => new Entry
            {
                Type = "claim", Glob = r.Glob, Wt = r.Wt, Text = $"claim {r.Glob} (hub unreachable: allowed and queued, not yet leased)",
                Expires = Store.Ts(DateTimeOffset.UtcNow.AddSeconds(r.Ttl is > 0 ? r.Ttl.Value : Store.DefaultTtlSeconds)),
            },
            _ => r.Entry ?? new Entry { Type = "flag", Text = "empty record" },
        };
        if (r.Verb == "append" && e.Type is not ("note" or "todo" or "awaiting" or "stop"))
            return Response.Fail($"cannot append type '{e.Type}'");
        e.Session = r.Session ?? "-"; e.Agent = r.Agent ?? "main"; e.Client = r.Client; e.Nonce = r.Nonce;
        if (r.Verb == "record") { e.Session = "sankofad"; e.Agent = "sankofad"; }
        var q = Queue(rep, e);
        return r.Verb == "claim"
            ? new Response
            {
                Id = q.Id, Granted = true, State = "queued", Entry = q,
                Lease = new Lease { Id = q.Id, Glob = q.Glob ?? "", Session = q.Session, Agent = q.Agent, Claimed = q.Ts, Expires = q.Expires ?? "", Dirty = true, State = "held" },
            }
            : new Response { Id = q.Id, Entry = q, State = "queued" };
    }

    /// <summary>A holder ended (SubagentStop, SessionEnd) with no repo named: end its leases on the hub too.</summary>
    public void FinishEverywhere(Request r)
    {
        if (Config() is null || !Online) return;
        var fwd = Clone(r);
        fwd.Repo = null;
        Post(fwd, Timeout);
    }

    /// <summary>An event the daemon observed in an attached repo (overlap, landed).</summary>
    Entry Record(string root, Entry e)
    {
        var r = Handle(new Request { Verb = "record", Repo = root, Entry = e, Client = "sankofad" });
        return r.Entry ?? throw new SankofaException(r.Error ?? "record failed");
    }

    static Request Clone(Request r) => JsonSerializer.Deserialize(JsonSerializer.Serialize(r, Json.Wire.Request), Json.Wire.Request)!;

    // ---------------------------------------------------------------- attach

    /// <summary>
    /// Make this repo's ledger a replica of the hub's. If the hub has nothing for it yet, this
    /// device's history is sent as its first events (ids change; refs follow). Otherwise this
    /// device's history stays in .sankofa/ledger.pre-hub.jsonl and the replica starts from the hub.
    /// </summary>
    public Response Attach(string root)
    {
        root = Store.RepoKey(root);
        var c = Config() ?? throw new SankofaException("no hub configured on this device (sankofa hub join <url> <token>)");
        var id = HubRepo(root) ?? throw new SankofaException("no hub_repo in .sankofa/config");
        if (store.Replica(root) is { } existing) return Status(root);
        var probe = Post(new Request { Verb = "last", Repo = id, N = 1 }, Timeout)
                    ?? throw new SankofaException($"hub unreachable at {c.Url}");
        if (!probe.Ok) throw new SankofaException(probe.Error ?? "hub refused");
        var dir = Path.Combine(root, ".sankofa");
        var ledger = Path.Combine(dir, "ledger.jsonl");
        bool seed;
        using (LedgerLock.Acquire(dir))
        {
            if (File.Exists(ledger)) File.Copy(ledger, Path.Combine(dir, "ledger.pre-hub.jsonl"), overwrite: true);
            else File.WriteAllText(ledger, "");
            seed = probe.Entries is not { Count: > 0 };
            if (!seed) File.WriteAllText(ledger, "");
        }
        store.ReplicaSave(new Store.ReplicaState(root, id, 0, 0));
        var rep = Get(root);
        if (!Reconcile(rep)) throw new SankofaException("attached, but the first sync failed; it retries on its own");
        return Status(root, seed ? "seeded" : "joined");
    }

    /// <summary>Stop replicating: the replica is kept as ledger.hub.jsonl and the pre-hub ledger comes back.</summary>
    public Response Detach(string root)
    {
        root = Store.RepoKey(root);
        if (store.Replica(root) is null) throw new SankofaException("this repo is not attached to a hub");
        reps.TryRemove(root, out _);
        store.ReplicaDrop(root);
        var dir = Path.Combine(root, ".sankofa");
        using (LedgerLock.Acquire(dir))
        {
            var ledger = Path.Combine(dir, "ledger.jsonl");
            if (File.Exists(ledger)) File.Move(ledger, Path.Combine(dir, "ledger.hub.jsonl"), overwrite: true);
            var pre = Path.Combine(dir, "ledger.pre-hub.jsonl");
            if (File.Exists(pre)) File.Move(pre, ledger); else File.WriteAllText(ledger, "");
        }
        return new Response { State = "detached" };
    }

    public Response Status(string root, string? state = null)
    {
        root = Store.RepoKey(root);
        var c = Config();
        var st = store.Replica(root);
        var rep = st is null ? null : reps.GetValueOrDefault(root);
        long queued = st is null || !File.Exists(Path.Combine(root, ".sankofa", "ledger.jsonl")) ? 0
                    : Math.Max(0, new FileInfo(Path.Combine(root, ".sankofa", "ledger.jsonl")).Length - (rep?.Synced ?? st.Synced));
        var online = rep?.Streaming ?? false;
        return new Response
        {
            State = state ?? (st is null ? "off" : online ? "online" : "offline"),
            Device = c?.Device,
            Id = st?.HubRepo,
            Error = c is null ? "no hub configured on this device" : null,
            Url = c?.Url,
            Cursor = rep?.Cursor ?? st?.Cursor,
            Queued = queued,
        };
    }

    public void Dispose() { stop.Cancel(); http.Dispose(); fast.Dispose(); streamer.Dispose(); }
}
