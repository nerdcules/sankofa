using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sankofa;

public sealed class SankofaException(string message) : Exception(message);

/// <summary>
/// The ledger and leases for every repo, in one SQLite file. Every write is an appended
/// event, mirrored as a line to the repo's .sankofa/ledger.jsonl (the 0.1 format), so the
/// Stop hook, the watcher and the no-daemon fallback keep working. Lines written by the
/// fallback while the daemon was down are imported on the next touch of that repo.
/// The store records and answers; it never decides for an agent.
/// </summary>
public sealed partial class Store : IDisposable
{
    public const string Version = "0.8.0";
    public const long DefaultTtlSeconds = 1800;
    static readonly HashSet<string> AppendTypes = ["note", "todo", "awaiting", "stop", "flag"];
    const string Cols = "seq, repo, id, ts, type, session, agent, text, ref, wt, glob, expires, nonce, branch, sha, device, client";
    const string LeaseCols = "id, glob, session, agent, claimed, expires, dirty, state, device";

    readonly Db db;
    readonly Func<DateTimeOffset> clock;
    readonly Lock gate = new();
    readonly Dictionary<string, (IDisposable Lock, int Depth)> fileLocks = [];

    /// <summary>Hold the repo's ledger file lock; reentrant within the store (a restore appends a flag).</summary>
    IDisposable FileLock(string repo)
    {
        if (fileLocks.TryGetValue(repo, out var h)) { fileLocks[repo] = (h.Lock, h.Depth + 1); }
        else fileLocks[repo] = (LedgerLock.Acquire(Path.GetDirectoryName(LedgerPath(repo))!), 1);
        return new FileLockScope(this, repo);
    }

    sealed class FileLockScope(Store s, string repo) : IDisposable
    {
        public void Dispose()
        {
            var (l, d) = s.fileLocks[repo];
            if (d > 1) s.fileLocks[repo] = (l, d - 1);
            else { s.fileLocks.Remove(repo); l.Dispose(); }
        }
    }

    /// <summary>Raised for every new event (written or imported), inside the store lock.</summary>
    public event Action<Entry>? Appended;

    public Store(string path, Func<DateTimeOffset>? clock = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        db = new Db(path);
        db.Exec("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              repo TEXT NOT NULL, id TEXT NOT NULL, ts TEXT NOT NULL, type TEXT NOT NULL,
              session TEXT NOT NULL, agent TEXT NOT NULL, text TEXT NOT NULL,
              ref TEXT, wt TEXT, glob TEXT, expires TEXT, nonce TEXT, source TEXT NOT NULL,
              UNIQUE(repo, id));
            CREATE INDEX IF NOT EXISTS events_ref ON events(repo, ref);
            CREATE TRIGGER IF NOT EXISTS events_no_update BEFORE UPDATE ON events
              BEGIN SELECT RAISE(ABORT, 'events are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS events_no_delete BEFORE DELETE ON events
              BEGIN SELECT RAISE(ABORT, 'events are append-only'); END;
            CREATE TABLE IF NOT EXISTS leases(
              repo TEXT NOT NULL, id TEXT NOT NULL, glob TEXT NOT NULL,
              session TEXT NOT NULL, agent TEXT NOT NULL, claimed TEXT NOT NULL,
              expires TEXT NOT NULL, dirty INTEGER NOT NULL, state TEXT NOT NULL,
              PRIMARY KEY(repo, id));
            CREATE TABLE IF NOT EXISTS repos(repo TEXT PRIMARY KEY, size INTEGER NOT NULL, count INTEGER NOT NULL);
            """);
        // 0.4: when a lease was last claimed or renewed (databases from 0.3 lack the column)
        try { db.Exec("ALTER TABLE leases ADD COLUMN touched TEXT"); } catch (SqliteException) { }
        // 0.6: landed events name a branch and a commit
        try { db.Exec("ALTER TABLE events ADD COLUMN branch TEXT"); } catch (SqliteException) { }
        try { db.Exec("ALTER TABLE events ADD COLUMN sha TEXT"); } catch (SqliteException) { }
        // 0.7: a hub records which device and client wrote each event
        try { db.Exec("ALTER TABLE events ADD COLUMN device TEXT"); } catch (SqliteException) { }
        try { db.Exec("ALTER TABLE events ADD COLUMN client TEXT"); } catch (SqliteException) { }
        db.Exec("CREATE INDEX IF NOT EXISTS events_nonce ON events(repo, nonce)");
        try { db.Exec("ALTER TABLE leases ADD COLUMN device TEXT"); } catch (SqliteException) { }
        InitWatch();
        InitHub();
    }

    public const long DefaultHandoverIdleSeconds = 5;

    public void Dispose() { lock (gate) db.Dispose(); }

    public static string Ts(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    string Now() => Ts(clock());

    public static string RepoKey(string repo) => Path.GetFullPath(repo).TrimEnd('/') is { Length: > 0 } r ? r : "/";

    // ---------------------------------------------------------------- verbs

    public Entry Append(string repo, string type, string text, string? session, string? agent,
                        string? reference = null, string? wt = null, string? nonce = null)
    {
        if (!AppendTypes.Contains(type)) throw new SankofaException($"cannot append type '{type}'");
        if (type != "stop" && string.IsNullOrWhiteSpace(text)) throw new SankofaException($"{type} needs text");
        lock (gate)
        {
            repo = RepoKey(repo);
            if (ByNonce(repo, nonce) is { } dup) return dup;   // a retry of a write whose answer was lost
            ExpireDue(repo);
            return AppendLocked(repo, new Entry
            {
                Type = type, Text = text, Session = session ?? "-", Agent = agent ?? "main", Ref = reference, Wt = wt,
                Nonce = nonce,
            });
        }
    }

    /// <summary>Resolve a todo or awaiting item, or finish a lease (clears its dirty mark).</summary>
    public Entry Done(string repo, string id, string? text, string? session, string? agent, string? wt = null, string? nonce = null)
    {
        lock (gate)
        {
            repo = RepoKey(repo);
            SyncLocked(repo); ExpireDue(repo);
            if (ByNonce(repo, nonce) is { } dup) return dup;
            var e = Get(repo, id) ?? throw new SankofaException($"no open item with id {id}");
            switch (e.Type)
            {
                case "todo" or "awaiting":
                    if (db.Scalar("SELECT 1 FROM events WHERE repo=? AND type='done' AND ref=?", repo, id) is not null)
                        throw new SankofaException($"{id} is already resolved");
                    break;
                case "claim":
                    if (GetLease(repo, id)?.State == "done") throw new SankofaException($"{id} is already resolved");
                    break;
                default:
                    throw new SankofaException($"no open item with id {id}");
            }
            return AppendLocked(repo, new Entry
            {
                Type = "done", Text = string.IsNullOrWhiteSpace(text) ? "resolved" : text!, Ref = id,
                Session = session ?? "-", Agent = agent ?? "main", Wt = wt, Nonce = nonce,
            });
        }
    }

    public sealed record ClaimResult(bool Granted, bool Renewed, Entry Event, Lease? Lease, List<Lease> Conflicts, List<Lease> Warnings);

    /// <summary>
    /// Ask for a lease on a glob. Granted unless another holder has a live lease that overlaps.
    /// The same holder claiming the same glob renews it. Either way the answer is recorded.
    /// </summary>
    /// <param name="inherit">A subagent may take over leases its own session's main agent holds
    /// (the parent delegated the work), but only ones the parent has not claimed or renewed for
    /// <paramref name="idleSeconds"/>: handing over a file mid-edit would clobber the parent's edit.
    /// The parent's lease is released with a note saying so.</param>
    public ClaimResult Claim(string repo, string glob, string? session, string? agent, long? ttlSeconds = null,
                             string? wt = null, bool inherit = false, long? idleSeconds = null, string? why = null)
    {
        session ??= "-"; agent ??= "main";
        glob = Glob.Normalize(glob);
        var ttl = ttlSeconds is > 0 ? ttlSeconds.Value : DefaultTtlSeconds;
        lock (gate)
        {
            repo = RepoKey(repo);
            SyncLocked(repo); ExpireDue(repo);
            var held = Leases(repo, "state='held'");
            var expires = Ts(clock().AddSeconds(ttl));
            var mine = held.FirstOrDefault(l => l.Session == session && l.Agent == agent && l.Glob == glob);
            if (mine is not null)
            {
                var ev = AppendLocked(repo, new Entry
                {
                    Type = "renew", Text = $"renew {glob}{why}", Ref = mine.Id, Glob = glob, Expires = expires,
                    Session = session, Agent = agent, Wt = wt,
                });
                return new(true, true, ev, GetLease(repo, mine.Id), [], []);
            }
            var conflicts = held.Where(l => (l.Session != session || l.Agent != agent) && Glob.Overlap(l.Glob, glob)).ToList();
            var idleBefore = Ts(clock().AddSeconds(-(idleSeconds is >= 0 ? idleSeconds.Value : DefaultHandoverIdleSeconds)));
            if (inherit && agent != "main" && conflicts.Count > 0 &&
                conflicts.All(c => c.Session == session && c.Agent == "main" && string.CompareOrdinal(Touched(repo, c.Id), idleBefore) <= 0))
            {
                foreach (var c in conflicts)
                    AppendLocked(repo, new Entry
                    {
                        Type = "release", Ref = c.Id, Glob = c.Glob, Session = session, Agent = agent,
                        Text = $"release {c.Glob}: handed from main to {agent} of the same session",
                    });
                conflicts.Clear();
            }
            if (conflicts.Count > 0)
            {
                var ev = AppendLocked(repo, new Entry
                {
                    Type = "contend", Glob = glob, Session = session, Agent = agent, Wt = wt,
                    Text = $"claim on {glob}{why} overlaps " + string.Join("; ", conflicts.Select(c =>
                        $"{c.Id} {c.Glob} held by {c.Session} [{c.Agent}]{(c.Device is null ? "" : " on " + c.Device)} until {c.Expires}")),
                });
                return new(false, false, ev, null, conflicts, []);
            }
            var warnings = Leases(repo, "state IN ('released','expired') AND dirty=1")
                .Where(l => Glob.Overlap(l.Glob, glob)).ToList();
            var claim = AppendLocked(repo, new Entry
            {
                Type = "claim", Text = $"claim {glob}{why}", Glob = glob, Expires = expires,
                Session = session, Agent = agent, Wt = wt,
            });
            return new(true, false, claim, GetLease(repo, claim.Id), [], warnings);
        }
    }

    /// <summary>Give up a lease without saying the work is finished: the dirty mark stays.</summary>
    public Entry Release(string repo, string id, string? session, string? agent)
    {
        lock (gate)
        {
            repo = RepoKey(repo);
            SyncLocked(repo); ExpireDue(repo);
            var l = GetLease(repo, id) ?? throw new SankofaException($"no lease with id {id}");
            if (l.State is "released" or "done") throw new SankofaException($"{id} is already {l.State}");
            return AppendLocked(repo, new Entry
            {
                Type = "release", Text = $"release {l.Glob}", Ref = id, Glob = l.Glob,
                Session = session ?? "-", Agent = agent ?? "main",
            });
        }
    }

    /// <summary>Which of these paths are held by someone other than this holder? Read-only (the commit gate).</summary>
    public List<(string Path, Lease Lease)> Check(string repo, IEnumerable<string> paths, string? session, string? agent)
    {
        session ??= "-"; agent ??= "main";
        lock (gate)
        {
            repo = RepoKey(repo);
            SyncLocked(repo); ExpireDue(repo);
            var held = Leases(repo, "state='held'").Where(l => l.Session != session || l.Agent != agent).ToList();
            var hits = new List<(string, Lease)>();
            foreach (var raw in paths)
            {
                var p = Glob.Normalize(raw);
                foreach (var l in held) if (Glob.Match(l.Glob, p)) hits.Add((p, l));
            }
            return hits;
        }
    }

    /// <summary>
    /// Finish every live lease of a holder (agent null: every agent of the session), in one repo
    /// or, with repo null, in every repo: a session's hooks run in its own directory, but its
    /// subagents may hold leases anywhere.
    /// </summary>
    public List<Entry> Finish(string? repo, string session, string? agent, string text)
    {
        lock (gate)
        {
            if (repo is null)
            {
                var repos = db.Query("SELECT DISTINCT repo FROM leases WHERE state='held' AND session=?", session)
                              .Select(r => (string)r[0]!).ToList();
                return repos.Where(r => Directory.Exists(r) && !Replicated(r)).SelectMany(r => Finish(r, session, agent, text)).ToList();
            }
            repo = RepoKey(repo);
            SyncLocked(repo); ExpireDue(repo);
            var mine = Leases(repo, "state='held' AND session=?", session).Where(l => agent is null || l.Agent == agent).ToList();
            return mine.Select(l => AppendLocked(repo, new Entry
            {
                Type = "done", Ref = l.Id, Glob = l.Glob, Session = session, Agent = agent ?? l.Agent,
                Text = $"{text}: {l.Glob}",
            })).ToList();
        }
    }

    public sealed record AskResult(string State, Lease? Lease, List<Lease> Holders, List<Lease> Dirty);

    /// <summary>Who holds this path? "held" with the lease, or "free"; dirty leases that cover it are listed.</summary>
    public AskResult Ask(string repo, string path)
    {
        path = Glob.Normalize(path);
        lock (gate)
        {
            repo = RepoKey(repo);
            SyncLocked(repo); ExpireDue(repo);
            var cover = Leases(repo, "state IN ('held','released','expired')")
                .Where(l => Glob.Overlap(l.Glob, path)).ToList();
            var holders = cover.Where(l => l.State == "held").ToList();
            var dirty = cover.Where(l => l.State != "held" && l.Dirty).ToList();
            return holders.Count > 0
                ? new("held", holders[0], holders, dirty)
                : new("free", null, [], dirty);
        }
    }

    /// <summary>Live leases plus ended leases still marked dirty.</summary>
    public List<Lease> Leases(string repo)
    {
        lock (gate)
        {
            repo = RepoKey(repo);
            SyncLocked(repo); ExpireDue(repo);
            return Leases(repo, "state='held' OR (dirty=1 AND state IN ('released','expired'))");
        }
    }

    public List<Entry> Open(string repo)
    {
        lock (gate)
        {
            repo = RepoKey(repo);
            SyncLocked(repo);
            return Rows($"""
                SELECT {Cols} FROM events e WHERE repo=? AND type IN ('todo','awaiting')
                AND NOT EXISTS (SELECT 1 FROM events d WHERE d.repo=e.repo AND d.type='done' AND d.ref=e.id)
                ORDER BY seq
                """, repo);
        }
    }

    public List<Entry> Last(string repo, int n)
    {
        lock (gate)
        {
            repo = RepoKey(repo);
            SyncLocked(repo);
            var rows = Rows($"SELECT {Cols} FROM events WHERE repo=? ORDER BY seq DESC LIMIT ?", repo, (long)n);
            rows.Reverse();
            return rows;
        }
    }

    /// <summary>Events after a daemon-wide sequence number, optionally for one repo.</summary>
    public List<Entry> EventsSince(long since, string? repo = null, int limit = 10000)
    {
        lock (gate)
        {
            if (repo is null) return Rows($"SELECT {Cols} FROM events WHERE seq>? ORDER BY seq LIMIT ?", since, (long)limit);
            repo = RepoKey(repo);
            SyncLocked(repo);
            return Rows($"SELECT {Cols} FROM events WHERE seq>? AND repo=? ORDER BY seq LIMIT ?", since, repo, (long)limit);
        }
    }

    /// <summary>Bring the database up to date with the repo's JSONL file; returns the entry count.</summary>
    public long Sync(string repo)
    {
        lock (gate) return SyncLocked(RepoKey(repo));
    }

    // ---------------------------------------------------------------- internals

    static string LedgerPath(string repo)
    {
        var dir = Path.Combine(repo, ".sankofa");
        if (!Directory.Exists(dir)) throw new SankofaException($"no .sankofa/ in {repo}; run 'sankofa init' there");
        return Path.Combine(dir, "ledger.jsonl");
    }

    long SyncLocked(string repo)
    {
        if (Replicated(repo)) throw new SankofaException($"{repo} is replicated from a hub; sankofad forwards its verbs there");
        using var _ = FileLock(repo);
        var path = LedgerPath(repo);
        if (!File.Exists(path)) File.WriteAllText(path, "");
        long size = new FileInfo(path).Length;
        var st = db.Query("SELECT size, count FROM repos WHERE repo=?", repo);
        long knownSize = st.Count > 0 ? (long)st[0][0]! : -1, count = st.Count > 0 ? (long)st[0][1]! : 0;
        if (size == knownSize) return count;

        var (lines, complete) = ReadLines(path);
        if (lines.Count > count)
        {
            var imported = new List<Entry>();
            db.Exec("BEGIN IMMEDIATE");
            try
            {
                for (int i = (int)count; i < lines.Count; i++)
                {
                    Entry e;
                    try { e = JsonSerializer.Deserialize(lines[i], Json.Wire.Entry) ?? throw new JsonException(); }
                    catch (JsonException)
                    {
                        e = new Entry { Ts = Now(), Type = "flag", Session = "sankofad", Agent = "sankofad",
                                        Text = $"ledger line {i + 1} was not JSON; kept as text: {lines[i]}" };
                    }
                    if (string.IsNullOrEmpty(e.Id) || Get(repo, e.Id) is not null) e.Id = $"e{i + 1}";
                    if (Get(repo, e.Id) is not null) e.Id = $"e{i + 1}-dup";
                    if (string.IsNullOrEmpty(e.Ts)) e.Ts = Now();
                    e.Repo = repo;
                    e.Seq = Insert(repo, e, "import");
                    Apply(repo, e);
                    imported.Add(e);
                }
                SetRepo(repo, complete ? size : -1, lines.Count);
                db.Exec("COMMIT");
            }
            catch { db.Exec("ROLLBACK"); throw; }
            foreach (var e in imported) Appended?.Invoke(e);
            return lines.Count;
        }
        if (lines.Count < count)
        {
            // The file lost entries the database has: write them all back, then say so.
            var all = Rows($"SELECT {Cols} FROM events WHERE repo=? ORDER BY seq", repo);
            var tmp = path + ".restore." + Environment.ProcessId;
            File.WriteAllText(tmp, string.Concat(all.Select(e => Json.Line(e) + "\n")));
            File.Move(tmp, path, overwrite: true);
            SetRepo(repo, new FileInfo(path).Length, count);
            AppendLocked(repo, new Entry
            {
                Type = "flag", Session = "sankofad", Agent = "sankofad",
                Text = $"ledger file had {lines.Count} of {count} entries; restored the rest from sankofad",
            });
            return count + 1;
        }
        SetRepo(repo, complete ? size : -1, count);
        return count;
    }

    static (List<string> lines, bool complete) ReadLines(string path)
    {
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var r = new StreamReader(fs, Encoding.UTF8)) text = r.ReadToEnd();
        var parts = text.Split('\n');
        bool complete = parts[^1].Length == 0;   // a half-written last line is left for the next sync
        return (parts[..^1].Where(l => !string.IsNullOrWhiteSpace(l)).ToList(), complete);
    }

    void SetRepo(string repo, long size, long count) =>
        db.Run("INSERT INTO repos(repo,size,count) VALUES(?,?,?) ON CONFLICT(repo) DO UPDATE SET size=excluded.size, count=excluded.count",
               repo, size, count);

    Entry AppendLocked(string repo, Entry e, bool keepTs = false)
    {
        using var _ = FileLock(repo);
        long count = SyncLocked(repo);
        e.Id = $"e{count + 1}";
        if (!keepTs || string.IsNullOrEmpty(e.Ts)) e.Ts = Now();
        if (stamp is { } st) { e.Device = st.Device; e.Client ??= st.Client; }
        e.Repo = repo;
        var path = LedgerPath(repo);
        db.Exec("BEGIN IMMEDIATE");
        try
        {
            e.Seq = Insert(repo, e, "daemon");
            Apply(repo, e);
            using (var f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                f.Write(Encoding.UTF8.GetBytes(Json.Line(e) + "\n"));
                f.Flush(flushToDisk: true);
            }
            SetRepo(repo, new FileInfo(path).Length, count + 1);
            db.Exec("COMMIT");
        }
        catch { db.Exec("ROLLBACK"); throw; }   // a line that reached the file but not the db is imported next time
        Appended?.Invoke(e);
        return e;
    }

    long Insert(string repo, Entry e, string source) =>
        (long)db.Query("""
            INSERT INTO events(repo,id,ts,type,session,agent,text,ref,wt,glob,expires,nonce,branch,sha,device,client,source)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?) RETURNING seq
            """, repo, e.Id, e.Ts, e.Type, e.Session, e.Agent, e.Text, e.Ref, e.Wt, e.Glob, e.Expires, e.Nonce,
                 e.Branch, e.Sha, e.Device, e.Client, source)[0][0]!;

    /// <summary>Lease state follows from events; imported events go through here too.</summary>
    void Apply(string repo, Entry e)
    {
        switch (e.Type)
        {
            case "claim" when e.Glob is not null && e.Expires is not null:
                db.Run("INSERT OR REPLACE INTO leases(repo,id,glob,session,agent,claimed,expires,dirty,state,touched,device) VALUES(?,?,?,?,?,?,?,1,'held',?,?)",
                       repo, e.Id, e.Glob, e.Session, e.Agent, e.Ts, e.Expires, e.Ts, e.Device);
                break;
            case "renew" when e.Ref is not null && e.Expires is not null:
                db.Run("UPDATE leases SET expires=?, touched=? WHERE repo=? AND id=? AND state='held'", e.Expires, e.Ts, repo, e.Ref);
                break;
            case "release" when e.Ref is not null:
                db.Run("UPDATE leases SET state='released' WHERE repo=? AND id=? AND state IN ('held','expired')", repo, e.Ref);
                break;
            case "expire" when e.Ref is not null:
                db.Run("UPDATE leases SET state='expired' WHERE repo=? AND id=? AND state='held'", repo, e.Ref);
                break;
            case "done" when e.Ref is not null && GetLease(repo, e.Ref) is { } l:
                db.Run("UPDATE leases SET state='done', dirty=0 WHERE repo=? AND id=?", repo, e.Ref);
                // finishing work on these paths also settles older half-edits under them
                foreach (var old in Leases(repo, "dirty=1 AND state IN ('released','expired')").Where(o => Glob.Overlap(o.Glob, l.Glob)))
                    db.Run("UPDATE leases SET dirty=0 WHERE repo=? AND id=?", repo, old.Id);
                break;
        }
    }

    void ExpireDue(string repo)
    {
        var due = Leases(repo, "state='held' AND expires <= ?", Now());
        foreach (var l in due)
            AppendLocked(repo, new Entry
            {
                Type = "expire", Ref = l.Id, Glob = l.Glob, Session = "sankofad", Agent = "sankofad",
                Text = $"lease {l.Id} on {l.Glob} held by {l.Session} [{l.Agent}] expired at {l.Expires}" +
                       (l.Dirty ? "; its paths may hold a half-finished edit" : ""),
            });
    }

    string Touched(string repo, string id) =>
        db.Scalar("SELECT COALESCE(touched, claimed) FROM leases WHERE repo=? AND id=?", repo, id) as string ?? "";

    Entry? ByNonce(string repo, string? nonce) =>
        string.IsNullOrEmpty(nonce) ? null : Rows($"SELECT {Cols} FROM events WHERE repo=? AND nonce=? ORDER BY seq LIMIT 1", repo, nonce).FirstOrDefault();

    Entry? Get(string repo, string id) =>
        Rows($"SELECT {Cols} FROM events WHERE repo=? AND id=?", repo, id).FirstOrDefault();

    Lease? GetLease(string repo, string id) => Leases(repo, "id=?", id).FirstOrDefault();

    List<Lease> Leases(string repo, string where, params object?[] args) =>
        db.Query($"SELECT {LeaseCols} FROM leases WHERE repo=? AND ({where}) ORDER BY claimed", [repo, .. args])
          .Select(r => new Lease
          {
              Id = (string)r[0]!, Glob = (string)r[1]!, Session = (string)r[2]!, Agent = (string)r[3]!,
              Claimed = (string)r[4]!, Expires = (string)r[5]!, Dirty = (long)r[6]! != 0, State = (string)r[7]!, Device = (string?)r[8],
          }).ToList();

    List<Entry> Rows(string sql, params object?[] args) =>
        db.Query(sql, args).Select(r => new Entry
        {
            Seq = (long)r[0]!, Repo = (string)r[1]!, Id = (string)r[2]!, Ts = (string)r[3]!, Type = (string)r[4]!,
            Session = (string)r[5]!, Agent = (string)r[6]!, Text = (string)r[7]!, Ref = (string?)r[8],
            Wt = (string?)r[9], Glob = (string?)r[10], Expires = (string?)r[11], Nonce = (string?)r[12],
            Branch = (string?)r[13], Sha = (string?)r[14], Device = (string?)r[15], Client = (string?)r[16],
        }).ToList();
}
