namespace Sankofa.Cli;

/// <summary>claim, release, ask, leases, events, daemon: the verbs that talk to sankofad about paths.</summary>
public static class Leases
{
    public static int Claim(string[] a) => Program.Fallback("claim", a);
    public static int Release(string[] a) => Program.Fallback("release", a);
    public static int Ask(string[] a) => Program.Fallback("ask", a);
    public static int List(string[] a) => Program.Fallback("leases", a);
    public static int Events(string[] a) => Program.Fallback("events", a);
    public static int DaemonStatus() => Program.Fallback("daemon", []);
}
