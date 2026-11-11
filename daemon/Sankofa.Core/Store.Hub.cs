namespace Sankofa;

/// <summary>
/// 0.7: what the store needs to be a hub (stamping each event with the device and client that
/// wrote it, importing events a device wrote while it could not reach the hub) and to be a
/// device that replicates a hub (where its copy of each hub ledger stands).
/// </summary>
public sealed partial class Store
{
    [ThreadStatic] static (string Device, string? Client)? stamp;

    /// <summary>On a device: true for a repo whose ledger is a replica of a hub's. The store never
    /// writes or imports such a ledger; events it would record go to <see cref="RecordElsewhere"/>.</summary>
    public Func<string, bool>? IsReplica { get; set; }
    public Func<string, Entry, Entry>? RecordElsewhere { get; set; }

    bool Replicated(string repo) => IsReplica?.Invoke(repo) == true;
    public bool IsReplicated(string repo) => Replicated(RepoKey(repo));

    /// <summary>Every event appended on this thread until disposed records this device and client.
    /// The hub sets it from the request's token, so a device cannot write as another.</summary>
    public static IDisposable Stamp(string device, string? client)
    {
        var was = stamp;
        stamp = (device, client);
        return new Restore(was);
    }

    sealed class Restore((string, string?)? was) : IDisposable { public void Dispose() => stamp = was; }

    void InitHub() => db.Exec("""
        CREATE TABLE IF NOT EXISTS replicas(repo TEXT PRIMARY KEY, hub_repo TEXT NOT NULL,
          cursor INTEGER NOT NULL, synced INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS imported(repo TEXT NOT NULL, device TEXT NOT NULL, local_id TEXT NOT NULL,
          hub_id TEXT NOT NULL, PRIMARY KEY(repo, device, local_id));
        """);

    /// <summary>
    /// Events a device wrote while the hub was unreachable, in the order it wrote them. Each is
    /// applied as if it arrived now, except that it keeps its own time. Ids the device made up
    /// (q...) are mapped to the hub's, in refs too. A queued write the hub already has (same
    /// nonce) is not applied again. Nothing is refused as a whole: a queued claim on paths
    /// another device holds is recorded as contention plus a flag, since the edit already
    /// happened; a queued done for something already resolved becomes a flag.
    /// </summary>
    public (Dictionary<string, string> Map, List<Entry> Written) Import(string repo, IEnumerable<Entry> queued)
    {
        var map = new Dictionary<string, string>();
        var written = new List<Entry>();
        var device = stamp?.Device ?? "?";
        lock (gate)
        {
            repo = RepoKey(repo);
            SyncLocked(repo); ExpireDue(repo);
            foreach (var q in queued)
            {
                var localId = q.Id;
                if (ByNonce(repo, q.Nonce) is { } dup)
                {
                    map[localId] = dup.Id;
                    db.Run("INSERT OR REPLACE INTO imported(repo, device, local_id, hub_id) VALUES(?, ?, ?, ?)", repo, device, localId, dup.Id);
                    continue;
                }
                // a ref to an id this device made up: mapped in this batch, or in an earlier one
                var reference = q.Ref is null ? null
                    : map.TryGetValue(q.Ref, out var m) ? m
                    : db.Scalar("SELECT hub_id FROM imported WHERE repo=? AND device=? AND local_id=?", repo, device, q.Ref) as string ?? q.Ref;
                var e = new Entry
                {
                    Type = q.Type, Text = q.Text, Session = q.Session, Agent = q.Agent, Ref = reference, Wt = q.Wt,
                    Glob = q.Glob, Expires = q.Expires, Nonce = q.Nonce, Branch = q.Branch, Sha = q.Sha, Letter = q.Letter, Ts = q.Ts,
                    Client = q.Client,
                };
                Entry? got = null;
                using var who = Stamp(device, q.Client ?? "cli");   // the client that wrote it, not the link that carried it
                try
                {
                    switch (q.Type)
                    {
                        case "claim" or "renew" when q.Glob is not null:
                        {
                            var left = q.Expires is { } x && DateTimeOffset.TryParse(x, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? (long)(t - clock()).TotalSeconds : DefaultTtlSeconds;
                            var c = Claim(repo, q.Glob, q.Session, q.Agent, Math.Max(1, left), q.Wt, why: $" (queued while {device} was offline)");
                            got = c.Event;
                            if (!c.Granted)
                                written.Add(AppendLocked(repo, new Entry
                                {
                                    Type = "flag", Session = q.Session, Agent = q.Agent, Glob = q.Glob, Ts = q.Ts,
                                    Text = $"while {device} could not reach the hub, {q.Session} [{q.Agent}] edited {q.Glob} " +
                                           "without a lease; " + string.Join("; ", c.Conflicts.Select(l =>
                                               $"{l.Id} {l.Glob} is held by {l.Session} [{l.Agent}]")) + ". Check both edits.",
                                }, keepTs: true));
                            break;
                        }
                        case "done" when reference is not null:
                            got = Done(repo, reference, q.Text, q.Session, q.Agent, q.Wt, q.Nonce);
                            break;
                        case "release" when reference is not null:
                            got = Release(repo, reference, q.Session, q.Agent);
                            break;
                        case "note" or "todo" or "awaiting" or "handoff" or "stop" or "flag" or "overlap" or "landed":
                            got = AppendLocked(repo, e, keepTs: true);
                            break;
                        default:
                            got = AppendLocked(repo, new Entry
                            {
                                Type = "flag", Session = q.Session, Agent = q.Agent, Ts = q.Ts,
                                Text = $"{device} queued a {q.Type} event the hub does not replay: {q.Text}",
                            }, keepTs: true);
                            break;
                    }
                }
                catch (SankofaException ex)
                {
                    got = AppendLocked(repo, new Entry
                    {
                        Type = "flag", Session = q.Session, Agent = q.Agent, Ts = q.Ts, Nonce = q.Nonce,
                        Text = $"{device} queued {q.Type} {q.Ref} while offline; the hub could not apply it: {ex.Message}",
                    }, keepTs: true);
                }
                map[localId] = got.Id;
                db.Run("INSERT OR REPLACE INTO imported(repo, device, local_id, hub_id) VALUES(?, ?, ?, ?)", repo, device, localId, got.Id);
                written.Add(got);
            }
            return (map, written);
        }
    }

    /// <summary>The hub id a device's queued (q...) id became, if it was imported.</summary>
    public string? ImportedId(string repo, string device, string localId)
    {
        lock (gate)
            return db.Scalar("SELECT hub_id FROM imported WHERE repo=? AND device=? AND local_id=?", RepoKey(repo), device, localId) as string;
    }

    /// <summary>A repo's events after a sequence number, at most <paramref name="limit"/> (the tail a hub answer carries).</summary>
    public (List<Entry> Tail, bool More) Tail(string repo, long since, int limit = 5000)
    {
        var rows = EventsSince(since, repo, limit + 1);
        return rows.Count > limit ? (rows[..limit], true) : (rows, false);
    }

    // ---------------------------------------------------------------- device side

    public sealed record ReplicaState(string Repo, string HubRepo, long Cursor, long Synced);

    public ReplicaState? Replica(string repo)
    {
        lock (gate)
        {
            var r = db.Query("SELECT repo, hub_repo, cursor, synced FROM replicas WHERE repo=?", RepoKey(repo));
            return r.Count == 0 ? null : new((string)r[0][0]!, (string)r[0][1]!, (long)r[0][2]!, (long)r[0][3]!);
        }
    }

    /// <summary>Every repo this daemon keeps a ledger for: its own and the hub replicas.</summary>
    public List<string> LedgerRepos()
    {
        lock (gate)
            return db.Query("SELECT repo FROM repos UNION SELECT repo FROM replicas ORDER BY 1").Select(r => (string)r[0]!).ToList();
    }

    public List<ReplicaState> Replicas()
    {
        lock (gate)
            return db.Query("SELECT repo, hub_repo, cursor, synced FROM replicas ORDER BY repo")
                     .Select(r => new ReplicaState((string)r[0]!, (string)r[1]!, (long)r[2]!, (long)r[3]!)).ToList();
    }

    public void ReplicaSave(ReplicaState s)
    {
        lock (gate)
            db.Run("INSERT OR REPLACE INTO replicas(repo, hub_repo, cursor, synced) VALUES(?, ?, ?, ?)",
                   RepoKey(s.Repo), s.HubRepo, s.Cursor, s.Synced);
    }

    public void ReplicaDrop(string repo)
    {
        lock (gate) db.Run("DELETE FROM replicas WHERE repo=?", RepoKey(repo));
    }
}
