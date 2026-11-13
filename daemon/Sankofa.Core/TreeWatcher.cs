namespace Sankofa;

/// <summary>
/// What the overlap detector needs from a file watcher: which tree changed, and when events were
/// lost. inotify on Linux (one watch per source directory, see Inotify.cs); FileSystemWatcher
/// elsewhere (FSEvents on macOS, ReadDirectoryChangesW on Windows), one per tree.
/// </summary>
public interface ITreeWatcher : IDisposable
{
    /// <summary>A file under this tree changed (created, written, moved, deleted).</summary>
    event Action<string>? Changed;
    /// <summary>Events were lost: every tree should be rescanned.</summary>
    event Action? Overflow;
    /// <summary>The system's watch limit was hit while adding this tree.</summary>
    event Action<string>? LimitReached;
    IReadOnlyCollection<string> Trees { get; }
    /// <summary>Watch a tree. Returns how many directories (or watchers) that took.</summary>
    int Add(string tree);
    void Remove(string tree);

    /// <summary>inotify on Linux, FileSystemWatcher everywhere else.</summary>
    static ITreeWatcher Create() => OperatingSystem.IsLinux() ? new Inotify() : new FsWatcher();
}

/// <summary>
/// The watcher for macOS and Windows: one FileSystemWatcher per tree, recursive. It cannot skip
/// git-ignored directories the way inotify watches can, so changes under .git/ and .sankofa/ are
/// dropped here and the rest is left to the scan, which asks git. A watcher whose buffer
/// overflowed reports Overflow, and the 30 s rescan is the floor either way.
/// </summary>
public sealed class FsWatcher : ITreeWatcher
{
    readonly Lock gate = new();
    readonly Dictionary<string, FileSystemWatcher> byTree = [];

    public event Action<string>? Changed;
    public event Action? Overflow;
    public event Action<string>? LimitReached;

    public IReadOnlyCollection<string> Trees { get { lock (gate) return byTree.Keys.ToList(); } }

    public int Add(string tree)
    {
        tree = Store.RepoKey(tree);
        lock (gate)
        {
            if (byTree.ContainsKey(tree)) return 1;
            FileSystemWatcher w;
            try
            {
                w = new FileSystemWatcher(tree)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                LimitReached?.Invoke(tree);
                return 0;
            }
            FileSystemEventHandler on = (_, e) => { if (!Skip(tree, e.FullPath)) Fire(tree); };
            w.Changed += on; w.Created += on; w.Deleted += on;
            w.Renamed += (_, e) => { if (!Skip(tree, e.FullPath) || !Skip(tree, e.OldFullPath)) Fire(tree); };
            w.Error += (_, e) =>
            {
                // the buffer overflowed (too many changes at once) or the watch broke: rescan everything
                try { Overflow?.Invoke(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            };
            try { w.EnableRaisingEvents = true; }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                w.Dispose();
                LimitReached?.Invoke(tree);
                return 0;
            }
            byTree[tree] = w;
            return 1;
        }
    }

    public void Remove(string tree)
    {
        tree = Store.RepoKey(tree);
        lock (gate)
        {
            if (!byTree.Remove(tree, out var w)) return;
            try { w.EnableRaisingEvents = false; } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            w.Dispose();
        }
    }

    void Fire(string tree)
    {
        // the watcher's own thread: a throw here would end the process, not one scan
        try { Changed?.Invoke(tree); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"sankofad: watcher: {ex.Message}");
        }
    }

    /// <summary>Changes under the tree's own .git/ and .sankofa/ are not source changes.</summary>
    static bool Skip(string tree, string path)
    {
        if (path.Length <= tree.Length) return true;
        var rel = path[tree.Length..].TrimStart('/', '\\');
        return rel.StartsWith(".git", StringComparison.Ordinal) && (rel.Length == 4 || rel[4] is '/' or '\\')
            || rel.StartsWith(".sankofa", StringComparison.Ordinal) && (rel.Length == 8 || rel[8] is '/' or '\\');
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var w in byTree.Values) { try { w.EnableRaisingEvents = false; } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } w.Dispose(); }
            byTree.Clear();
        }
    }
}
