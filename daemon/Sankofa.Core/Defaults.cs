namespace Sankofa;

public static class Defaults
{
    /// <summary>
    /// The daemon's socket: SANKOFA_SOCKET; else on Linux $XDG_RUNTIME_DIR/sankofa/sankofad.sock
    /// (or systemd's /run/user/&lt;uid&gt;); on macOS $TMPDIR/sankofa/sankofad.sock (per user there);
    /// on Windows %LOCALAPPDATA%\sankofa\sankofad.sock (Unix sockets exist there since Windows 10).
    /// </summary>
    public static string Socket()
    {
        var env = Environment.GetEnvironmentVariable("SANKOFA_SOCKET");
        if (!string.IsNullOrEmpty(env)) return env;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sankofa", "sankofad.sock");
        if (OperatingSystem.IsMacOS())
        {
            var tmp = Environment.GetEnvironmentVariable("TMPDIR");
            return Path.Combine(string.IsNullOrEmpty(tmp) ? $"/tmp/sankofa-{Environment.UserName}" : tmp, "sankofa", "sankofad.sock");
        }
        var run = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        // clients may strip the environment (Codex does for MCP servers): use systemd's runtime dir
        if (string.IsNullOrEmpty(run) && Directory.Exists($"/run/user/{Uid()}")) run = $"/run/user/{Uid()}";
        return string.IsNullOrEmpty(run) ? $"/tmp/sankofa-{Environment.UserName}/sankofad.sock" : Path.Combine(run, "sankofa", "sankofad.sock");
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")] static extern uint GetUid();
    static uint Uid() => OperatingSystem.IsWindows() ? 0 : GetUid();

    public static string Db()
    {
        var env = Environment.GetEnvironmentVariable("SANKOFA_DB");
        if (!string.IsNullOrEmpty(env)) return env;
        return Path.Combine(Xdg("XDG_DATA_HOME", ".local", "share"), "sankofa", "sankofa.db");
    }

    /// <summary>An XDG base directory: the variable, else ~/&lt;fallback&gt; on Unix, else the matching
    /// Windows folder (%APPDATA% for config, %LOCALAPPDATA% for data and state).</summary>
    public static string Xdg(string var, params string[] fallback)
    {
        var v = Environment.GetEnvironmentVariable(var);
        if (!string.IsNullOrEmpty(v)) return v;
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(var == "XDG_CONFIG_HOME" ? Environment.SpecialFolder.ApplicationData : Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), .. fallback]);
    }

    /// <summary>This device's hub client config: url=, token=, device= (sankofa hub join).</summary>
    public static string HubConf() =>
        Environment.GetEnvironmentVariable("SANKOFA_HUB_CONF") is { Length: > 0 } p ? p : Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "sankofa", "hub.conf");

    /// <summary>On the hub box: device token hashes, and where the hub keeps its ledgers.</summary>
    public static string HubTokens() =>
        Environment.GetEnvironmentVariable("SANKOFA_HUB_TOKENS") is { Length: > 0 } p ? p : Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "sankofa", "hub-tokens");

    public static string HubData() =>
        Environment.GetEnvironmentVariable("SANKOFA_HUB_DATA") is { Length: > 0 } p ? p : Path.Combine(Xdg("XDG_DATA_HOME", ".local", "share"), "sankofa", "hub");

    /// <summary>The repo root: the nearest directory at or above <paramref name="dir"/> holding .sankofa/.</summary>
    public static string? FindRoot(string dir)
    {
        for (var d = Path.GetFullPath(dir); d is not null; d = Path.GetDirectoryName(d))
        {
            if (Directory.Exists(Path.Combine(d, ".sankofa"))) return d;
            if (d == "/" || Path.GetPathRoot(d) == d) break;
        }
        return null;
    }

    /// <summary>A key from .sankofa/config (key=value lines; last one wins).</summary>
    public static string? Config(string root, string key)
    {
        var path = Path.Combine(root, ".sankofa", "config");
        if (!File.Exists(path)) return null;
        string? v = null;
        foreach (var line in File.ReadLines(path))
            if (line.StartsWith(key + "=", StringComparison.Ordinal)) v = line[(key.Length + 1)..].Trim();
        return v;
    }

    /// <summary>A duration in seconds from the environment (tests and simulations speed the loops up).</summary>
    public static double Seconds(string env, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(env), System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    public static long TtlSeconds(string? s, long fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        s = s.Trim();
        long mult = s[^1] switch { 's' => 1, 'm' => 60, 'h' => 3600, 'd' => 86400, _ => 0 };
        var num = mult == 0 ? s : s[..^1];
        return long.TryParse(num, out var n) && n > 0 ? n * (mult == 0 ? 1 : mult) : fallback;
    }
}
