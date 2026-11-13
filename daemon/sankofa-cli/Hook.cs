using System.Text;
using System.Text.Json;

namespace Sankofa.Cli;

/// <summary>hook stop | subagent-stop | session-end: Claude Code hook JSON on stdin. Silent no-op outside a repo.</summary>
public static class Hook
{
    static readonly string[] EditTools = ["Edit", "Write", "MultiEdit", "NotebookEdit"];

    static string Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    static bool Has(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>The same holder name the pre-edit gate gives this subagent.</summary>
    static string AgentName(JsonElement o)
    {
        var id = Str(o, "agent_id");
        if (id.Length > 0)
        {
            var t = Str(o, "agent_type");
            return $"subagent:{(t.Length == 0 ? "agent" : t)}/{id}";
        }
        var name = Has(o, "agent_name") ? Str(o, "agent_name") : Has(o, "agent_type") ? Str(o, "agent_type") : "unnamed";
        return "subagent:" + name;
    }

    public static int Run(string[] a)
    {
        var kind = a.Length > 0 ? a[0] : "";
        if (kind is not ("stop" or "subagent-stop" or "session-end")) throw new Exit("hook stop|subagent-stop|session-end");
        var text = Console.In.ReadToEnd();
        JsonElement input;
        try { input = JsonDocument.Parse(text).RootElement; }
        catch (JsonException) { input = JsonDocument.Parse("{}").RootElement; }
        var session = Has(input, "session_id") ? Str(input, "session_id") : "-";
        if (session.Length == 0) session = "-";

        // Leases end with their holder in every repo, whatever directory the hook runs in:
        // a session's subagents may have leased files in repos other than the session's cwd.
        if (kind is "subagent-stop" or "session-end")
        {
            var fin = new Request { Verb = "finish", Session = session };
            if (kind == "session-end") fin.Text = "session ended";
            else { fin.Agent = AgentName(input); fin.Text = "subagent finished"; }
            Daemon.Call(fin);
        }
        if (kind == "session-end") return 0;
        var c = Ctx.Find();
        if (c is null) return 0;

        var transcript = Str(input, "transcript_path");
        bool active = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("stop_hook_active", out var ah)
                      && (ah.ValueKind == JsonValueKind.True || (ah.ValueKind == JsonValueKind.String && ah.GetString() == "true"));
        var msg = Message(Str(input, "last_assistant_message"));
        var agent = kind == "subagent-stop" ? AgentName(input) : "main";

        var all = Ledger.Read(c);
        var open = Ledger.Open(all);
        int nopen = open.Count;

        if (kind == "stop")
        {
            var require = Env.Get("SANKOFA_REQUIRE_NOTE") ?? c.Cfg("require_note", "1");
            if (require == "1" && !active && nopen > 0)
            {
                // Baseline: this session's last ledger write. Before its first write, the tree as the
                // ledger last saw it before this session showed up, and every transcript edit counts.
                // Through a hub, only this device's fingerprints describe this device's working tree.
                var dev = Env.ThisDevice;
                Entry? w = null;
                foreach (var e in all) if (e.Session == session && e.Type is "note" or "todo" or "awaiting" or "done") w = e;
                int i = all.FindIndex(e => e.Session == session); if (i < 0) i = all.Count;
                Entry? pre = null;
                for (int k = 0; k < i; k++) if (all[k].Wt is not null && (all[k].Device ?? dev) == dev) pre = all[k];
                string since, baseWt;
                if (w is not null) { since = w.Ts; baseWt = w.Wt ?? "-"; }
                else { since = ""; baseWt = pre?.Wt ?? "-"; }

                int edits = 0;
                if (transcript.Length > 0 && File.Exists(transcript))
                {
                    var rootSlash = c.Root + "/";
                    foreach (var line in File.ReadLines(transcript))
                    {
                        if (line.Length == 0) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var o = doc.RootElement;
                            if (Str(o, "type") != "assistant") continue;
                            if (string.CompareOrdinal(Str(o, "timestamp"), since) <= 0) continue;
                            if (!o.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                            foreach (var part in content.EnumerateArray())
                            {
                                if (part.ValueKind != JsonValueKind.Object || Str(part, "type") != "tool_use" || !EditTools.Contains(Str(part, "name"))) continue;
                                var p = "";
                                if (part.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.Object)
                                    p = Has(inp, "file_path") ? Str(inp, "file_path") : Has(inp, "notebook_path") ? Str(inp, "notebook_path") : "";
                                // edits to files in this repo only; an edit tool call without a path counts to be safe
                                if (p.Length == 0 || p.StartsWith(rootSlash, StringComparison.Ordinal)) edits++;
                            }
                        }
                        catch (JsonException) { }
                    }
                }
                bool changed = baseWt != "-" && c.WtFp() != baseWt;
                if (edits > 0 || changed)
                {
                    var why = new List<string>();
                    if (edits > 0) why.Add($"{edits} file edit(s)");
                    if (changed) why.Add("working tree changed");
                    var sb = new StringBuilder();
                    sb.Append($"sankofa: {nopen} open item(s) in the ledger, and this session changed things ({string.Join(", ", why)}) since its last ledger note.\n");
                    sb.Append("Before stopping, run: sankofa note \"<what changed; what is still open>\"\n");
                    sb.Append("and sankofa done <id> for anything on this list that is finished:\n");
                    foreach (var it in open) sb.Append(Out.Printable($"  {it.Id}  {it.Type}  {it.Text}") + "\n");
                    Console.Error.Write(sb.ToString());
                    return 2;
                }
            }
        }

        Ledger.Append(c, "stop", msg.Length > 0 ? msg : "stopped", null, session, agent);
        return 0;
    }

    /// <summary>gsub("[\n\t]+"; " ") | sub(" +$"; "") | .[0:400]</summary>
    static string Message(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool inRun = false;
        foreach (var ch in s)
        {
            if (ch is '\n' or '\t') { if (!inRun) sb.Append(' '); inRun = true; }
            else { sb.Append(ch); inRun = false; }
        }
        var t = sb.ToString().TrimEnd(' ');
        if (t.Length <= 400) return t;
        // jq slices by code point; cut on a boundary the same way
        var e = new System.Globalization.StringInfo(t);
        return e.LengthInTextElements <= 400 ? t : e.SubstringByTextElements(0, 400);
    }
}
