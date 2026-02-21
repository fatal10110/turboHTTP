using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketClientTests
    {
        [Test]
        public void ConnectSendReceiveClose_AndEvents_Work()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                int connectedCount = 0;
                int messageCount = 0;
                int closedCount = 0;
                var messageEventTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                client.OnConnected += () => Interlocked.Increment(ref connectedCount);
                client.OnMessage += message =>
                {
                    try
                    {
                        Interlocked.Increment(ref messageCount);
                        messageEventTcs.TrySetResult(true);
                    }
                    finally
                    {
                        message.Dispose();
                    }
                };
                client.OnClosed += (_, __) => Interlocked.Increment(ref closedCount);

                await client.ConnectAsync(server.CreateUri("/client"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                await client.SendAsync("hello", CancellationToken.None).ConfigureAwait(false);
                using (var message = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    Assert.AreEqual("hello", message.Text);
                }

                await WaitWithTimeout(messageEventTcs.Task, TimeSpan.FromSeconds(2), "OnMessage was not fired.")
                    .ConfigureAwait(false);

                await client.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);

                Assert.AreEqual(1, Volatile.Read(ref connectedCount));
                Assert.GreaterOrEqual(Volatile.Read(ref messageCount), 1);
                Assert.AreEqual(1, Volatile.Read(ref closedCount));
            });
        }

        [Test]
        public void ReceiveAsync_ConcurrentCalls_RejectSecondCall()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(
                    new WebSocketTestServerOptions
                    {
                        EchoDelayMs = 200
                    });
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/concurrent-receive"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                var firstReceiveTask = client.ReceiveAsync(cts.Token).AsTask();
                await Task.Delay(10).ConfigureAwait(false);

                AssertAsync.ThrowsAsync<InvalidOperationException>(async () =>
                    await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false));

                try
                {
                    await firstReceiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected; the first receive was only used to hold the receive slot.
                }
            });
        }

        [Test]
        public void SendAfterClose_ThrowsInvalidOperationException()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/send-after-close"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                await client.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);

                AssertAsync.ThrowsAsync<InvalidOperationException>(async () =>
                    await client.SendAsync("should-fail", CancellationToken.None).ConfigureAwait(false));
            });
        }

        [Test]
        public void ReceiveAllAsync_ConsumesMessages_AndEndsOnClose()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/receive-all"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                await using var enumerator = client.ReceiveAllAsync(CancellationToken.None).GetAsyncEnumerator();

                await client.SendAsync("one", CancellationToken.None).ConfigureAwait(false);
                bool moved = await WaitWithTimeout(
                    enumerator.MoveNextAsync().AsTask(),
                    TimeSpan.FromSeconds(2),
                    "Timed out waiting for first streamed message.").ConfigureAwait(false);
                Assert.IsTrue(moved);
                using (var message = enumerator.Current)
                {
                    Assert.AreEqual("one", message.Text);
                }

                await client.SendAsync("two", CancellationToken.None).ConfigureAwait(false);
                moved = await WaitWithTimeout(
                    enumerator.MoveNextAsync().AsTask(),
                    TimeSpan.FromSeconds(2),
                    "Timed out waiting for second streamed message.").ConfigureAwait(false);
                Assert.IsTrue(moved);
                using (var message = enumerator.Current)
                {
                    Assert.AreEqual("two", message.Text);
                }

                await client.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);

                bool hasMore = await WaitWithTimeout(
                    enumerator.MoveNextAsync().AsTask(),
                    TimeSpan.FromSeconds(2),
                    "Timed out waiting for streamed close completion.").ConfigureAwait(false);
                Assert.IsFalse(hasMore);
            });
        }

        [Test]
        public void ReceiveAllAsync_Cancellation_ThrowsOperationCanceledException()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/receive-all-cancel"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await using var enumerator = client.ReceiveAllAsync(cts.Token).GetAsyncEnumerator();

                AssertAsync.ThrowsAsync<OperationCanceledException>(async () =>
                    await enumerator.MoveNextAsync().AsTask().ConfigureAwait(false));

                await client.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            });
        }

        [Test]
        public void ReceiveAllAsync_RejectsConcurrentEnumerators_AndAllowsNewAfterDispose()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/receive-all-exclusive"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                var enumerator1 = client.ReceiveAllAsync(CancellationToken.None).GetAsyncEnumerator();

                Assert.Throws<InvalidOperationException>(() =>
                {
                    _ = client.ReceiveAllAsync(CancellationToken.None);
                });

                AssertAsync.ThrowsAsync<InvalidOperationException>(async () =>
                    await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false));

                await enumerator1.DisposeAsync().ConfigureAwait(false);

                await using var enumerator2 = client.ReceiveAllAsync(CancellationToken.None).GetAsyncEnumerator();
                await enumerator2.DisposeAsync().ConfigureAwait(false);

                await client.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            });
        }

        [Test]
        public void MethodsThrowObjectDisposedException_AfterDispose()
        {
            var client = new WebSocketClient(new TestTcpWebSocketTransport());
            client.Dispose();

            AssertAsync.ThrowsAsync<ObjectDisposedException>(async () =>
                await client.ConnectAsync(new Uri("ws://localhost/"), CancellationToken.None).ConfigureAwait(false));

            AssertAsync.ThrowsAsync<ObjectDisposedException>(async () =>
                await client.SendAsync("x", CancellationToken.None).ConfigureAwait(false));
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

        private static async Task WaitWithTimeout(Task task, TimeSpan timeout, string errorMessage)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
                throw new TimeoutException(errorMessage);

            await task.ConfigureAwait(false);
        }

        private static async Task<T> WaitWithTimeout<T>(Task<T> task, TimeSpan timeout, string errorMessage)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
                throw new TimeoutException(errorMessage);

            return await task.ConfigureAwait(false);
        }
    }
}
