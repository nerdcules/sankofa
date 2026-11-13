using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sankofa.Cli;

/// <summary>
/// sankofa setup: after a package install, wire the hooks, the services and the MCP registrations.
/// On Linux and macOS it runs install.sh (beside a checkout, or where a package put it) with
/// --packaged when this binary lives in /usr/bin or /usr/local/bin. On Windows it does the work
/// itself: the four hooks in %USERPROFILE%\.claude\settings.json (the same JSON install.sh
/// writes, a backup beside it, idempotent; --uninstall removes them), the MCP registration with
/// Claude Code, and two scheduled tasks at logon for the daemon and the notifier.
/// </summary>
public static class Setup
{
    public static int Run(string[] a)
    {
        if (OperatingSystem.IsWindows()) return Windows(a);
        var self = Env.SelfDir;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(self, "..", "..", "install.sh")),   // a checkout: bin/out/../../install.sh
            Path.GetFullPath(Path.Combine(self, "..", "install.sh")),         // a tarball unpacked with install.sh beside bin/
            "/usr/share/sankofa/install.sh", "/usr/local/share/sankofa/install.sh",
        };
        var script = candidates.FirstOrDefault(File.Exists)
            ?? throw new Exit("install.sh not found beside this sankofa nor in /usr/share/sankofa");
        var packaged = script.StartsWith("/usr/", StringComparison.Ordinal) || self is "/usr/bin" or "/usr/local/bin";
        var args = new List<string> { script };
        if (packaged && !a.Contains("--uninstall")) args.Add("--packaged");
        args.AddRange(a);
        return Env.RunInherit("sh", args);
    }

    // ------------------------------------------------------------------ Windows

    sealed record HookSpec(string Event, string Command, string? Matcher = null, int? Timeout = null);
    static readonly HookSpec[] Hooks =
    [
        new("Stop", "sankofa hook stop"),
        new("SubagentStop", "sankofa hook subagent-stop"),
        new("SessionEnd", "sankofa hook session-end"),
        new("PreToolUse", "sankofad gate claude-code", "Edit|Write|MultiEdit|NotebookEdit", 5),
    ];

    static string SettingsPath => Env.Get("CLAUDE_SETTINGS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    static bool NoServices => Env.Get("SANKOFA_NO_SYSTEMD") == "1";

    static int Windows(string[] a)
    {
        var uninstall = a.Contains("--uninstall");
        var settings = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        if (!File.Exists(settings)) File.WriteAllText(settings, "{}\n");
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(settings)) as JsonObject ?? []; }
        catch (JsonException) { throw new Exit($"{settings} is not valid JSON; fix it first"); }

        if (uninstall)
        {
            Backup(settings);
            StripHooks(root);
            File.WriteAllText(settings, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            if (!NoServices) foreach (var t in new[] { "sankofad", "sankofa-watch" }) Schtasks("/Delete", "/F", "/TN", t);
            if (Env.Get("SANKOFA_NO_MCP") != "1" && OnPath("claude") is { } cl) RunQuiet(cl, "mcp", "remove", "--scope", "user", "sankofa");
            Out.Line($"setup: removed the sankofa hooks from {settings} and the two scheduled tasks");
            return 0;
        }

        if (HooksPresent(root)) Out.Line($"setup: hooks already present in {settings}");
        else
        {
            Backup(settings);
            AddHooks(root);
            File.WriteAllText(settings, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Out.Line($"setup: added the Stop, SubagentStop, SessionEnd and PreToolUse (edit gate) hooks to {settings} (backup kept beside it)");
        }

        var self = Env.SelfDir;
        var sankofa = Path.Combine(self, "sankofa.exe");
        var sankofad = Path.Combine(self, "sankofad.exe");
        if (!File.Exists(sankofad)) Out.Err($"setup: sankofad.exe is not beside {sankofa}; the hooks and tasks expect both on PATH");
        if (!NoServices)
        {
            // two tasks at logon, in this user's session: the daemon and the desktop notifier
            Schtasks("/Create", "/F", "/SC", "ONLOGON", "/TN", "sankofad", "/TR", $"\"{sankofad}\" serve");
            Schtasks("/Create", "/F", "/SC", "ONLOGON", "/TN", "sankofa-watch", "/TR", $"\"{sankofa}\" watch serve");
            Schtasks("/Run", "/TN", "sankofad");
            Schtasks("/Run", "/TN", "sankofa-watch");
            Out.Line("setup: scheduled tasks sankofad and sankofa-watch run at logon (schtasks /Query /TN sankofad)");
        }
        if (Env.Get("SANKOFA_NO_MCP") != "1" && OnPath("claude") is { } claude)
        {
            if (RunQuiet(claude, "mcp", "get", "sankofa") == 0) Out.Line("setup: Claude Code already has the sankofa MCP server");
            else if (RunQuiet(claude, "mcp", "add", "--scope", "user", "sankofa", "--", "sankofad", "mcp") == 0) Out.Line("setup: registered sankofad mcp with Claude Code (user scope)");
        }
        var onPath = (Env.Get("PATH") ?? "").Split(Path.PathSeparator).Any(d => string.Equals(Path.GetFullPath(d.Length > 0 ? d : "."), self, StringComparison.OrdinalIgnoreCase));
        if (!onPath) Out.Line($"setup: add {self} to your PATH so the hooks find sankofa and sankofad");
        Out.Line("setup: in each repo that should keep a ledger: sankofa init; sankofa watch add; sankofa gate install");
        return 0;
    }

    static void Backup(string settings)
    {
        var b = settings + ".bak-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        File.Copy(settings, b, overwrite: true);
    }

    static bool HooksPresent(JsonObject root) => Hooks.All(h => Commands(root, h.Event).Contains(h.Command));

    static IEnumerable<string> Commands(JsonObject root, string ev) =>
        root["hooks"]?[ev] is JsonArray groups
            ? groups.OfType<JsonObject>().SelectMany(g => g["hooks"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(h => h["command"]?.GetValue<string>() ?? "")
            : [];

    static void AddHooks(JsonObject root)
    {
        var hooks = root["hooks"] as JsonObject ?? (JsonObject)(root["hooks"] = new JsonObject());
        foreach (var h in Hooks)
        {
            if (Commands(root, h.Event).Contains(h.Command)) continue;
            var list = hooks[h.Event] as JsonArray ?? (JsonArray)(hooks[h.Event] = new JsonArray());
            var entry = new JsonObject { ["type"] = "command", ["command"] = h.Command };
            if (h.Timeout is { } t) entry["timeout"] = t;
            var group = new JsonObject { ["hooks"] = new JsonArray(entry) };
            if (h.Matcher is { } m) group["matcher"] = m;
            list.Add((JsonNode)group);
        }
    }

    static void StripHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var h in Hooks)
        {
            if (hooks[h.Event] is not JsonArray groups) continue;
            var keep = new JsonArray();
            foreach (var g in groups.OfType<JsonObject>().ToList())
            {
                var inner = new JsonArray();
                foreach (var x in (g["hooks"] as JsonArray ?? []).OfType<JsonObject>().ToList())
                    if (x["command"]?.GetValue<string>() != h.Command) { x.Parent?.AsArray().Remove(x); inner.Add((JsonNode)x); }
                if (inner.Count > 0) { g["hooks"] = inner; groups.Remove(g); keep.Add((JsonNode)g); }
            }
            if (keep.Count > 0) hooks[h.Event] = keep; else hooks.Remove(h.Event);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    }

    static string? OnPath(string name)
    {
        foreach (var ext in new[] { ".cmd", ".exe", ".bat", "" })
            foreach (var d in (Env.Get("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = Path.Combine(d, name + ext);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    static int RunQuiet(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in args) psi.ArgumentList.Add(x);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return 1;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return 1; }
    }

    static void Schtasks(params string[] args)
    {
        var rc = RunQuiet("schtasks.exe", args);
        if (rc != 0) Out.Err($"setup: schtasks {string.Join(' ', args)} failed ({rc})");
    }
}
