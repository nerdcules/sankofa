using System.Text;
using System.Text.Json;

namespace Sankofa;

/// <summary>
/// Folds ledger events into what a person wants to see at a glance: what waits on them, what is
/// open, who holds which paths, the last word of each session, and what happened without anyone
/// asking. It only reads. The same rules as the daemon's: a done finishes an item or a lease (and
/// older half-finished leases under its glob), and without a daemon writing expire events a held
/// lease past its time counts as expired.
/// </summary>
public sealed class Board
{
    public sealed record Item(string Repo, string Id, string Type, string Text, string Ts, string Who, string? Branch);
    public sealed record LeaseRow(string Repo, string Id, string Glob, string State, bool Dirty, string Who, string Claimed, string Expires);
    public sealed record Word(string Repo, string Session, string Who, string Ts, string Type, string Text);
    public sealed record Alert(string Repo, string Id, string Type, string Ts, string Text);

    sealed class L
    {
        public required string Id, Glob, Who, Claimed;
        public string Expires = "", State = "held";
        public bool Dirty = true;
    }

    sealed class Repo
    {
        public readonly Dictionary<string, Item> Items = [];
        public readonly Dictionary<string, L> Leases = [];
        /// <summary>Ended leases still marked half-finished: the only ones a later done can clear,
        /// kept apart so a done does not look at every lease the repo ever had.</summary>
        public readonly HashSet<L> DirtyEnded = [];
        public readonly Dictionary<string, Word> Words = [];   // by session
        public readonly List<Alert> Alerts = [];
        public int Events;
        public string LastTs = "";
    }

    readonly Dictionary<string, Repo> repos = [];

    public static string Who(Entry e)
    {
        var s = e.Agent == "main" ? e.Session : e.Agent;
        if (s == "-") s = "";   // written by hand, outside any agent session
        if (s.Length > 28) s = s[..12] + "…" + s[^8..];
        return e.Device is { Length: > 0 } d ? (s.Length > 0 ? $"{s} @{d}" : $"@{d}") : s;
    }

    public void Reset(string repo) => repos.Remove(repo);

    public void Ingest(string repo, Entry e)
    {
        if (!repos.TryGetValue(repo, out var r)) repos[repo] = r = new Repo();
        r.Events++;
        if (string.CompareOrdinal(e.Ts, r.LastTs) > 0) r.LastTs = e.Ts;
        switch (e.Type)
        {
            case "todo" or "awaiting" or "handoff":
                r.Items[e.Id] = new Item(repo, e.Id, e.Type, e.Text, e.Ts, Who(e), e.Branch);
                break;
            case "claim" when e.Glob is not null:
                r.Leases[e.Id] = new L { Id = e.Id, Glob = e.Glob, Who = Who(e), Claimed = e.Ts, Expires = e.Expires ?? "" };
                break;
            case "renew" when e.Ref is not null && r.Leases.TryGetValue(e.Ref, out var l):
                l.Expires = e.Expires ?? l.Expires;
                break;
            case "release" when e.Ref is not null && r.Leases.TryGetValue(e.Ref, out var l):
                l.State = "released";
                if (l.Dirty) r.DirtyEnded.Add(l);
                break;
            case "expire" when e.Ref is not null && r.Leases.TryGetValue(e.Ref, out var l):
                l.State = "expired";
                if (l.Dirty) r.DirtyEnded.Add(l);
                break;
            case "done" when e.Ref is not null:
                // a resolved item or a finished lease is never shown again, so neither is kept
                if (r.Items.Remove(e.Ref)) break;
                if (r.Leases.Remove(e.Ref, out var d))
                {
                    d.State = "done"; d.Dirty = false;
                    r.DirtyEnded.Remove(d);
                    // the done vouches for what it covers: ended leases there are no longer half-finished
                    foreach (var old in r.DirtyEnded.Where(o => Glob.Overlap(d.Glob, o.Glob)).ToList())
                    {
                        old.Dirty = false; r.DirtyEnded.Remove(old); r.Leases.Remove(old.Id);
                    }
                }
                break;
            case "overlap" or "landed" or "flag" or "contend":
                r.Alerts.Add(new Alert(repo, e.Id, e.Type, e.Ts, e.Text));
                if (r.Alerts.Count > 200) r.Alerts.RemoveAt(0);
                break;
        }
        // a Stop entry with no text says only that the session ended
        if (e.Type is "note" or "stop" && !(e.Type == "stop" && e.Text.Length == 0) && e.Session != "sankofad")
            r.Words[e.Session] = new Word(repo, e.Session, Who(e), e.Ts, e.Type, e.Text);
    }

    IEnumerable<Repo> Pick(string? repo) =>
        repos.Where(kv => repo is null || kv.Key == repo).Select(kv => kv.Value);

    public List<string> Repos() => repos.Keys.Order(StringComparer.Ordinal).ToList();

    public List<Item> Open(string? repo = null, params string[] types) =>
        Pick(repo).SelectMany(r => r.Items.Values)
            .Where(i => types.Length == 0 || types.Contains(i.Type))
            .OrderByDescending(i => i.Ts, StringComparer.Ordinal).ToList();

    /// <summary>Held leases, and ended ones still marked half-finished; held first, newest first.</summary>
    public List<LeaseRow> Leases(DateTimeOffset now, string? repo = null)
    {
        var at = Store.Ts(now);
        return repos.Where(kv => repo is null || kv.Key == repo)
            .SelectMany(kv => kv.Value.Leases.Values.Select(l =>
            {
                var state = l.State == "held" && l.Expires.Length > 0 && string.CompareOrdinal(l.Expires, at) <= 0 ? "expired" : l.State;
                return new LeaseRow(kv.Key, l.Id, l.Glob, state, l.Dirty, l.Who, l.Claimed, l.Expires);
            }))
            .Where(l => l.State == "held" || (l.Dirty && l.State is "released" or "expired"))
            .OrderBy(l => l.State == "held" ? 0 : 1).ThenByDescending(l => l.Claimed, StringComparer.Ordinal).ToList();
    }

    public List<Word> Words(string? repo = null, int limit = 30) =>
        Pick(repo).SelectMany(r => r.Words.Values).OrderByDescending(w => w.Ts, StringComparer.Ordinal).Take(limit).ToList();

    public List<Alert> Alerts(string? repo = null, int limit = 30) =>
        Pick(repo).SelectMany(r => r.Alerts).OrderByDescending(a => a.Ts, StringComparer.Ordinal).Take(limit).ToList();

    public int Events(string? repo = null) => Pick(repo).Sum(r => r.Events);
    public string LastTs(string? repo = null) => Pick(repo).Select(r => r.LastTs).DefaultIfEmpty("").Max(StringComparer.Ordinal)!;
}

/// <summary>
/// Follows ledger files into a <see cref="Board"/>: reads what was appended since the last look,
/// leaves a half-written last line for the next look, and starts the repo over when the file
/// shrank or its first or last-read bytes changed (a hub replica rewritten). Polled, so it works the same on every
/// platform and with no daemon running.
/// </summary>
public sealed class BoardFiles
{
    sealed class Followed(string repo)
    {
        public readonly string Repo = repo;
        public long Offset;
        /// <summary>The file's first bytes, and the bytes just before Offset, when last read: if
        /// the file no longer has them there, it was replaced (a replica rewritten), not grown.</summary>
        public byte[] Head = [], Tail = [];
        public readonly List<byte> Partial = [];
    }

    readonly Board board;
    readonly Dictionary<string, Followed> files = [];

    public BoardFiles(Board board) { this.board = board; }

    /// <summary>A repo root, or a ledger file. Returns the repo name it is shown under.</summary>
    public static (string Repo, string Ledger) Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) && full.EndsWith(".jsonl", StringComparison.Ordinal))
        {
            var dir = Path.GetDirectoryName(full)!;
            var repo = Path.GetFileName(dir) == ".sankofa" ? Path.GetDirectoryName(dir)! : full;
            return (repo, full);
        }
        return (full.TrimEnd('/'), Path.Combine(full, ".sankofa", "ledger.jsonl"));
    }

    /// <summary>The repos <c>sankofa watch add</c> registered.</summary>
    public static List<string> Watched(string? registry = null)
    {
        registry ??= Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c
            ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "sankofa", "watched");
        try { return File.ReadAllLines(registry).Where(l => l.Length > 0).Distinct().ToList(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>The nearest directory at or above <paramref name="dir"/> holding .sankofa/.</summary>
    public static string? FindRoot(string dir)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(dir)); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".sankofa"))) return d.FullName;
        return null;
    }

    public void Follow(string path)
    {
        var (repo, ledger) = Resolve(path);
        files.TryAdd(ledger, new Followed(repo));
    }

    public IEnumerable<string> Ledgers => files.Keys;

    /// <summary>Read what is new in every followed file. True when anything changed.</summary>
    public bool Poll()
    {
        var changed = false;
        foreach (var (ledger, f) in files)
        {
            var repo = f.Repo;
            if (!File.Exists(ledger)) continue;
            byte[] chunk;
            try
            {
                using var fs = new FileStream(ledger, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < f.Offset || (f.Offset > 0 && !Same(fs, f)))
                {
                    board.Reset(repo); f.Offset = 0; f.Head = []; f.Tail = []; f.Partial.Clear(); changed = true;
                }
                if (fs.Length == f.Offset) continue;
                fs.Seek(f.Offset, SeekOrigin.Begin);
                chunk = ReadAll(fs, fs.Length - f.Offset);
            }
            catch (IOException) { continue; }
            var keep = (int)Math.Min(64, f.Offset + chunk.Length);
            f.Tail = [.. f.Tail.Concat(chunk).TakeLast(keep)];
            if (f.Head.Length < 64) f.Head = [.. f.Head.Concat(chunk).Take(64)];
            f.Offset += chunk.Length;
            foreach (var b in chunk)
            {
                if (b != '\n') { f.Partial.Add(b); continue; }
                var line = Encoding.UTF8.GetString([.. f.Partial]);
                f.Partial.Clear();
                if (line.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize(line, Json.Wire.Entry) is { } e) { board.Ingest(repo, e); changed = true; }
                }
                catch (JsonException) { }   // a line that is not an event is skipped, as the CLI's jq would
            }
        }
        return changed;
    }

    static bool Same(FileStream fs, Followed f)
    {
        fs.Seek(0, SeekOrigin.Begin);
        if (!ReadAll(fs, f.Head.Length).AsSpan().SequenceEqual(f.Head)) return false;
        fs.Seek(f.Offset - f.Tail.Length, SeekOrigin.Begin);
        return ReadAll(fs, f.Tail.Length).AsSpan().SequenceEqual(f.Tail);
    }

    static byte[] ReadAll(FileStream fs, long count)
    {
        var buf = new byte[count];
        var n = 0;
        while (n < buf.Length && fs.Read(buf, n, buf.Length - n) is var k and > 0) n += k;
        if (n < buf.Length) Array.Resize(ref buf, n);
        return buf;
    }
}
