namespace Sankofa.Cli;

/// <summary>hook stop | subagent-stop | session-end: Claude Code hook JSON on stdin.</summary>
public static class Hook
{
    public static int Run(string[] a) => Program.Fallback("hook", a);
}
