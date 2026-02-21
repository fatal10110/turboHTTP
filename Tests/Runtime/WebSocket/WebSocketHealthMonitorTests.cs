using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketHealthMonitorTests
    {
        [Test]
        public void Health_Quality_TransitionsFromUnknown_AfterRttBaseline()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                var qualityChangedTcs = new TaskCompletionSource<ConnectionQuality>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnConnectionQualityChanged += quality =>
                {
                    if (quality != ConnectionQuality.Unknown)
                        qualityChangedTcs.TrySetResult(quality);
                };

                await client.ConnectAsync(server.CreateUri("/health-quality"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                var quality = await WaitWithTimeout(
                    qualityChangedTcs.Task,
                    TimeSpan.FromSeconds(3),
                    "Health quality did not transition out of Unknown.").ConfigureAwait(false);

                Assert.AreNotEqual(ConnectionQuality.Unknown, quality);

                var snapshot = client.Health;
                Assert.AreNotEqual(ConnectionQuality.Unknown, snapshot.Quality);
                Assert.GreaterOrEqual(snapshot.CurrentRtt, TimeSpan.Zero);
                Assert.GreaterOrEqual(snapshot.AverageRtt, TimeSpan.Zero);
                Assert.GreaterOrEqual(snapshot.RttJitter, TimeSpan.Zero);
            });
        }

        [Test]
        public void Health_RecentThroughput_UpdatesAfterTraffic()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var transport = new TestTcpWebSocketTransport();
                await using var client = new WebSocketClient(transport);

                await client.ConnectAsync(server.CreateUri("/health-throughput"), CreateOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                await client.SendAsync("throughput", CancellationToken.None).ConfigureAwait(false);
                using (var message = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    Assert.AreEqual("throughput", message.Text);
                }

                await WaitUntilAsync(
                    () => client.Health.RecentThroughput > 0d,
                    TimeSpan.FromSeconds(2),
                    "Expected health throughput to become positive after traffic.")
                    .ConfigureAwait(false);

                Assert.Greater(client.Health.RecentThroughput, 0d);
            });
        }

        private static WebSocketConnectionOptions CreateOptions()
        {
            return new WebSocketConnectionOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(2),
                CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                PingInterval = TimeSpan.FromMilliseconds(50),
                PongTimeout = TimeSpan.FromMilliseconds(500),
                ReceiveQueueCapacity = 32,
                MetricsUpdateMessageInterval = 1,
                MetricsUpdateInterval = TimeSpan.FromMilliseconds(50),
                EnableHealthMonitoring = true
            };
        }

        private static async Task<T> WaitWithTimeout<T>(Task<T> task, TimeSpan timeout, string errorMessage)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
                throw new TimeoutException(errorMessage);

            return await task.ConfigureAwait(false);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string errorMessage)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (predicate())
                    return;

                await Task.Delay(25).ConfigureAwait(false);
            }

            throw new TimeoutException(errorMessage);
        }
    }
}
