namespace Sankofa.Cli;

/// <summary>sankofa setup: after a package install, wire the hooks, the services and the MCP registrations.
/// On Linux and macOS it runs install.sh (beside the checkout, or in /usr/share/sankofa); on Windows it does
/// the work itself.</summary>
public static class Setup
{
    public static int Run(string[] a) => Program.Fallback("setup", a);
}
