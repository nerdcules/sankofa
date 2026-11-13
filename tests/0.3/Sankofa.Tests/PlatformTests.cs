namespace Sankofa.Tests;

/// <summary>0.14: the pieces that differ per platform, as far as Linux can exercise them.</summary>
public class PlatformTests
{
    [Fact]
    public void SqliteLibraryNamesFollowThePlatform()
    {
        var first = Native.Candidates().First();
        if (OperatingSystem.IsWindows()) Assert.Equal("e_sqlite3", first);
        else if (OperatingSystem.IsMacOS()) Assert.Equal("libsqlite3.dylib", first);
        else Assert.Equal("libsqlite3.so.0", first);
    }

    [Fact]
    public void TheSharedLockSerialisesWriters()
    {
        // the Windows lock (an exclusive open), which .NET also honours on Unix through its FileShare emulation
        var dir = Directory.CreateTempSubdirectory("sankofa-lock-").FullName;
        var path = Path.Combine(dir, "ledger.lock");
        var first = LedgerLock.AcquireShared(path);
        var got = new ManualResetEventSlim();
        var second = Task.Run(() => { using var _ = LedgerLock.AcquireShared(path); got.Set(); });
        Assert.False(got.Wait(300), "the second writer got in while the first held the lock");
        first.Dispose();
        Assert.True(got.Wait(5000), "the second writer never got the lock after the first let go");
        second.Wait();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void TheDaemonSocketHonoursTheOverride()
    {
        var old = Environment.GetEnvironmentVariable("SANKOFA_SOCKET");
        try
        {
            Environment.SetEnvironmentVariable("SANKOFA_SOCKET", "/x/y.sock");
            Assert.Equal("/x/y.sock", Defaults.Socket());
        }
        finally { Environment.SetEnvironmentVariable("SANKOFA_SOCKET", old); }
    }

    [Fact]
    public void TheTreeWatcherIsPickedByPlatform()
    {
        using var w = ITreeWatcher.Create();
        if (OperatingSystem.IsLinux()) Assert.IsType<Inotify>(w); else Assert.IsType<FsWatcher>(w);
    }

    [Fact]
    public void FileSystemWatcherBackedTreesReportAChange()
    {
        // the macOS and Windows watcher, which also runs on Linux (on inotify underneath)
        var tree = Directory.CreateTempSubdirectory("sankofa-fsw-").FullName;
        Directory.CreateDirectory(Path.Combine(tree, ".git"));
        Directory.CreateDirectory(Path.Combine(tree, "src"));
        using var w = new FsWatcher();
        var changed = new ManualResetEventSlim();
        string? which = null;
        w.Changed += t => { which = t; changed.Set(); };
        Assert.Equal(1, w.Add(tree));
        Assert.Contains(Store.RepoKey(tree), w.Trees);
        File.WriteAllText(Path.Combine(tree, ".git", "index"), "not a source change");
        Assert.False(changed.Wait(300), "a write under .git/ counted as a change");
        File.WriteAllText(Path.Combine(tree, "src", "a.c"), "int x;");
        Assert.True(changed.Wait(5000), "a source write was not reported");
        Assert.Equal(Store.RepoKey(tree), which);
        w.Remove(tree);
        Assert.Empty(w.Trees);
        Directory.Delete(tree, true);
    }
}
