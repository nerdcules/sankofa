using System.Runtime.InteropServices;

namespace Sankofa;

/// <summary>
/// inotify over whole working trees, one watch per directory. Skips .git/, .sankofa/, nested
/// repositories and git-ignored directories (build output, node_modules), so a tree costs about
/// as many watches as it has source directories. Says only which tree changed, and when the
/// kernel's queue overflowed (then every tree counts as changed).
/// </summary>
public sealed partial class Inotify : IDisposable
{
    [LibraryImport("libc", SetLastError = true)] private static partial int inotify_init1(int flags);
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int inotify_add_watch(int fd, string path, uint mask);
    [LibraryImport("libc", SetLastError = true)] private static partial int inotify_rm_watch(int fd, int wd);
    [LibraryImport("libc", SetLastError = true)] private static unsafe partial nint read(int fd, byte* buf, nint count);
    [LibraryImport("libc", SetLastError = true)] private static unsafe partial int poll(PollFd* fds, nuint n, int timeout);
    [LibraryImport("libc")] private static partial int close(int fd);

    [StructLayout(LayoutKind.Sequential)] struct PollFd { public int Fd; public short Events; public short Revents; }

    const int IN_NONBLOCK = 0x800, IN_CLOEXEC = 0x80000, ENOSPC = 28;
    const uint IN_MODIFY = 0x2, IN_CLOSE_WRITE = 0x8, IN_MOVED_FROM = 0x40, IN_MOVED_TO = 0x80, IN_CREATE = 0x100,
               IN_DELETE = 0x200, IN_DELETE_SELF = 0x400, IN_ONLYDIR = 0x1000000, IN_Q_OVERFLOW = 0x4000,
               IN_IGNORED = 0x8000, IN_ISDIR = 0x40000000, IN_DONT_FOLLOW = 0x2000000;
    // IN_DONT_FOLLOW: a directory swapped for a symlink between the check and the watch is not followed out of the tree
    const uint Mask = IN_MODIFY | IN_CLOSE_WRITE | IN_MOVED_FROM | IN_MOVED_TO | IN_CREATE | IN_DELETE | IN_DELETE_SELF | IN_ONLYDIR | IN_DONT_FOLLOW;

    readonly int fd;
    readonly Lock gate = new();
    readonly Dictionary<int, (string Tree, string Dir)> byWd = [];
    readonly Dictionary<string, HashSet<int>> byTree = [];
    readonly Thread reader;
    volatile bool stopping;

    /// <summary>A file under this tree changed (created, written, moved, deleted).</summary>
    public event Action<string>? Changed;
    /// <summary>Events were lost (queue overflow): every tree should be rescanned.</summary>
    public event Action? Overflow;
    /// <summary>The per-user watch limit was hit while adding this tree (fs.inotify.max_user_watches).</summary>
    public event Action<string>? LimitReached;

    public Inotify()
    {
        fd = inotify_init1(IN_NONBLOCK | IN_CLOEXEC);
        if (fd < 0) throw new IOException($"inotify_init1 failed (errno {Marshal.GetLastPInvokeError()})");
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "sankofad-inotify" };
        reader.Start();
    }

    public IReadOnlyCollection<string> Trees { get { lock (gate) return byTree.Keys.ToList(); } }

    /// <summary>Watch every source directory of a tree. Returns the number of directories watched.</summary>
    public int Add(string tree)
    {
        tree = Store.RepoKey(tree);
        var ignored = IgnoredDirs(tree);
        lock (gate)
        {
            if (byTree.ContainsKey(tree)) return byTree[tree].Count;
            byTree[tree] = [];
        }
        int n = 0;
        AddDir(tree, tree, ignored, ref n);
        return n;
    }

    public void Remove(string tree)
    {
        tree = Store.RepoKey(tree);
        lock (gate)
        {
            if (!byTree.Remove(tree, out var wds)) return;
            foreach (var wd in wds) { inotify_rm_watch(fd, wd); byWd.Remove(wd); }
        }
    }

    /// <summary>Directories git ignores in this tree, relative and ending in '/'.</summary>
    static HashSet<string> IgnoredDirs(string tree) =>
        Git.Run(tree, "ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory") is { Ok: true } r
            ? r.Z.Where(p => p.EndsWith('/')).ToHashSet()
            : [];

    void AddDir(string tree, string dir, HashSet<string>? ignored, ref int n)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            int wd = inotify_add_watch(fd, d, Mask);
            if (wd < 0)
            {
                if (Marshal.GetLastPInvokeError() == ENOSPC) { LimitReached?.Invoke(tree); return; }
                continue;   // vanished or unreadable
            }
            lock (gate)
            {
                if (!byTree.TryGetValue(tree, out var set)) { inotify_rm_watch(fd, wd); return; }   // removed meanwhile
                set.Add(wd);
                byWd[wd] = (tree, d);
            }
            n++;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(d).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var s in subs)
                if (!Skip(tree, s, ignored)) stack.Push(s);
        }
    }

    static bool Skip(string tree, string dir, HashSet<string>? ignored)
    {
        var name = Path.GetFileName(dir);
        if (name is ".git" or ".sankofa") return true;
        if (Path.Exists(Path.Combine(dir, ".git"))) return true;   // a nested repository or worktree: its own tree
        try { if (new DirectoryInfo(dir).LinkTarget is not null) return true; } catch (IOException) { return true; }
        var rel = Path.GetRelativePath(tree, dir) + "/";
        if (ignored is not null) return ignored.Contains(rel);
        // a directory made after the tree was added: ask git
        return Git.Run(tree, "check-ignore", "-q", "--", rel) is { Code: 0 };
    }

    unsafe void ReadLoop()
    {
        var buf = new byte[64 * 1024];
        var pfd = new PollFd { Fd = fd, Events = 1 /* POLLIN */ };
        while (!stopping)
        {
            int r = poll(&pfd, 1, 500);
            if (r <= 0) continue;
            nint len;
            fixed (byte* p = buf) len = read(fd, p, buf.Length);
            if (len <= 0) continue;
            var trees = new HashSet<string>();
            var newDirs = new List<(string Tree, string Dir)>();
            bool overflow = false;
            for (int off = 0; off + 16 <= len;)
            {
                int wd = BitConverter.ToInt32(buf, off);
                uint mask = BitConverter.ToUInt32(buf, off + 4);
                int nlen = (int)BitConverter.ToUInt32(buf, off + 12);
                var name = nlen > 0 ? System.Text.Encoding.UTF8.GetString(buf, off + 16, nlen).TrimEnd('\0') : "";
                off += 16 + nlen;
                if ((mask & IN_Q_OVERFLOW) != 0) { overflow = true; continue; }
                (string Tree, string Dir) w;
                lock (gate)
                {
                    if (!byWd.TryGetValue(wd, out w)) continue;
                    if ((mask & IN_IGNORED) != 0) { byWd.Remove(wd); if (byTree.TryGetValue(w.Tree, out var s)) s.Remove(wd); continue; }
                }
                if (w.Dir == w.Tree && name is ".git" or ".sankofa") continue;
                trees.Add(w.Tree);
                if ((mask & IN_ISDIR) != 0 && (mask & (IN_CREATE | IN_MOVED_TO)) != 0 && name.Length > 0)
                    newDirs.Add((w.Tree, Path.Combine(w.Dir, name)));
            }
            // this is the daemon's own thread: a throw here would end the process, not one scan
            try
            {
                foreach (var (tree, dir) in newDirs)
                    if (!Skip(tree, dir, null)) { int n = 0; AddDir(tree, dir, null, ref n); }
                if (overflow) Overflow?.Invoke();
                foreach (var t in trees) Changed?.Invoke(t);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Console.Error.WriteLine($"sankofad: watcher: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        stopping = true;
        reader.Join(2000);
        close(fd);
    }
}
