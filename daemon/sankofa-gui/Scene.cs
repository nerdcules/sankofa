using System.Globalization;
using System.Text;

namespace Sankofa.Gui;

/// <summary>What a row of the ledger is, for colour and for what it asks of you.</summary>
enum Kind { Waiting, Handoff, Todo, Held, Half, Queued, Contested, Landed, Overlap, Flag, Word }

/// <summary>Anything that can be clicked: what the side panel shows for it, and the CLI line it stands for.</summary>
sealed record Pick(string Key, Kind Kind, string Title, string Text, string Repo, string Who, string Ts, string? Command);

/// <summary>A session (or subagent) seen in the ledger, for the constellation.</summary>
sealed record Node(string Key, string Label, string Sub, double Age, int Leases, Kind Kind, Pick Pick);

/// <summary>A device (from the hub's @device on events; "this machine" without a hub), for the map.</summary>
sealed record Device(string Name, double Age, int Sessions, int Leases, int Queued, bool Remote, Pick Pick);

/// <summary>A leased or contested path, for the heatmap.</summary>
sealed record Tile(string Repo, string Glob, Kind State, string Who, double Age, Pick Pick);

/// <summary>Anything in the ledger worth a look, placed by age, for the radar.</summary>
sealed record Blip(string Key, Kind Kind, double Angle, double Distance, double Age, string Label, Pick Pick);

/// <summary>
/// The four pictures the window draws, from one <see cref="Board"/> at one moment. Pure: no
/// window, no clock, so the same ledger and the same now always give the same scene (and the
/// tests can read it as text).
/// </summary>
sealed class Scene
{
    public required List<Pick> Waiting, Open;
    public required List<Node> Nodes;
    public required List<Device> Devices;
    public required List<Tile> Tiles;
    public required List<Blip> Blips;
    public required List<string> Repos;
    public required int Leases, Alerts, Events;
    public required double LastAge;
    public required string? Overlap;
    public bool Hub => Devices.Any(d => !d.Remote && d.Name != Local);
    public const string Local = "this machine";

    public static string Name(string repo)
    {
        var n = Path.GetFileName(repo.TrimEnd('/'));
        if (n.EndsWith(".jsonl", StringComparison.Ordinal)) n = n[..^6];   // a ledger file named on its own
        return n.Length > 0 ? n : repo;
    }

    public static double Age(string ts, DateTimeOffset now) =>
        DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? Math.Max(0, (now - t).TotalSeconds) : double.MaxValue;

    public static string Ago(double s) =>
        s == double.MaxValue ? "?" : s < 60 ? "now" : s < 3600 ? $"{(int)(s / 60)}m" : s < 172800 ? $"{(int)(s / 3600)}h" : $"{(int)(s / 86400)}d";

    /// <summary>"s-91cc @laptop" → ("s-91cc", "laptop"); no device → this machine.</summary>
    public static (string Who, string Device) Split(string who)
    {
        var at = who.LastIndexOf(" @", StringComparison.Ordinal);
        if (who.StartsWith('@')) return ("", who[1..]);
        return at < 0 ? (who, Local) : (who[..at], who[(at + 2)..]);
    }

    /// <summary>The path an overlap or contention alert is about: its text starts with it.</summary>
    public static string AlertPath(string text)
    {
        var cut = text.IndexOf(" is changed", StringComparison.Ordinal);
        var head = cut > 0 ? text[..cut] : text.Split(' ', 2)[0];
        return head.Trim();
    }

    /// <summary>Stable angle for a key, so a blip stays where it was between polls.</summary>
    static double Angle(string key)
    {
        uint h = 2166136261;
        foreach (var c in key) h = (h ^ c) * 16777619;
        return h % 3600 / 10.0;
    }

    /// <summary>Radar distance: now at the centre, a day or older at the edge, minutes spread out.</summary>
    public static double Distance(double age) =>
        Math.Clamp(Math.Log(1 + age / 60) / Math.Log(1 + 1440), 0.06, 1);

    static string Flat(string s) => Safe.Printable(s);

    public static Scene Of(Board b, DateTimeOffset now)
    {
        var repos = b.Repos();
        Pick ItemPick(Board.Item i) => new(i.Repo + "/" + i.Id,
            i.Type switch { "handoff" => Kind.Handoff, "awaiting" => Kind.Waiting, _ => Kind.Todo },
            i.Type == "handoff" ? $"{i.Id} handoff {i.Branch?.Split('/', 2)[^1]}" : $"{i.Id} {i.Type}",
            Flat(i.Text), i.Repo, i.Who, i.Ts, (i.Type == "handoff" ? "sankofa pickup " : "sankofa done ") + Safe.ShellWord(i.Id));

        var waiting = b.Open(null, "awaiting", "handoff").Select(ItemPick).ToList();
        var open = b.Open(null, "todo").Select(ItemPick).ToList();
        var leases = b.Leases(now);
        var alerts = b.Alerts(null, 50);
        var words = b.Words(null, 40);

        var recentConflicts = alerts.Where(a => a.Type is "overlap" or "contend" && Age(a.Ts, now) < 86400).ToList();
        bool Contested(Board.LeaseRow l) => recentConflicts.Any(a => a.Repo == l.Repo && Glob.Overlap(l.Glob, AlertPath(a.Text)));

        // the heatmap: every lease worth seeing, and overlapping paths nobody holds
        var tiles = new List<Tile>();
        foreach (var l in leases)
        {
            var state = l.Id.StartsWith('q') ? Kind.Queued : Contested(l) ? Kind.Contested : l.State == "held" ? Kind.Held : Kind.Half;
            var text = state switch
            {
                Kind.Queued => "Claimed while the hub was unreachable; sent when it answers.",
                Kind.Contested => "Changed in more than one tree. Tell the human before merging.",
                Kind.Held => $"Held until {l.Expires}.",
                _ => "The lease ended with the paths possibly half-finished.",
            };
            tiles.Add(new Tile(l.Repo, l.Glob, state, l.Who, Age(l.Claimed, now),
                new Pick(l.Repo + "/" + l.Id, state, $"{l.Id} {Flat(l.Glob)}", text, l.Repo, l.Who, l.Claimed, "sankofa ask " + Safe.ShellWord(l.Glob))));
        }
        foreach (var a in recentConflicts)
        {
            var path = AlertPath(a.Text);
            if (tiles.Any(t => t.Repo == a.Repo && Glob.Overlap(t.Glob, path))) continue;
            tiles.Add(new Tile(a.Repo, path, Kind.Overlap, "", Age(a.Ts, now),
                new Pick(a.Repo + "/" + a.Id, Kind.Overlap, $"{a.Id} {a.Type}", Flat(a.Text), a.Repo, "", a.Ts, "sankofa ask " + Safe.ShellWord(path))));
        }

        // the constellation: each session's newest word or claim, one node per session whichever
        // device field its events carried (a hub stamps some, a local fallback write none)
        var nodes = new Dictionary<string, (string Device, string Repo, string Ts, string Text, int Leases, bool Dirty)>();
        void Seen(string who, string repo, string ts, string text, int lease, bool dirty)
        {
            var (w, d) = Split(who);
            if (w.Length == 0) w = d == Local ? "you" : "@" + d;
            if (nodes.TryGetValue(w, out var n))
            {
                var newer = string.CompareOrdinal(ts, n.Ts) > 0;
                nodes[w] = (n.Device != Local ? n.Device : d, newer ? repo : n.Repo, newer ? ts : n.Ts,
                    newer && text.Length > 0 || n.Text.Length == 0 ? (text.Length > 0 ? text : n.Text) : n.Text, n.Leases + lease, n.Dirty || dirty);
            }
            else nodes[w] = (d, repo, ts, text, lease, dirty);
        }
        foreach (var w in words) Seen(w.Who, w.Repo, w.Ts, Flat(w.Text), 0, false);
        foreach (var l in leases) Seen(l.Who, l.Repo, l.Claimed, "", l.State == "held" ? 1 : 0, l.State != "held");
        var nodeList = nodes.OrderByDescending(kv => kv.Value.Ts, StringComparer.Ordinal).Take(10).Select(kv =>
        {
            var who = kv.Key;
            var (dev, repo, ts, text, held, dirty) = kv.Value;
            var kind = dirty ? Kind.Half : held > 0 ? Kind.Held : Kind.Word;
            var label = who.Length > 18 ? who[..8] + "…" + who[^6..] : who;
            var sub = Name(repo) + (dev != Local ? " · " + dev : "") + (held > 0 ? $" · {held} held" : "");
            var full = dev != Local ? $"{who} @{dev}" : who;
            return new Node(full, label, sub, Age(ts, now), held, kind,
                new Pick("s/" + full, kind, full, text.Length > 0 ? text : "No word yet; holds paths.", repo, full, ts, null));
        }).ToList();

        // the map: devices, from what each wrote
        var devs = new Dictionary<string, (string Ts, HashSet<string> Sessions, int Leases, int Queued)>();
        void On(string who, string ts, bool lease, bool queued)
        {
            var (w, d) = Split(who);
            if (!devs.TryGetValue(d, out var v)) devs[d] = v = ("", [], 0, 0);
            if (w.Length > 0) v.Sessions.Add(w);
            devs[d] = (string.CompareOrdinal(ts, v.Ts) > 0 ? ts : v.Ts, v.Sessions, v.Leases + (lease ? 1 : 0), v.Queued + (queued ? 1 : 0));
        }
        foreach (var w in words) On(w.Who, w.Ts, false, false);
        foreach (var l in leases) On(l.Who, l.Claimed, l.State == "held", l.Id.StartsWith('q'));
        foreach (var i in waiting.Concat(open)) On(i.Who, i.Ts, false, i.Title.StartsWith('q'));
        var devices = devs.OrderBy(kv => kv.Key == Local ? 0 : 1).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
        {
            var (ts, sessions, held, queued) = kv.Value;
            var text = $"{sessions.Count} session{(sessions.Count == 1 ? "" : "s")}, {held} lease{(held == 1 ? "" : "s")} held"
                + (queued > 0 ? $", {queued} written while the hub was unreachable" : "") + ".";
            return new Device(kv.Key, Age(ts, now), sessions.Count, held, queued, false,
                new Pick("d/" + kv.Key, Kind.Word, kv.Key, text, "", string.Join(", ", sessions.Order(StringComparer.Ordinal)), ts, kv.Key == Local ? "sankofa leases" : "sankofa hub"));
        }).ToList();
        var landed = alerts.Where(a => a.Type == "landed").ToList();
        if (landed.Count > 0)
        {
            var a = landed[0];
            devices.Add(new Device("remote", Age(a.Ts, now), 0, 0, 0, true,
                new Pick("d/remote", Kind.Landed, "remote · landed", Flat(a.Text), a.Repo, "", a.Ts, "git fetch && git log --oneline @{u} -5")));
        }

        // the radar: everything, by age
        var blips = new List<Blip>();
        void Add(Pick p, string label) => blips.Add(new Blip(p.Key, p.Kind, Angle(p.Key), Distance(Age(p.Ts, now)), Age(p.Ts, now), label, p));
        foreach (var p in waiting) Add(p, p.Title.Split(' ')[0] + (p.Kind == Kind.Handoff ? " handoff" : " waits"));
        foreach (var p in open) Add(p, p.Title.Split(' ')[0]);
        foreach (var t in tiles) Add(t.Pick, t.Glob.Length > 22 ? "…" + t.Glob[^20..] : t.Glob);
        foreach (var a in alerts.Where(a => a.Type is "landed" or "flag").Take(12))
            Add(new Pick(a.Repo + "/" + a.Id, a.Type == "landed" ? Kind.Landed : Kind.Flag, $"{a.Id} {a.Type}", Flat(a.Text), a.Repo, "", a.Ts, null),
                a.Type == "landed" ? "landed" : "flag");
        foreach (var n in nodeList.Where(n => n.Kind == Kind.Word).Take(8))
            Add(n.Pick with { Kind = Kind.Word }, n.Label);

        var last = b.LastTs();
        return new Scene
        {
            Waiting = waiting, Open = open, Nodes = nodeList, Devices = devices, Tiles = tiles, Blips = blips,
            Repos = repos, Leases = leases.Count, Alerts = alerts.Count, Events = b.Events(),
            LastAge = last.Length > 0 ? Age(last, now) : double.MaxValue,
            Overlap = recentConflicts.FirstOrDefault() is { } o ? AlertPath(o.Text) : null,
        };
    }

    /// <summary>The scene as text: what <c>sankofa-gui scene</c> prints, and what the tests read.</summary>
    public string Text()
    {
        var o = new StringBuilder();
        o.Append($"repos {Repos.Count} · events {Events} · waiting {Waiting.Count} · open {Open.Count} · leases {Leases} · alerts {Alerts}\n");
        foreach (var n in Nodes) o.Append($"node    {n.Kind,-9} {n.Label}  ({n.Sub})  {Ago(n.Age)}\n");
        foreach (var d in Devices) o.Append($"device  {(d.Remote ? "remote" : "local"),-9} {d.Name}  sessions {d.Sessions} leases {d.Leases} queued {d.Queued}  {Ago(d.Age)}\n");
        foreach (var t in Tiles) o.Append($"tile    {t.State,-9} {Name(t.Repo)} {t.Glob}  {t.Who}\n");
        foreach (var b in Blips) o.Append($"blip    {b.Kind,-9} {b.Label}  d={b.Distance:0.00}\n");
        if (Overlap is not null) o.Append($"overlap {Overlap}\n");
        return o.ToString();
    }
}
