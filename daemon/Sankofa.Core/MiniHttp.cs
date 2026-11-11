using System.Net.Sockets;
using System.Text;

namespace Sankofa;

/// <summary>
/// The HTTP/1.1 client a device uses to reach its hub: plain HTTP over the private network,
/// one kept-alive connection for requests, one connection per event stream. Written out here
/// rather than using HttpClient, which doubled the size of sankofad and slowed every pre-edit
/// gate (the same binary) by 0.7 ms.
/// </summary>
public sealed class MiniHttp : IDisposable
{
    public sealed record Reply(int Status, string Body);

    readonly Lock gate = new();
    readonly TimeSpan connectTimeout;
    Conn? kept;

    public MiniHttp(TimeSpan connectTimeout) { this.connectTimeout = connectTimeout; }

    sealed class Conn(Socket s, string key) : IDisposable
    {
        public readonly Socket Socket = s;
        public readonly NetworkStream Stream = new(s, ownsSocket: true);
        public readonly string Key = key;
        public void Dispose() => Stream.Dispose();
    }

    static (string Host, int Port, string Path) Split(Uri u) => (u.Host, u.Port, u.PathAndQuery);

    Conn Open(Uri u, TimeSpan deadline)
    {
        var (host, port, _) = Split(u);
        if (u.Scheme != "http") throw new IOException($"only http:// hubs are supported (use the private network for encryption): {u}");
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var cts = new CancellationTokenSource(connectTimeout < deadline ? connectTimeout : deadline);
            s.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            s.Dispose();
            throw new IOException(ex is OperationCanceledException ? $"connect to {host}:{port} timed out" : ex.Message);
        }
        return new Conn(s, $"{host}:{port}");
    }

    static byte[] Head(string method, Uri u, string token, string? type, int length, bool keepAlive) =>
        Encoding.ASCII.GetBytes(
            $"{method} {u.PathAndQuery} HTTP/1.1\r\nHost: {u.Authority}\r\nAuthorization: Bearer {token}\r\n" +
            (type is null ? "" : $"Content-Type: {type}\r\nContent-Length: {length}\r\n") +
            (keepAlive ? "" : "Connection: close\r\n") + "User-Agent: sankofad\r\n\r\n");

    /// <summary>POST a body and read the whole answer, within the timeout.</summary>
    public Reply Post(Uri u, string token, string json, TimeSpan timeout)
    {
        lock (gate)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var started = DateTime.UtcNow;
            for (int attempt = 0; ; attempt++)
            {
                bool reused = kept is not null && kept.Key == $"{u.Host}:{u.Port}";
                if (!reused) { kept?.Dispose(); kept = null; kept = Open(u, timeout); }
                var c = kept!;
                var left = timeout - (DateTime.UtcNow - started);
                if (left <= TimeSpan.Zero) { Drop(); throw new IOException("timed out"); }
                c.Socket.SendTimeout = c.Socket.ReceiveTimeout = Math.Max(1, (int)left.TotalMilliseconds);
                try
                {
                    c.Stream.Write([.. Head("POST", u, token, "application/json", body.Length, true), .. body]);
                    var r = new Reader(c.Stream);
                    var (status, headers) = r.Headers();
                    var text = r.Body(headers);
                    if (headers.TryGetValue("connection", out var cv) && cv.Equals("close", StringComparison.OrdinalIgnoreCase)) Drop();
                    return new Reply(status, text);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    Drop();
                    // a kept connection the hub had already closed: once more on a fresh one
                    // (writes carry nonces, so a repeat is recognised by the hub)
                    if (reused && attempt == 0) continue;
                    throw new IOException(ex.Message);
                }
            }
        }
    }

    void Drop() { kept?.Dispose(); kept = null; }

    /// <summary>Forget the kept connection (the hub restarted).</summary>
    public void Reset() { lock (gate) Drop(); }

    /// <summary>GET a streamed answer (chunked JSON lines); each line is passed on until the hub closes it.</summary>
    public async Task Stream(Uri u, string token, Action<int> onStatus, Action<string> onLine, TimeSpan silence, CancellationToken ct)
    {
        using var c = Open(u, TimeSpan.FromSeconds(10));
        c.Socket.ReceiveTimeout = (int)silence.TotalMilliseconds;
        await using var reg = ct.Register(() => c.Dispose());
        try
        {
            await c.Stream.WriteAsync(Head("GET", u, token, null, 0, false), ct);
            var r = new Reader(c.Stream);
            var (status, headers) = r.Headers();
            onStatus(status);
            bool chunked = headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase);
            var pending = new StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                string piece;
                if (chunked)
                {
                    var size = Convert.ToInt32(r.Line().Split(';')[0].Trim(), 16);
                    if (size == 0) return;
                    piece = Encoding.UTF8.GetString(r.Exactly(size));
                    r.Line();
                }
                else
                {
                    var b = r.Some();
                    if (b.Length == 0) return;
                    piece = Encoding.UTF8.GetString(b);
                }
                pending.Append(piece);
                var all = pending.ToString();
                int nl;
                while ((nl = all.IndexOf('\n')) >= 0)
                {
                    onLine(all[..nl].TrimEnd('\r'));
                    all = all[(nl + 1)..];
                }
                pending.Clear().Append(all);
            }
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>Byte-level reading of one HTTP answer from a stream (blocking, honours the socket timeout).</summary>
    sealed class Reader(NetworkStream s)
    {
        public sealed class Closed() : IOException("the hub closed the connection");

        readonly byte[] buf = new byte[16384];
        int pos, len;

        bool Fill()
        {
            if (pos < len) return true;
            pos = 0; len = s.Read(buf, 0, buf.Length);
            return len > 0;
        }

        public string Line()
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (!Fill()) { if (sb.Length == 0) throw new Closed(); throw new IOException("truncated answer"); }
                byte b = buf[pos++];
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
            }
        }

        public byte[] Exactly(int n)
        {
            var o = new byte[n];
            int got = 0;
            while (got < n)
            {
                if (!Fill()) throw new IOException("truncated answer");
                int k = Math.Min(n - got, len - pos);
                Array.Copy(buf, pos, o, got, k);
                pos += k; got += k;
            }
            return o;
        }

        public byte[] Some()
        {
            if (!Fill()) return [];
            var o = buf.AsSpan(pos, len - pos).ToArray();
            pos = len;
            return o;
        }

        public (int, Dictionary<string, string>) Headers()
        {
            var status = Line().Split(' ');
            if (status.Length < 2 || !int.TryParse(status[1], out var code)) throw new IOException("not an HTTP answer");
            var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string l;
            while ((l = Line()).Length > 0)
                if (l.IndexOf(':') is var i and > 0) h[l[..i].Trim()] = l[(i + 1)..].Trim();
            return (code, h);
        }

        public string Body(Dictionary<string, string> h)
        {
            if (h.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var n)) return Encoding.UTF8.GetString(Exactly(n));
            if (h.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                var all = new List<byte>();
                while (true)
                {
                    var size = Convert.ToInt32(Line().Split(';')[0].Trim(), 16);
                    if (size == 0) { while (Line().Length > 0) { } break; }
                    all.AddRange(Exactly(size));
                    Line();
                }
                return Encoding.UTF8.GetString(all.ToArray());
            }
            return "";
        }
    }

    public void Dispose() { lock (gate) Drop(); }
}
