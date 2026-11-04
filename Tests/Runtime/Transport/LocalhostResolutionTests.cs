using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.WebSocket;
using TurboHTTP.Transport.Connection;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// Localhost names reach loopback without the platform resolver (RFC 6761 section 6.3, #322).
    /// On an Android IL2CPP player the resolver answers "localhost" with the interface addresses
    /// only, so a client that asks it never reaches a server bound to 127.0.0.1. The
    /// "*.localhost" cases fail on the old code on every host whose resolver does not know them.
    /// </summary>
    [TestFixture]
    public sealed class LocalhostResolutionTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

        [TestCase("localhost", true)]
        [TestCase("LOCALHOST", true)]
        [TestCase("localhost.", true)]
        [TestCase("api.localhost", true)]
        [TestCase("Api.Dev.LocalHost.", true)]
        [TestCase("localhost.example", false)]
        [TestCase("notlocalhost", false)]
        [TestCase("localhostx", false)]
        [TestCase(".localhost", false)]
        [TestCase("localhost..", false)]
        [TestCase("127.0.0.1", false)]
        [TestCase("::1", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void IsLocalhostName_MatchesRfc6761Names(string host, bool expected)
        {
            Assert.AreEqual(expected, LocalhostNames.IsLocalhostName(host));
        }

        [Test]
        public void LoopbackAddresses_AreLoopbackOnly_AndIncludeIpv4()
        {
            var addresses = LocalhostNames.GetLoopbackAddresses();

            CollectionAssert.Contains(addresses, IPAddress.Loopback);
            foreach (var address in addresses)
                Assert.IsTrue(IPAddress.IsLoopback(address), address.ToString());
            Assert.AreEqual(Socket.OSSupportsIPv6, Array.IndexOf(addresses, IPAddress.IPv6Loopback) >= 0);
        }

        [Test]
        public void Pool_LocalhostName_ConnectsToIpv4LoopbackListener(
            [Values("localhost", "LocalHost.", "turbohttp-322.localhost")] string host)
        {
            AssertAsync.Run(async () =>
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var accept = listener.AcceptSocketAsync();
                    using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                    using var cts = new CancellationTokenSource(Bound);

                    using var lease = await pool.GetConnectionAsync(host, port, false, cts.Token);

                    using var accepted = await accept;
                    Assert.IsTrue(IPAddress.IsLoopback(((IPEndPoint)accepted.RemoteEndPoint).Address),
                        accepted.RemoteEndPoint.ToString());
                }
                finally
                {
                    listener.Stop();
                }
            }, Bound + TimeSpan.FromSeconds(5));
        }

        [Test]
        public void WebSocket_LocalhostSubdomain_ConnectsToLoopbackServer()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());
                var options = new WebSocketConnectionOptions
                {
                    AllowCleartext = true,
                    HandshakeTimeout = TimeSpan.FromSeconds(10),
                    CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                    PingInterval = TimeSpan.Zero
                };

                await client.ConnectAsync(
                    new Uri("ws://turbohttp-322.localhost:" + server.Port + "/echo"), options, CancellationToken.None);
                await client.SendAsync("loopback", CancellationToken.None);
                using var message = await client.ReceiveAsync(CancellationToken.None);

                Assert.AreEqual("loopback", message.Text);
                Assert.AreEqual(1, server.ConnectionCount);
            }, Bound);
        }
    }
}
