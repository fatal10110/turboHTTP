using System;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport;
using TurboHTTP.Tests.Transport.Tls;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #221: wss with the BouncyCastle backend against a loopback TLS 1.2 server, directly and
    /// through an HTTP CONNECT proxy. WebSocket offers no ALPN (HTTP/1.1 Upgrade).
    /// </summary>
    [TestFixture]
    public sealed class WebSocketBouncyCastleTlsTests
    {
        [Test]
        public void Wss_BouncyCastleBackend_EchoesText()
        {
            AssertAsync.Run(async () =>
            {
                TestRequirements.RequireBouncyCastle();
                using var certificate = LocalTlsTestServer.TryCreateLocalhostCertificate();
                if (certificate == null)
                    TestRequirements.IgnoreEnvironment("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");

                using var server = new WebSocketTestServer(new WebSocketTestServerOptions
                {
                    ServerCertificate = certificate
                });
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());

                await client.ConnectAsync(server.CreateSecureUri("/bc"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);
                await client.SendAsync("over-bc", CancellationToken.None).ConfigureAwait(false);
                using var message = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual("over-bc", message.Text);
                Assert.AreEqual(1, server.ConnectionCount);
            });
        }

        [Test]
        public void Wss_BouncyCastleBackend_ThroughConnectProxy_EchoesText()
        {
            AssertAsync.Run(async () =>
            {
                TestRequirements.RequireBouncyCastle();
                using var certificate = LocalTlsTestServer.TryCreateLocalhostCertificate();
                if (certificate == null)
                    TestRequirements.IgnoreEnvironment("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");

                using var server = new WebSocketTestServer(new WebSocketTestServerOptions
                {
                    ServerCertificate = certificate
                });
                using var proxy = new WebSocketTestProxyServer();
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());

                var options = CreateOptions();
                options.ProxySettings = new WebSocketProxySettings(new Uri("http://127.0.0.1:" + proxy.Port));

                await client.ConnectAsync(server.CreateSecureUri("/bc-proxy"), options, CancellationToken.None)
                    .ConfigureAwait(false);
                await client.SendAsync("tunneled-bc", CancellationToken.None).ConfigureAwait(false);
                using var message = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual("tunneled-bc", message.Text);
                Assert.AreEqual("localhost:" + server.Port, proxy.LastAuthority);
            });
        }

        private static WebSocketConnectionOptions CreateOptions()
        {
            return new WebSocketConnectionOptions
            {
                TlsBackend = TlsBackend.BouncyCastle,
                HandshakeTimeout = TimeSpan.FromSeconds(10),
                CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                PingInterval = TimeSpan.Zero,
                PongTimeout = TimeSpan.FromMilliseconds(200),
                ReceiveQueueCapacity = 32
            };
        }
    }
}
