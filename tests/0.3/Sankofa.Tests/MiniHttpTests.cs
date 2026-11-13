using System.Net;
using System.Net.Sockets;
using System.Text;
using Sankofa;

namespace Sankofa.Tests;

// The device side reads what a hub sends with a reader of its own. A hub (or whoever sits on the
// plain-HTTP path) that answers nonsense must get an IOException, which the link treats as the
// hub being away, never an exception that ends the daemon.
public class MiniHttpTests
{
    static (MiniHttp.Reader Reader, Action<string> Send, IDisposable Done) Pair()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var client = new TcpClient();
        client.Connect((IPEndPoint)l.LocalEndpoint);
        var server = l.AcceptTcpClient();
        var cs = client.GetStream();
        cs.ReadTimeout = 2000;
        var ss = server.GetStream();
        return (new MiniHttp.Reader(cs), text => { ss.Write(Encoding.ASCII.GetBytes(text)); ss.Flush(); },
                new Closer(() => { client.Dispose(); server.Dispose(); l.Stop(); }));
    }

    sealed class Closer(Action a) : IDisposable { public void Dispose() => a(); }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: -1\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 2000000000\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n-5\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n7fffffff\r\n")]
    public void ABadBodyIsAnIOException(string answer)
    {
        var (r, send, done) = Pair();
        using (done)
        {
            send(answer);
            var (status, headers) = r.Headers();
            Assert.Equal(200, status);
            Assert.Throws<IOException>(() => r.Body(headers));
        }
    }

    [Fact]
    public void AnEndlessHeaderLineIsAnIOException()
    {
        var (r, send, done) = Pair();
        using (done)
        {
            var t = Task.Run(() => { send("HTTP/1.1 200 OK\r\nX: " + new string('a', 70 * 1024) + "\r\n\r\n"); });
            Assert.Throws<IOException>(() => r.Headers());
            t.Wait();
        }
    }

    [Fact]
    public void AGoodAnswerStillReads()
    {
        var (r, send, done) = Pair();
        using (done)
        {
            send("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n1\r\n!\r\n0\r\n\r\n");
            var (_, headers) = r.Headers();
            Assert.Equal("hello!", r.Body(headers));
            send("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
            var (_, h2) = r.Headers();
            Assert.Equal("ok", r.Body(h2));
        }
    }
}
