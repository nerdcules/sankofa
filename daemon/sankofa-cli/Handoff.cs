using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sankofa.Cli;

/// <summary>
/// handoff and pickup. A handoff is a branch and a letter. The branch is pushed; the letter (the
/// text plus the open items) travels as a tag object at refs/sankofa/handoff/&lt;branch&gt; on the same
/// remote, so a teammate with nothing but the git remote can pick it up, and as a handoff event in
/// the ledger, which a hub shares. pickup takes it: switches to the branch and removes the ref from
/// the remote, which tells the sender's bridge (and anyone else's) that it was taken.
///
/// A handoff or pickup that dies halfway (a killed session, a dropped connection) is finished by
/// running it again: each keeps a journal in .sankofa/ while it runs, and sankofa open reports one
/// left behind. The remote decides who has a handoff: pickup removes the letter and, in the same
/// atomic push, leaves a marker at refs/sankofa/taken/&lt;branch&gt; naming it, so either side can tell
/// afterwards whether a letter it lost track of was taken or never arrived.
/// </summary>
public static partial class Handoff
{
    const string HX = ":(exclude).sankofa";
    const int GitTimeout = 600_000;   // pushes and fetches to a real remote take as long as they take

    // ------------------------------------------------------------------ git and journals

    /// <summary>git in the repo root; Out trimmed of its final newline, as a shell substitution would be.</summary>
    sealed class G(string root)
    {
        public string Root => root;
        public Git.Result R(params string[] a) => Git.RunTimed(root, GitTimeout, a);
        public bool Ok(params string[] a) => R(a).Ok;
        /// <summary>Output when the command succeeds, else null.</summary>
        public string? Try(params string[] a) { var r = R(a); return r.Ok ? r.Out.TrimEnd('\n') : null; }
        /// <summary>Output, or "" when the command fails.</summary>
        public string Q(params string[] a) => Try(a) ?? "";
        public string Short(string sha) => Q("rev-parse", "--short", sha);
    }

    static string JH(Ctx c) => Path.Combine(c.SankofaDir, "handoff.json");
    static string JP(Ctx c) => Path.Combine(c.SankofaDir, "pickup.json");

    static Dictionary<string, string> JRead(string path)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), Json.Wire.DictionaryStringString) ?? []; }
        catch (Exception e) when (e is JsonException or IOException) { return []; }
    }
    static string JGet(Dictionary<string, string> j, string k) => j.TryGetValue(k, out var v) ? v : "";
    static void JPut(string path, Dictionary<string, string> j)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(j, Json.Wire.DictionaryStringString) + "\n");
        File.Move(path + ".tmp", path, overwrite: true);
    }
    static void Rm(string path) { try { File.Delete(path); } catch (IOException) { } }

    /// <summary>Drops control bytes (escape sequences included) so text written by someone else cannot drive the terminal; keeps tabs and newlines.</summary>
    static string Printable(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (ch is '\t' or '\n' || (ch >= ' ' && ch != '\u007f')) sb.Append(ch);
        return sb.ToString();
    }

    static G GitHere(Ctx c, string verb)
    {
        var g = new G(c.Root);
        if (!g.Ok("rev-parse", "--git-dir")) throw new Exit($"{verb} needs a git repository");
        return g;
    }

    static void NoChanges(G g, string msg)
    {
        var ch = g.Q("status", "--porcelain", "--untracked-files=no", "--", ".", HX);
        if (ch.Length > 0) throw new Exit(msg + "\n" + ch);
    }

    /// <summary>rref: the remote's object id for each named ref ("" when it has none); null when the
    /// remote cannot be asked, which is not the same as having none.</summary>
    static List<string>? RRef(G g, string remote, params string[] refs)
    {
        var r = g.R(["ls-remote", remote, .. refs]);
        if (!r.Ok) return null;
        var have = new Dictionary<string, string>();
        foreach (var line in r.Out.Split('\n'))
        {
            var t = line.Split('\t');
            if (t.Length >= 2 && !have.ContainsKey(t[1])) have[t[1]] = t[0];
        }
        return refs.Select(x => have.TryGetValue(x, out var v) ? v : "").ToList();
    }

    static string TaggerName(string tagText)
    {
        foreach (var line in tagText.Split('\n'))
        {
            if (!line.StartsWith("tagger ", StringComparison.Ordinal)) continue;
            var i = line.LastIndexOf(" <", StringComparison.Ordinal);
            if (i < 0) return "";
            return line[7..i];
        }
        return "";
    }

    /// <summary>Who took letter <paramref name="tag"/> of <paramref name="rb"/> from the remote, by the marker there; "" when it was not taken.</summary>
    static string TakenBy(G g, string remote, string rb, string tag, string mark)
    {
        if (mark.Length == 0) return "";
        if (!g.Ok("fetch", "-q", "--no-tags", remote, $"refs/sankofa/taken/{rb}")) return "";
        if (g.Q("rev-parse", "FETCH_HEAD") != mark) return "";
        var t = g.Q("cat-file", "tag", "FETCH_HEAD");
        var first = t.Split('\n')[0];
        if (!first.StartsWith("object ", StringComparison.Ordinal) || first[7..] != tag) return "";
        return TaggerName(t);
    }

    static string? LastHandoffId(List<Entry> all, string letter)
    {
        string? id = null;
        foreach (var e in all) if (e.Type == "handoff" && e.Letter == letter) id = e.Id;
        return id;
    }

    // ------------------------------------------------------------------ handoff

    /// <summary>Offer the letter in the journal unless it was taken meanwhile, record it, drop the journal.</summary>
    static int FinishHandoff(Ctx c, G g)
    {
        var jh = JH(c);
        var j = JRead(jh);
        string remote = JGet(j, "remote"), rb = JGet(j, "rb"), sha = JGet(j, "sha"), tag = JGet(j, "tag"), old = JGet(j, "old"), text = JGet(j, "text");
        var st = RRef(g, remote, $"refs/sankofa/handoff/{rb}", $"refs/sankofa/taken/{rb}")
            ?? throw new Exit($"cannot reach {remote} to finish the handoff of {rb}; run sankofa handoff again when it answers");
        string now = st[0], mk = st[1], by = "";
        if (now == tag) { }
        else if ((by = Printable(TakenBy(g, remote, rb, tag, mk))).Length > 0) { }
        else if (now == old || now.Length == 0)
        {
            // --force-with-lease: replace only what was there when this handoff began (nothing, or
            // this branch's previous letter), never a letter someone else wrote meanwhile
            var p = g.R("push", "-q", $"--force-with-lease=refs/sankofa/handoff/{rb}:{now}", remote, $"{tag}:refs/sankofa/handoff/{rb}");
            if (!p.Ok)
            {
                var err = (p.Out + p.Err).Trim();
                var st2 = RRef(g, remote, $"refs/sankofa/handoff/{rb}");
                if ((st2 is null ? "" : st2[0]) != tag)
                {
                    if (!c.OnHub) throw new Exit($"the branch is pushed, but {remote} refused refs/sankofa/handoff/{rb}: {err}");
                    Out.Err($"sankofa: {remote} refused the handoff ref; the hub ledger carries the handoff");
                }
            }
        }
        else
        {
            Rm(jh);
            throw new Exit($"{rb} was handed off again by someone else meanwhile; this handoff is dropped");
        }
        var all = Ledger.Read(c);
        var id = LastHandoffId(all, tag) ?? Ledger.Append(c, "handoff", text, x: new Ledger.Extra($"{remote}/{rb}", sha, tag));
        Rm(jh);
        // this branch's earlier handoffs from here are replaced by this one (a hub ledger keeps them
        // open otherwise; without a hub the bridge closes them when it sees the letter replaced)
        var me = Env.ThisDevice;
        all = Ledger.Read(c);
        foreach (var o in Ledger.Open(all).Where(i => i.Type == "handoff").Select(i => i.Id))
        {
            if (o == id) continue;
            var e = all.FirstOrDefault(e => e.Id == o && e.Branch == $"{remote}/{rb}"
                && ((e.Letter is not null && e.Letter == old) || (e.Device ?? "") == me || (e.Device is null && e.Session != "sankofad")));
            if (e is null) continue;
            try { Items.Done(c, o, $"superseded by {id}"); } catch (Exit) { }
        }
        // the work is the teammate's now: the leases this session holds here would only block them
        // (through a hub, on every device) until they expired
        var fin = Daemon.Call(new Request { Verb = "finish", Repo = c.Root, Session = Env.SessionId, Text = $"handed off as {id}" });
        if (fin.Ok)
        {
            var n = fin.Resp!.Entries?.Count ?? 0;
            if (n != 0) Out.Err($"sankofa: finished {n} lease(s) this session held here");
        }
        if (by.Length > 0)
        {
            try { Items.Done(c, id, $"picked up by {by}"); } catch (Exit) { }
            Out.Line($"handed off {rb} at {g.Short(sha)}; {by} has picked it up already  {id}");
            return 0;
        }
        Out.Line($"handed off {rb} at {g.Short(sha)} (pushed to {remote})  {id}");
        Out.Line($"on the other machine: sankofa pickup {rb}");
        return 0;
    }

    public static int Run(string[] args)
    {
        var c = Ctx.Need();
        var g = GitHere(c, "handoff");
        var br = g.Try("symbolic-ref", "-q", "--short", "HEAD") ?? throw new Exit("not on a branch; switch to the branch to hand off");
        if (br.Length == 0) throw new Exit("not on a branch; switch to the branch to hand off");
        var remote = g.Q("config", $"branch.{br}.remote");
        var rb = g.Q("config", $"branch.{br}.merge");
        if (rb.StartsWith("refs/heads/", StringComparison.Ordinal)) rb = rb[11..];
        var setup = false;
        if (remote.Length == 0 || remote == ".")
        {
            var remotes = g.Q("remote").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            remote = remotes.Contains("origin") ? "origin" : "";
            rb = br; setup = true;
            if (remote.Length == 0)
            {
                if (remotes.Length > 1) throw new Exit($"{br} tracks no remote and there are several; push it to the one to hand off through first (git push -u <remote> {br})");
                remote = remotes.Length == 1 ? remotes[0] : "";
            }
            if (remote.Length == 0) throw new Exit("no git remote to hand off through (git remote add origin <url>)");
        }
        var jh = JH(c);
        if (File.Exists(jh))
        {
            var j = JRead(jh);
            if (JGet(j, "stage") == "letter")
            {
                string jr = JGet(j, "rb"), js = JGet(j, "sha");
                Out.Err($"sankofa: finishing the handoff of {jr} that was interrupted");
                FinishHandoff(c, g);
                if (jr == rb && js == g.Q("rev-parse", "HEAD")) return 0;
            }
            else if (JGet(j, "rb") != rb)
                throw new Exit($"the handoff of {JGet(j, "rb")} was interrupted before its letter was written; switch to it and run sankofa handoff again");
        }
        NoChanges(g, "commit first; these changes would stay behind on this machine:");
        var un = g.Q("ls-files", "-o", "--exclude-standard", "--", ".", HX);
        if (un.Length > 0)
            Out.Err("sankofa: untracked files stay here: " + string.Concat(un.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(3).Select(f => f + " ")));
        var sha = g.Q("rev-parse", "HEAD");
        JPut(jh, new() { ["stage"] = "branch", ["remote"] = remote, ["rb"] = rb, ["sha"] = sha });
        // the teammate gets the remote branch: it must not have commits this one lacks
        if (g.Ok("fetch", "-q", remote, $"refs/heads/{rb}") && !g.Ok("merge-base", "--is-ancestor", "FETCH_HEAD", "HEAD"))
        {
            Rm(jh);
            throw new Exit($"{remote}/{rb} has commits this branch does not; pull them first");
        }
        var oldRef = RRef(g, remote, $"refs/sankofa/handoff/{rb}");
        if (oldRef is null) { Rm(jh); throw new Exit($"cannot reach {remote}"); }
        var old = oldRef[0];
        // the same handoff again (run twice, or again after a crash that came after it was finished)
        // changes nothing: same commit, same letter text, still on offer
        // (through a hub whose remote refuses the letter, the ledger alone says it is on offer)
        var last = g.Q("rev-parse", "-q", "--verify", $"refs/sankofa/handoff/{rb}");
        var textArg = string.Join(' ', args);
        if (last.Length > 0 && g.Q("rev-parse", $"{last}^{{commit}}") == sha)
        {
            var all = Ledger.Read(c);
            var stillOffered = old == last
                || (old.Length == 0 && c.OnHub && LastHandoffId(all, last) is { } lid && Ledger.Open(all).Any(i => i.Id == lid));
            if (stillOffered)
            {
                Entry? prev = null;
                foreach (var e in all) if (e.Type == "handoff" && e.Letter == last) prev = e;
                if (prev is not null && (textArg.Length == 0 || prev.Text == textArg))
                {
                    Rm(jh);
                    Out.Line($"already handed off {rb} at {g.Short(sha)}  {prev.Id}");
                    Out.Line($"on the other machine: sankofa pickup {rb}");
                    return 0;
                }
            }
        }
        var push = setup ? g.R("push", "-q", "-u", remote, $"HEAD:refs/heads/{rb}") : g.R("push", "-q", remote, $"HEAD:refs/heads/{rb}");
        if (!push.Ok) throw new Exit($"git push to {remote} failed; run sankofa handoff again");
        var text = textArg;
        if (text.Length == 0)
        {
            foreach (var e in Ledger.Read(c)) if (e.Type == "stop" && e.Text.Length > 0) text = e.Text;
        }
        if (text.Length == 0) { Rm(jh); throw new Exit("handoff needs text: what is done, what is next"); }
        var items = Ledger.Open(c).Where(i => i.Type != "handoff").Select(i => $"- {i.Id} {i.Type}: {i.Text}").ToList();
        var msg = items.Count == 0 ? text : text + "\n\nOpen items:\n" + string.Join("\n", items);
        var ident = g.Q("var", "GIT_COMMITTER_IDENT");
        var tagObj = Env.GitIn(c.Root, $"object {sha}\ntype commit\ntag handoff/{rb}\ntagger {ident}\n\n{msg}\n", "hash-object", "-t", "tag", "-w", "--stdin");
        var tag = tagObj.Ok ? tagObj.Out.Trim() : "";
        if (tag.Length == 0) throw new Exit("could not write the handoff letter");
        // this machine's own letters are known by this ref, whatever the ledger says
        g.R("update-ref", $"refs/sankofa/handoff/{rb}", tag);
        JPut(jh, new() { ["stage"] = "letter", ["remote"] = remote, ["rb"] = rb, ["sha"] = sha, ["tag"] = tag, ["old"] = old, ["text"] = text });
        return FinishHandoff(c, g);
    }

    // ------------------------------------------------------------------ pickup

    sealed record Offer(string Branch, string Sha, string Id);

    /// <summary>Handoffs on offer: remote/branch, sha, ledger id (or -); never this machine's own.</summary>
    static List<Offer> Handoffs(Ctx c, G g)
    {
        var all = Ledger.Read(c);
        var me = Env.ThisDevice;
        // this machine's own handoffs, by letter (the same commit may come back in someone else's letter):
        // the ones in the ledger and the ones git knows it wrote, which a crash may have kept from the ledger
        var mine = new HashSet<string>();
        foreach (var e in all)
            if (e.Type == "handoff" && ((e.Device ?? "") == me || (e.Device is null && e.Session != "sankofad")))
                mine.Add(e.Letter ?? e.Sha ?? "");
        foreach (var l in g.Q("for-each-ref", "--format=%(objectname)", "refs/sankofa/handoff").Split('\n', StringSplitOptions.RemoveEmptyEntries)) mine.Add(l);
        var hub = c.OnHub;
        // letters a shared ledger says were picked up (the remote's copy may be one a crash left behind)
        var gone = new HashSet<string>();
        if (hub)
        {
            var done = new HashSet<string>(all.Where(e => e.Type == "done" && e.Ref is not null).Select(e => e.Ref!));
            foreach (var e in all) if (e.Type == "handoff" && e.Letter is not null && done.Contains(e.Id)) gone.Add(e.Letter);
        }
        // each row: branch, sha, ledger id or -, g (git has the letter) or l (only the ledger), letter
        var rows = new List<(string B, string Sha, string Id, char Kind, string Letter)>();
        foreach (var hid in Ledger.Open(all).Where(i => i.Type == "handoff").Select(i => i.Id))
            foreach (var e in all.Where(e => e.Id == hid))
                rows.Add((e.Branch ?? "null", e.Sha ?? "null", e.Id, 'l', e.Letter ?? e.Sha ?? "null"));
        foreach (var line in g.Q("for-each-ref", "--format=%(refname:strip=3)%09%(objectname)", "refs/sankofa/remote").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Split('\t');
            if (t.Length < 2) continue;
            rows.Add((t[0], g.Q("rev-parse", $"{t[1]}^{{commit}}"), "-", 'g', t[1]));
        }
        var order = new List<(string B, string Sha)>();
        var ids = new Dictionary<(string, string), string>();
        var inGit = new HashSet<(string, string)>();
        foreach (var r in rows)
        {
            if (mine.Contains(r.Letter) || mine.Contains(r.Sha)) continue;
            if (r.Kind == 'g' && gone.Contains(r.Letter)) continue;
            var k = (r.B, r.Sha);
            if (!ids.ContainsKey(k) && !inGit.Contains(k) && !order.Contains(k)) order.Add(k);
            if (r.Id != "-") ids[k] = r.Id;
            if (r.Kind == 'g') inGit.Add(k);
        }
        // without a hub the letter on the remote is the offer: a ledger copy without one was taken already
        return order.Where(k => hub || inGit.Contains(k)).Select(k => new Offer(k.B, k.Sha, ids.TryGetValue(k, out var i) ? i : "-")).ToList();
    }

    /// <summary>What a pickup in progress knows: the journal's fields.</summary>
    sealed class Pick
    {
        public string B = "", Sha = "", Tip = "", Hid = "-", Tag = "", Mark = "", Who = "", Letter = "";
        public string Remote => B.Contains('/') ? B[..B.IndexOf('/')] : B;
        public string Rb => B.Contains('/') ? B[(B.IndexOf('/') + 1)..] : "";
        public static Pick FromJournal(Dictionary<string, string> j) => new()
        {
            B = JGet(j, "b"), Sha = JGet(j, "sha"), Tip = JGet(j, "tip"), Hid = JGet(j, "hid"), Tag = JGet(j, "tag"),
            Mark = JGet(j, "mark"), Who = JGet(j, "who"), Letter = JGet(j, "letter"),
        };
        public Dictionary<string, string> ToJournal(string stage) => new()
        {
            ["stage"] = stage, ["b"] = B, ["sha"] = Sha, ["tip"] = Tip, ["hid"] = Hid, ["tag"] = Tag, ["mark"] = Mark, ["who"] = Who, ["letter"] = Letter,
        };
    }

    /// <summary>The handoff was taken but git would not switch after all: offer it again, as it was.</summary>
    static Exit SwitchFailed(Ctx c, G g, Pick p, string err)
    {
        if (p.Tag.Length > 0) g.R("push", "-q", p.Remote, $"{p.Tag}:refs/sankofa/handoff/{p.Rb}");
        if (p.Hid != "-") { try { Ledger.Append(c, "handoff", p.Letter, x: new Ledger.Extra(p.B, p.Sha, p.Tag)); } catch (Exit) { } }
        Rm(JP(c));
        return new Exit($"could not switch to {p.Rb}, so the handoff is offered again: {err}");
    }

    /// <summary>The claim: take the handoff in the journal, or find out that someone else did.</summary>
    static void ClaimHandoff(Ctx c, G g, Pick p)
    {
        var hub = c.OnHub;
        var jp = JP(c);
        string remote = p.Remote, rb = p.Rb;
        var mine = $"picked up on {Env.ThisDevice} at {g.Short(p.Sha)}";
        if (p.Tag.Length > 0)
        {
            // one atomic push removes the letter (only if it is still the one we saw) and leaves the marker
            var push = g.R("push", "-q", "--atomic", $"--force-with-lease=refs/sankofa/handoff/{rb}:{p.Tag}", remote,
                           $":refs/sankofa/handoff/{rb}", $"+{p.Mark}:refs/sankofa/taken/{rb}");
            if (!push.Ok)
            {
                var perr = (push.Out + push.Err).Trim();
                var st = RRef(g, remote, $"refs/sankofa/handoff/{rb}", $"refs/sankofa/taken/{rb}")
                    ?? throw new Exit($"cannot reach {remote}; run sankofa pickup again to finish taking {rb}");
                string now = st[0], mk = st[1];
                if (mk != p.Mark)
                {
                    if (now == p.Tag)
                    {
                        // the letter is there and still ours to take. A server that cannot take both refs at once,
                        // or will not keep the marker, says so: then removing the letter alone is still a claim.
                        // Anything else (the connection) is for running pickup again; the journal says where it got to
                        if (!perr.Contains("atomic", StringComparison.Ordinal) && !perr.Contains($"refs/sankofa/taken/{rb} (", StringComparison.Ordinal))
                            throw new Exit($"could not take {rb} from {remote}: {(perr.Length > 0 ? perr : "push failed")}; run sankofa pickup again");
                        if (!g.Ok("push", "-q", $"--force-with-lease=refs/sankofa/handoff/{rb}:{p.Tag}", remote, $":refs/sankofa/handoff/{rb}") && !hub)
                        {
                            Rm(jp);
                            throw new Exit($"{remote} refused removing refs/sankofa/handoff/{rb}, so the handoff cannot be taken");
                        }
                    }
                    else
                    {
                        Rm(jp);
                        g.R("update-ref", "-d", $"refs/sankofa/remote/{p.B}");
                        if (now.Length == 0) throw new Exit($"someone else picked up {rb} first");
                        throw new Exit($"{rb} was handed off again meanwhile; run sankofa pickup again");
                    }
                }
            }
            g.R("update-ref", "-d", $"refs/sankofa/remote/{p.B}");
        }
        var all = Ledger.Read(c);
        if (p.Tag.Length == 0 && p.Hid != "-")
        {
            // only the ledger offers it: if the remote has its letter's marker, someone took it through
            // git and their done has not reached this ledger yet
            string? l = null;
            foreach (var e in all) if (e.Id == p.Hid && e.Letter is { Length: > 0 }) l = e.Letter;
            if (l is not null && RRef(g, remote, $"refs/sankofa/taken/{rb}") is { } mkl)
            {
                var by = TakenBy(g, remote, rb, l, mkl[0]);
                if (by.Length > 0) { Rm(jp); throw new Exit($"{by} picked up {rb} first"); }
            }
        }
        if (p.Hid != "-")
        {
            try { Items.Done(c, p.Hid, mine); }
            catch (Exit)
            {
                // already done: by this pickup before it was interrupted, or (through a hub) by someone else
                var d = Ledger.Read(c).FirstOrDefault(e => e.Type == "done" && e.Ref == p.Hid)?.Text ?? "";
                if (hub && p.Tag.Length == 0 && d != mine) { Rm(jp); throw new Exit($"someone else picked up {rb} first"); }
            }
        }
    }

    [GeneratedRegex(@"^- [^ ]* [a-z]*: ")]
    private static partial Regex OpenItemLine();

    public static int Pickup(string[] args)
    {
        var c = Ctx.Need();
        var g = GitHere(c, "pickup");
        NoChanges(g, "commit or stash first; pickup switches branches:");
        foreach (var r in g.Q("remote").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (!g.Ok("fetch", "-q", "--prune", "--no-tags", r, $"+refs/heads/*:refs/remotes/{r}/*", $"+refs/sankofa/handoff/*:refs/sankofa/remote/{r}/*"))
                Out.Err($"sankofa: could not fetch {r}");
        var jp = JP(c);
        Pick p;
        if (File.Exists(jp))
        {
            var j = JRead(jp);
            p = Pick.FromJournal(j);
            Out.Err($"sankofa: finishing the pickup of {p.Rb} that was interrupted");
            if (JGet(j, "stage") != "claimed") { ClaimHandoff(c, g, p); JPut(jp, p.ToJournal("claimed")); }
        }
        else
        {
            var list = Handoffs(c, g);
            if (args.Length > 0)
            {
                var a = args[0];
                list = list.Where(o => o.Id == a || o.Branch == a || (o.Branch.Contains('/') && o.Branch[(o.Branch.IndexOf('/') + 1)..] == a)).ToList();
            }
            if (list.Count == 0)
            {
                Out.Line("nothing handed off" + (args.Length > 0 ? $" as {args[0]}" : ""));
                return args.Length == 0 ? 0 : 1;
            }
            if (list.Count > 1)
            {
                Out.Err("more than one handoff; name one: sankofa pickup <branch>");
                foreach (var o in list) Out.Err($"  {o.Branch}  at {(o.Sha.Length > 7 ? o.Sha[..7] : o.Sha)}  {o.Id}");
                throw Exit.Silent(1);
            }
            p = new Pick { B = list[0].Branch, Sha = list[0].Sha, Hid = list[0].Id };
            if (!g.Ok("cat-file", "-e", $"{p.Sha}^{{commit}}")) throw new Exit($"commit {p.Sha} of {p.B} is not here; did git fetch {p.Remote} fail?");
            p.Tag = g.Q("rev-parse", "-q", "--verify", $"refs/sankofa/remote/{p.B}");
            if (p.Tag.Length > 0 && g.Q("rev-parse", $"{p.Tag}^{{commit}}") == p.Sha)
            {
                // written by whoever could push the ref: keep it printable before it reaches a terminal
                var t = g.Q("cat-file", "tag", p.Tag);
                var i = t.IndexOf("\n\n", StringComparison.Ordinal);
                p.Letter = Printable(i >= 0 ? t[(i + 2)..] : "");
                p.Who = Printable(TaggerName(t));
            }
            else
            {
                p.Tag = "";
                var all = Ledger.Read(c);
                var texts = all.Where(e => e.Id == p.Hid).Select(e => e.Text).ToList();
                p.Letter = string.Join("\n", texts);
                p.Who = string.Join("\n", all.Where(e => e.Id == p.Hid).Select(e => e.Device ?? e.Session));
            }
            p.Tip = p.Sha;
            if (g.Ok("rev-parse", "-q", "--verify", $"refs/remotes/{p.B}") && g.Ok("merge-base", "--is-ancestor", p.Sha, $"refs/remotes/{p.B}")) p.Tip = $"refs/remotes/{p.B}";
            p.Tip = g.Q("rev-parse", p.Tip);
        }
        string rb = p.Rb, b = p.B, remote = p.Remote;
        // everything that could refuse the switch is checked before the handoff is taken
        if (g.Ok("show-ref", "-q", "--verify", $"refs/heads/{rb}") && !g.Ok("merge-base", "--is-ancestor", $"refs/heads/{rb}", p.Tip))
            throw new Exit($"your {rb} has commits of its own; merge {b} by hand");
        var tracked = g.Q("ls-tree", "-r", "--name-only", p.Tip).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var untracked = g.Q("ls-files", "-o", "--exclude-standard").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var inWay = tracked.Intersect(untracked).OrderBy(s => s, StringComparer.Ordinal).Take(3).ToList();
        if (inWay.Count > 0) throw new Exit($"untracked files here are in {rb} too; move them first: {string.Concat(inWay.Select(f => f + " "))}");
        if (!File.Exists(jp))
        {
            p.Mark = "";
            if (p.Tag.Length > 0)
            {
                var ident = g.Q("var", "GIT_COMMITTER_IDENT");
                var m = Env.GitIn(c.Root, $"object {p.Tag}\ntype tag\ntag taken/{rb}\ntagger {ident}\n\npicked up on {Env.ThisDevice}\n", "hash-object", "-t", "tag", "-w", "--stdin");
                p.Mark = m.Ok ? m.Out.Trim() : "";
            }
            JPut(jp, p.ToJournal("claiming"));
            // take it first, so two people picking up at once cannot both have it
            ClaimHandoff(c, g, p);
            JPut(jp, p.ToJournal("claimed"));
        }
        if (g.Q("symbolic-ref", "-q", "--short", "HEAD") == rb && g.Q("rev-parse", "HEAD") == p.Tip) { }
        else if (g.Ok("show-ref", "-q", "--verify", $"refs/heads/{rb}"))
        {
            // one step, so a refusal leaves you where you were: rb only moves forward (checked above)
            var r = g.R("switch", "-q", "-C", rb, p.Tip);
            if (!r.Ok) throw SwitchFailed(c, g, p, (r.Out + r.Err).Trim());
        }
        else if (g.Ok("rev-parse", "-q", "--verify", $"refs/remotes/{b}") && g.Q("rev-parse", $"refs/remotes/{b}") == p.Tip)
        {
            var r = g.R("switch", "-q", "-c", rb, "--track", b);
            if (!r.Ok) throw SwitchFailed(c, g, p, (r.Out + r.Err).Trim());
        }
        else
        {
            var r = g.R("switch", "-q", "-c", rb, p.Tip);
            if (!r.Ok) throw SwitchFailed(c, g, p, (r.Out + r.Err).Trim());
        }
        var shortSha = g.Q("rev-parse", "--short", "HEAD");
        if (p.Hid == "-")
        {
            var t = $"picked up {rb} at {shortSha} from {p.Who}: {p.Letter}";
            if (!Ledger.Read(c).Any(e => e.Type == "note" && e.Text == t)) Ledger.Append(c, "note", t);
        }
        // without a hub the open items exist only in the letter: carry them over as todos here, but not
        // one already open here (an interrupted pickup finished, or the same item in an earlier letter)
        if (!c.OnHub)
        {
            // the letter and the tagger came from the remote: they are data here, never part of a program
            var on = false;
            foreach (var line in p.Letter.Split('\n'))
            {
                if (line == "Open items:") { on = true; continue; }
                if (!on) continue;
                var m = OpenItemLine().Match(line);
                if (!m.Success) continue;
                var id = line[2..];
                var sp = id.IndexOf(' ');
                if (sp >= 0) id = id[..sp];
                var t = $"{line[m.Length..]} (from {p.Who}, was {id})";
                if (!Ledger.Open(c).Any(i => i.Type == "todo" && i.Text == t)) Ledger.Append(c, "todo", t);
            }
        }
        Rm(jp);
        // what became of the branch after the handoff: the letter still holds the commit either way
        if (!g.Ok("rev-parse", "-q", "--verify", $"refs/remotes/{b}"))
            Out.Err($"sankofa: {b} is no longer on {remote}; you have the handed-off commit");
        else if (!g.Ok("merge-base", "--is-ancestor", p.Sha, $"refs/remotes/{b}"))
            Out.Err($"sankofa: {b} was rewritten after the handoff; you are at the handed-off commit, {b} is at {g.Short($"refs/remotes/{b}")}");
        Out.Line($"picked up {rb} from {p.Who}, now at {shortSha}\n\n{p.Letter}");
        return 0;
    }
}
