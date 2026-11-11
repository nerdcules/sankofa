namespace Sankofa;

/// <summary>
/// The remote bridge: work that lands while nobody is looking (a scheduled cloud agent pushing
/// overnight, a teammate's branch) is recorded as a <c>landed</c> event per branch that moved.
/// Each bridged repo is fetched every interval; its remote-tracking branches are compared with
/// what the daemon saw last, which is kept in the database. So a push made while the machine
/// slept, or picked up by the human's own <c>git fetch</c>, is still reported on the next look.
/// The first look only records a baseline. A branch whose new tip is already in a local branch
/// is the human's own push (or work they already pulled) and is not reported.
/// </summary>
public sealed class Bridge : IDisposable
{
    readonly Store store;
    readonly Lock gate = new();
    readonly HashSet<string> busy = [];
    readonly Dictionary<string, DateTimeOffset> retryAt = [];
    Timer? tick;

    /// <summary>How often the loop checks whether a repo is due.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>After a failed fetch, try again this soon (capped at the repo's interval).</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromSeconds(30);
    public int FetchTimeoutMs { get; init; } = 120_000;

    public Bridge(Store store) { this.store = store; }

    /// <summary>Fetch every repo at once (a boot is a morning: the night's pushes should not wait
    /// for the interval), then each when it is due.</summary>
    public void Start()
    {
        first = true;
        tick = new Timer(_ => Due(), null, TimeSpan.Zero, Tick);
    }

    bool first;

    void Due()
    {
        var now = DateTimeOffset.UtcNow;
        bool all;
        lock (gate) { all = first; first = false; }
        foreach (var b in store.Bridges())
        {
            if (!Directory.Exists(Path.Combine(b.Repo, ".sankofa"))) continue;
            var last = b.LastFetch is null ? DateTimeOffset.MinValue : DateTimeOffset.Parse(b.LastFetch, System.Globalization.CultureInfo.InvariantCulture);
            var due = last.AddSeconds(b.Interval);
            lock (gate) if (b.LastError is not null && retryAt.TryGetValue(b.Repo, out var r) && r < due) due = r;
            if (now < due && !all) continue;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Poll(b.Repo); }
                catch (Exception ex) { Console.Error.WriteLine($"sankofad: bridge {b.Repo}: {ex.Message}"); }
            });
        }
    }

    /// <summary>Fetch now and record what landed. Null if a poll of this repo is already running.</summary>
    public List<Entry>? Poll(string repo, bool fetch = true)
    {
        repo = Store.RepoKey(repo);
        lock (gate) if (!busy.Add(repo)) return null;
        try
        {
            string? error = null;
            if (fetch)
            {
                var f = Git.RunTimed(repo, FetchTimeoutMs, "fetch", "--all", "--prune", "--quiet");
                if (!f.Ok) error = string.IsNullOrEmpty(f.Err) ? $"git fetch exited {f.Code}" : f.Err.Split('\n')[^1];
            }
            // compare even after a failed fetch: the human's own fetch may have moved refs
            var refs = RemoteBranches(repo);
            List<Entry> written = [];
            if (refs is not null)
            {
                var known = store.RemoteRefs(repo);
                var landed = known is null ? [] : Landings(repo, known, refs);
                written = store.RemoteRefsSeen(repo, refs, landed);
            }
            else error ??= "not a git repository";
            store.BridgeFetched(repo, error);
            lock (gate)
                if (error is null) retryAt.Remove(repo);
                else retryAt[repo] = DateTimeOffset.UtcNow + RetryAfter;
            return written;
        }
        finally { lock (gate) busy.Remove(repo); }
    }

    /// <summary>refs/remotes/* (not the symbolic */HEAD) to commit ids.</summary>
    static Dictionary<string, string>? RemoteBranches(string repo)
    {
        var r = Git.Run(repo, "for-each-ref", "--format=%(refname)%00%(objectname)%00%(symref)", "refs/remotes");
        if (!r.Ok) return null;
        var d = new Dictionary<string, string>();
        foreach (var line in r.Lines)
        {
            var p = line.Split('\0');
            if (p.Length >= 2 && (p.Length < 3 || p[2].Length == 0)) d[p[0]] = p[1];
        }
        return d;
    }

    static List<Entry> Landings(string repo, Dictionary<string, string> known, Dictionary<string, string> now)
    {
        var mine = Git.Run(repo, "for-each-ref", "--format=%(objectname)", "refs/heads") is { Ok: true } h ? h.Lines.ToHashSet() : [];
        var list = new List<Entry>();
        foreach (var (rf, sha) in now.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            known.TryGetValue(rf, out var old);
            if (old == sha || mine.Contains(sha)) continue;
            // already in a local branch: the human's own push (perhaps with more commits on top
            // since), or work they already pulled
            if (Git.Run(repo, "for-each-ref", "--count=1", "--contains", sha, "--format=x", "refs/heads") is { Ok: true } c &&
                c.Out.Trim().Length > 0) continue;
            var branch = rf.StartsWith("refs/remotes/", StringComparison.Ordinal) ? rf["refs/remotes/".Length..] : rf;
            var info = Git.Run(repo, "log", "-1", "--format=%h%x00%an%x00%s", sha);
            var parts = info.Ok ? info.Out.TrimEnd('\n').Split('\0') : [sha[..Math.Min(7, sha.Length)], "?", ""];
            string what;
            if (old is null)
            {
                // commits on the new branch that no other branch already has
                var others = now.Where(kv => kv.Key != rf).Select(kv => "^" + kv.Value).Concat(mine.Select(m => "^" + m)).Distinct();
                var n = Git.Run(repo, ["rev-list", "--count", sha, .. others]);
                what = $"new branch {branch}" + (n.Ok ? $", {Plural(n.Out.Trim(), "new commit")}" : "");
            }
            else if (Git.Run(repo, "merge-base", "--is-ancestor", old, sha).Ok)
            {
                var n = Git.Run(repo, "rev-list", "--count", $"{old}..{sha}");
                what = $"{branch}: {(n.Ok ? Plural(n.Out.Trim(), "new commit") : "new commits")}";
            }
            else what = $"{branch} was rewritten (force push) from {old[..Math.Min(7, old.Length)]}";
            list.Add(new Entry
            {
                Type = "landed", Branch = branch, Sha = sha,
                Text = $"{what}, now {parts[0]} \"{(parts.Length > 2 ? parts[2] : "")}\" by {(parts.Length > 1 ? parts[1] : "?")}",
            });
        }
        return list;
    }

    static string Plural(string n, string word) => n == "1" ? $"1 {word}" : $"{n} {word}s";

    public void Dispose() => tick?.Dispose();
}
