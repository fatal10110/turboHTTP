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
    }
}
