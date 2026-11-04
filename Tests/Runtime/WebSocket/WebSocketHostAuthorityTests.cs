using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core.Internal;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #309: the WebSocket handshake <c>Host</c> header (RFC 6455 Section 4.1, RFC 9110 Section
    /// 7.2) and the proxy CONNECT authority (RFC 9110 Section 9.3.6) carry the same RFC 3986 wire
    /// host as the HTTP stack (#46/#340): an IDN in A-label form (RFC 5890), an IPv6 literal in one
    /// pair of brackets without a zone id, and an invalid host fails the handshake before any
    /// socket opens, without the host in the message.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketHostAuthorityTests
    {
        // ü + U+00A0: IDNA2003 maps the no-break space to U+0020, so .NET and Unity Mono return
        // "xn-- -dha.de" from Uri.IdnHost (#340).
        private static readonly string NoBreakSpaceHost = "ü .de";

        // Hosts a URI can parse but that have no RFC 3986 wire form. Unity Mono parses "-ü.de" and
        // the 70-character label as an empty host; .NET keeps the Unicode name, which has no
        // A-label form. ü + U+FDD0 makes Uri.IdnHost throw on .NET and Mono (#319).
        private static readonly string[] InvalidUriHosts =
        {
            NoBreakSpaceHost,
            "-ü.de",
            new string('a', 70) + "ü.de",
            "ü﷐.de",
            new string('a', 64) + ".com",
        };

        private static readonly object[] HostHeaderCases =
        {
            new object[] { "wss://münchen.de/", "xn--mnchen-3ya.de" },
            new object[] { "ws://münchen.de:8080/chat", "xn--mnchen-3ya.de:8080" },
            new object[] { "ws://10.0.0.1/", "10.0.0.1" },
            new object[] { "ws://127.0.0.1:9000/", "127.0.0.1:9000" },
            new object[] { "wss://[::1]/", "[::1]" },
            new object[] { "ws://[::1]:8080/", "[::1]:8080" },
            new object[] { "wss://[2001:db8::1]:8443/", "[2001:db8::1]:8443" },
            new object[] { "ws://example.com./", "example.com." },
            new object[] { "wss://example.com:443/", "example.com" },
            new object[] { "ws://example.com:443/", "example.com:443" },
        };

        private static readonly object[] ConnectAuthorityCases =
        {
            new object[] { "wss://münchen.de/", "xn--mnchen-3ya.de:443" },
            new object[] { "ws://münchen.de/", "xn--mnchen-3ya.de:80" },
            new object[] { "ws://127.0.0.1/", "127.0.0.1:80" },
            new object[] { "wss://[::1]/", "[::1]:443" },
            new object[] { "ws://[::1]:8080/", "[::1]:8080" },
            new object[] { "ws://example.com./", "example.com.:80" },
        };

        [TestCaseSource(nameof(HostHeaderCases))]
        public void BuildRequest_HostHeader_IsTheWireHost(string uri, string expected)
        {
            var request = WebSocketHandshake.BuildRequest(new Uri(uri));

            Assert.AreEqual(expected, request.HostHeader);
            StringAssert.Contains("\r\nHost: " + expected + "\r\n", Encoding.ASCII.GetString(request.RequestBytes));
            foreach (var b in request.RequestBytes)
                Assert.Less(b, 0x80, "the handshake request is ASCII");
        }

        // Mono/Unity has returned Uri.Host for an IPv6 literal both bare and bracketed; both become
        // one pair of brackets, and a zone id never reaches the wire.
        [TestCase("::1", "[::1]")]
        [TestCase("[::1]", "[::1]")]
        [TestCase("fe80::1%eth0", "[fe80::1]")]
        [TestCase("[fe80::1%eth0]", "[fe80::1]")]
        [TestCase("münchen.de", "xn--mnchen-3ya.de")]
        [TestCase("example.com.", "example.com.")]
        public void WireHost_BareOrBracketedIPv6AndUnicode_IsFormatted(string host, string expected)
        {
            Assert.IsTrue(WireHost.TryFormat(host, out var wireHost, out var error), error);
            Assert.AreEqual(expected, wireHost);
        }

        [TestCaseSource(nameof(ConnectAuthorityCases))]
        public void ProxyConnect_Authority_IsTheWireHostAndPort(string uri, string expectedAuthority)
        {
            AssertAsync.Run(async () =>
            {
                var proxy = new ScriptedProxyStream("HTTP/1.1 200 Connection Established\r\n\r\n");

                await ProxyTunnelConnector.EstablishAsync(
                    proxy, new Uri(uri), Proxy, true, CancellationToken.None);

                StringAssert.StartsWith("CONNECT " + expectedAuthority + " HTTP/1.1\r\n", proxy.Written);
                StringAssert.Contains("\r\nHost: " + expectedAuthority + "\r\n", proxy.Written);
            });
        }

        [Test]
        public void BuildRequest_HostWithoutWireForm_ThrowsWithoutTheHost(
            [ValueSource(nameof(InvalidUriHosts))] string host)
        {
            var uri = ParseOrPass("ws://" + host + "/");

            var ex = Assert.Throws<ArgumentException>(() => WebSocketHandshake.BuildRequest(uri));
            StringAssert.DoesNotContain(host, ex.Message);
            StringAssert.DoesNotContain("xn--", ex.Message);
        }

        [Test]
        public void ProxyConnect_HostWithoutWireForm_FailsHandshakeAndWritesNothing(
            [ValueSource(nameof(InvalidUriHosts))] string host)
        {
            var uri = ParseOrPass("wss://" + host + "/");
            var proxy = new ScriptedProxyStream("HTTP/1.1 200 Connection Established\r\n\r\n");

            var ex = AssertAsync.ThrowsAsync<WebSocketException>(
                () => ProxyTunnelConnector.EstablishAsync(proxy, uri, Proxy, true, CancellationToken.None));

            Assert.AreEqual(WebSocketError.HandshakeFailed, ex.Error);
            Assert.AreEqual(string.Empty, proxy.Written);
        }

        // Through a loopback proxy so the old behavior is observable without DNS: the transport
        // would connect to the proxy and send CONNECT. The listener never accepts; a connection the
        // client made would be pending on it.
        [Test]
        public void TransportConnect_HostWithoutWireForm_FailsHandshakeBeforeAnySocket(
            [Values("ws", "wss")] string scheme,
            [ValueSource(nameof(InvalidUriHosts))] string host)
        {
            var uri = ParseOrPass(scheme + "://" + host + "/socket");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var transport = new RawSocketWebSocketTransport();
                var options = ProxyOptions(listener);

                // Safety bound only: the refusal is synchronous, before the token is ever observed.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var ex = AssertAsync.ThrowsAsync<WebSocketException>(
                    () => transport.ConnectAsync(uri, options, cts.Token));

                Assert.AreEqual(WebSocketError.HandshakeFailed, ex.Error, ex.ToString());
                Assert.IsFalse(ex.IsRetryable());
                StringAssert.DoesNotContain(host, ex.Message);
                StringAssert.DoesNotContain("xn--", ex.Message);
                Assert.IsFalse(listener.Pending(), "no connection is opened for an invalid host");
            }
            finally
            {
                listener.Stop();
            }
        }

        // WebSocketConnection rethrows a transport WebSocketException as-is, so the caller sees a
        // non-retryable HandshakeFailed rather than a retryable ConnectionClosed.
        [Test]
        public void ClientConnect_NoBreakSpaceHost_KeepsHandshakeFailed()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var ex = AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                {
                    await using var client = new WebSocketClient(new RawSocketWebSocketTransport());
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await client.ConnectAsync(
                        new Uri("ws://" + NoBreakSpaceHost + "/socket"), ProxyOptions(listener), cts.Token);
                });

                Assert.AreEqual(WebSocketError.HandshakeFailed, ex.Error, ex.ToString());
                Assert.IsFalse(listener.Pending(), "no connection is opened for an invalid host");
            }
            finally
            {
                listener.Stop();
            }
        }

        // A custom IWebSocketTransport gets no ConnectAsync call for an invalid host: the connection
        // refuses it first, with a non-retryable HandshakeFailed, and finalizes its state.
        [Test]
        public void ConnectionConnect_HostWithoutWireForm_FailsBeforeCustomTransport(
            [ValueSource(nameof(InvalidUriHosts))] string host)
        {
            var uri = ParseOrPass("ws://" + host + "/socket");
            var transport = new CountingTransport();
            var connection = new WebSocketConnection();
            try
            {
                // Safety bound only: the refusal happens before the token is observed.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var ex = AssertAsync.ThrowsAsync<WebSocketException>(
                    () => connection.ConnectAsync(uri, transport, new WebSocketConnectionOptions(), cts.Token));

                Assert.AreEqual(WebSocketError.HandshakeFailed, ex.Error, ex.ToString());
                Assert.IsFalse(ex.IsRetryable());
                StringAssert.DoesNotContain(host, ex.Message);
                Assert.AreEqual(0, transport.ConnectCalls, "the transport is never asked to connect");
                Assert.AreEqual(WebSocketState.Closed, connection.State);
            }
            finally
            {
                connection.Dispose();
            }
        }

        private sealed class CountingTransport : IWebSocketTransport
        {
            public int ConnectCalls;

            public Task<Stream> ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)
            {
                Interlocked.Increment(ref ConnectCalls);
                return Task.FromResult<Stream>(new MemoryStream());
            }

            public void Dispose()
            {
            }
        }

        private static readonly WebSocketProxySettings Proxy =
            new WebSocketProxySettings(new Uri("http://127.0.0.1:3128"));

        private static WebSocketConnectionOptions ProxyOptions(TcpListener listener)
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return new WebSocketConnectionOptions
            {
                AllowCleartext = true,
                ProxySettings = new WebSocketProxySettings(new Uri("http://127.0.0.1:" + port)),
            };
        }

        // A runtime whose Uri parser rejects the host already fails before any socket: that is the
        // outcome under test, so it passes (.NET and Unity Mono parse all of InvalidUriHosts today).
        private static Uri ParseOrPass(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                Assert.Pass("The runtime's Uri parser rejects this host before the handshake.");
            return parsed;
        }

        /// <summary>Serves a scripted proxy reply and records what the client wrote.</summary>
        private sealed class ScriptedProxyStream : Stream
        {
            private readonly byte[] _script;
            private readonly MemoryStream _written = new MemoryStream();
            private int _readOffset;

            public ScriptedProxyStream(string script)
            {
                _script = Encoding.ASCII.GetBytes(script);
            }

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
                var toCopy = Math.Min(count, _script.Length - _readOffset);
                if (toCopy <= 0)
                    return 0;
                Buffer.BlockCopy(_script, _readOffset, buffer, offset, toCopy);
                _readOffset += toCopy;
                return toCopy;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                Task.FromResult(Read(buffer, offset, count));

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
