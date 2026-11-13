using Sankofa;

/// <summary>The terminal dashboard's fold (0.10), on the same fixtures the Qt dashboard's tests use.</summary>
public class BoardTests : IDisposable
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T10:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);
    static string Fixtures => Path.Combine(AppContext.BaseDirectory, "../../../../../fixtures");
    readonly string dir = Directory.CreateTempSubdirectory("board").FullName;
    public void Dispose() => Directory.Delete(dir, true);

    static Entry E(string json) => System.Text.Json.JsonSerializer.Deserialize(json, Json.Wire.Entry)!;

    [Fact]
    public void FoldsOpenItemsLeasesWordsAndAlerts()
    {
        var b = new Board();
        var f = new BoardFiles(b);
        f.Follow(Path.Combine(Fixtures, "api.jsonl"));
        f.Follow(Path.Combine(Fixtures, "web.jsonl"));
        Assert.True(f.Poll());
        Assert.Equal(2, b.Repos().Count);
        Assert.Equal(2, b.Open(null, "awaiting").Count);
        Assert.Equal(2, b.Open(null, "todo").Count);   // the web todo was done
        var leases = b.Leases(Now);
        Assert.Equal(4, leases.Count);
        Assert.Contains(leases, l => l.Glob == "src/auth/session.ts" && l.State == "expired" && l.Dirty);
        Assert.Equal("Tax rounding: half-even everywhere; tests pinned to the 2025 tables.", b.Words()[0].Text);
        Assert.Equal(2, b.Alerts().Count);
        Assert.False(f.Poll());   // nothing new
    }

    [Fact]
    public void AHandoffIsOpenUntilPickedUp()
    {
        var b = new Board();
        b.Ingest("r", E("""{"id":"e1","type":"handoff","text":"parser done","branch":"origin/p","sha":"abc","ts":"2026-11-11T09:00:00.000Z"}"""));
        Assert.Equal("origin/p", Assert.Single(b.Open("r", "handoff")).Branch);
        b.Ingest("r", E("""{"id":"e2","type":"done","ref":"e1","text":"picked up on lap","ts":"2026-11-11T09:05:00.000Z"}"""));
        Assert.Empty(b.Open("r"));
    }

    [Fact]
    public void ADoneLeaseSettlesHalfEditsUnderIt()
    {
        var b = new Board();
        b.Ingest("r", E("""{"id":"e1","type":"claim","glob":"src/a.c","ts":"2026-11-11T09:00:00.000Z","expires":"2026-11-11T09:30:00.000Z"}"""));
        b.Ingest("r", E("""{"id":"e2","type":"release","ref":"e1","ts":"2026-11-11T09:01:00.000Z"}"""));
        Assert.Equal("released", Assert.Single(b.Leases(Now)).State);
        b.Ingest("r", E("""{"id":"e3","type":"claim","glob":"src/**","ts":"2026-11-11T09:02:00.000Z","expires":"2026-11-11T11:00:00.000Z"}"""));
        b.Ingest("r", E("""{"id":"e4","type":"done","ref":"e3","ts":"2026-11-11T09:03:00.000Z"}"""));
        Assert.Empty(b.Leases(Now));
    }

    [Fact]
    public void ALongLedgerFoldsInLinearTime()
    {
        // 0.11: every done used to look at every lease the repo ever had, so a year of edit-gate
        // leases took the dashboard 25 s to open; now each done looks only at ended, dirty ones
        var b = new Board();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++)
        {
            b.Ingest("r", new Entry { Id = $"c{i}", Type = "claim", Glob = $"src/m{i % 3000}/f{i % 50}.cs", Session = "s", Ts = "2026-11-11T09:00:00.000Z", Text = "" });
            b.Ingest("r", new Entry { Id = $"d{i}", Type = "done", Ref = $"c{i}", Session = "s", Ts = "2026-11-11T09:00:01.000Z", Text = "" });
        }
        b.Ingest("r", new Entry { Id = "x", Type = "claim", Glob = "src/m1/*", Session = "s", Ts = "2026-11-11T09:00:00.000Z", Text = "" });
        b.Ingest("r", new Entry { Id = "y", Type = "expire", Ref = "x", Session = "sankofad", Ts = "2026-11-11T09:30:00.000Z", Text = "" });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        Assert.Equal("x", Assert.Single(b.Leases(Now)).Id);   // finished leases are gone, the dirty one stays
        b.Ingest("r", new Entry { Id = "z", Type = "claim", Glob = "src/m1/f1.cs", Session = "t", Ts = "2026-11-11T09:40:00.000Z", Text = "" });
        b.Ingest("r", new Entry { Id = "w", Type = "done", Ref = "z", Session = "t", Ts = "2026-11-11T09:41:00.000Z", Text = "" });
        Assert.Empty(b.Leases(Now));   // a done under the dirty one settles it
    }

    [Fact]
    public void AHeldLeasePastItsTimeIsExpired()
    {
        var b = new Board();
        b.Ingest("r", E("""{"id":"e1","type":"claim","glob":"x","ts":"2026-11-11T08:00:00.000Z","expires":"2026-11-11T08:30:00.000Z"}"""));
        Assert.Equal("expired", Assert.Single(b.Leases(Now)).State);
    }

    [Fact]
    public void FollowsAppendsHalfLinesAndReplacements()
    {
        var repo = Path.Combine(dir, "r");
        Directory.CreateDirectory(Path.Combine(repo, ".sankofa"));
        var ledger = Path.Combine(repo, ".sankofa", "ledger.jsonl");
        const string a = """{"id":"e1","type":"todo","text":"one","ts":"2026-11-11T09:00:00.000Z"}""";
        const string c = """{"id":"e2","type":"todo","text":"two","ts":"2026-11-11T09:01:00.000Z"}""";
        File.WriteAllText(ledger, a + "\n" + c[..20]);   // the second line half-written
        var b = new Board();
        var f = new BoardFiles(b);
        f.Follow(repo);
        f.Poll();
        Assert.Equal(["one"], b.Open(repo).Select(i => i.Text));
        File.AppendAllText(ledger, c[20..] + "\n");
        Assert.True(f.Poll());
        Assert.Equal(2, b.Open(repo).Count);
        // replaced by a different history of the same length or longer (a hub replica rewritten)
        File.WriteAllText(ledger + ".new", """{"id":"e1","type":"todo","text":"ONE","ts":"2026-11-11T09:00:00.000Z"}""" + "\n" + c + "\n");
        File.Move(ledger + ".new", ledger, true);
        Assert.True(f.Poll());
        Assert.Equal(["two", "ONE"], b.Open(repo).Select(i => i.Text));
        File.WriteAllText(ledger, "");   // emptied
        Assert.True(f.Poll());
        Assert.Empty(b.Open(repo));
    }

    [Fact]
    public void FindsTheRepoAndTheWatchedList()
    {
        var repo = Path.Combine(dir, "r");
        Directory.CreateDirectory(Path.Combine(repo, ".sankofa"));
        Directory.CreateDirectory(Path.Combine(repo, "src", "deep"));
        Assert.Equal(repo, BoardFiles.FindRoot(Path.Combine(repo, "src", "deep")));
        var reg = Path.Combine(dir, "watched");
        File.WriteAllText(reg, $"{repo}\n\n{repo}\n/elsewhere\n");
        Assert.Equal([repo, "/elsewhere"], BoardFiles.Watched(reg));
        Assert.Empty(BoardFiles.Watched(Path.Combine(dir, "none")));
    }
}
