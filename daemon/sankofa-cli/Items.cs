namespace Sankofa.Cli;

/// <summary>init, note, todo, awaiting, done, open, last, log.</summary>
public static class Items
{
    public static int Init()
    {
        if (Directory.Exists(".sankofa")) { Out.Line("sankofa: .sankofa/ already exists"); return 0; }
        Directory.CreateDirectory(".sankofa");
        File.WriteAllText(Path.Combine(".sankofa", "ledger.jsonl"), "");
        File.WriteAllText(Path.Combine(".sankofa", "config"), "require_note=1\n");
        Out.Line("sankofa: initialised .sankofa/ (ledger.jsonl, config)");
        return 0;
    }

    static string Text(string verb, string[] args) => args.Length >= 1 ? string.Join(' ', args) : throw new Exit($"{verb} needs text");

    public static int Note(string[] a) { var t = Text("note", a); Ledger.Append(Ctx.Need(), "note", t); return 0; }
    public static int Todo(string[] a) { var t = Text("todo", a); Out.Line(Ledger.Append(Ctx.Need(), "todo", t)); return 0; }
    public static int Awaiting(string[] a) { var t = Text("awaiting", a); Out.Line(Ledger.Append(Ctx.Need(), "awaiting", t)); return 0; }

    /// <summary>done &lt;id&gt; [text]: through the daemon; without it, only an open item or an unfinished lease.</summary>
    public static int Done(string[] a)
    {
        if (a.Length < 1) throw new Exit("done needs an id");
        var c = Ctx.Need();
        Done(c, a[0], a.Length > 1 ? string.Join(' ', a[1..]) : "resolved");
        return 0;
    }

    public static void Done(Ctx c, string id, string text)
    {
        var nonce = Env.NewNonce();
        var ans = Daemon.Call(new Request { Verb = "done", Repo = c.Root, Ref = id, Text = text, Session = Env.SessionId, Agent = Env.Agent, Nonce = nonce, Client = Env.Client });
        switch (ans.Rc)
        {
            case 0: return;
            case 4: if (Daemon.NonceId(c, nonce) is not null) return; break;
            case 3: break;
            default: throw new Exit(ans.Error);
        }
        var all = Ledger.Read(c);
        if (!Ledger.ItemExists(all, id)) throw new Exit($"no open item with id {id}");
        if (Ledger.Open(all).Any(i => i.Id == id) || Ledger.LeaseUnfinished(all, id))
            Ledger.LocalAppend(c, "done", text, id, null, null, null, nonce);
        else throw new Exit($"{id} is already resolved");
    }

    public static int Open()
    {
        var c = Ctx.Need();
        foreach (var j in new[] { "handoff", "pickup" })
        {
            var jf = Path.Combine(c.SankofaDir, j + ".json");
            if (!File.Exists(jf)) continue;
            var w = "?";
            try
            {
                var d = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jf)).RootElement;
                if (d.TryGetProperty("rb", out var rb) && rb.ValueKind == System.Text.Json.JsonValueKind.String) w = rb.GetString()!;
                else if (d.TryGetProperty("b", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.String)
                { var s = b.GetString()!; var i = s.IndexOf('/'); w = i >= 0 ? s[(i + 1)..] : s; }
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or IOException) { }
            Out.Line($"the {j} of {Out.Printable(w)} was interrupted; run sankofa {j} again to finish it");
        }
        var items = Ledger.Open(c);
        if (items.Count == 0) { Out.Line("no open items"); return 0; }
        foreach (var i in items) Out.Line(Out.Printable($"{i.Id,-5} {i.Type,-9} {i.Ts}  {i.Text}"));
        return 0;
    }

    public static int Last(string[] a)
    {
        var c = Ctx.Need();
        int n = 5;
        if (a.Length > 0 && !int.TryParse(a[0], out n)) throw new Exit($"last takes a number, not '{a[0]}'");
        var lines = File.Exists(c.Ledger) ? File.ReadAllLines(c.Ledger) : [];
        foreach (var line in lines.Skip(Math.Max(0, lines.Length - n)))
        {
            if (line.Length == 0) continue;
            try { if (System.Text.Json.JsonSerializer.Deserialize(line, Json.Wire.Entry) is { } e) Out.Line(Ledger.Render(e)); }
            catch (System.Text.Json.JsonException) { }
        }
        return 0;
    }

    public static int Log()
    {
        var c = Ctx.Need();
        foreach (var e in Ledger.Read(c)) Out.Line(Ledger.Render(e));
        return 0;
    }
}
