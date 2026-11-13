using System.Text.Json;

namespace Sankofa.Cli;

/// <summary>claim, release, ask, leases, events, daemon: the verbs that talk to sankofad about paths.</summary>
public static class Leases
{
    static string Dev(Lease l) => l.Device is { Length: > 0 } ? $" on {l.Device}" : "";

    public static int Claim(string[] a)
    {
        if (a.Length < 1) throw new Exit("claim needs a glob");
        var c = Ctx.Need();
        var g = c.RepoRel(a[0]);
        var ttl = Env.TtlSecs(a.Length > 1 ? a[1] : "30m");
        var wt = c.WtFp();
        var ans = Daemon.Call(new Request
        {
            Verb = "claim", Repo = c.Root, Glob = g, Ttl = ttl, Session = Env.SessionId, Agent = Env.Agent, Client = Env.Client,
            Wt = wt == "-" ? null : wt,
        });
        switch (ans.Rc)
        {
            case 3:
                Ledger.LocalAppend(c, "flag", $"claim on {g} not recorded: sankofad unreachable");
                Out.Err($"sankofa: sankofad unreachable; claim on {g} not recorded (flagged in the ledger)");
                return 0;
            case 0: break;
            default: throw new Exit(ans.Error);
        }
        var r = ans.Resp!;
        if (r.Granted == true)
        {
            var verb = r.Renewed == true ? "renewed" : "held";
            if (r.State == "queued") verb = "allowed (hub unreachable; queued, not leased)";
            var l = r.Lease ?? new Lease();
            Out.Line(Out.Printable($"{l.Id}  {verb}  {l.Glob}  until {l.Expires}"));
            foreach (var w in r.Warnings ?? [])
                Out.Err(Out.Printable($"warning: {w.Id} on {w.Glob} by {w.Session} [{w.Agent}] ended {w.State} without done; its last edit is unconfirmed"));
            return 0;
        }
        Out.Err($"denied: {g} overlaps");
        foreach (var l in r.Conflicts ?? [])
            Out.Err(Out.Printable($"  {l.Id}  {l.Glob}  held by {l.Session} [{l.Agent}]{Dev(l)} until {l.Expires}"));
        return 2;
    }

    public static int Release(string[] a)
    {
        if (a.Length < 1) throw new Exit("release needs a lease id");
        var c = Ctx.Need();
        var ans = Daemon.Call(new Request { Verb = "release", Repo = c.Root, Ref = a[0], Session = Env.SessionId, Agent = Env.Agent });
        return ans.Rc switch
        {
            0 => 0,
            3 => throw new Exit("sankofad unreachable; leases live in the daemon"),
            _ => throw new Exit(ans.Error),
        };
    }

    public static int Ask(string[] a)
    {
        if (a.Length < 1) throw new Exit("ask needs a path");
        var c = Ctx.Need();
        var p = c.RepoRel(a[0]);
        var ans = Daemon.Call(new Request { Verb = "ask", Repo = c.Root, Path = p });
        switch (ans.Rc)
        {
            case 0: break;
            case 3: Out.Line($"unknown  {p}  (sankofad unreachable)"); return 0;
            default: throw new Exit(ans.Error);
        }
        var r = ans.Resp!;
        if (r.State == "held" && r.Lease is { } l)
            Out.Line(Out.Printable($"held  {p}  {l.Id} {l.Glob} by {l.Session} [{l.Agent}]{Dev(l)} until {l.Expires}"));
        else Out.Line($"free  {p}");
        foreach (var w in r.Warnings ?? [])
            Out.Line(Out.Printable($"dirty  {w.Id} {w.Glob} by {w.Session} [{w.Agent}] ended {w.State} without done"));
        return 0;
    }

    public static int List(string[] a)
    {
        var c = Ctx.Need();
        if (a.Length > 0 && a[0] == "settle")
        {
            var r = Daemon.Need(new Request { Verb = "settle", Repo = c.Root });
            var entries = r.Entries ?? [];
            if (entries.Count == 0) { Out.Line("nothing to settle"); return 0; }
            foreach (var e in entries) Out.Line(Out.Printable($"{e.Ref}\t{e.Text}"));
            return 0;
        }
        var ans = Daemon.Call(new Request { Verb = "leases", Repo = c.Root });
        switch (ans.Rc)
        {
            case 0: break;
            case 3: throw new Exit("sankofad unreachable; leases live in the daemon");
            default: throw new Exit(ans.Error);
        }
        var leases = ans.Resp!.Leases ?? [];
        if (leases.Count == 0) { Out.Line("no leases"); return 0; }
        foreach (var l in leases)
        {
            var state = l.State + (l.Dirty ? ",dirty" : "");
            var who = $"{l.Session} [{l.Agent}]" + (l.Device is { Length: > 0 } ? "@" + l.Device : "");
            Out.Line(Out.Printable($"{l.Id,-5} {state,-14} {l.Glob,-24} {who,-28} until {l.Expires}"));
        }
        return 0;
    }

    public static int Events(string[] a)
    {
        bool follow = false, all = false;
        long? since = null;
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--follow" or "-f": follow = true; break;
                case "--since":
                    if (i + 1 >= a.Length || !long.TryParse(a[i + 1], out var n)) throw new Exit("events [--follow] [--since N] [--all]");
                    since = n; i++; break;
                case "--all": all = true; break;
                default: throw new Exit("events [--follow] [--since N] [--all]");
            }
        }
        var req = new Request { Verb = "events", Follow = follow, Since = since };
        if (!all) req.Repo = Ctx.Need().Root;
        if (follow) return Daemon.Follow(req);
        var ans = Daemon.Call(req);
        switch (ans.Rc)
        {
            case 0: break;
            case 3: throw new Exit($"sankofad unreachable at {Daemon.Socket}");
            default: throw new Exit(ans.Error);
        }
        foreach (var e in ans.Resp!.Entries ?? []) Out.Line(JsonSerializer.Serialize(e, Json.Wire.Entry));
        return 0;
    }

    public static int DaemonStatus()
    {
        var ans = Daemon.Call(new Request { Verb = "ping" });
        if (ans.Rc == 0) { Out.Line($"sankofad {ans.Resp!.Version} on {Daemon.Socket}"); return 0; }
        Out.Line($"sankofad not answering on {Daemon.Socket}; writes go to the local ledger");
        return 1;
    }
}
