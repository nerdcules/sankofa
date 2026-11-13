namespace Sankofa.Cli;

/// <summary>gate, trees, bridge, hub, watch add/list/rm, dash, setup.</summary>
public static class Admin
{
    public static int Gate(string[] a) => Program.Fallback("gate", a);
    public static int Trees(string[] a) => Program.Fallback("trees", a);
    public static int Bridge(string[] a) => Program.Fallback("bridge", a);
    public static int Hub(string[] a) => Program.Fallback("hub", a);
    public static int Dash(string[] a) => Program.Fallback("dash", a);
    public static int Setup(string[] a) => Sankofa.Cli.Setup.Run(a);
    public static int WatchReg(string[] a) => Program.Fallback("watch", a);
}
