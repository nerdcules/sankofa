using System.Runtime.InteropServices;
using Sankofa;

// sankofa-hub: one ledger for many devices, over HTTP (also reached as `sankofad hub`).
//   sankofa-hub serve [--listen URL[,URL]] [--db P] [--data DIR] [--tokens F]
//   sankofa-hub token add <device> | revoke <device> | list [--tokens F]
static string Opt(string[] args, string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

switch (args.Length > 0 ? args[0] : "help")
{
    case "serve":
        return HubServe(Opt(args, "--listen", Environment.GetEnvironmentVariable("SANKOFA_HUB_LISTEN") ?? "http://127.0.0.1:8787/"),
                        Opt(args, "--db", Path.Combine(Defaults.HubData(), "hub.db")), Opt(args, "--data", Defaults.HubData()),
                        Opt(args, "--tokens", Defaults.HubTokens()));
    case "token" when args.Length > 1:
        return HubToken(args[1], args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null, Opt(args, "--tokens", Defaults.HubTokens()));
    case "version" or "--version": Console.WriteLine($"sankofa-hub {Store.Version}"); return 0;
    default:
        Console.Error.WriteLine("usage: sankofa-hub serve [--listen URL] [--db P] [--data DIR] [--tokens F] | token add|revoke <device> | token list");
        return 2;
}

static int HubServe(string listen, string dbPath, string data, string tokensPath)
{
    using var cts = new CancellationTokenSource();
    using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });
    using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; cts.Cancel(); });
    try
    {
        using var store = new Store(dbPath);
        var server = new Server(store, "");   // no socket: the hub is reached over HTTP only
        var tokens = new Tokens(tokensPath);
        var hub = new Hub(store, server, tokens, data, listen.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Keepalive = TimeSpan.FromSeconds(Defaults.Seconds("SANKOFA_HUB_KEEPALIVE", 15)),
        };
        hub.Start();
        var n = tokens.Devices().Count;
        Console.Error.WriteLine($"sankofa hub {Store.Version}: listening on {listen}, db {dbPath}, {n} device(s) in {tokensPath}");
        if (n == 0) Console.Error.WriteLine("sankofa hub: no device tokens yet; add one with: sankofad hub token add <device>");
        hub.RunAsync(cts.Token).GetAwaiter().GetResult();
        return 0;
    }
    catch (System.Net.HttpListenerException ex) { Console.Error.WriteLine($"sankofa hub: cannot listen on {listen}: {ex.Message}"); return 1; }
    catch (SankofaException ex) { Console.Error.WriteLine($"sankofa hub: {ex.Message}"); return 1; }
    catch (SqliteException ex) { Console.Error.WriteLine($"sankofa hub: database: {ex.Message}"); return 1; }
}

static int HubToken(string sub, string? device, string path)
{
    var t = new Tokens(path);
    try
    {
        switch (sub)
        {
            case "add" when device is not null:
                Console.WriteLine(t.Add(device));
                Console.Error.WriteLine($"sankofa hub: token for {device} added to {path}; it is shown only now. On that device:");
                Console.Error.WriteLine("  sankofa hub join <hub url> <token>");
                return 0;
            case "revoke" when device is not null:
                var n = t.Revoke(device);
                Console.Error.WriteLine(n > 0 ? $"sankofa hub: revoked {n} token(s) of {device}" : $"sankofa hub: no token for {device}");
                return n > 0 ? 0 : 1;
            case "list":
                foreach (var d in t.Devices()) Console.WriteLine(d);
                return 0;
        }
    }
    catch (SankofaException ex) { Console.Error.WriteLine($"sankofa hub: {ex.Message}"); return 1; }
    Console.Error.WriteLine("usage: sankofad hub token add <device> | revoke <device> | list [--tokens F]");
    return 2;
}

