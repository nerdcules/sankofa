using System.Text.Json;

namespace Sankofa.Tests;

/// <summary>0.6: the state the overlap watcher and the bridge keep in the store.</summary>
public sealed class WatchStoreTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("sankofa-watch-").FullName;
    readonly string repo;
    readonly Store store;

    public WatchStoreTests()
    {
        repo = Path.Combine(dir, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".sankofa"));
        store = new Store(Path.Combine(dir, "s.db"));
    }

    public void Dispose() { store.Dispose(); Directory.Delete(dir, true); }

    List<Entry> FileEntries() => File.ReadAllLines(Path.Combine(repo, ".sankofa", "ledger.jsonl"))
        .Select(l => JsonSerializer.Deserialize(l, Json.Default.Entry)!).ToList();

    [Fact]
    public void Clients_cannot_append_daemon_events_and_the_daemon_cannot_record_notes()
    {
        Assert.Throws<SankofaException>(() => store.Append(repo, "landed", "fake", "s", "main"));
        Assert.Throws<SankofaException>(() => store.Record(repo, new Entry { Type = "note", Text = "x" }));
        var e = store.Record(repo, new Entry { Type = "overlap", Glob = "a.c", Text = "a.c in 2 trees" });
        Assert.Equal(("sankofad", "sankofad"), (e.Session, e.Agent));
    }

    [Fact]
    public void Landed_carries_branch_and_sha_into_the_jsonl_file()
    {
        store.BridgeOn(repo, 60);
        store.RemoteRefsSeen(repo, new() { ["refs/remotes/origin/x"] = "aaa" }, []);
        store.RemoteRefsSeen(repo, new() { ["refs/remotes/origin/x"] = "bbb" },
                             [new Entry { Type = "landed", Branch = "origin/x", Sha = "bbb", Text = "x moved" }]);
        var e = Assert.Single(FileEntries());
        Assert.Equal(("landed", "origin/x", "bbb"), (e.Type, e.Branch, e.Sha));
        Assert.Equal("bbb", store.RemoteRefs(repo)!["refs/remotes/origin/x"]);
    }

    [Fact]
    public void A_landing_found_again_after_a_crash_is_not_recorded_twice()
    {
        store.BridgeOn(repo, 60);
        store.RemoteRefsSeen(repo, [], []);
        var landed = () => new List<Entry> { new() { Type = "landed", Branch = "origin/x", Sha = "bbb", Text = "x" } };
        store.RemoteRefsSeen(repo, new() { ["refs/remotes/origin/x"] = "bbb" }, landed());
        // as if the refs had not been saved: the same landing is found again
        store.RemoteRefsSeen(repo, new() { ["refs/remotes/origin/x"] = "bbb" }, landed());
        Assert.Single(FileEntries());
    }

    [Fact]
    public void Before_the_first_look_there_is_no_baseline_and_off_forgets_it()
    {
        store.BridgeOn(repo, null);
        Assert.Null(store.RemoteRefs(repo));
        Assert.Equal(Store.DefaultFetchSeconds, store.Bridge(repo)!.Interval);
        store.RemoteRefsSeen(repo, new() { ["refs/remotes/origin/main"] = "aaa" }, []);
        Assert.NotNull(store.RemoteRefs(repo));
        Assert.True(store.BridgeOff(repo));
        store.BridgeOn(repo, null);
        Assert.Null(store.RemoteRefs(repo));
    }

    [Fact]
    public void Reported_overlaps_survive_a_restart_until_cleared()
    {
        store.TreeAdd(repo, Store.AutoTrees);
        store.OverlapSet(repo, "a.c", "/x\n/y");
        using (var again = new Store(Path.Combine(dir, "s.db")))
        {
            Assert.Equal("/x\n/y", again.OverlapsReported(repo)["a.c"]);
            Assert.Equal([Store.AutoTrees], again.TreeRows(repo));
        }
        store.OverlapClear(repo, "a.c");
        Assert.Empty(store.OverlapsReported(repo));
        Assert.Equal(1, store.TreeRemove(repo, null));
        Assert.Empty(store.TreeHomes());
    }
}
