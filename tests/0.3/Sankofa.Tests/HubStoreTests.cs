using System.Text.Json;

namespace Sankofa.Tests;

/// <summary>0.7: the hub side of the store: stamping and importing what devices queued offline.</summary>
public sealed class HubStoreTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("sankofa-hub-").FullName;
    readonly string repo;
    readonly Store store;

    public HubStoreTests()
    {
        repo = Path.Combine(dir, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".sankofa"));
        store = new Store(Path.Combine(dir, "s.db"));
    }

    public void Dispose() { store.Dispose(); Directory.Delete(dir, true); }

    List<Entry> FileEntries() => File.ReadAllLines(Path.Combine(repo, ".sankofa", "ledger.jsonl"))
        .Select(l => JsonSerializer.Deserialize(l, Json.Default.Entry)!).ToList();

    static Entry Q(string id, string type, string text, string? @ref = null, string? glob = null, string nonce = "") =>
        new() { Id = id, Type = type, Text = text, Ref = @ref, Glob = glob, Session = "s-lap", Agent = "main",
                Ts = "2026-11-10T09:00:00.000Z", Nonce = nonce.Length > 0 ? nonce : id + "-n", Client = "cli",
                Expires = glob is null ? null : Store.Ts(DateTimeOffset.UtcNow.AddMinutes(30)) };

    (Dictionary<string, string> Map, List<Entry> Written) Import(string device, params Entry[] q)
    {
        using var _ = Store.Stamp(device, "sankofad");
        return store.Import(repo, q);
    }

    [Fact]
    public void Every_event_written_under_a_stamp_records_device_and_client()
    {
        using (Store.Stamp("desk", "cli")) store.Append(repo, "note", "hello", "s", "main");
        var e = Assert.Single(FileEntries());
        Assert.Equal(("desk", "cli"), (e.Device, e.Client));
        store.Append(repo, "note", "no stamp", "s", "main");
        Assert.Null(FileEntries()[1].Device);
    }

    [Fact]
    public void Queued_events_keep_their_time_and_client_and_refs_follow_new_ids()
    {
        var (map, _) = Import("lap", Q("q1", "todo", "parser"), Q("q2", "done", "parser done", @ref: "q1"));
        var f = FileEntries();
        Assert.Equal("2026-11-10T09:00:00.000Z", f[0].Ts);
        Assert.Equal(("lap", "cli"), (f[0].Device, f[0].Client));
        Assert.Equal(map["q1"], f[1].Ref);
        Assert.Empty(store.Open(repo));
    }

    [Fact]
    public void A_ref_to_an_id_from_an_earlier_batch_is_mapped_too()
    {
        // the todo went up in one reconcile, its done was queued while that was in flight
        Import("lap", Q("q1", "todo", "parser"));
        Import("lap", Q("q2", "done", "parser done", @ref: "q1"));
        Assert.Empty(store.Open(repo));
        Assert.DoesNotContain(FileEntries(), e => e.Type == "flag");
    }

    [Fact]
    public void A_batch_sent_twice_is_applied_once()
    {
        var a = Import("lap", Q("q1", "note", "train"), Q("q2", "todo", "x"));
        var b = Import("lap", Q("q1", "note", "train"), Q("q2", "todo", "x"));
        Assert.Equal(2, FileEntries().Count);
        Assert.Equal(a.Map, b.Map);
    }

    [Fact]
    public void A_queued_claim_on_a_path_another_device_held_is_contention_plus_a_flag()
    {
        using (Store.Stamp("desk", "gate:claude-code")) Assert.True(store.Claim(repo, "docs/**", "s-desk", "main", 600).Granted);
        Import("lap", Q("q1", "claim", "claim docs/plan.md", glob: "docs/plan.md"));
        var f = FileEntries();
        Assert.Contains(f, e => e.Type == "contend" && e.Text.Contains("queued while lap was offline"));
        var flag = Assert.Single(f, e => e.Type == "flag");
        Assert.Contains("lap could not reach the hub", flag.Text);
        Assert.Contains("held by s-desk", flag.Text);
        Assert.Equal("desk", Assert.Single(store.Leases(repo)).Device);
    }

    [Fact]
    public void A_queued_done_the_hub_cannot_apply_becomes_a_flag_not_a_failure()
    {
        var (_, written) = Import("lap", Q("q1", "done", "finished", @ref: "e99"), Q("q2", "note", "after"));
        Assert.Equal(["flag", "note"], written.Select(e => e.Type));
    }
}
