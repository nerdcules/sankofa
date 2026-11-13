namespace Sankofa.Cli;

/// <summary>watch serve | once: the desktop notifier (what the sankofa-watch service runs).</summary>
public static class Watch
{
    public static int Run(string[] a) => throw new Exit("the notifier is not ported yet; run sankofa-watch");
}
