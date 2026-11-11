namespace Sankofa;

/// <summary>
/// Clears half-finished marks that git can vouch for. A lease that ends without a done (it
/// expired, or was released) keeps its dirty mark, because an edit under it may have been cut
/// off. Most of those edits were finished and committed long ago: the edit gate takes a lease
/// before every edit, and nothing finishes it until the session ends. So, every so often, each
/// ended lease still marked dirty is checked against git: when none of its paths has an
/// uncommitted change in any worktree of the repo, the daemon finishes it with a done that says
/// why. Paths git ignores count as clean. A lease in a repo shared through a hub is settled only
/// by the device that held it (only its worktrees can show the edit), and only while the hub is
/// reachable, so a settle is never queued and replayed late.
/// </summary>
public sealed class Settle : IDisposable
{
    readonly Store store;
    readonly Func<Request, Response> dispatch;
    readonly HubLink? link;
    readonly Lock running = new();
    Timer? timer;

    public TimeSpan Period { get; init; } = TimeSpan.FromSeconds(60);

    /// <param name="dispatch">The server's own dispatch, so a hub repo's leases are read from and
    /// finished on the hub, the same way the CLI's are.</param>
    public Settle(Store store, Func<Request, Response> dispatch, HubLink? link)
    {
        this.store = store; this.dispatch = dispatch; this.link = link;
    }

    public void Start() => timer = new Timer(_ => Quietly(), null, TimeSpan.FromSeconds(2), Period);

    void Quietly()
    {
        try { All(); }
        catch (Exception ex) { Console.Error.WriteLine($"sankofad: settle: {ex.Message}"); }
    }

    /// <summary>Settle every repo this daemon keeps a ledger for. Returns the done events written.</summary>
    public List<Entry> All() => store.LedgerRepos().Where(Directory.Exists).SelectMany(Repo).ToList();

    public List<Entry> Repo(string repo)
    {
        lock (running)
        {
            repo = Store.RepoKey(repo);
            bool remote = link?.Covers(repo) == true;
            if (remote && !link!.Online) return [];
            var me = remote ? link!.DeviceName : null;
            var listed = dispatch(new Request { Verb = "leases", Repo = repo });
            if (!listed.Ok || listed.Leases is null) return [];
            var ended = listed.Leases
                .Where(l => l.Dirty && l.State is "released" or "expired" && (!remote || l.Device == me))
                .OrderByDescending(l => l.Claimed, StringComparer.Ordinal).ToList();
            if (ended.Count == 0) return [];
            if (Changed(repo) is not { } changed) return [];   // not a git repo, or git failed: no opinion
            var written = new List<Entry>();
            var settled = new List<string>();
            foreach (var l in ended)
            {
                // a done also clears older dirty marks on overlapping paths (Store.Apply): those need no done of their own
                if (settled.Any(g => Glob.Overlap(g, l.Glob))) continue;
                if (changed.Any(p => Glob.Match(l.Glob, p))) continue;
                var r = dispatch(new Request
                {
                    Verb = "done", Repo = repo, Ref = l.Id, Session = "sankofad", Agent = "sankofad",
                    Text = $"settled: {l.Glob} has no uncommitted changes in git",
                });
                if (r.Ok && r.Entry is { } e) { written.Add(e); settled.Add(l.Glob); }
            }
            return written;
        }
    }

    /// <summary>Paths, relative to the repo, with uncommitted changes (staged, unstaged or untracked)
    /// in any worktree of it. Null when git cannot say.</summary>
    static HashSet<string>? Changed(string repo)
    {
        var prefix = Git.Run(repo, "rev-parse", "--show-prefix");
        var list = Git.Run(repo, "worktree", "list", "--porcelain");
        if (!prefix.Ok || !list.Ok) return null;
        var pre = prefix.Out.Trim();
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in list.Lines.Where(l => l.StartsWith("worktree ", StringComparison.Ordinal)).Select(l => l[9..]))
        {
            if (!Directory.Exists(tree)) continue;   // a worktree removed without git worktree remove
            var st = Git.Run(tree, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames");
            if (!st.Ok) return null;
            foreach (var rec in st.Z)
                if (rec.Length > 3 && rec[3..] is var p && p.StartsWith(pre, StringComparison.Ordinal)) set.Add(p[pre.Length..]);
        }
        return set;
    }

    public void Dispose() => timer?.Dispose();
}
