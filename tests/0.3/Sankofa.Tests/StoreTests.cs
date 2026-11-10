using System.Text.Json;

namespace Sankofa.Tests;

public sealed class StoreTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("sankofa-store-").FullName;
    readonly string repo, dbPath;
    DateTimeOffset now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    readonly Store store;

    public StoreTests()
    {
        repo = Path.Combine(dir, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".sankofa"));
        File.WriteAllText(Ledger, "");
        dbPath = Path.Combine(dir, "s.db");
        store = new Store(dbPath, () => now);
    }

    public void Dispose() { store.Dispose(); Directory.Delete(dir, true); }

    string Ledger => Path.Combine(repo, ".sankofa", "ledger.jsonl");
    List<Entry> FileEntries() => File.ReadAllLines(Ledger).Select(l => JsonSerializer.Deserialize(l, Json.Default.Entry)!).ToList();
    void Advance(int seconds) => now = now.AddSeconds(seconds);

    [Fact]
    public void Overlapping_claim_by_another_session_is_denied_and_recorded()
    {
        var a = store.Claim(repo, "src/**", "s1", "main", 600);
        Assert.True(a.Granted);
        var b = store.Claim(repo, "src/net/socket.c", "s2", "main", 600);
        Assert.False(b.Granted);
        Assert.Equal(a.Lease!.Id, Assert.Single(b.Conflicts).Id);
        Assert.Equal("contend", b.Event.Type);
        Assert.True(store.Claim(repo, "docs/readme.md", "s2", "main", 600).Granted);
        Assert.Equal(["claim", "contend", "claim"], FileEntries().Select(e => e.Type));
    }

    [Fact]
    public void Subagents_of_one_session_are_different_holders()
    {
        Assert.True(store.Claim(repo, "include/shared.h", "s1", "subagent:ui", 600).Granted);
        var other = store.Claim(repo, "include/*.h", "s1", "subagent:net", 600);
        Assert.False(other.Granted);
        Assert.Equal("subagent:ui", Assert.Single(other.Conflicts).Agent);
    }

    [Fact]
    public void Expired_lease_frees_the_path_once_and_stays_dirty()
    {
        var a = store.Claim(repo, "src/a.c", "s1", "main", 60);
        Advance(61);
        var ask = store.Ask(repo, "src/a.c");
        Assert.Equal("free", ask.State);
        Assert.Equal(a.Lease!.Id, Assert.Single(ask.Dirty).Id);
        store.Ask(repo, "src/a.c");
        Assert.Single(FileEntries(), e => e.Type == "expire");

        var b = store.Claim(repo, "src/a.c", "s2", "main", 60);
        Assert.True(b.Granted);
        Assert.Equal(a.Lease.Id, Assert.Single(b.Warnings).Id);
    }

    [Fact]
    public void Same_holder_claiming_again_renews()
    {
        var a = store.Claim(repo, "src/**", "s1", "main", 60);
        Advance(50);
        var again = store.Claim(repo, "src/**", "s1", "main", 60);
        Assert.True(again.Granted && again.Renewed);
        Assert.Equal(a.Lease!.Id, again.Lease!.Id);
        Advance(50);   // 100 s after the first claim: past the original expiry, inside the renewed one
        Assert.False(store.Claim(repo, "src/x.c", "s2", "main", 60).Granted);
        Assert.Equal("held", store.Ask(repo, "src/x.c").State);
        Advance(11);
        Assert.Equal("free", store.Ask(repo, "src/x.c").State);
    }

    [Fact]
    public void Dirty_on_claim_cleared_on_done()
    {
        var a = store.Claim(repo, "src/a.c", "s1", "main", 600);
        Assert.True(a.Lease!.Dirty);
        Assert.True(store.Ask(repo, "src/a.c").Lease!.Dirty);
        store.Done(repo, a.Lease.Id, "edit finished", "s1", "main");
        var ask = store.Ask(repo, "src/a.c");
        Assert.Equal("free", ask.State);
        Assert.Empty(ask.Dirty);
        Assert.Empty(store.Leases(repo));
        Assert.Throws<SankofaException>(() => store.Done(repo, a.Lease.Id, null, "s1", "main"));
    }

    [Fact]
    public void Release_keeps_dirty_until_later_work_on_the_path_is_done()
    {
        var a = store.Claim(repo, "src/a.c", "s1", "main", 600);
        store.Release(repo, a.Lease!.Id, "s1", "main");
        Assert.Equal(a.Lease.Id, Assert.Single(store.Ask(repo, "src/a.c").Dirty).Id);
        var b = store.Claim(repo, "src/*.c", "s2", "main", 600);
        Assert.Single(b.Warnings);
        store.Done(repo, b.Lease!.Id, "rewrote it", "s2", "main");
        Assert.Empty(store.Ask(repo, "src/a.c").Dirty);
    }

    [Fact]
    public void Todo_and_awaiting_resolve_like_the_shell_ledger()
    {
        var t = store.Append(repo, "todo", "write tests", "s1", "main");
        var w = store.Append(repo, "awaiting", "sudo pacman -U", "s1", "main");
        Assert.Equal(["e1", "e2"], new[] { t.Id, w.Id });
        store.Done(repo, "e1", "written", "s1", "main");
        Assert.Equal("e2", Assert.Single(store.Open(repo)).Id);
        Assert.Equal("e2 is already resolved", Record(() => store.Done(repo, "e2", null, null, null), () => store.Done(repo, "e2", null, null, null)));
        Assert.Equal("no open item with id e9", Record(() => store.Done(repo, "e9", null, null, null)));
    }

    static string Record(params Action[] acts)
    {
        string msg = "";
        foreach (var a in acts)
            try { a(); } catch (SankofaException ex) { msg = ex.Message; }
        return msg;
    }

    [Fact]
    public void Events_are_append_only_in_the_database()
    {
        store.Append(repo, "note", "hello", "s1", "main");
        using var raw = new Db(dbPath);
        Assert.Throws<SqliteException>(() => raw.Run("UPDATE events SET text='changed'"));
        Assert.Throws<SqliteException>(() => raw.Run("DELETE FROM events"));
    }

    [Fact]
    public void Lines_written_without_the_daemon_are_imported_in_order()
    {
        store.Append(repo, "note", "through the daemon", "s1", "main");
        File.AppendAllText(Ledger,
            """{"ts":"2026-11-10T12:00:01Z","id":"e2","type":"note","session":"s2","agent":"main","text":"daemon was down"}""" + "\n" +
            """{"ts":"2026-11-10T12:00:02Z","id":"e3","type":"todo","session":"s2","agent":"main","text":"left open"}""" + "\n");
        var next = store.Append(repo, "note", "daemon is back", "s3", "main");
        Assert.Equal("e4", next.Id);
        Assert.Equal(["through the daemon", "daemon was down", "left open", "daemon is back"],
            store.Last(repo, 10).Select(e => e.Text));
        Assert.Equal("e3", Assert.Single(store.Open(repo)).Id);
        Assert.Equal(4, FileEntries().Count);
    }

    [Fact]
    public void A_half_written_last_line_waits_for_the_next_sync()
    {
        File.AppendAllText(Ledger, """{"ts":"2026-11-10T12:00:01Z","id":"e1","type":"note","session":"s","agent":"main","text":"par""");
        Assert.Equal(0, store.Sync(repo));
        File.AppendAllText(Ledger, "tial\"}\n");
        Assert.Equal(1, store.Sync(repo));
        Assert.Equal("partial", Assert.Single(store.Last(repo, 5)).Text);
    }

    [Fact]
    public void A_truncated_ledger_file_is_restored_and_flagged()
    {
        for (int i = 0; i < 5; i++) store.Append(repo, "note", $"n{i}", "s1", "main");
        File.WriteAllLines(Ledger, File.ReadAllLines(Ledger).Take(2));
        store.Append(repo, "note", "after", "s1", "main");
        var lines = FileEntries();
        Assert.Equal(["n0", "n1", "n2", "n3", "n4"], lines.Take(5).Select(e => e.Text));
        Assert.Equal("flag", lines[5].Type);
        Assert.Contains("had 2 of 5", lines[5].Text);
        Assert.Equal(("e7", "after"), (lines[6].Id, lines[6].Text));
    }

    [Fact]
    public void A_subagent_inherits_only_its_own_parents_lease()
    {
        var main = store.Claim(repo, "src/a.c", "s1", "main", 600);
        Assert.False(store.Claim(repo, "src/a.c", "s1", "subagent:x/1", 600).Granted);      // no inherit asked
        Advance(3);
        Assert.False(store.Claim(repo, "src/a.c", "s1", "subagent:x/1", 600, inherit: true).Granted); // parent may be mid-edit
        store.Claim(repo, "src/a.c", "s1", "main", 600);                                      // parent edits again: renew
        Advance(4);
        Assert.False(store.Claim(repo, "src/a.c", "s1", "subagent:x/1", 600, inherit: true).Granted); // idle counts from the renew
        Advance(2);
        var sub = store.Claim(repo, "src/a.c", "s1", "subagent:x/1", 600, inherit: true);
        Assert.True(sub.Granted);
        Assert.Contains(FileEntries(), e => e.Type == "release" && e.Ref == main.Lease!.Id && e.Text.Contains("handed from main"));
        Assert.False(store.Claim(repo, "src/a.c", "s1", "main", 600, inherit: true).Granted);   // the parent waits
        Assert.False(store.Claim(repo, "src/a.c", "s1", "subagent:x/2", 600, inherit: true).Granted); // a sibling waits
        store.Claim(repo, "src/b.c", "s2", "main", 600);
        Advance(10);
        Assert.False(store.Claim(repo, "src/b.c", "s1", "subagent:x/1", 600, inherit: true).Granted); // another session's
    }

    [Fact]
    public void Check_reports_paths_held_by_others_and_changes_nothing()
    {
        store.Claim(repo, "src/**", "s1", "main", 600);
        store.Claim(repo, "docs/a.md", "s2", "main", 600);
        int before = FileEntries().Count;
        var hits = store.Check(repo, ["src/x.c", "docs/a.md", "README.md"], "s2", "main");
        Assert.Equal(("src/x.c", "src/**"), (Assert.Single(hits).Path, hits[0].Lease.Glob));
        Assert.Empty(store.Check(repo, ["src/x.c"], "s1", "main"));
        Assert.Equal(before, FileEntries().Count);
    }

    [Fact]
    public void Finish_ends_one_agents_leases_or_the_whole_sessions()
    {
        store.Claim(repo, "a", "s1", "main", 600);
        store.Claim(repo, "b", "s1", "subagent:x/1", 600);
        store.Claim(repo, "c", "s1", "subagent:x/2", 600);
        store.Claim(repo, "d", "s2", "main", 600);
        Assert.Equal("b", Assert.Single(store.Finish(repo, "s1", "subagent:x/1", "subagent finished")).Glob);
        Assert.Equal(["a", "c"], store.Finish(repo, "s1", null, "session ended").Select(e => e.Glob).Order());
        Assert.Equal("d", Assert.Single(store.Leases(repo)).Glob);
        Assert.All(FileEntries().Where(e => e.Type == "done"), e => Assert.NotNull(e.Ref));
    }

    [Fact]
    public void Each_event_is_raised_once_with_a_rising_sequence()
    {
        var seen = new List<Entry>();
        store.Appended += seen.Add;
        store.Append(repo, "note", "one", "s1", "main");
        File.AppendAllText(Ledger, """{"ts":"2026-11-10T12:00:01Z","id":"e2","type":"note","session":"s2","agent":"main","text":"two"}""" + "\n");
        store.Append(repo, "note", "three", "s1", "main");
        Assert.Equal(["one", "two", "three"], seen.Select(e => e.Text));
        Assert.True(seen.Zip(seen.Skip(1)).All(p => p.First.Seq < p.Second.Seq));
        Assert.Equal(["one", "two", "three"], store.EventsSince(0, repo).Select(e => e.Text));
    }
}
