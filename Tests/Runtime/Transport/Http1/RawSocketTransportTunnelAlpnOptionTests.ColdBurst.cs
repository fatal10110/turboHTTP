using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #1180: a cold burst of HTTPS requests through one CONNECT proxy to one origin, with HTTP/2 through
    /// tunnels enabled, waits for a single in-flight tunnel instead of each request opening its own
    /// (RFC 9113 Section 9.1); when that tunnel yields HTTP/1.1 or fails, the others open their own, and
    /// a failed tunnel falls back to the HTTP/2 connection another request published meanwhile. The
    /// loopback proxy counts CONNECT requests; the TLS stand-in can hold or fail each handshake.
    /// </summary>
    public partial class RawSocketTransportTunnelAlpnOptionTests
    {
        [Test]
        public void HttpsViaProxy_ColdBurst_TwentyConcurrentRequests_OneConnectTunnel()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var provider = InstallMockBouncyCastle(MockAlpnMode.HonorOffer, _ => release.Task);
                using var proxy = new TunnelOriginProxy();
                using var burst = new TunnelBurstTransport(proxy.Port);

                const int count = 20;
                var tasks = new Task<UHttpResponse>[count];
                for (int i = 0; i < count; i++)
                    tasks[i] = burst.SendAsync("/r" + i, cts.Token);

                // Every request is parked: in a tunnel handshake (without the gate, all 20 are) or
                // awaiting the first request's tunnel.
                await WaitForTunnelConditionAsync(
                    () => provider.Offers.Count + burst.AwaitingTunnel() >= count,
                    cts.Token);
                Assert.AreEqual(1, provider.Offers.Count, "the burst awaits one tunnel");

                release.TrySetResult(true);
                for (int i = 0; i < count; i++)
                    await AssertBodyAsync(tasks[i], "h2:/r" + i);

                Assert.AreEqual(1, proxy.ConnectRequests.Count, "one CONNECT for the whole burst");
                CollectionAssert.AreEqual(new[] { "h2" }, proxy.Protocols.ToArray());
                Assert.AreEqual(count, proxy.Http2Requests.Count);
                Assert.AreEqual(0, provider.Streams.Single().DisposeCount, "the one tunnel stays open");
                Assert.IsTrue(burst.Transport.HasHttp2Connection(OriginHost, OriginPort, "127.0.0.1", proxy.Port));
            });
        }

        [Test]
        public void HttpsViaProxy_ColdBurst_FirstTunnelNegotiatesHttp11_WaitersOpenOwnTunnels()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var provider = InstallMockBouncyCastle(
                    MockAlpnMode.AlwaysHttp11,
                    n => n == 1 ? releaseFirst.Task : Task.CompletedTask);
                using var proxy = new TunnelOriginProxy();
                using var burst = new TunnelBurstTransport(proxy.Port);

                var taskA = burst.SendAsync("/a", cts.Token);
                await WaitForTunnelConditionAsync(() => provider.Offers.Count == 1, cts.Token);
                var taskB = burst.SendAsync("/b", cts.Token);
                var taskC = burst.SendAsync("/c", cts.Token);
                await WaitForTunnelConditionAsync(
                    () => provider.Offers.Count + burst.AwaitingTunnel() >= 3,
                    cts.Token);
                Assert.AreEqual(1, provider.Offers.Count, "B and C await A's tunnel");

                // A's tunnel negotiates HTTP/1.1: B and C open their own instead of awaiting again.
                releaseFirst.TrySetResult(true);
                await AssertBodyAsync(taskA, "http/1.1:/a");
                await AssertBodyAsync(taskB, "http/1.1:/b");
                await AssertBodyAsync(taskC, "http/1.1:/c");
                Assert.AreEqual(3, proxy.ConnectRequests.Count);
                CollectionAssert.AreEqual(new[] { "http/1.1", "http/1.1", "http/1.1" }, proxy.Protocols.ToArray());

                // The tunnel key is remembered as HTTP/1.1: a later request opens its tunnel at once.
                int awaitedBefore = burst.AwaitingTunnel();
                await AssertBodyAsync(burst.SendAsync("/d", cts.Token), "http/1.1:/d");
                Assert.AreEqual(awaitedBefore, burst.AwaitingTunnel(), "an HTTP/1.1 tunnel key is not gated");
                Assert.AreEqual(4, proxy.ConnectRequests.Count);
            });
        }

        [Test]
        public void HttpsViaProxy_ColdBurst_FirstTunnelFails_WaitersOpenOwn_FailedOwnTunnelReusesPublishedConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                var failFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var failThird = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var provider = InstallMockBouncyCastle(MockAlpnMode.HonorOffer, async n =>
                {
                    if (n == 1)
                    {
                        await failFirst.Task;
                        throw new IOException("first tunnel handshake refused");
                    }

                    if (n == 2)
                    {
                        await releaseSecond.Task;
                        return;
                    }

                    await failThird.Task;
                    throw new IOException("third tunnel handshake refused");
                });
                using var proxy = new TunnelOriginProxy();
                using var burst = new TunnelBurstTransport(proxy.Port);

                var taskA = burst.SendAsync("/a", cts.Token);
                await WaitForTunnelConditionAsync(() => provider.Offers.Count == 1, cts.Token);
                var taskB = burst.SendAsync("/b", cts.Token);
                var taskC = burst.SendAsync("/c", cts.Token);
                await WaitForTunnelConditionAsync(
                    () => provider.Offers.Count + burst.AwaitingTunnel() >= 3,
                    cts.Token);
                Assert.AreEqual(1, provider.Offers.Count, "B and C await A's tunnel");

                // A's tunnel fails: A fails (no connection to fall back to), B and C open their own.
                failFirst.TrySetResult(true);
                await TestHelpers.AssertThrowsAsync<UHttpException>(() => taskA);
                await WaitForTunnelConditionAsync(() => provider.Offers.Count == 3, cts.Token);

                // The second tunnel publishes HTTP/2 and serves its request.
                releaseSecond.TrySetResult(true);
                var first = await Task.WhenAny(taskB, taskC);
                using (var response = await first)
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                // The third tunnel fails after that: its request re-checks the manager and succeeds on the
                // published connection instead of failing.
                failThird.TrySetResult(true);
                using (var response = await (ReferenceEquals(first, taskB) ? taskC : taskB))
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                Assert.AreEqual(3, proxy.ConnectRequests.Count);
                CollectionAssert.AreEqual(new[] { "h2" }, proxy.Protocols.ToArray());
                Assert.AreEqual(2, proxy.Http2Requests.Count);
                Assert.IsTrue(
                    burst.Contexts.Any(c => c.Timeline.Any(e => e.Name == "TransportH2DialFallback")),
                    "the failed own tunnel fell back to the published connection");
            });
        }

        private static async Task AssertBodyAsync(Task<UHttpResponse> task, string expectedBody)
        {
            using var response = await task;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(expectedBody, response.GetBodyAsString());
        }

        // Waits for a state change made by another task; bounded by the test's token, never a fixed sleep.
        private static async Task WaitForTunnelConditionAsync(Func<bool> condition, CancellationToken ct)
        {
            while (!condition())
                await Task.Delay(1, ct);
        }

        /// <summary>
        /// A BouncyCastle transport with HTTP/2 through tunnels enabled, dispatching each request through
        /// the loopback proxy with its own <see cref="RequestContext"/>, so the test can count the
        /// requests that await another request's tunnel. The pool allows many connections, so only the
        /// cold-dial gate (not the per-host permit) can serialise the tunnels.
        /// </summary>
        private sealed class TunnelBurstTransport : IDisposable
        {
            private readonly int _proxyPort;
            private readonly TcpConnectionPool _pool;

            public TunnelBurstTransport(int proxyPort)
            {
                _proxyPort = proxyPort;
                _pool = new TcpConnectionPool(maxConnectionsPerHost: 64, tlsBackend: TlsBackend.BouncyCastle);
                Transport = new RawSocketTransport(
                    _pool,
                    TlsBackend.BouncyCastle,
                    new Http2Options { EnableHttp2ThroughConnectTunnels = true });
            }

            public RawSocketTransport Transport { get; }

            // Added by the test thread only.
            public List<RequestContext> Contexts { get; } = new List<RequestContext>();

            public int AwaitingTunnel()
            {
                return Contexts.Sum(c => c.Timeline.Count(e => e.Name == "TransportProxyH2AwaitDial"));
            }

            public Task<UHttpResponse> SendAsync(string path, CancellationToken ct)
            {
                // The proxy hop is http:// loopback: opt in, as players default to no cleartext (#1261).
                var request = TestHelpers.AllowLoopbackCleartext(
                    new UHttpRequest(HttpMethod.GET, new Uri($"https://{OriginHost}{path}"))
                        .WithProxy(new ProxySettings
                        {
                            Address = new Uri($"http://127.0.0.1:{_proxyPort}"),
                            UseEnvironmentVariables = false
                        }));
                var context = new RequestContext(request);
                Contexts.Add(context);
                return TransportDispatchHelper.CollectResponseAsync(Transport.DispatchAsync, request, context, ct);
            }

            public void Dispose()
            {
                Transport.Dispose();
                _pool.Dispose();
            }
        }
    }
}
