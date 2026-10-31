using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Http1;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #166: the WebSocket CONNECT tunnel reads the proxy reply through the shared bounded reader;
    /// a 2xx reply hands every following byte to the tunnel, a 407 body is drained exactly.
    /// </summary>
    [TestFixture]
    public class ProxyTunnelConnectorTests
    {
        private static readonly Uri Target = new Uri("ws://echo.example.com/socket");
        private static readonly WebSocketProxySettings Proxy =
            new WebSocketProxySettings(new Uri("http://127.0.0.1:3128"));

        [Test]
        public void TwoHundredWithContentLength_LeavesTunnelBytesForTheTunnel()
        {
            AssertAsync.Run(async () =>
            {
                var proxy = new ScriptedProxyStream(
                    "HTTP/1.1 200 Connection Established\r\nContent-Length: 5\r\n\r\nHELLO");

                var tunnel = await ProxyTunnelConnector.EstablishAsync(proxy, Target, Proxy, CancellationToken.None);

                Assert.AreEqual("HELLO", await ReadAllAsync(tunnel));
                StringAssert.StartsWith("CONNECT echo.example.com:80 HTTP/1.1\r\n", proxy.Written);
            });
        }

        [Test]
        public void NonTwoHundredSuccessStatus_EstablishesTunnel()
        {
            AssertAsync.Run(async () =>
            {
                var proxy = new ScriptedProxyStream("HTTP/1.1 204 No Content\r\n\r\nWS");

                var tunnel = await ProxyTunnelConnector.EstablishAsync(proxy, Target, Proxy, CancellationToken.None);

                Assert.AreEqual("WS", await ReadAllAsync(tunnel));
            });
        }

        [Test]
        public void ProxyAuthWithContentLength_DrainsBodyAndRetriesOnSameConnection()
        {
            AssertAsync.Run(async () =>
            {
                var proxy = new ScriptedProxyStream(
                    "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                    "Proxy-Authenticate: Basic realm=\"proxy\"\r\n" +
                    "Content-Length: 3\r\n\r\nabc" +
                    "HTTP/1.1 200 Connection Established\r\n\r\nTUN");
                var settings = new WebSocketProxySettings(
                    new Uri("http://127.0.0.1:3128"),
                    new ProxyCredentials("user", "pass"));

                var tunnel = await ProxyTunnelConnector.EstablishAsync(proxy, Target, settings, CancellationToken.None);

                Assert.AreEqual("TUN", await ReadAllAsync(tunnel));
                var written = proxy.Written;
                var second = written.IndexOf("CONNECT ", 1, StringComparison.Ordinal);
                Assert.Greater(second, 0, "CONNECT was not retried after 407");
                Assert.IsFalse(written.Substring(0, second).Contains("Proxy-Authorization"));
                StringAssert.Contains("Proxy-Authorization: Basic ", written.Substring(second));
            });
        }

        [Test]
        public void ProxyAuthWithChunkedBody_IsNotRetried()
        {
            var proxy = new ScriptedProxyStream(
                "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                "Transfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n0\r\n\r\n");
            var settings = new WebSocketProxySettings(
                new Uri("http://127.0.0.1:3128"),
                new ProxyCredentials("user", "pass"));

            var ex = AssertAsync.ThrowsAsync<WebSocketException>(
                () => ProxyTunnelConnector.EstablishAsync(proxy, Target, settings, CancellationToken.None));

            Assert.AreEqual(WebSocketError.ProxyTunnelFailed, ex.Error);
        }

        [Test]
        public void StatusLineWithoutLf_FailsAtLineCap()
        {
            var proxy = new ScriptedProxyStream("HTTP/1.1 200 " + new string('A', 128 * 1024));

            var ex = AssertAsync.ThrowsAsync<WebSocketException>(
                () => ProxyTunnelConnector.EstablishAsync(proxy, Target, Proxy, CancellationToken.None));

            Assert.AreEqual(WebSocketError.ProxyTunnelFailed, ex.Error);
            Assert.LessOrEqual(proxy.ReadOffset, ConnectReplyReader.MaxLineBytes + 1);
        }

        private static async Task<string> ReadAllAsync(Stream stream)
        {
            var buffer = new byte[256];
            var sb = new StringBuilder();
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            return sb.ToString();
        }

        /// <summary>Serves a scripted proxy reply and records what the client wrote.</summary>
        private sealed class ScriptedProxyStream : Stream
        {
            private readonly byte[] _script;
            private readonly MemoryStream _written = new MemoryStream();

            public ScriptedProxyStream(string script)
            {
                _script = Encoding.ASCII.GetBytes(script);
            }

            public int ReadOffset { get; private set; }

            public string Written => Encoding.ASCII.GetString(_written.ToArray());

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var toCopy = Math.Min(count, _script.Length - ReadOffset);
                if (toCopy <= 0)
                    return 0;
                Buffer.BlockCopy(_script, ReadOffset, buffer, offset, toCopy);
                ReadOffset += toCopy;
                return toCopy;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }

            public override void Flush()
            {
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
