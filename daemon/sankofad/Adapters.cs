using System.Text.Json;

namespace Sankofa;

/// <summary>Who wants to edit which files, as one client's pre-tool hook describes it.</summary>
sealed record EditIntent(string Session, string Agent, string Cwd, List<string> Paths);

/// <summary>
/// One adapter per client. Each reads that client's hook payload and nothing else; the gate
/// behind them is the same. Payload shapes are from each client's hook documentation.
/// </summary>
static class Adapters
{
    public static EditIntent? For(string client, JsonElement root) => client switch
    {
        "claude-code" => ClaudeCode(root),
        "codex" => Codex(root),
        _ => throw new SankofaException($"unknown client '{client}' (claude-code, codex)"),
    };

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string EnvAgent() => Environment.GetEnvironmentVariable("SANKOFA_AGENT") is { Length: > 0 } a ? a : "main";

    /// <summary>The holder name for a Claude Code subagent; the SubagentStop hook builds the same one.</summary>
    public static string? SubagentName(string? id, string? type) =>
        string.IsNullOrEmpty(id) ? null : $"subagent:{(string.IsNullOrEmpty(type) ? "agent" : type)}/{id}";

    /// <summary>
    /// Claude Code PreToolUse. Edit, Write and MultiEdit carry tool_input.file_path (absolute);
    /// NotebookEdit carries notebook_path. Inside a subagent the payload adds agent_id and agent_type.
    /// </summary>
    static EditIntent? ClaudeCode(JsonElement root)
    {
        if (Str(root, "tool_name") is not ("Edit" or "Write" or "MultiEdit" or "NotebookEdit")) return null;
        if (!root.TryGetProperty("tool_input", out var input)) return null;
        var path = Str(input, "file_path") ?? Str(input, "notebook_path");
        if (string.IsNullOrEmpty(path)) return null;
        var cwd = Str(root, "cwd") ?? Environment.CurrentDirectory;
        var agent = SubagentName(Str(root, "agent_id"), Str(root, "agent_type")) ?? EnvAgent();
        return new(Str(root, "session_id") ?? "-", agent, cwd, [Path.GetFullPath(path, cwd)]);
    }

    /// <summary>
    /// Codex PreToolUse for apply_patch. The patch text is tool_input.command; its
    /// "*** Add File:", "*** Update File:", "*** Delete File:" and "*** Move to:" headers name
    /// the files. Codex does not say which subagent made the call, so the holder is the session.
    /// </summary>
    static EditIntent? Codex(JsonElement root)
    {
        if (Str(root, "tool_name") is not ("apply_patch" or "ApplyPatch")) return null;
        if (!root.TryGetProperty("tool_input", out var input)) return null;
        var text = input.ValueKind == JsonValueKind.String ? input.GetString()
                 : Str(input, "command") ?? Str(input, "patch") ?? Str(input, "input");
        if (string.IsNullOrEmpty(text)) return null;
        var cwd = Str(root, "cwd") ?? Environment.CurrentDirectory;
        string[] heads = ["*** Add File: ", "*** Update File: ", "*** Delete File: ", "*** Move to: "];
        var paths = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            foreach (var h in heads)
                if (line.StartsWith(h, StringComparison.Ordinal) && line[h.Length..].Trim() is { Length: > 0 } p)
                    paths.Add(Path.GetFullPath(p, cwd));
        }
        return paths.Count == 0 ? null : new(Str(root, "session_id") ?? "-", EnvAgent(), cwd, paths.Distinct().ToList());
    }
}
