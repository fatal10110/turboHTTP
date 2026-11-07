using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// #238 through RawSocketTransport: a dead idle connection found by the reuse PING probe is replaced
    /// without failing the request, and MaxConnectionsPerOrigin opens a second connection for a saturated
    /// origin but never a third.
    /// </summary>
    public partial class RawSocketTransportHttp2ConnectionManagementTests
    {
        [Test]
        public void IdleReuseProbe_PeerNeverAcks_RequestSucceedsOnFreshConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                var threshold = TimeSpan.FromHours(1);
                using var harness = new Harness(new Http2Options
                {
                    PingInterval = TimeSpan.Zero,
                    PingTimeout = TimeSpan.FromMilliseconds(200),
                    ReuseProbeIdleThreshold = threshold
                });

                // Both peers answer requests but ignore PING (never ACK): the first goes silent while idle.
                int connectionCount = 0;
                for (int i = 0; i < 2; i++)
                {
                    harness.AddIdleConnection((codec, ct) =>
                    {
                        Interlocked.Increment(ref connectionCount);
                        return ServeAsync(codec, (c, request) => RespondAsync(c, request, ct), ct);
                    }, cts.Token);
                }

                await AssertOkAsync(harness.SendAsync(HttpMethod.GET, "/warm", null, cts.Token));
                var idle = GetManager(harness).GetIfExists(Host, Port);
                Assert.IsNotNull(idle);
                idle.BackdateInboundActivityForTest(threshold + threshold);

                // Not idempotent: replayed only because the probe failed before anything was sent.
                await AssertOkAsync(harness.SendAsync(HttpMethod.POST, "/after-idle", "x", cts.Token));

                Assert.AreEqual(2, Volatile.Read(ref connectionCount));
                Assert.IsFalse(idle.IsAlive, "the dead idle connection is closed");
                Assert.AreNotSame(idle, GetManager(harness).GetIfExists(Host, Port));
            });
        }

        [Test]
        public void MaxConnectionsTwo_SaturatedOrigin_OpensSecondConnectionButNeverThird()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                using var harness = new Harness(new Http2Options
                {
                    PingInterval = TimeSpan.Zero,
                    MaxConnectionsPerOrigin = 2
                });

                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var oneStream = new byte[] { 0x00, 0x03, 0x00, 0x00, 0x00, 0x01 }; // MAX_CONCURRENT_STREAMS = 1
                int connectionCount = 0;
                int requestsSeen = 0;
                for (int i = 0; i < 3; i++)
                {
                    harness.AddIdleConnection((codec, server, ct) =>
                    {
                        Interlocked.Increment(ref connectionCount);
                        return ServeAsync(codec, async (c, request) =>
                        {
                            Interlocked.Increment(ref requestsSeen);
                            await release.Task;
                            await RespondAsync(c, request, ct);
                        }, ct);
                    }, oneStream, cts.Token);
                }

                var taskA = harness.SendAsync(HttpMethod.GET, "/a", null, cts.Token);
                await WaitForConditionAsync(() => Volatile.Read(ref requestsSeen) == 1, cts.Token);

                // The only stream slot of the first connection is taken: a second connection opens.
                var taskB = harness.SendAsync(HttpMethod.GET, "/b", null, cts.Token);
                await WaitForConditionAsync(() => Volatile.Read(ref requestsSeen) == 2, cts.Token);
                Assert.AreEqual(2, Volatile.Read(ref connectionCount));

                // Both saturated at the maximum: the third request queues for a slot, no third connection.
                var taskC = harness.SendAsync(HttpMethod.GET, "/c", null, cts.Token);
                await WaitForConditionAsync(() => QueuedRequests(harness) == 1, cts.Token);
                Assert.AreEqual(2, Volatile.Read(ref connectionCount));
                Assert.AreEqual(2, GetConnections(harness).Length);

                release.TrySetResult(true);
                await AssertOkAsync(taskA);
                await AssertOkAsync(taskB);
                await AssertOkAsync(taskC);
                Assert.AreEqual(2, Volatile.Read(ref connectionCount));
            });
        }

        private static Http2ConnectionManager GetManager(Harness harness)
        {
            var field = typeof(RawSocketTransport).GetField("_h2Manager", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field);
            return (Http2ConnectionManager)field.GetValue(harness.Transport);
        }

        private static Http2Connection[] GetConnections(Harness harness)
        {
            var field = typeof(Http2ConnectionManager).GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field);
            var connections = (ConcurrentDictionary<string, Http2Connection[]>)field.GetValue(GetManager(harness));
            return connections.TryGetValue(Host + ":" + Port, out var set) ? set : Array.Empty<Http2Connection>();
        }

        private static int QueuedRequests(Harness harness)
        {
            int queued = 0;
            foreach (var connection in GetConnections(harness))
                queued += connection.QueuedAdmissionCount;
            return queued;
        }
    }
}
