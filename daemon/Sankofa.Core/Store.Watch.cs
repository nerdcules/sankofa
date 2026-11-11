namespace Sankofa;

/// <summary>
/// 0.6: what the overlap watcher and the remote bridge need kept across restarts. Which trees
/// each repo watches, which overlaps were already reported (so each is recorded once), which
/// repos are fetched and what their remote branches pointed at when last seen (so a push that
/// lands while the machine sleeps is still reported on the next fetch).
/// </summary>
public sealed partial class Store
{
    /// <summary>The tree row that means "every git worktree of the repo, found again on each scan".</summary>
    public const string AutoTrees = "*";
    public const long DefaultFetchSeconds = 300;
    static readonly HashSet<string> DaemonTypes = ["overlap", "landed", "handoff", "flag"];

    /// <summary>Raised (outside the store lock) when tree or bridge registrations change.</summary>
    public event Action? WatchChanged;

    void InitWatch() => db.Exec("""
        CREATE TABLE IF NOT EXISTS trees(home TEXT NOT NULL, path TEXT NOT NULL, PRIMARY KEY(home, path));
        CREATE TABLE IF NOT EXISTS overlaps(home TEXT NOT NULL, path TEXT NOT NULL, trees TEXT NOT NULL, PRIMARY KEY(home, path));
        CREATE TABLE IF NOT EXISTS bridges(repo TEXT PRIMARY KEY, interval INTEGER NOT NULL,
          last_fetch TEXT, last_ok TEXT, last_error TEXT, baselined INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS remote_refs(repo TEXT NOT NULL, ref TEXT NOT NULL, sha TEXT NOT NULL, PRIMARY KEY(repo, ref));
        """);

    /// <summary>Record an event the daemon itself observed (overlap, landed, flag).</summary>
    public Entry Record(string repo, Entry e)
    {
        if (!DaemonTypes.Contains(e.Type)) throw new SankofaException($"cannot record type '{e.Type}'");
        if (RecordElsewhere is { } fwd && Replicated(RepoKey(repo))) return fwd(RepoKey(repo), e);
        lock (gate)
        {
            repo = RepoKey(repo);
            e.Session = "sankofad"; e.Agent = "sankofad";
            return AppendLocked(repo, e);
        }
    }

    // ---------------------------------------------------------------- trees

    public void TreeAdd(string home, string path)
    {
        lock (gate)
        {
            home = RepoKey(home);
            LedgerPath(home);   // throws without .sankofa/
            db.Run("INSERT OR IGNORE INTO trees(home, path) VALUES(?, ?)", home, path == AutoTrees ? path : RepoKey(path));
        }
        WatchChanged?.Invoke();
    }

    /// <summary>Stop watching one tree, or (path null) every tree of the repo. Returns how many rows went.</summary>
    public int TreeRemove(string home, string? path)
    {
        int n;
        lock (gate)
        {
            home = RepoKey(home);
            n = path is null
                ? db.Run("DELETE FROM trees WHERE home=?", home)
                : db.Run("DELETE FROM trees WHERE home=? AND path=?", home, path == AutoTrees ? path : RepoKey(path));
            if (path is null) db.Run("DELETE FROM overlaps WHERE home=?", home);
        }
        if (n > 0) WatchChanged?.Invoke();
        return n;
    }

    public List<string> TreeRows(string home)
    {
        lock (gate) return db.Query("SELECT path FROM trees WHERE home=? ORDER BY path", RepoKey(home)).Select(r => (string)r[0]!).ToList();
    }

    public List<string> TreeHomes()
    {
        lock (gate) return db.Query("SELECT DISTINCT home FROM trees ORDER BY home").Select(r => (string)r[0]!).ToList();
    }

    /// <summary>Overlaps already recorded and still standing: path to the trees (newline-joined) it was reported for.</summary>
    public Dictionary<string, string> OverlapsReported(string home)
    {
        lock (gate)
            return db.Query("SELECT path, trees FROM overlaps WHERE home=?", RepoKey(home))
                     .ToDictionary(r => (string)r[0]!, r => (string)r[1]!);
    }

    public void OverlapSet(string home, string path, string trees)
    {
        lock (gate) db.Run("INSERT OR REPLACE INTO overlaps(home, path, trees) VALUES(?, ?, ?)", RepoKey(home), path, trees);
    }

    public void OverlapClear(string home, string path)
    {
        lock (gate) db.Run("DELETE FROM overlaps WHERE home=? AND path=?", RepoKey(home), path);
    }

    // ---------------------------------------------------------------- bridge

    public void BridgeOn(string repo, long? intervalSeconds)
    {
        var iv = intervalSeconds is > 0 ? intervalSeconds.Value : DefaultFetchSeconds;
        lock (gate)
        {
            repo = RepoKey(repo);
            LedgerPath(repo);
            db.Run("INSERT INTO bridges(repo, interval) VALUES(?, ?) ON CONFLICT(repo) DO UPDATE SET interval=excluded.interval", repo, iv);
        }
        WatchChanged?.Invoke();
    }

    /// <summary>Stop fetching. What the remote branches pointed at is forgotten too: turning the
    /// bridge on again starts from a fresh baseline rather than reporting everything since.</summary>
    public bool BridgeOff(string repo)
    {
        int n;
        lock (gate)
        {
            repo = RepoKey(repo);
            n = db.Run("DELETE FROM bridges WHERE repo=?", repo);
            db.Run("DELETE FROM remote_refs WHERE repo=?", repo);
        }
        if (n > 0) WatchChanged?.Invoke();
        return n > 0;
    }

    public List<BridgeInfo> Bridges()
    {
        lock (gate)
            return db.Query("""
                SELECT b.repo, b.interval, b.last_fetch, b.last_ok, b.last_error,
                       (SELECT COUNT(*) FROM remote_refs r WHERE r.repo=b.repo)
                FROM bridges b ORDER BY b.repo
                """).Select(r => new BridgeInfo
            {
                Repo = (string)r[0]!, Interval = (long)r[1]!, LastFetch = (string?)r[2], LastOk = (string?)r[3],
                LastError = (string?)r[4], Refs = (int)(long)r[5]!,
            }).ToList();
    }

    public BridgeInfo? Bridge(string repo)
    {
        var key = RepoKey(repo);
        return Bridges().FirstOrDefault(b => b.Repo == key);
    }

    public void BridgeFetched(string repo, string? error)
    {
        lock (gate)
        {
            var now = Now();
            if (error is null) db.Run("UPDATE bridges SET last_fetch=?, last_ok=?, last_error=NULL WHERE repo=?", now, now, RepoKey(repo));
            else db.Run("UPDATE bridges SET last_fetch=?, last_error=? WHERE repo=?", now, error, RepoKey(repo));
        }
    }

    /// <summary>Remote-tracking refs as last seen, or null before the first look (the baseline).</summary>
    public Dictionary<string, string>? RemoteRefs(string repo)
    {
        lock (gate)
        {
            repo = RepoKey(repo);
            if (db.Scalar("SELECT baselined FROM bridges WHERE repo=?", repo) is not long b || b == 0) return null;
            return db.Query("SELECT ref, sha FROM remote_refs WHERE repo=?", repo).ToDictionary(r => (string)r[0]!, r => (string)r[1]!);
        }
    }

    /// <summary>Replace the remembered refs with what was just seen, and record the landings found, in one step.</summary>
    public List<Entry> RemoteRefsSeen(string repo, Dictionary<string, string> refs, IEnumerable<Entry> landed)
    {
        if (RecordElsewhere is { } fwd && Replicated(RepoKey(repo)))
        {
            var sent = landed.Select(e => fwd(RepoKey(repo), e)).ToList();
            SaveRefs(RepoKey(repo), refs);
            return sent;
        }
        lock (gate)
        {
            repo = RepoKey(repo);
            if (db.Scalar("SELECT 1 FROM bridges WHERE repo=?", repo) is null) return [];   // turned off meanwhile
            // a crash between writing the events and saving the refs re-finds the same landings: skip those
            var written = landed
                .Where(e => e.Type == "handoff"
                    // the sender's own handoff, or one already recorded, whichever branch name it came under
                    ? db.Scalar("SELECT 1 FROM events WHERE repo=? AND type='handoff' AND sha=?", repo, e.Sha) is null
                    : db.Scalar("SELECT sha FROM events WHERE repo=? AND type='landed' AND branch=? ORDER BY seq DESC LIMIT 1",
                                repo, e.Branch) as string != e.Sha)
                .Select(e => { e.Session = "sankofad"; e.Agent = "sankofad"; return AppendLocked(repo, e); }).ToList();
            SaveRefs(repo, refs);
            return written;
        }
    }

    /// <summary>Finish the open handoffs whose ref is gone from a remote that just answered a fetch
    /// (someone picked them up), or now names another commit (handed off again, and recorded anew).</summary>
    public List<Entry> HandoffsTaken(string repo, ISet<string> fetched, Func<Entry, string?> refNow)
    {
        List<Entry> open;
        lock (gate) open = Open(repo).Where(e => e.Type == "handoff").ToList();
        var done = new List<Entry>();
        foreach (var h in open)
        {
            var remote = h.Branch?.Split('/')[0];
            if (remote is null || !fetched.Contains(remote)) continue;
            var at = refNow(h);
            if (at == h.Sha) continue;
            done.Add(Done(repo, h.Id, at is null ? $"picked up: the handoff of {h.Branch} is gone from {remote}"
                                                   : $"superseded: {h.Branch} was handed off again at {at[..Math.Min(7, at.Length)]}",
                          "sankofad", "sankofad"));
        }
        return done;
    }

    void SaveRefs(string repo, Dictionary<string, string> refs)
    {
        lock (gate)
        {
            db.Exec("BEGIN IMMEDIATE");
            try
            {
                db.Run("DELETE FROM remote_refs WHERE repo=?", repo);
                foreach (var (r, sha) in refs) db.Run("INSERT INTO remote_refs(repo, ref, sha) VALUES(?, ?, ?)", repo, r, sha);
                db.Run("UPDATE bridges SET baselined=1 WHERE repo=?", repo);
                db.Exec("COMMIT");
            }
            catch { db.Exec("ROLLBACK"); throw; }
        }
    }
}
