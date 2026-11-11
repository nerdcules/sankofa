using System.Diagnostics;

namespace Sankofa;

/// <summary>Runs git for the watcher and the bridge: never prompts, never takes optional locks.</summary>
public static class Git
{
    public sealed record Result(int Code, string Out, string Err)
    {
        public bool Ok => Code == 0;
        /// <summary>NUL-separated output (from -z) as a list.</summary>
        public List<string> Z => Out.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        public List<string> Lines => Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public static Result Run(string dir, params string[] args) => RunTimed(dir, 30_000, args);

    public static Result RunTimed(string dir, int timeoutMs, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(dir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        // a git hook that runs us (the pre-commit gate) sets these; they would point every call at that repo
        foreach (var v in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_PREFIX" })
            psi.Environment.Remove(v);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";   // a fetch that needs a password fails instead of hanging
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";    // never contend with the human's own git for index.lock
        psi.Environment["LC_ALL"] = "C";
        Process p;
        try { p = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception ex) { return new(-1, "", $"cannot run git: {ex.Message}"); }
        using (p)
        {
            p.StandardInput.Close();
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                p.WaitForExit();
                return new(-1, "", $"git {string.Join(' ', args)} timed out after {timeoutMs / 1000} s");
            }
            p.WaitForExit();
            return new(p.ExitCode, o.Result, e.Result.Trim());
        }
    }
}
