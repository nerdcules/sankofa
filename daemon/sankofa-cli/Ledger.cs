using System.Text;
using System.Text.Json;

namespace Sankofa.Cli;

/// <summary>The ledger file: reading it, the open items, and the local append the daemon's absence falls back to.</summary>
public static class Ledger
{
    /// <summary>Every well-formed line, in order; garbage lines are skipped.</summary>
    public static List<Entry> Read(Ctx c)
    {
        var list = new List<Entry>();
        if (!File.Exists(c.Ledger)) return list;
        foreach (var line in File.ReadLines(c.Ledger))
        {
            if (line.Length == 0) continue;
            try { if (JsonSerializer.Deserialize(line, Json.Wire.Entry) is { } e) list.Add(e); }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>Lines in the file (what wc -l counts), the basis of the next local id.</summary>
    public static long LineCount(Ctx c)
    {
        if (!File.Exists(c.Ledger)) return 0;
        long n = 0;
        using var f = File.OpenRead(c.Ledger);
        var buf = new byte[65536];
        int k;
        while ((k = f.Read(buf, 0, buf.Length)) > 0) for (int i = 0; i < k; i++) if (buf[i] == (byte)'\n') n++;
        return n;
    }

    public sealed record Item(string Id, string Type, string Ts, string Text);

    /// <summary>Todos, awaiting items and handoffs with no done yet, in ledger order.</summary>
    public static List<Item> Open(Ctx c) => Open(Read(c));

    public static List<Item> Open(List<Entry> all)
    {
        var done = new HashSet<string>(all.Where(e => e.Type == "done" && e.Ref is not null).Select(e => e.Ref!));
        return all.Where(e => e.Type is "todo" or "awaiting" or "handoff" && !done.Contains(e.Id))
                  .Select(e => new Item(e.Id, e.Type, e.Ts, e.Text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')))
                  .ToList();
    }

    public static bool ItemExists(List<Entry> all, string id) => all.Any(e => e.Id == id && e.Type is "todo" or "awaiting" or "handoff" or "claim");

    /// <summary>A claim with no done yet (whether it is still live is the daemon's call).</summary>
    public static bool LeaseUnfinished(List<Entry> all, string id) =>
        all.Any(e => e.Id == id && e.Type == "claim") && !all.Any(e => e.Type == "done" && e.Ref == id);

    /// <summary>In a repo attached to a hub, lines written without the daemon are queued for it under q-ids.</summary>
    static string NextId(Ctx c) =>
        c.OnHub ? $"q{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{Environment.ProcessId % 1000}" : $"e{LineCount(c) + 1}";

    /// <summary>Extra fields a handoff event carries.</summary>
    public sealed record Extra(string? Branch = null, string? Sha = null, string? Letter = null);

    /// <summary>The 0.1 path, no daemon: one JSON line under the lock every ledger writer takes.</summary>
    public static string LocalAppend(Ctx c, string type, string text, string? reference = null, string? session = null, string? agent = null,
                                     string? wt = null, string? nonce = null, Extra? x = null)
    {
        using var _ = LedgerLock.Acquire(c.SankofaDir);
        var id = NextId(c);
        wt ??= c.WtFp();
        var e = new Entry
        {
            Ts = Env.Now(), Id = id, Type = type, Session = session ?? Env.SessionId, Agent = agent ?? Env.Agent, Text = text,
            Ref = reference is { Length: > 0 } ? reference : null,
            Branch = x?.Branch is { Length: > 0 } ? x.Branch : null, Sha = x?.Branch is { Length: > 0 } ? x.Sha : null,
            Letter = x?.Letter is { Length: > 0 } ? x.Letter : null,
            Wt = wt == "-" ? null : wt, Nonce = nonce is { Length: > 0 } ? nonce : null,
        };
        using var f = new FileStream(c.Ledger, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        f.Write(Encoding.UTF8.GetBytes(Json.Line(e) + "\n"));
        return id;
    }

    /// <summary>append: through sankofad if it answers, else locally. Returns the id.</summary>
    public static string Append(Ctx c, string type, string text, string? reference = null, string? session = null, string? agent = null, Extra? x = null)
    {
        var wt = c.WtFp();
        var nonce = Env.NewNonce();
        var req = new Request
        {
            Verb = "append", Repo = c.Root, Type = type, Text = text, Session = session ?? Env.SessionId, Agent = agent ?? Env.Agent,
            Nonce = nonce, Client = Env.Client, Ref = reference is { Length: > 0 } ? reference : null, Wt = wt == "-" ? null : wt,
            Branch = x?.Branch is { Length: > 0 } ? x.Branch : null, Sha = x?.Branch is { Length: > 0 } ? x.Sha : null,
            Letter = x?.Letter is { Length: > 0 } ? x.Letter : null,
        };
        var a = Daemon.Call(req);
        switch (a.Rc)
        {
            case 0: return a.Resp!.Id ?? "";
            case 4:
                if (Daemon.NonceId(c, nonce) is { } got) return got;
                return LocalAppend(c, type, text, reference, session, agent, wt, nonce, x);
            case 3: return LocalAppend(c, type, text, reference, session, agent, wt, nonce, x);
            default: throw new Exit(a.Error);
        }
    }

    /// <summary>One ledger line as `sankofa last` and `log` show it.</summary>
    public static string Render(Entry e)
    {
        var dev = e.Device is { Length: > 0 } ? "@" + e.Device : "";
        var r = e.Ref is { Length: > 0 } ? $"  ({e.Ref})" : "";
        return Out.Printable($"{e.Ts}  {e.Id}  {e.Type}\t[{e.Agent}{dev}]  {e.Text}{r}");
    }
}
