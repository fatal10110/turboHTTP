using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Unity.WebSocket;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #1110: <see cref="WebSocketClient.Create"/> without a transport and without a reconnect policy
    /// builds a client on the registered default transport factory instead of throwing
    /// <see cref="ArgumentNullException"/>, so <c>UHttpClient.WebSocket(uri)</c> connects too.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketCreateDefaultTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/create-default");

        private InMemoryWebSocketPeer _peer;
        private int _factoryCalls;

        [SetUp]
        public void SetUp()
        {
            _peer = new InMemoryWebSocketPeer();
            _factoryCalls = 0;
            WebSocketTransportFactory.Register(_ =>
            {
                Interlocked.Increment(ref _factoryCalls);
                return _peer;
            });
        }

        [TearDown]
        public void TearDown()
        {
            WebSocketTransportFactory.Reset();
            RawSocketWebSocketTransport.EnsureRegistered();
            _peer.Dispose();
        }

        [Test]
        public void Create_DefaultOptionsWithoutTransport_ConnectsThroughRegisteredFactory()
        {
            AssertAsync.Run(async () =>
            {
                IWebSocketClient client = null;
                Assert.DoesNotThrow(() => client = WebSocketClient.Create());
                using (client)
                {
                    Assert.IsInstanceOf<WebSocketClient>(client);

                    await client.ConnectAsync(TestUri, WebSocketReceiveBackpressureTests.CreateOptions(capacity: 8))
                        .ConfigureAwait(false);

                    Assert.AreEqual(WebSocketState.Open, client.State);
                    Assert.AreEqual(1, Volatile.Read(ref _factoryCalls));
                }
            });
        }

        [Test]
        public void UHttpClientWebSocket_DefaultOptions_Connects()
        {
            AssertAsync.Run(async () =>
            {
                using var http = new UHttpClient(new UHttpClientOptions { Transport = new MockTransport() });

                using var client = await http.WebSocket(TestUri).ConfigureAwait(false);

                Assert.AreEqual(WebSocketState.Open, client.State);
                Assert.AreEqual(1, Volatile.Read(ref _factoryCalls));
            });
        }
    }
}
