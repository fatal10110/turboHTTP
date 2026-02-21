using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketProxyTests
    {
        [Test]
        public void ConnectThroughHttpProxy_Succeeds()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var proxy = new WebSocketTestProxyServer();
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());

                var options = CreateOptions();
                options.ProxySettings = new WebSocketProxySettings(
                    new Uri("http://127.0.0.1:" + proxy.Port));

                await client.ConnectAsync(
                    server.CreateUri("/proxy-connect"),
                    options,
                    CancellationToken.None).ConfigureAwait(false);

                await client.SendAsync("via-proxy", CancellationToken.None).ConfigureAwait(false);
                using var message = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("via-proxy", message.Text);
                Assert.AreEqual("127.0.0.1:" + server.Port, proxy.LastAuthority);
            });
        }

        [Test]
        public void ProxyAuthentication_407_RetriesWithBasicCredentials()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var proxy = new WebSocketTestProxyServer("user", "pass");
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());

                var options = CreateOptions();
                options.ProxySettings = new WebSocketProxySettings(
                    new Uri("http://127.0.0.1:" + proxy.Port),
                    new ProxyCredentials("user", "pass"));

                await client.ConnectAsync(
                    server.CreateUri("/proxy-auth"),
                    options,
                    CancellationToken.None).ConfigureAwait(false);

                await client.SendAsync("auth-ok", CancellationToken.None).ConfigureAwait(false);
                using var message = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("auth-ok", message.Text);
                Assert.GreaterOrEqual(proxy.AuthenticatedConnectCount, 1);
            });
        }

        [Test]
        public void ProxyAuthenticationRequired_WithoutCredentials_ThrowsSpecificError()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var proxy = new WebSocketTestProxyServer("user", "pass");
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());

                var options = CreateOptions();
                options.ProxySettings = new WebSocketProxySettings(
                    new Uri("http://127.0.0.1:" + proxy.Port));

                var ex = AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                    await client.ConnectAsync(
                        server.CreateUri("/proxy-auth-required"),
                        options,
                        CancellationToken.None).ConfigureAwait(false));

                Assert.AreEqual(WebSocketError.ProxyAuthenticationRequired, ex.Error);
            });
        }

        [Test]
        public void ProxyAuthentication_407ConnectionClose_DoesNotRetryUnsafeConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var proxy = new WebSocketTestProxyServer(
                    username: "user",
                    password: "pass",
                    authChallengeConnectionClose: true);
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());

                var options = CreateOptions();
                options.ProxySettings = new WebSocketProxySettings(
                    new Uri("http://127.0.0.1:" + proxy.Port),
                    new ProxyCredentials("user", "pass"));

                var ex = AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                    await client.ConnectAsync(
                        server.CreateUri("/proxy-auth-close"),
                        options,
                        CancellationToken.None).ConfigureAwait(false));

                Assert.AreEqual(WebSocketError.ProxyTunnelFailed, ex.Error);
                StringAssert.Contains("cannot be safely retried", ex.Message);
                Assert.AreEqual(0, proxy.AuthenticatedConnectCount);
            });
        }

        [Test]
        public void ProxyAuthentication_407ChunkedBody_DoesNotRetryUnsafeConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var proxy = new WebSocketTestProxyServer(
                    username: "user",
                    password: "pass",
                    authChallengeChunkedBody: true);
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());

                var options = CreateOptions();
                options.ProxySettings = new WebSocketProxySettings(
                    new Uri("http://127.0.0.1:" + proxy.Port),
                    new ProxyCredentials("user", "pass"));

                var ex = AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                    await client.ConnectAsync(
                        server.CreateUri("/proxy-auth-chunked"),
                        options,
                        CancellationToken.None).ConfigureAwait(false));

                Assert.AreEqual(WebSocketError.ProxyTunnelFailed, ex.Error);
                StringAssert.Contains("cannot be safely retried", ex.Message);
                Assert.AreEqual(0, proxy.AuthenticatedConnectCount);
            });
        }

        [Test]
        public void ProxySettings_BypassList_MatchesExactAndWildcard()
        {
            var settings = new WebSocketProxySettings(
                new Uri("http://proxy.local:8080"),
                bypassList: new[] { "example.com", "*.internal.local" });

            Assert.IsTrue(settings.ShouldBypass("example.com"));
            Assert.IsTrue(settings.ShouldBypass("api.internal.local"));
            Assert.IsTrue(settings.ShouldBypass("a.b.internal.local"));
            Assert.IsFalse(settings.ShouldBypass("internal.local"));
            Assert.IsFalse(settings.ShouldBypass("example.net"));
        }

        [Test]
        public void ProxyConnectionFailure_ThrowsProxyConnectionFailed()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());

                int closedPort;
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();

                var options = CreateOptions();
                options.ProxySettings = new WebSocketProxySettings(
                    new Uri("http://127.0.0.1:" + closedPort));

                var ex = AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                    await client.ConnectAsync(
                        server.CreateUri("/proxy-connect-failed"),
                        options,
                        CancellationToken.None).ConfigureAwait(false));

                Assert.AreEqual(WebSocketError.ProxyConnectionFailed, ex.Error);
            });
        }

        [Test]
        public void ProxyTunnelRejected_ThrowsProxyTunnelFailed()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var proxy = new WebSocketTestProxyServer(
                    forcedConnectStatusCode: 502,
                    forcedConnectReason: "Bad Gateway");
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());

                var options = CreateOptions();
                options.ProxySettings = new WebSocketProxySettings(
                    new Uri("http://127.0.0.1:" + proxy.Port));

                var ex = AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                    await client.ConnectAsync(
                        server.CreateUri("/proxy-connect-rejected"),
                        options,
                        CancellationToken.None).ConfigureAwait(false));

                Assert.AreEqual(WebSocketError.ProxyTunnelFailed, ex.Error);
            });
        }

        private static WebSocketConnectionOptions CreateOptions()
        {
            return new WebSocketConnectionOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(2),
                CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                PingInterval = TimeSpan.Zero,
                PongTimeout = TimeSpan.FromMilliseconds(200),
                ReceiveQueueCapacity = 32
            };
        }

    }
}
