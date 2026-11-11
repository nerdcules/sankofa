namespace Sankofa;

static class Defaults
{
    public static string Socket()
    {
        var env = Environment.GetEnvironmentVariable("SANKOFA_SOCKET");
        if (!string.IsNullOrEmpty(env)) return env;
        var run = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        // clients may strip the environment (Codex does for MCP servers): use systemd's runtime dir
        if (string.IsNullOrEmpty(run) && Directory.Exists($"/run/user/{Uid()}")) run = $"/run/user/{Uid()}";
        return string.IsNullOrEmpty(run) ? $"/tmp/sankofa-{Environment.UserName}/sankofad.sock" : Path.Combine(run, "sankofa", "sankofad.sock");
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")] static extern uint GetUid();
    static uint Uid() => GetUid();

    public static string Db()
    {
        var env = Environment.GetEnvironmentVariable("SANKOFA_DB");
        if (!string.IsNullOrEmpty(env)) return env;
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(data)) data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "sankofa", "sankofa.db");
    }

    /// <summary>The repo root: the nearest directory at or above <paramref name="dir"/> holding .sankofa/.</summary>
    public static string? FindRoot(string dir)
    {
        for (var d = Path.GetFullPath(dir); d is not null; d = Path.GetDirectoryName(d))
        {
            if (Directory.Exists(Path.Combine(d, ".sankofa"))) return d;
            if (d == "/") break;
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
