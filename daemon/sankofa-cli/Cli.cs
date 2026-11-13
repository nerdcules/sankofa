using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Sankofa.Cli;

/// <summary>`die`: the message goes to stderr as "sankofa: ...", the process exits with Code.</summary>
public sealed class Exit(string message, int code = 1) : Exception(message)
{
    public int Code { get; } = code;
    /// <summary>An exit with nothing more to say (the message was printed already).</summary>
    public static Exit Silent(int code) => new("", code);
}

/// <summary>What every verb needs: the repo, its ledger, who is writing, and the environment.</summary>
public sealed class Ctx
{
    public string Root { get; }
    public string Ledger => Path.Combine(Root, ".sankofa", "ledger.jsonl");
    public string Config => Path.Combine(Root, ".sankofa", "config");
    public string SankofaDir => Path.Combine(Root, ".sankofa");

    Ctx(string root) { Root = root; }

    /// <summary>The nearest .sankofa/ at or above the working directory, else die.</summary>
    public static Ctx Need()
    {
        var root = Defaults.FindRoot(Directory.GetCurrentDirectory())
            ?? throw new Exit("no .sankofa/ here or above; run 'sankofa init' in the repo root");
        var c = new Ctx(root);
        if (!File.Exists(c.Ledger)) File.WriteAllText(c.Ledger, "");
        return c;
    }

    /// <summary>The same, or null outside a repo (hooks are silent no-ops there).</summary>
    public static Ctx? Find()
    {
        var root = Defaults.FindRoot(Directory.GetCurrentDirectory());
        if (root is null) return null;
        var c = new Ctx(root);
        if (!File.Exists(c.Ledger)) File.WriteAllText(c.Ledger, "");
        return c;
    }

    public string Cfg(string key, string fallback) => Defaults.Config(Root, key) is { Length: > 0 } v ? v : fallback;
    public bool OnHub => Cfg("hub_repo", "").Length > 0;

    /// <summary>set_cfg: replace key=value in .sankofa/config (empty value removes the key).</summary>
    public void SetCfg(string key, string value)
    {
        var lines = File.Exists(Config) ? File.ReadAllLines(Config).Where(l => !l.StartsWith(key + "=", StringComparison.Ordinal)).ToList() : [];
        if (value.Length > 0) lines.Add($"{key}={value}");
        var tmp = Config + ".tmp." + Environment.ProcessId;
        File.WriteAllText(tmp, lines.Count == 0 ? "" : string.Join("\n", lines) + "\n");
        File.Move(tmp, Config, overwrite: true);
    }

    /// <summary>A path given relative to the current directory (or absolute), relative to the repo root.</summary>
    public string RepoRel(string p)
    {
        string rel;
        if (Path.IsPathRooted(p))
        {
            var full = Path.GetFullPath(p);
            rel = full.StartsWith(Root, StringComparison.Ordinal) ? full[Root.Length..].TrimStart('/', '\\') : full;
        }
        else
        {
            var here = Directory.GetCurrentDirectory();
            var sub = here.StartsWith(Root, StringComparison.Ordinal) ? here[Root.Length..].TrimStart('/', '\\') : "";
            rel = sub.Length > 0 ? sub + "/" + p : p;
        }
        rel = rel.Replace('\\', '/');
        return rel.Length == 0 ? "." : rel;
    }

    /// <summary>Fingerprint of the git working tree under the root, ignoring .sankofa/. "-" outside git.</summary>
    public string WtFp()
    {
        if (!Git.Run(Root, "rev-parse", "--git-dir").Ok) return "-";
        const string x = ":(exclude).sankofa";
        var sb = new StringBuilder();
        var head = Git.Run(Root, "rev-parse", "-q", "--verify", "HEAD");
        if (head.Ok)
        {
            sb.Append(head.Out);
            sb.Append(Git.Run(Root, "diff", "HEAD", "--", ".", x).Out);
        }
        else
        {
            sb.Append(Git.Run(Root, "diff", "--cached", "--", ".", x).Out);
            sb.Append(Git.Run(Root, "diff", "--", ".", x).Out);
        }
        var u = Git.Run(Root, "ls-files", "-o", "--exclude-standard", "--", ".", x).Out;
        if (u.Length > 0)
        {
            sb.Append(u);
            sb.Append(Env.GitIn(Root, u, "hash-object", "--stdin-paths").Out);
        }
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(h)[..16];
    }
}

/// <summary>The environment the shell CLI read: who is writing, where the daemon is, which device this is.</summary>
public static class Env
{
    public static string? Get(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    public static string SessionId => Get("CLAUDE_CODE_SESSION_ID") ?? Get("CLAUDE_SESSION_ID") ?? Get("SANKOFA_SESSION") ?? "-";
    public static string Agent => Get("SANKOFA_AGENT") ?? "main";
    public static string Client => Get("SANKOFA_CLIENT") ?? "cli";

    public static string HubConf => Defaults.HubConf();

    /// <summary>device= from hub.conf, else the machine's name (uname -n).</summary>
    public static string ThisDevice
    {
        get
        {
            try
            {
                if (File.Exists(HubConf))
                {
                    string? d = null;
                    foreach (var l in File.ReadLines(HubConf)) if (l.StartsWith("device=", StringComparison.Ordinal)) d = l[7..];
                    if (!string.IsNullOrEmpty(d)) return d;
                }
            }
            catch (IOException) { }
            return Environment.MachineName;
        }
    }

    /// <summary>Millisecond UTC timestamps, comparable with Claude Code transcript timestamps.</summary>
    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static string NewNonce() => $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{Random.Shared.Next(100000, 999999)}-{Environment.ProcessId}";

    /// <summary>The folder this binary runs from: sankofad and the others sit beside it.</summary>
    public static string SelfDir
    {
        get
        {
            var p = Environment.ProcessPath;
            if (p is null) return AppContext.BaseDirectory;
            try { p = new FileInfo(p).ResolveLinkTarget(true)?.FullName ?? p; } catch (IOException) { }
            return Path.GetDirectoryName(p) ?? AppContext.BaseDirectory;
        }
    }

    /// <summary>A sibling program (sankofad, sankofa-tui, ...): beside this binary, else on PATH, else null.</summary>
    public static string? Sibling(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var beside = Path.Combine(SelfDir, exe);
        if (File.Exists(beside)) return beside;
        var path = Get("PATH") ?? "";
        foreach (var d in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(d, exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>The repo's hook scripts: beside the checkout (bin/out/../../hooks), or where a package put them.</summary>
    public static string? HookFile(string name)
    {
        foreach (var d in new[] { Path.Combine(SelfDir, "..", "..", "hooks"), Path.Combine(SelfDir, "..", "hooks"), "/usr/share/sankofa/hooks", "/usr/local/share/sankofa/hooks" })
        {
            var p = Path.GetFullPath(Path.Combine(d, name));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>git with text on stdin (hash-object --stdin, --stdin-paths).</summary>
    public static Git.Result GitIn(string dir, string stdin, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(dir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var v in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_PREFIX" }) psi.Environment.Remove(v);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";
        Process p;
        try { p = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception ex) { return new(-1, "", $"cannot run git: {ex.Message}"); }
        using (p)
        {
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            try { p.StandardInput.Write(stdin); } catch (IOException) { }
            p.StandardInput.Close();
            p.WaitForExit();
            return new(p.ExitCode, o.Result, e.Result.Trim());
        }
    }

    /// <summary>Runs a program with the terminal, waits, returns its exit code (no shell in between).</summary>
    public static int RunInherit(string file, IEnumerable<string> args, IDictionary<string, string?>? env = null, string? stdin = null)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardInput = stdin is not null };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) { if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v; }
        using var p = Process.Start(psi) ?? throw new Exit($"could not run {file}");
        if (stdin is not null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>ttl like 90, 30m, 2h, 1d, in seconds.</summary>
    public static long TtlSecs(string s)
    {
        if (s.Length == 0 || s.Any(c => !char.IsAsciiDigit(c) && c is not ('s' or 'm' or 'h' or 'd'))) throw new Exit($"bad ttl '{s}' (use 90, 30m, 2h, 1d)");
        long mult = s[^1] switch { 'd' => 86400, 'h' => 3600, 'm' => 60, 's' => 1, _ => 0 };
        var num = mult == 0 ? s : s[..^1];
        if (!long.TryParse(num, out var n)) throw new Exit($"bad ttl '{s}' (use 90, 30m, 2h, 1d)");
        return n * (mult == 0 ? 1 : mult);
    }
}

/// <summary>Output: stdout lines, stderr lines, with text from elsewhere made printable first.</summary>
public static class Out
{
    public static void Line(string s) => Console.Out.Write(s + "\n");
    public static void Err(string s) => Console.Error.Write(s + "\n");
    /// <summary>Drops control bytes (escape sequences included) so text written by someone else cannot drive the terminal.</summary>
    public static string Printable(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\t' or '\n' or '\r') { sb.Append(c); continue; }
            if (c < ' ' || c == '\u007f') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
