using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketReconnectionTests
    {
        [Test]
        public void UnexpectedDisconnect_FiresErrorThenReconnectingThenReconnected()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(
                    new WebSocketTestServerOptions
                    {
                        DisconnectFirstConnectionAfterHandshake = true
                    });

                await using var client = new ResilientWebSocketClient(new TestTcpWebSocketTransport());

                var sequence = new ConcurrentQueue<string>();
                var reconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                client.OnError += _ => sequence.Enqueue("error");
                client.OnReconnecting += (attempt, delay) => sequence.Enqueue("reconnecting:" + attempt);
                client.OnReconnected += () =>
                {
                    sequence.Enqueue("reconnected");
                    reconnectedTcs.TrySetResult(true);
                };

                var options = CreateReconnectOptions(maxRetries: 4);
                await client.ConnectAsync(server.CreateUri("/reconnect-order"), options, CancellationToken.None)
                    .ConfigureAwait(false);

                await WaitWithTimeout(reconnectedTcs.Task, TimeSpan.FromSeconds(3), "Reconnect did not complete.")
                    .ConfigureAwait(false);

                await client.SendAsync("hello", CancellationToken.None).ConfigureAwait(false);
                using var message = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("hello", message.Text);

                var events = sequence.ToArray();
                int errorIndex = Array.FindIndex(events, s => s == "error");
                int reconnectingIndex = Array.FindIndex(events, s => s.StartsWith("reconnecting:", StringComparison.Ordinal));
                int reconnectedIndex = Array.FindIndex(events, s => s == "reconnected");

                Assert.GreaterOrEqual(errorIndex, 0, "Expected OnError event.");
                Assert.Greater(reconnectingIndex, errorIndex, "Expected OnReconnecting after OnError.");
                Assert.Greater(reconnectedIndex, reconnectingIndex, "Expected OnReconnected after OnReconnecting.");
            });
        }

        [Test]
        public void NormalClose_DoesNotTriggerReconnection()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                await using var client = new ResilientWebSocketClient(new TestTcpWebSocketTransport());

                int reconnectingCount = 0;
                client.OnReconnecting += (_, __) => Interlocked.Increment(ref reconnectingCount);

                await client.ConnectAsync(
                    server.CreateUri("/normal-close"),
                    CreateReconnectOptions(maxRetries: 3),
                    CancellationToken.None).ConfigureAwait(false);

                await client.CloseAsync(WebSocketCloseCode.NormalClosure, "client-close", CancellationToken.None)
                    .ConfigureAwait(false);

                await Task.Delay(200).ConfigureAwait(false);
                Assert.AreEqual(0, Volatile.Read(ref reconnectingCount));
            });
        }

        [Test]
        public void DisposeDuringBackoff_CancelsReconnectLoop()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(
                    new WebSocketTestServerOptions
                    {
                        DisconnectEveryConnectionAfterHandshake = true
                    });

                await using var client = new ResilientWebSocketClient(new TestTcpWebSocketTransport());

                var reconnectingTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnReconnecting += (_, __) => reconnectingTcs.TrySetResult(true);

                var options = new WebSocketConnectionOptions
                {
                    HandshakeTimeout = TimeSpan.FromSeconds(2),
                    CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                    PingInterval = TimeSpan.Zero
                }.WithReconnection(new WebSocketReconnectPolicy(
                    maxRetries: -1,
                    initialDelay: TimeSpan.FromMilliseconds(500),
                    maxDelay: TimeSpan.FromMilliseconds(500),
                    backoffMultiplier: 1.0,
                    jitterFactor: 0.0));

                await client.ConnectAsync(server.CreateUri("/dispose-backoff"), options, CancellationToken.None)
                    .ConfigureAwait(false);

                await WaitWithTimeout(
                    reconnectingTcs.Task,
                    TimeSpan.FromSeconds(3),
                    "Expected reconnect loop to enter backoff.").ConfigureAwait(false);

                var disposeTask = client.DisposeAsync().AsTask();
                await WaitWithTimeout(disposeTask, TimeSpan.FromSeconds(2), "DisposeAsync did not complete promptly.")
                    .ConfigureAwait(false);
            });
        }

        [Test]
        public void ReceiveAllAsync_BlocksDuringReconnect_ThenResumesAfterReconnected()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(
                    new WebSocketTestServerOptions
                    {
                        DisconnectFirstConnectionAfterHandshake = true
                    });

                await using var client = new ResilientWebSocketClient(new TestTcpWebSocketTransport());

                var reconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnReconnected += () => reconnectedTcs.TrySetResult(true);

                var options = CreateReconnectOptions(maxRetries: 4);
                await client.ConnectAsync(server.CreateUri("/receive-all-reconnect"), options, CancellationToken.None)
                    .ConfigureAwait(false);

                await using var enumerator = client.ReceiveAllAsync(CancellationToken.None).GetAsyncEnumerator();
                var moveNextTask = enumerator.MoveNextAsync().AsTask();

                await Task.Delay(50).ConfigureAwait(false);
                Assert.IsFalse(moveNextTask.IsCompleted, "Expected MoveNextAsync to wait for inbound data.");

                await WaitWithTimeout(
                    reconnectedTcs.Task,
                    TimeSpan.FromSeconds(3),
                    "Reconnect did not complete.").ConfigureAwait(false);

                await client.SendAsync("after-reconnect", CancellationToken.None).ConfigureAwait(false);

                bool moved = await WaitWithTimeout(
                    moveNextTask,
                    TimeSpan.FromSeconds(3),
                    "Timed out waiting for streamed message after reconnection.").ConfigureAwait(false);
                Assert.IsTrue(moved);
                using (var message = enumerator.Current)
                {
                    Assert.AreEqual("after-reconnect", message.Text);
                }

                await client.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);

                bool hasMore = await WaitWithTimeout(
                    enumerator.MoveNextAsync().AsTask(),
                    TimeSpan.FromSeconds(2),
                    "Timed out waiting for stream completion after close.").ConfigureAwait(false);
                Assert.IsFalse(hasMore);
            });
        }

        [Test]
        public void ReceiveAllAsync_ReturnsFalse_WhenReconnectExhausted()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer(
                    new WebSocketTestServerOptions
                    {
                        DisconnectEveryConnectionAfterHandshake = true
                    });

                await using var client = new ResilientWebSocketClient(new TestTcpWebSocketTransport());
                var options = CreateReconnectOptions(maxRetries: 1);

                await client.ConnectAsync(server.CreateUri("/receive-all-exhausted"), options, CancellationToken.None)
                    .ConfigureAwait(false);

                await using var enumerator = client.ReceiveAllAsync(CancellationToken.None).GetAsyncEnumerator();
                bool moved = await WaitWithTimeout(
                    enumerator.MoveNextAsync().AsTask(),
                    TimeSpan.FromSeconds(5),
                    "Timed out waiting for stream completion after reconnect exhaustion.").ConfigureAwait(false);

                Assert.IsFalse(moved);
            });
        }

        private static WebSocketConnectionOptions CreateReconnectOptions(int maxRetries)
        {
            return new WebSocketConnectionOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(2),
                CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                PingInterval = TimeSpan.Zero,
                PongTimeout = TimeSpan.FromMilliseconds(200),
                ReceiveQueueCapacity = 32
            }.WithReconnection(new WebSocketReconnectPolicy(
                maxRetries: maxRetries,
                initialDelay: TimeSpan.FromMilliseconds(25),
                maxDelay: TimeSpan.FromMilliseconds(100),
                backoffMultiplier: 2.0,
                jitterFactor: 0.0));
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
