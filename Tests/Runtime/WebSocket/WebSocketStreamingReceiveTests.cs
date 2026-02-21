using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketStreamingReceiveTests
    {
        [Test]
        public void ReceiveAllAsync_ConsumesAndCompletesOnClose()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/streaming-receive"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                await using var enumerator = client.ReceiveAllAsync(CancellationToken.None).GetAsyncEnumerator();

                await client.SendAsync("stream-one", CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(await enumerator.MoveNextAsync().AsTask().ConfigureAwait(false));
                using (var message = enumerator.Current)
                {
                    Assert.AreEqual("stream-one", message.Text);
                }

                await client.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);

                Assert.IsFalse(await enumerator.MoveNextAsync().AsTask().ConfigureAwait(false));
            });
        }

        [Test]
        public void ReceiveAllAsync_RejectsConcurrentEnumerators()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/streaming-exclusive"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                await using var enumerator = client.ReceiveAllAsync(CancellationToken.None).GetAsyncEnumerator();
                Assert.Throws<InvalidOperationException>(() => _ = client.ReceiveAllAsync(CancellationToken.None));
            });
        }

        [Test]
        public void ReceiveAllAsync_CancellationToken_CancelsMoveNext()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/streaming-cancel"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await using var enumerator = client.ReceiveAllAsync(cts.Token).GetAsyncEnumerator();

                AssertAsync.ThrowsAsync<OperationCanceledException>(async () =>
                    await enumerator.MoveNextAsync().AsTask().ConfigureAwait(false));
            });
        }

        [Test]
        public void ReceiveAllAsync_DisposeAsync_ResetsEnumeratorGate()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/streaming-dispose-reset"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                var firstEnumerable = client.ReceiveAllAsync(CancellationToken.None);
                await using (var firstEnumerator = firstEnumerable.GetAsyncEnumerator())
                {
                    // No-op: disposing this enumerator should unlock ReceiveAllAsync for subsequent calls.
                }

                await using var secondEnumerator = client.ReceiveAllAsync(CancellationToken.None).GetAsyncEnumerator();
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
