using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sankofa;

/// <summary>
/// `sankofad mcp`: the ledger and leases as MCP tools over stdio, for any client that speaks
/// MCP. Each tool runs the sankofa CLI, so MCP gets exactly the CLI's behaviour: writes through
/// the daemon or the local fallback, write nonces, repo discovery from the client's directory.
/// Failures come back as tool errors (isError), never as a crashed server.
/// </summary>
static class Mcp
{
    sealed record ToolDef(string Name, string Description, string Schema, Func<JsonElement, string[]> Args);

    static string S(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s : throw new SankofaException($"'{k}' is required");

    static string? Opt(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    const string Repo = """ "repo": {"type": "string", "description": "Path inside the repo (default: the client's working directory)."} """;
    static string Obj(string props, params string[] required) =>
        $$"""{"type": "object", "properties": {{{string.Join(", ", new[] { props, Repo }.Where(p => p.Trim().Length > 0))}}}, "required": [{{string.Join(", ", required.Select(r => $"\"{r}\""))}}], "additionalProperties": false}""";
    const string Text = """ "text": {"type": "string", "description": "What to record, in plain words."} """;
    const string Id = """ "id": {"type": "string", "description": "Ledger id, like e12."} """;

    static readonly ToolDef[] Tools =
    [
        new("note", "Record what changed or what was decided, for the next session to read.", Obj(Text, "text"),
            a => ["note", S(a, "text")]),
        new("todo", "Record an open item someone must finish later. Returns its id.", Obj(Text, "text"),
            a => ["todo", S(a, "text")]),
        new("awaiting", "Record a step that needs the human (sudo, a login, a decision). Returns its id.", Obj(Text, "text"),
            a => ["awaiting", S(a, "text")]),
        new("done", "Resolve a todo or awaiting item, or finish a lease (clears its half-finished mark).",
            Obj(Id + ", " + Text.Replace("What to record", "How it was resolved"), "id"),
            a => Opt(a, "text") is { Length: > 0 } t ? ["done", S(a, "id"), t] : ["done", S(a, "id")]),
        new("open", "List the unresolved todo and awaiting items.", Obj(""), _ => ["open"]),
        new("last", "Show the newest ledger entries.", Obj(""" "n": {"type": "integer", "minimum": 1, "maximum": 200, "description": "How many (default 10)."} """),
            a => ["last", a.ValueKind == JsonValueKind.Object && a.TryGetProperty("n", out var n) && n.TryGetInt32(out var k) ? k.ToString(System.Globalization.CultureInfo.InvariantCulture) : "10"]),
        new("claim", "Lease paths before editing them, so other sessions and subagents leave them alone. Says who holds them if they are taken.",
            Obj(""" "glob": {"type": "string", "description": "Path or glob relative to the repo root, like src/net/** ."}, "ttl": {"type": "string", "description": "How long, like 90, 30m, 2h (default 30m)."} """, "glob"),
            a => Opt(a, "ttl") is { Length: > 0 } t ? ["claim", S(a, "glob"), t] : ["claim", S(a, "glob")]),
        new("release", "Give a lease up without finishing; it stays marked as possibly half-finished. Prefer done.", Obj(Id, "id"),
            a => ["release", S(a, "id")]),
        new("ask", "Who holds this path, and is there a half-finished edit under it?",
            Obj(""" "path": {"type": "string", "description": "Path relative to the repo root."} """, "path"), a => ["ask", S(a, "path")]),
        new("leases", "List live leases and ended ones still marked half-finished.", Obj(""), _ => ["leases"]),
    ];

    static string Cli()
    {
        if (Environment.GetEnvironmentVariable("SANKOFA_CLI") is { Length: > 0 } env) return env;
        var self = Environment.ProcessPath is { } p ? new FileInfo(p).ResolveLinkTarget(true)?.FullName ?? p : null;
        // bin/out/sankofa-mcp -> bin/sankofa in a checkout; /usr/bin/sankofa-mcp -> /usr/bin/sankofa installed
        foreach (var c in new[] { self is null ? null : Path.Combine(Path.GetDirectoryName(self)!, "..", "sankofa"),
                                  self is null ? null : Path.Combine(Path.GetDirectoryName(self)!, "sankofa") })
            if (c is not null && File.Exists(c)) return Path.GetFullPath(c);
        return "sankofa";
    }

    static (int Code, string Out, string Err) Run(string[] args, string cwd, string? client)
    {
        var psi = new ProcessStartInfo(Cli()) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (client is not null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SANKOFA_AGENT")))
            psi.Environment["SANKOFA_AGENT"] = $"mcp:{client}";
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o.Result.TrimEnd(), e.TrimEnd());
    }

    static CallToolResult Result(string text, bool error) =>
        new() { Content = [new TextContentBlock { Text = text }], IsError = error };

    public static int Serve()
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "sankofa", Version = Store.Version },
            ServerInstructions =
                "sankofa is this repo's handoff ledger and path leases, shared by every session and client. " +
                "Start by calling open and last. Record changes and decisions with note, unfinished work with todo, " +
                "steps that need the human with awaiting, and finished items with done. Before editing paths others " +
                "might touch, claim them; if a claim is refused, work on something else.",
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools = Tools.Select(t => new Tool
                    {
                        Name = t.Name, Description = t.Description, InputSchema = JsonDocument.Parse(t.Schema).RootElement.Clone(),
                    }).ToList(),
                }),
                CallToolHandler = (ctx, _) =>
                {
                    var name = ctx.Params?.Name ?? "";
                    var tool = Tools.FirstOrDefault(t => t.Name == name);
                    if (tool is null) return ValueTask.FromResult(Result($"unknown tool '{name}'", true));
                    var args = ctx.Params?.Arguments is { } d
                        ? JsonSerializer.SerializeToElement(d.ToDictionary(kv => kv.Key, kv => kv.Value), Json.Wire.DictionaryStringJsonElement)
                        : JsonDocument.Parse("{}").RootElement;
                    try
                    {
                        var cwd = Opt(args, "repo") is { Length: > 0 } r ? r : Environment.CurrentDirectory;
                        if (!Directory.Exists(cwd)) cwd = Path.GetDirectoryName(cwd) ?? cwd;
                        var (code, outp, err) = Run(tool.Args(args), cwd, ctx.Server.ClientInfo?.Name);
                        var text = string.Join("\n", new[] { outp, err }.Where(s => s.Length > 0));
                        // claim exits 2 when someone else holds the paths: an answer, not a failure
                        bool failed = code != 0 && !(name == "claim" && code == 2);
                        return ValueTask.FromResult(Result(text.Length > 0 ? text : failed ? $"sankofa {name} failed ({code})" : "ok", failed));
                    }
                    catch (SankofaException ex) { return ValueTask.FromResult(Result(ex.Message, true)); }
                    catch (Exception ex) { return ValueTask.FromResult(Result($"{name} failed: {ex.Message}", true)); }
                },
            },
        };
        var transport = new StdioServerTransport(options, null);
        var server = McpServer.Create(transport, options, null, null);
        server.RunAsync().GetAwaiter().GetResult();
        return 0;
    }
}
