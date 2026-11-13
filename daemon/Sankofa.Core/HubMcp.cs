using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sankofa;

/// <summary>
/// MCP over streamable HTTP on the hub (POST /mcp, JSON answers, no server-initiated stream),
/// so a client that cannot run sankofad (a cloud session, a phone) reaches the shared ledger
/// directly. The same tools as the stdio server; each names the repo by its hub name. The MCP
/// session id is the lease holder's session, and the client's name its agent.
/// </summary>
sealed class HubMcp(Hub hub)
{
    const string Proto = "2025-06-18";
    readonly ConcurrentDictionary<string, string> clients = new();

    sealed record Tool(string Name, string Description, string Props, string[] Required);

    const string Text = """ "text": {"type": "string", "description": "What to record, in plain words."} """;
    const string Id = """ "id": {"type": "string", "description": "Ledger id, like e12."} """;
    static readonly Tool[] Tools =
    [
        new("note", "Record what changed or what was decided, for the next session to read.", Text, ["text"]),
        new("todo", "Record an open item someone must finish later. Returns its id.", Text, ["text"]),
        new("awaiting", "Record a step that needs the human (a login, a decision). Returns its id.", Text, ["text"]),
        new("done", "Resolve a todo or awaiting item, or finish a lease.", Id + ", " + Text.Replace("What to record", "How it was resolved"), ["id"]),
        new("open", "List the unresolved todo and awaiting items.", "", []),
        new("last", "Show the newest ledger entries.", """ "n": {"type": "integer", "minimum": 1, "maximum": 200} """, []),
        new("claim", "Lease paths before editing them, so other sessions leave them alone. Says who holds them if taken.",
            """ "glob": {"type": "string", "description": "Path or glob relative to the repo root."}, "ttl": {"type": "integer", "description": "Seconds (default 1800)."} """, ["glob"]),
        new("release", "Give a lease up without finishing; it stays marked possibly half-finished. Prefer done.", Id, ["id"]),
        new("ask", "Who holds this path, and is there a half-finished edit under it?", """ "path": {"type": "string"} """, ["path"]),
        new("leases", "List live leases and ended ones still marked half-finished.", "", []),
    ];

    static string Schema(Tool t)
    {
        const string repo = """ "repo": {"type": "string", "description": "The repo's name on the hub, like github.com/you/repo."} """;
        var props = string.Join(", ", new[] { t.Props, repo }.Where(p => p.Trim().Length > 0));
        return $$"""{"type": "object", "properties": {{{props}}}, "required": [{{string.Join(", ", t.Required.Append("repo").Select(r => $"\"{r}\""))}}], "additionalProperties": false}""";
    }

    public async Task HandleAsync(HttpListenerContext ctx, string device, CancellationToken ct)
    {
        var res = ctx.Response;
        switch (ctx.Request.HttpMethod)
        {
            case "GET": res.StatusCode = 405; res.Headers["Allow"] = "POST, DELETE"; return;   // no server-initiated stream
            case "DELETE":
                if (ctx.Request.Headers["Mcp-Session-Id"] is { } gone) clients.TryRemove(gone, out _);
                res.StatusCode = 200; return;
            case "POST": break;
            default: res.StatusCode = 405; return;
        }
        var body = await Hub.ReadBody(ctx.Request, ct);
        if (body is null) { await Write(res, 413, Error(null, -32600, "request body too large").ToJsonString()); return; }
        JsonNode? msg;
        try { msg = JsonNode.Parse(body); }
        catch (JsonException) { await Write(res, 400, Error(null, -32700, "parse error").ToJsonString()); return; }
        var session = ctx.Request.Headers["Mcp-Session-Id"];
        var answers = new JsonArray();
        var batch = msg as JsonArray;
        foreach (var m in batch is not null ? batch.ToList() : [msg])
        {
            if (m is not JsonObject o) { answers.Add((JsonNode)Error(null, -32600, "invalid request")); continue; }
            var a = Answer(o, device, ref session, res);
            if (a is not null) answers.Add((JsonNode)a);
        }
        if (answers.Count == 0) { res.StatusCode = 202; return; }
        if (session is not null) res.Headers["Mcp-Session-Id"] = session;
        await Write(res, 200, batch is not null ? answers.ToJsonString() : answers[0]!.ToJsonString());
    }

    JsonObject? Answer(JsonObject o, string device, ref string? session, HttpListenerResponse res)
    {
        var id = o["id"]?.DeepClone();
        var method = o["method"]?.GetValue<string>() ?? "";
        if (id is null) return null;   // a notification
        var p = o["params"] as JsonObject;
        switch (method)
        {
            case "initialize":
            {
                session = Guid.NewGuid().ToString("n");
                clients[session] = p?["clientInfo"]?["name"]?.GetValue<string>() ?? "client";
                var asked = p?["protocolVersion"]?.GetValue<string>();
                return Ok(id, new JsonObject
                {
                    ["protocolVersion"] = asked ?? Proto,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "sankofa-hub", ["version"] = Store.Version },
                    ["instructions"] = "sankofa is the shared handoff ledger and path leases for this person's sessions on every device. " +
                                       "Start with open and last. Record changes with note, unfinished work with todo, steps that need " +
                                       "the human with awaiting, finished items with done. Claim paths before editing; if refused, work elsewhere.",
                });
            }
            case "ping": return Ok(id, new JsonObject());
            case "tools/list":
                return Ok(id, new JsonObject
                {
                    ["tools"] = new JsonArray(Tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = JsonNode.Parse(Schema(t)),
                    }).ToArray()),
                });
            case "tools/call":
            {
                var name = p?["name"]?.GetValue<string>() ?? "";
                var args = p?["arguments"] as JsonObject ?? [];
                var who = session is not null && clients.TryGetValue(session, out var c) ? c : "client";
                string text; bool isError;
                try { (text, isError) = Call(name, args, session ?? "-", who, device); }
                catch (Exception ex) when (ex is SankofaException or InvalidOperationException or FormatException) { (text, isError) = (ex.Message, true); }
                return Ok(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = isError,
                });
            }
            default: return Error(id, -32601, $"method not found: {method}");
        }
    }

    (string, bool) Call(string name, JsonObject a, string session, string client, string device)
    {
        string S(string k) => a[k]?.GetValue<string>() is { Length: > 0 } v ? v : throw new SankofaException($"'{k}' is required");
        string? O(string k) => a[k]?.GetValue<string>();
        var agent = $"mcp:{client}";
        var rq = new Request { Repo = S("repo"), Session = session, Agent = agent, Client = $"mcp:{client}" };
        switch (name)
        {
            case "note" or "todo" or "awaiting": rq.Verb = "append"; rq.Type = name; rq.Text = S("text"); break;
            case "done": rq.Verb = "done"; rq.Ref = S("id"); rq.Text = O("text"); break;
            case "open": rq.Verb = "open"; break;
            case "last": rq.Verb = "last"; rq.N = a["n"] is { } n ? Math.Clamp(n.GetValue<int>(), 1, 200) : 10; break;
            case "claim": rq.Verb = "claim"; rq.Glob = S("glob"); rq.Ttl = a["ttl"] is { } t ? t.GetValue<long>() : null; break;
            case "release": rq.Verb = "release"; rq.Ref = S("id"); break;
            case "ask": rq.Verb = "ask"; rq.Path = S("path"); break;
            case "leases": rq.Verb = "leases"; break;
            default: throw new SankofaException($"unknown tool '{name}'");
        }
        var r = hub.Rpc(rq, device);
        if (!r.Ok) return (r.Error ?? "refused", true);
        static string Line(Entry e) => $"{e.Ts}  {e.Id}  {e.Type}  [{e.Agent}{(e.Device is null ? "" : "@" + e.Device)}]  {e.Text}";
        static string L(Lease l) => $"{l.Id} {l.State}{(l.Dirty ? ",dirty" : "")} {l.Glob} by {l.Session} [{l.Agent}] until {l.Expires}";
        return name switch
        {
            "note" or "todo" or "awaiting" or "done" or "release" => (r.Id ?? "ok", false),
            "open" => (r.Entries is { Count: > 0 } o ? string.Join("\n", o.Select(Line)) : "no open items", false),
            "last" => (string.Join("\n", (r.Entries ?? []).Select(Line)), false),
            "claim" when r.Granted == true => ($"granted {r.Lease?.Id} {r.Lease?.Glob} until {r.Lease?.Expires}", false),
            "claim" => ("held: " + string.Join("; ", (r.Conflicts ?? []).Select(L)), false),
            "ask" => (r.State == "held" && r.Lease is { } l ? $"held  {rq.Path}  {L(l)}" : $"free  {rq.Path}", false),
            _ => (r.Leases is { Count: > 0 } ls ? string.Join("\n", ls.Select(L)) : "no leases", false),
        };
    }

    static JsonObject Ok(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    static async Task Write(HttpListenerResponse res, int code, string json)
    {
        var b = Encoding.UTF8.GetBytes(json);
        res.StatusCode = code; res.ContentType = "application/json"; res.ContentLength64 = b.Length;
        await res.OutputStream.WriteAsync(b);
    }
}
