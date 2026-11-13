using System.Globalization;

namespace Sankofa.Tui;

/// <summary>
/// What the dashboard shows, as rows, from a <see cref="Board"/>: the panels, the text each row
/// reads as, and the command a row stands for. Shared by the window and <c>summary</c>.
/// </summary>
sealed class Snapshot
{
    public required List<Board.Item> Waiting, Open;
    public required List<Board.LeaseRow> Leases;
    public required List<Board.Word> Words;
    public required List<Board.Alert> Alerts;
    public required List<string> Repos, Missing;
    public required DateTimeOffset Now;
    /// <summary>Show the repo on each row (more than one repo on the board).</summary>
    public bool Many => Repos.Count > 1;

    public static Snapshot Of(Board b, string? repo, IEnumerable<string> missing, DateTimeOffset now) => new()
    {
        // a handoff waits on whoever picks it up, like an awaiting item waits on the human
        Waiting = b.Open(repo, "awaiting", "handoff"),
        Open = b.Open(repo, "todo"),
        Leases = b.Leases(now, repo),
        Words = b.Words(repo),
        Alerts = b.Alerts(repo, 50),
        Repos = repo is null ? b.Repos() : [repo],
        Missing = missing.ToList(),
        Now = now,
    };

    public string Ago(string ts)
    {
        if (!DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) return "?";
        var d = Now - t;
        return d.TotalSeconds < 60 ? "now"
            : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}m"
            : d.TotalHours < 48 ? $"{(int)d.TotalHours}h"
            : $"{(int)d.TotalDays}d";
    }

    public static string Name(string repo) => Path.GetFileName(repo.TrimEnd('/')) is { Length: > 0 } n ? n : repo;
    string In(string repo) => Many ? $"{Name(repo)}  " : "";
    static string Flat(string s) => Safe.Printable(s);   // one line, and no escape sequence from another writer reaches the terminal
    static string By(string who) => who.Length > 0 ? $"  ({who})" : "";

    // id and age first, then the text, the writer last: a narrow panel cuts the end, not the point
    public string Row(Board.Item i) => i.Type == "handoff"
        ? $"{i.Id,-5} {Ago(i.Ts),4}  {In(i.Repo)}handoff {i.Branch?.Split('/', 2)[^1]}: {Flat(i.Text)}{By(i.Who)}"
        : $"{i.Id,-5} {Ago(i.Ts),4}  {In(i.Repo)}{Flat(i.Text)}{By(i.Who)}";

    public string Row(Board.LeaseRow l) =>
        $"{l.Id,-5} {(l.State == "held" ? "held" : "ended?"),-6}  {In(l.Repo)}{Flat(l.Glob)}{By(l.Who)}";

    public string Row(Board.Word w) => $"{Ago(w.Ts),4}  {In(w.Repo)}{(w.Who.Length > 0 ? w.Who : "you")}: {Flat(w.Text)}";
    public string Row(Board.Alert a) => $"{Ago(a.Ts),4}  {In(a.Repo)}{a.Type}: {Flat(a.Text)}";

    /// <summary>The CLI line a row stands for: what Space copies and what the Item menu runs.</summary>
    public static string Command(Board.Item i) => (i.Type == "handoff" ? "sankofa pickup " : "sankofa done ") + Safe.ShellWord(i.Id);
}
