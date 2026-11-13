namespace Sankofa;

/// <summary>
/// Detection where prevention is not possible: two worktrees of one repo changing the same path.
/// Leases stop two agents editing one file in one tree; agents in separate worktrees never ask,
/// and find out at merge time. This watches every tree of a registered repo with inotify and,
/// after each burst of changes, asks git which paths each tree has changed. A tree's changes are
/// counted from where it left the home tree's history (the merge base of the two HEADs), so
/// commits made in a worktree still count until they are merged. A path changed in two or more
/// trees is recorded once as an <c>overlap</c> event in the home repo's ledger, and again only if
/// another tree joins in, or after the overlap has cleared.
/// </summary>
public sealed class Overlaps : IDisposable
{
    readonly Store store;
    readonly ITreeWatcher? inotify;
    readonly Lock gate = new();
    readonly Dictionary<string, Group> groups = [];
    readonly Dictionary<string, string> treeHome = [];
    readonly HashSet<string> limitFlagged = [];
    Timer? periodic;

    /// <summary>Quiet time after the last change before a tree's group is scanned.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(300);
    /// <summary>Every group is also scanned this often: commits, merges and new worktrees, and anything inotify missed.</summary>
    public TimeSpan Period { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>More new overlapping paths than this in one scan, between the same trees, become one event.</summary>
    public int SummaryOver { get; init; } = 20;
    /// <summary>Raised after every scan with the overlap events it recorded (tests, the rescan verb).</summary>
    public event Action<string, List<Entry>>? Scanned;

    sealed class Group(string home)
    {
        public readonly string Home = home;
        public readonly Lock ScanLock = new();
        public Timer? Debounce;
        public HashSet<string> Trees = [];
    }

    public Overlaps(Store store, bool useInotify = true)
    {
        this.store = store;
        if (useInotify)
        {
            inotify = ITreeWatcher.Create();   // inotify on Linux, FileSystemWatcher on macOS and Windows
            inotify.Changed += tree => { if (HomeOf(tree) is { } h) Poke(h); };
            inotify.Overflow += () => { foreach (var h in Homes()) Poke(h); };
            inotify.LimitReached += LimitReached;
        }
        store.WatchChanged += () => ThreadPool.QueueUserWorkItem(_ => Refresh());
    }

    public void Start()
    {
        Refresh();
        foreach (var h in Homes()) Poke(h);
        periodic = new Timer(_ => { Refresh(); foreach (var h in Homes()) ScanQuietly(h); }, null, Period, Period);
    }

    List<string> Homes() { lock (gate) return groups.Keys.ToList(); }
    string? HomeOf(string tree) { lock (gate) return treeHome.GetValueOrDefault(tree); }

    void Poke(string home)
    {
        lock (gate)
        {
            if (!groups.TryGetValue(home, out var g)) return;
            g.Debounce ??= new Timer(_ => ScanQuietly(g.Home));
            g.Debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    void ScanQuietly(string home)
    {
        try { Scan(home); }
        catch (Exception ex) { Console.Error.WriteLine($"sankofad: overlap scan of {home}: {ex.Message}"); }
    }

    void LimitReached(string tree)
    {
        string? home;
        lock (gate) { if (!limitFlagged.Add(tree)) return; home = treeHome.GetValueOrDefault(tree); }
        if (home is null) return;
        store.Record(home, new Entry
        {
            Type = "flag",
            Text = $"inotify watch limit reached while watching {tree}; overlaps there are found by the " +
                   $"{(int)Period.TotalSeconds} s rescan only (raise fs.inotify.max_user_watches)",
        });
    }

    /// <summary>Bring groups and inotify watches in line with the registrations and git's worktree list.</summary>
    public void Refresh()
    {
        var want = store.TreeHomes().Where(h => Directory.Exists(Path.Combine(h, ".sankofa")))
                        .ToDictionary(h => h, h => Trees(h).Select(t => t.Path).ToHashSet());
        List<string> add = [], remove = [];
        lock (gate)
        {
            foreach (var h in groups.Keys.Where(h => !want.ContainsKey(h)).ToList())
            {
                remove.AddRange(groups[h].Trees);
                groups[h].Debounce?.Dispose();
                groups.Remove(h);
            }
            foreach (var (h, trees) in want)
            {
                if (!groups.TryGetValue(h, out var g)) groups[h] = g = new Group(h);
                remove.AddRange(g.Trees.Where(t => !trees.Contains(t)));
                add.AddRange(trees.Where(t => !g.Trees.Contains(t)));
                g.Trees = trees;
            }
            treeHome.Clear();
            foreach (var g in groups.Values) foreach (var t in g.Trees) treeHome.TryAdd(t, g.Home);
        }
        if (inotify is null) return;
        foreach (var t in remove.Distinct()) if (HomeOf(t) is null) inotify.Remove(t);
        foreach (var t in add.Distinct()) inotify.Add(t);
    }

    /// <summary>The home tree, every git worktree of it (with the auto row), and trees added by path.</summary>
    public List<TreeInfo> Trees(string home)
    {
        home = Store.RepoKey(home);
        var rows = store.TreeRows(home);
        var list = new List<TreeInfo> { new() { Path = home, Kind = "home" } };
        if (rows.Contains(Store.AutoTrees))
        {
            var wl = Git.Run(home, "worktree", "list", "--porcelain");
            if (wl.Ok)
                foreach (var line in wl.Lines)
                    if (line.StartsWith("worktree ", StringComparison.Ordinal) && line[9..] is var p &&
                        Store.RepoKey(p) != home && Directory.Exists(p))
                        list.Add(new() { Path = Store.RepoKey(p), Kind = "worktree" });
        }
        foreach (var p in rows.Where(r => r != Store.AutoTrees && Directory.Exists(r)))
            if (list.All(t => t.Path != p)) list.Add(new() { Path = p, Kind = "added" });
        foreach (var t in list)
        {
            var b = Git.Run(t.Path, "rev-parse", "--abbrev-ref", "HEAD");
            t.Branch = b.Ok ? b.Out.Trim() : "-";
        }
        return list;
    }

    /// <summary>Paths a tree has changed since it left the home tree's history, repo-relative.</summary>
    public static HashSet<string> Touched(string tree, string? homeHead)
    {
        var set = new HashSet<string>();
        var head = Git.Run(tree, "rev-parse", "-q", "--verify", "HEAD");
        if (!head.Ok)
        {
            // no commits yet: everything in the tree is new
            if (Git.Run(tree, "ls-files", "-z", "-co", "--exclude-standard") is { Ok: true } all) set.UnionWith(all.Z);
        }
        else
        {
            var baseRev = head.Out.Trim();
            if (homeHead is not null && homeHead != baseRev &&
                Git.Run(tree, "merge-base", baseRev, homeHead) is { Ok: true } mb)
                baseRev = mb.Out.Trim();
            if (Git.Run(tree, "diff", "--name-only", "-z", "--no-renames", baseRev, "--") is { Ok: true } d) set.UnionWith(d.Z);
            if (Git.Run(tree, "ls-files", "-z", "-o", "--exclude-standard") is { Ok: true } u) set.UnionWith(u.Z);
        }
        set.RemoveWhere(p => p.EndsWith('/') || p == ".sankofa" || p.StartsWith(".sankofa/", StringComparison.Ordinal));
        return set;
    }

    /// <summary>Scan one repo's trees now; returns the overlap events recorded.</summary>
    public List<Entry> Scan(string home)
    {
        home = Store.RepoKey(home);
        Group? g;
        lock (gate) g = groups.GetValueOrDefault(home);
        g ??= new Group(home);
        lock (g.ScanLock)
        {
            var trees = Trees(home);
            var reported = store.OverlapsReported(home);
            var recorded = new List<Entry>();
            var byPath = new Dictionary<string, List<TreeInfo>>();
            if (trees.Count >= 2)
            {
                var hh = Git.Run(home, "rev-parse", "-q", "--verify", "HEAD");
                string? homeHead = hh.Ok ? hh.Out.Trim() : null;
                foreach (var t in trees)
                {
                    var touched = Touched(t.Path, t.Kind == "home" ? null : homeHead);
                    t.Touched = touched.Count;
                    foreach (var p in touched)
                    {
                        if (!byPath.TryGetValue(p, out var l)) byPath[p] = l = [];
                        l.Add(t);
                    }
                }
            }
            var fresh = new List<(string Path, List<TreeInfo> Trees, string Key)>();
            foreach (var (p, ts) in byPath)
            {
                if (ts.Count < 2) continue;
                var key = string.Join('\n', ts.Select(t => t.Path).Order(StringComparer.Ordinal));
                if (reported.TryGetValue(p, out var was) && ts.All(t => was.Split('\n').Contains(t.Path))) continue;
                fresh.Add((p, ts, key));
            }
            foreach (var batch in fresh.GroupBy(f => f.Key))
            {
                var ts = batch.First().Trees;
                var where = string.Join(", ", ts.Select(t => $"{Label(t, trees)} [{t.Branch}]"));
                var paths = batch.Select(f => f.Path).Order(StringComparer.Ordinal).ToList();
                if (paths.Count > SummaryOver)
                    recorded.Add(store.Record(home, new Entry
                    {
                        Type = "overlap",
                        Text = $"{paths.Count} paths are changed in {ts.Count} worktrees: {where}. First ones: " +
                               string.Join(", ", paths.Take(10)),
                    }));
                else
                    foreach (var p in paths)
                        recorded.Add(store.Record(home, new Entry
                        {
                            Type = "overlap", Glob = p, Text = $"{p} is changed in {ts.Count} worktrees: {where}",
                        }));
                foreach (var p in paths)
                {
                    var union = reported.TryGetValue(p, out var was) ? was.Split('\n').Concat(ts.Select(t => t.Path)) : ts.Select(t => t.Path);
                    store.OverlapSet(home, p, string.Join('\n', union.Distinct().Order(StringComparer.Ordinal)));
                }
            }
            // an overlap that no longer stands (merged, reverted, a tree gone) may be reported again later
            foreach (var p in reported.Keys)
                if (!byPath.TryGetValue(p, out var ts) || ts.Count < 2) store.OverlapClear(home, p);
            Scanned?.Invoke(home, recorded);
            return recorded;
        }
    }

    /// <summary>A tree's directory name, or its full path when two trees of the group share the name.</summary>
    static string Label(TreeInfo t, List<TreeInfo> all)
    {
        var name = Path.GetFileName(t.Path);
        return all.Count(o => Path.GetFileName(o.Path) == name) > 1 || name.Length == 0 ? t.Path : name;
    }

    /// <summary>The trees of a repo with how many paths each has changed (for 'sankofa trees').</summary>
    public List<TreeInfo> Status(string home)
    {
        home = Store.RepoKey(home);
        var trees = Trees(home);
        var hh = Git.Run(home, "rev-parse", "-q", "--verify", "HEAD");
        foreach (var t in trees) t.Touched = Touched(t.Path, t.Kind == "home" || !hh.Ok ? null : hh.Out.Trim()).Count;
        return trees;
    }

    public void Dispose()
    {
        periodic?.Dispose();
        lock (gate) foreach (var g in groups.Values) g.Debounce?.Dispose();
        inotify?.Dispose();
    }
}
