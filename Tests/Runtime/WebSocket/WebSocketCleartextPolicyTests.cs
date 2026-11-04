using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #85: raw sockets bypass iOS ATS and the Android cleartext policy, so the WebSocket
    /// transport refuses ws:// and http:// proxy legs unless AllowCleartext is set and the
    /// platform policy permits the host, before any DNS lookup or socket.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketCleartextPolicyTests
    {
        private Func<string, bool> _savedPlatformPolicy;

        // CleartextTrafficPolicy.PlatformPolicy is process-wide: restore it even when a test body
        // timed out and was abandoned (#349).
        [SetUp]
        public void SetUp()
        {
            _savedPlatformPolicy = CleartextTrafficPolicy.PlatformPolicy;
        }

        [TearDown]
        public void TearDown()
        {
            CleartextTrafficPolicy.PlatformPolicy = _savedPlatformPolicy;
        }

        [Test]
        public void WsUri_AllowCleartextFalse_ThrowsBeforeAnySocket()
        {
            using var server = new WebSocketTestServer();
            using var transport = new RawSocketWebSocketTransport();
            var options = CreateOptions();
            options.AllowCleartext = false;

            // Safety bound only: the refusal is synchronous, before the token is ever observed.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ex = AssertAsync.ThrowsAsync<WebSocketException>(
                () => transport.ConnectAsync(server.CreateUri("/refused"), options, cts.Token));

            Assert.AreEqual(WebSocketError.CleartextNotPermitted, ex.Error);
            Assert.IsFalse(ex.IsRetryable());
            Assert.AreEqual(0, server.ConnectionCount);
        }

        [Test]
        public void WsUri_AllowCleartextTrue_Connects()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());
                var options = CreateOptions();
                options.AllowCleartext = true;

                await client.ConnectAsync(server.CreateUri("/allowed"), options, CancellationToken.None)
                    .ConfigureAwait(false);
                await client.SendAsync("clear", CancellationToken.None).ConfigureAwait(false);
                using var message = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual("clear", message.Text);
                Assert.AreEqual(1, server.ConnectionCount);
            });
        }

        [Test]
        public void WssThroughHttpProxy_AllowCleartextFalse_RefusesProxyBeforeAnySocket()
        {
            using var server = new WebSocketTestServer();
            using var proxy = new WebSocketTestProxyServer();
            using var transport = new RawSocketWebSocketTransport();
            var options = CreateOptions();
            options.AllowCleartext = false;
            options.ProxySettings = new WebSocketProxySettings(new Uri("http://127.0.0.1:" + proxy.Port));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ex = AssertAsync.ThrowsAsync<WebSocketException>(
                () => transport.ConnectAsync(
                    new Uri("wss://127.0.0.1:" + server.Port + "/secure"),
                    options,
                    cts.Token));

            Assert.AreEqual(WebSocketError.CleartextNotPermitted, ex.Error);
            Assert.IsNull(proxy.LastAuthority, "the proxy must never be contacted");
            Assert.AreEqual(0, server.ConnectionCount);
        }

        [Test]
        public void PlatformPolicyRefusingHost_ThrowsBeforeAnySocketEvenWithAllowCleartext()
        {
            var askedHosts = new List<string>();
            CleartextTrafficPolicy.PlatformPolicy = host =>
            {
                lock (askedHosts)
                    askedHosts.Add(host);
                return false;
            };

            // Server and transport live inside the bounded body: their construction and disposal
            // can never stall the run (#349). TearDown restores the policy.
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new RawSocketWebSocketTransport();
                var options = CreateOptions();
                options.AllowCleartext = true;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                WebSocketException refused = null;
                try
                {
                    using var stream = await transport.ConnectAsync(server.CreateUri("/policy"), options, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (WebSocketException ex)
                {
                    refused = ex;
                }

                Assert.IsNotNull(refused, "the platform policy refused the host; the connect must throw");
                Assert.AreEqual(WebSocketError.CleartextNotPermitted, refused.Error);
                Assert.AreEqual(0, server.ConnectionCount);
            });

            lock (askedHosts)
                CollectionAssert.AreEqual(new[] { "127.0.0.1" }, askedHosts);
        }

        [Test]
        public void PlatformPolicyPermittingHost_Connects()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());
                var options = CreateOptions();
                options.AllowCleartext = true;

                CleartextTrafficPolicy.PlatformPolicy = host => host == "127.0.0.1";
                await client.ConnectAsync(server.CreateUri("/policy-ok"), options, CancellationToken.None)
                    .ConfigureAwait(false);

                Assert.AreEqual(WebSocketState.Open, client.State);
            });
        }

        [Test]
        public void Options_DefaultFollowsPlatformAndCloneCopiesAllowCleartext()
        {
            var options = new WebSocketConnectionOptions();
            Assert.AreEqual(CleartextTrafficPolicy.DefaultAllowCleartext, options.AllowCleartext);

            options.AllowCleartext = !options.AllowCleartext;
            Assert.AreEqual(options.AllowCleartext, options.Clone().AllowCleartext);
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
