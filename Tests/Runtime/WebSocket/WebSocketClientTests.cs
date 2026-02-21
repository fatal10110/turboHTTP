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
    }
}
