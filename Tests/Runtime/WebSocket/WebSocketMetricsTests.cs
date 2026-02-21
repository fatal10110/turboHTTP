using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketMetricsTests
    {
        [Test]
        public void Metrics_CountersIncrease_AfterSendReceive()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/metrics-counters"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                await client.SendAsync("hello-metrics", CancellationToken.None).ConfigureAwait(false);
                using (var message = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    Assert.AreEqual("hello-metrics", message.Text);
                }

                var metrics = client.Metrics;
                Assert.AreEqual(1, metrics.MessagesSent);
                Assert.AreEqual(1, metrics.MessagesReceived);
                Assert.GreaterOrEqual(metrics.FramesSent, 1);
                Assert.GreaterOrEqual(metrics.FramesReceived, 1);
                Assert.Greater(metrics.BytesSent, 0);
                Assert.Greater(metrics.BytesReceived, 0);
                Assert.AreEqual(1.0d, metrics.CompressionRatio, 0.0001d);
                Assert.Greater(metrics.ConnectionUptime, TimeSpan.Zero);
                Assert.GreaterOrEqual(metrics.LastActivityAge, TimeSpan.Zero);
            });
        }

        [Test]
        public void MetricsUpdated_EventFires_WhenMessageIntervalReached()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                var metricsUpdatedTcs = new TaskCompletionSource<WebSocketMetrics>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnMetricsUpdated += metrics => metricsUpdatedTcs.TrySetResult(metrics);

                await client.ConnectAsync(server.CreateUri("/metrics-events"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                await client.SendAsync("event", CancellationToken.None).ConfigureAwait(false);

                var snapshot = await WaitWithTimeout(
                    metricsUpdatedTcs.Task,
                    TimeSpan.FromSeconds(2),
                    "Metrics update event was not raised.").ConfigureAwait(false);

                Assert.GreaterOrEqual(snapshot.MessagesSent, 1);
            });
        }

        [Test]
        public void Metrics_CaptureConcurrentSendReceiveTraffic()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/metrics-concurrency"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                const int messageCount = 20;
                var sendTasks = new Task[messageCount];
                for (int i = 0; i < messageCount; i++)
                {
                    int messageIndex = i;
                    sendTasks[i] = client.SendAsync("msg-" + messageIndex, CancellationToken.None);
                }

                await Task.WhenAll(sendTasks).ConfigureAwait(false);

                for (int i = 0; i < messageCount; i++)
                {
                    using var _ = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                }

                var metrics = client.Metrics;
                Assert.AreEqual(messageCount, metrics.MessagesSent);
                Assert.AreEqual(messageCount, metrics.MessagesReceived);
            });
        }

        [Test]
        public void MetricsSnapshot_IsImmutableAfterCapture()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/metrics-snapshot"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                var snapshotBeforeTraffic = client.Metrics;

                await client.SendAsync("after-snapshot", CancellationToken.None).ConfigureAwait(false);
                using var _ = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(0, snapshotBeforeTraffic.MessagesSent);
                Assert.AreEqual(0, snapshotBeforeTraffic.MessagesReceived);
                Assert.GreaterOrEqual(client.Metrics.MessagesSent, 1);
                Assert.GreaterOrEqual(client.Metrics.MessagesReceived, 1);
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
                ReceiveQueueCapacity = 32,
                MetricsUpdateMessageInterval = 1,
                MetricsUpdateInterval = TimeSpan.Zero
            };
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
