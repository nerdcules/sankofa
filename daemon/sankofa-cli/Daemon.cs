using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Sankofa.Cli;

/// <summary>
/// One request to sankofad over its socket, one answer. Rc: 0 ok, 1 refused (Resp.Error says why),
/// 3 no daemon (SANKOFA_NO_DAEMON=1, or nothing listens), 4 sent but unanswered (the daemon went
/// away: check the ledger for the request's nonce before writing again).
/// </summary>
public sealed class Answer
{
    public int Rc { get; init; }
    public Response? Resp { get; init; }
    public string Raw { get; init; } = "";
    public bool Ok => Rc == 0;
    /// <summary>The daemon's refusal, for die.</summary>
    public string Error => Resp?.Error ?? "sankofad refused the request";
}

public static class Daemon
{
    public static string Socket => Defaults.Socket();

    static Socket? Connect(int timeoutMs)
    {
        if (Env.Get("SANKOFA_NO_DAEMON") == "1") return null;
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { SendTimeout = timeoutMs, ReceiveTimeout = timeoutMs };
        try { s.Connect(Server.Endpoint(Socket)); return s; }
        catch (Exception e) when (e is SocketException or SankofaException or IOException) { s.Dispose(); return null; }
    }

    /// <summary>dcall: never throws; a daemon that is away is Rc 3, one that went quiet Rc 4.</summary>
    public static Answer Call(Request req, int timeoutMs = 60_000)
    {
        using var s = Connect(timeoutMs);
        if (s is null) return new() { Rc = 3 };
        using var ns = new NetworkStream(s, ownsSocket: false);
        using var r = new StreamReader(ns, new UTF8Encoding(false));
        try { ns.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req, Json.Wire.Request) + "\n")); }
        catch (IOException) { return new() { Rc = 4 }; }
        string? line;
        try { line = r.ReadLine(); } catch (IOException) { line = null; }
        if (line is null) return new() { Rc = 4 };
        Response? resp = null;
        try { resp = JsonSerializer.Deserialize(line, Json.Wire.Response); } catch (JsonException) { }
        return new() { Rc = resp is { Ok: true } ? 0 : 1, Resp = resp, Raw = line };
    }

    /// <summary>dneed: the daemon-only verbs (trees, bridge, settle, hub): die when it is down or refuses.</summary>
    public static Response Need(Request req)
    {
        var a = Call(req);
        return a.Rc switch
        {
            0 => a.Resp!,
            3 => throw new Exit($"sankofad unreachable at {Socket}; trees and the bridge run in the daemon"),
            _ => throw new Exit(a.Error),
        };
    }

    /// <summary>events --follow: every line the daemon streams, until it closes. Returns the exit code.</summary>
    public static int Follow(Request req)
    {
        using var s = Connect(5_000);
        if (s is null) throw new Exit($"sankofad unreachable at {Socket}");
        s.ReceiveTimeout = 0;
        using var ns = new NetworkStream(s, ownsSocket: false);
        using var r = new StreamReader(ns, new UTF8Encoding(false));
        try { ns.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req, Json.Wire.Request) + "\n")); }
        catch (IOException) { return 4; }
        using var stdout = Console.OpenStandardOutput();
        string? l;
        try
        {
            while ((l = r.ReadLine()) is not null) { stdout.Write(Encoding.UTF8.GetBytes(l + "\n")); stdout.Flush(); }
        }
        catch (IOException) { }
        return 0;
    }

    /// <summary>The id of the ledger line carrying this nonce, if the daemon wrote it before going away.</summary>
    public static string? NonceId(Ctx c, string nonce)
    {
        string? id = null;
        foreach (var e in Ledger.Read(c)) if (e.Nonce == nonce) id = e.Id;
        return id;
    }
}
