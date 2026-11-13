namespace Sankofa.Cli;

/// <summary>handoff and pickup: a branch and a letter over the git remote, with journals for a run cut off halfway.</summary>
public static class Handoff
{
    public static int Run(string[] a) => Program.Fallback("handoff", a);
    public static int Pickup(string[] a) => Program.Fallback("pickup", a);
}
