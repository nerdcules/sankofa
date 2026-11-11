// sankofa-tui: who holds what, what is open, what waits on you, across your sankofa ledgers,
// in the terminal.
//
//   sankofa-tui [open] [repo...]           the dashboard (default: this repo, else the watched ones)
//   sankofa-tui summary [--json] [repo...]  the same as text, with no terminal needed
//   sankofa-tui version
//
// It follows the ledger files, as the Qt dashboard does: each is the daemon's mirror, and the
// only copy for repos shared through a hub and for repos with no daemon running.
using System.Text;
using System.Text.Json;
using Sankofa;
using Sankofa.Tui;

var argv = args.ToList();
var command = argv.Count > 0 && argv[0] is "open" or "summary" or "version" or "--version" or "help" or "--help" or "-h"
    ? argv[0] : "open";
if (argv.Count > 0 && argv[0] == command) argv.RemoveAt(0);
string? registry = null;
var json = false;
var repos = new List<string>();
for (var i = 0; i < argv.Count; i++)
    switch (argv[i])
    {
        case "--json": json = true; break;
        case "--registry" when i + 1 < argv.Count: registry = argv[++i]; break;
        default: repos.Add(argv[i]); break;
    }

switch (command)
{
    case "version" or "--version":
        Console.WriteLine($"sankofa-tui {Store.Version}");
        return 0;
    case "help" or "--help" or "-h":
        Console.WriteLine("usage: sankofa-tui [open] [repo...] | summary [--json] [repo...] | version");
        Console.WriteLine("  repos default to the one you are in, else the ones `sankofa watch add` registered");
        return 0;
}

if (repos.Count == 0)
{
    if (BoardFiles.FindRoot(Environment.CurrentDirectory) is { } here) repos.Add(here);
    else repos.AddRange(BoardFiles.Watched(registry));
}
var board = new Board();
var files = new BoardFiles(board);
foreach (var r in repos) files.Follow(r);
files.Poll();
IEnumerable<string> Missing() => files.Ledgers.Where(l => !File.Exists(l));

if (command == "summary")
{
    var s = Snapshot.Of(board, null, Missing(), DateTimeOffset.UtcNow);
    Console.Out.Write(json ? Summary.Json(s) : Summary.Text(s));
    return 0;
}
if (repos.Count == 0)
{
    Console.Error.WriteLine("sankofa-tui: no repo here and none watched (cd into one, name one, or sankofa watch add)");
    return 1;
}
return new Dashboard(board, files, Missing).Run();

static class Summary
{
    public static string Text(Snapshot s)
    {
        var o = new StringBuilder();
        void Section<T>(string title, List<T> rows, Func<T, string> line)
        {
            o.Append($"{title} ({rows.Count})\n");
            foreach (var r in rows) o.Append("  ").Append(line(r)).Append('\n');
        }
        Section("Waiting on you", s.Waiting, s.Row);
        Section("Open", s.Open, s.Row);
        Section("Leases", s.Leases, s.Row);
        Section("Last word", s.Words, s.Row);
        Section("Alerts", s.Alerts, s.Row);
        foreach (var m in s.Missing) o.Append($"missing ledger: {m}\n");
        return o.ToString();
    }

    public static string Json(Snapshot s)
    {
        using var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            void Items(string name, List<Board.Item> items)
            {
                w.WriteStartArray(name);
                foreach (var i in items)
                {
                    w.WriteStartObject();
                    w.WriteString("id", i.Id); w.WriteString("type", i.Type); w.WriteString("text", i.Text); w.WriteString("ts", i.Ts);
                    w.WriteString("repo", i.Repo); w.WriteString("who", i.Who); w.WriteString("command", Snapshot.Command(i));
                    if (i.Branch is not null) w.WriteString("branch", i.Branch);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            Items("waiting", s.Waiting);
            Items("todos", s.Open);
            w.WriteStartArray("leases");
            foreach (var l in s.Leases)
            {
                w.WriteStartObject();
                w.WriteString("id", l.Id); w.WriteString("glob", l.Glob); w.WriteString("state", l.State); w.WriteBoolean("dirty", l.Dirty);
                w.WriteString("claimed", l.Claimed); w.WriteString("expires", l.Expires); w.WriteString("repo", l.Repo); w.WriteString("who", l.Who);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("handoffs");
            foreach (var h in s.Words)
            {
                w.WriteStartObject();
                w.WriteString("session", h.Session); w.WriteString("type", h.Type); w.WriteString("text", h.Text); w.WriteString("ts", h.Ts);
                w.WriteString("repo", h.Repo); w.WriteString("who", h.Who);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("alerts");
            foreach (var a in s.Alerts)
            {
                w.WriteStartObject();
                w.WriteString("id", a.Id); w.WriteString("type", a.Type); w.WriteString("text", a.Text); w.WriteString("ts", a.Ts); w.WriteString("repo", a.Repo);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("repos"); foreach (var r in s.Repos) w.WriteStringValue(r); w.WriteEndArray();
            w.WriteStartArray("missing"); foreach (var m in s.Missing) w.WriteStringValue(m); w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.ToArray()) + "\n";
    }
}
