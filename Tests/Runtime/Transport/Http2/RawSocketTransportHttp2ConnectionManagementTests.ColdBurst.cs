using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// #736: a cold burst to one HTTP/2 origin waits for a single in-flight dial instead of each request
    /// dialing TCP+TLS (RFC 9113 Section 9.1); when that dial fails the others dial themselves, and a
    /// failed dial falls back to a connection another request published meanwhile. The origin is a
    /// loopback TCP listener behind a TLS stand-in that counts (and can hold or fail) every handshake.
    /// </summary>
    public partial class RawSocketTransportHttp2ConnectionManagementTests
    {
        [Test]
        public void ColdBurst_FiftyConcurrentRequests_OneTcpTlsHandshake()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var origin = new ColdBurstOrigin((n, ct) => release.Task, maxConnectionsPerHost: 64);

                const int burst = 50;
                var tasks = new Task<UHttpResponse>[burst];
                for (int i = 0; i < burst; i++)
                    tasks[i] = origin.SendAsync("/r" + i, cts.Token);

                // Every request is parked: in a handshake (without the gate, all 50 are) or awaiting the
                // first request's dial.
                await WaitForConditionAsync(() => origin.Handshakes + origin.AwaitingDial() >= burst, cts.Token);
                Assert.AreEqual(1, origin.Handshakes, "the burst awaits one dial");

                release.TrySetResult(true);
                foreach (var task in tasks)
                    await AssertOkAsync(task);

                Assert.AreEqual(1, origin.Handshakes);
                Assert.AreEqual(1, origin.AcceptedConnections);
            });
        }

        [Test]
        public void ColdBurst_FirstDialFails_WaitersDialThemselves_FailedOwnDialReusesPublishedConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                var failFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var failThird = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var origin = new ColdBurstOrigin(async (n, ct) =>
                {
                    if (n == 1)
                    {
                        await failFirst.Task;
                        throw new IOException("first dial refused");
                    }

                    if (n == 2)
                    {
                        await releaseSecond.Task;
                        return;
                    }

                    await failThird.Task;
                    throw new IOException("third dial refused");
                }, maxConnectionsPerHost: 64);

                var taskA = origin.SendAsync("/a", cts.Token);
                await WaitForConditionAsync(() => origin.Handshakes == 1, cts.Token);
                var taskB = origin.SendAsync("/b", cts.Token);
                var taskC = origin.SendAsync("/c", cts.Token);
                await WaitForConditionAsync(() => origin.Handshakes + origin.AwaitingDial() >= 3, cts.Token);
                Assert.AreEqual(1, origin.Handshakes, "B and C await A's dial");

                // A's dial fails: A fails (no connection to fall back to), B and C dial themselves.
                failFirst.TrySetResult(true);
                await TestHelpers.AssertThrowsAsync<UHttpException>(() => taskA);
                await WaitForConditionAsync(() => origin.Handshakes == 3, cts.Token);

                // The second dial publishes HTTP/2 and serves its request.
                releaseSecond.TrySetResult(true);
                var first = await Task.WhenAny(taskB, taskC);
                await AssertOkAsync(first);

                // The third dial fails after that: its request re-checks the manager and succeeds on the
                // published connection instead of failing.
                failThird.TrySetResult(true);
                await AssertOkAsync(ReferenceEquals(first, taskB) ? taskC : taskB);
                Assert.AreEqual(3, origin.Handshakes);
                Assert.IsTrue(
                    origin.Contexts.Any(c => c.Timeline.Any(e => e.Name == "TransportH2DialFallback")),
                    "the failed own dial fell back to the published connection");
            });
        }

        [Test]
        public void ColdBurst_SinglePermit_OwnDialerWaitingForThePermitReusesThePublishedConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                var failFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var origin = new ColdBurstOrigin(async (n, ct) =>
                {
                    if (n == 1)
                    {
                        await failFirst.Task;
                        throw new IOException("first dial refused");
                    }

                    if (n == 2)
                        await releaseSecond.Task;
                }, maxConnectionsPerHost: 1);

                var taskA = origin.SendAsync("/a", cts.Token);
                await WaitForConditionAsync(() => origin.Handshakes == 1, cts.Token);
                var taskB = origin.SendAsync("/b", cts.Token);
                var taskC = origin.SendAsync("/c", cts.Token);
                await WaitForConditionAsync(() => origin.AwaitingDial() >= 2, cts.Token);

                // A's dial fails: B and C dial themselves. One holds the only permit in handshake 2, the
                // other waits for that permit.
                failFirst.TrySetResult(true);
                await TestHelpers.AssertThrowsAsync<UHttpException>(() => taskA);
                await WaitForConditionAsync(
                    () => origin.Handshakes == 2 && origin.Connecting() >= 3,
                    cts.Token);

                // The permit is released only after the HTTP/2 connection is published, so the waiter
                // sees it and sends on it instead of dialing a third connection.
                releaseSecond.TrySetResult(true);
                await AssertOkAsync(taskB);
                await AssertOkAsync(taskC);
                Assert.AreEqual(2, origin.Handshakes, "the permit waiter reused the published connection");
                Assert.AreEqual(2, origin.AcceptedConnections);
            });
        }

        [Test]
        public void ColdBurst_PermitWaitEndsAfterHttp2Publish_PoolSkipsTheDialAndReturnsThePermit()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                    var first = await pool.GetConnectionAsync("127.0.0.1", port, false, cts.Token);

                    int asked = 0;
                    var waiting = pool.GetConnectionOrSkipAsync(
                        "127.0.0.1",
                        port,
                        false,
                        (h, p) =>
                        {
                            Interlocked.Increment(ref asked);
                            return true;
                        },
                        cts.Token).AsTask();
                    Assert.IsFalse(waiting.IsCompleted, "the only permit is taken");
                    Assert.AreEqual(0, Volatile.Read(ref asked));

                    first.Dispose();
                    Assert.IsNull(await waiting, "no connection once the dial became unnecessary");
                    Assert.AreEqual(1, Volatile.Read(ref asked));

                    // The skipped request returned its permit: the next one gets it without waiting.
                    var next = pool.GetConnectionAsync("127.0.0.1", port, false, cts.Token).AsTask();
                    await WaitForConditionAsync(() => next.IsCompleted, cts.Token);
                    using var nextLease = await next;
                    Assert.IsNotNull(nextLease);
                }
                finally
                {
                    listener.Stop();
                }
            });
        }

        [Test]
        public void ColdDialGate_Outcomes_WakeWaitersAndRememberNonHttp2Origins()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var manager = new Http2ConnectionManager(
                    new Http2Options { PingInterval = TimeSpan.Zero },
                    new StreamingOptions());

                // HTTP/2: the waiter looks again.
                Assert.IsNull(manager.ClaimOrJoinColdDial("a.test", 443, out var claim));
                Assert.IsNotNull(claim);
                var pending = manager.ClaimOrJoinColdDial("a.test", 443, out var noClaim);
                Assert.IsNotNull(pending);
                Assert.IsNull(noClaim);
                Assert.IsFalse(pending.IsCompleted);
                manager.CompleteColdDial(ref claim, Http2ConnectionManager.ColdDialOutcome.Http2);
                Assert.IsNull(claim);
                Assert.IsTrue(await Http2ConnectionManager.WaitForColdDialAsync(pending, cts.Token));

                // Failed: the waiter dials itself; the next request claims a new dial.
                Assert.IsNull(manager.ClaimOrJoinColdDial("b.test", 443, out var failed));
                pending = manager.ClaimOrJoinColdDial("b.test", 443, out _);
                var stale = failed;
                manager.CompleteColdDial(ref failed, Http2ConnectionManager.ColdDialOutcome.Failed);
                Assert.IsFalse(await Http2ConnectionManager.WaitForColdDialAsync(pending, cts.Token));
                Assert.IsNull(manager.ClaimOrJoinColdDial("b.test", 443, out var again));
                Assert.IsNotNull(again, "after a failure the gate is free for a new dial");

                // A stale claim completed twice never completes the newer claim of the key.
                manager.CompleteColdDial(ref stale, Http2ConnectionManager.ColdDialOutcome.Failed);
                var joined = manager.ClaimOrJoinColdDial("b.test", 443, out _);
                Assert.IsFalse(joined.IsCompleted);
                manager.CompleteColdDial(ref again, Http2ConnectionManager.ColdDialOutcome.Abandoned);
                Assert.IsTrue(await Http2ConnectionManager.WaitForColdDialAsync(joined, cts.Token), "abandoned: look again");

                // HTTP/1.1: the waiter dials itself and later requests skip the gate for that origin.
                Assert.IsNull(manager.ClaimOrJoinColdDial("c.test", 443, out var h1));
                pending = manager.ClaimOrJoinColdDial("c.test", 443, out _);
                manager.CompleteColdDial(ref h1, Http2ConnectionManager.ColdDialOutcome.NotHttp2);
                Assert.IsFalse(await Http2ConnectionManager.WaitForColdDialAsync(pending, cts.Token));
                Assert.IsNull(manager.ClaimOrJoinColdDial("c.test", 443, out var skipped));
                Assert.IsNull(skipped, "an HTTP/1.1 origin dials in parallel");

                // A waiter observes its own cancellation.
                Assert.IsNull(manager.ClaimOrJoinColdDial("d.test", 443, out var held));
                pending = manager.ClaimOrJoinColdDial("d.test", 443, out _);
                using (var waiterCts = new CancellationTokenSource())
                {
                    var wait = Http2ConnectionManager.WaitForColdDialAsync(pending, waiterCts.Token);
                    waiterCts.Cancel();
                    await TestHelpers.AssertThrowsAsync<OperationCanceledException>(() => wait);
                }

                // Dispose wakes the waiters; a disposed manager hands out no claim.
                manager.Dispose();
                Assert.IsTrue(await Http2ConnectionManager.WaitForColdDialAsync(pending, cts.Token));
                Assert.IsNull(manager.ClaimOrJoinColdDial("d.test", 443, out var afterDispose));
                Assert.IsNull(afterDispose);
                manager.CompleteColdDial(ref held, Http2ConnectionManager.ColdDialOutcome.Failed);
            });
        }

        /// <summary>
        /// A loopback HTTP/2 origin (cleartext frames behind a TLS stand-in that reports ALPN "h2") with a
        /// transport whose pool dials it for real; <c>onHandshake</c> gets the 1-based handshake number
        /// and can hold the handshake or fail it by throwing.
        /// </summary>
        private sealed class ColdBurstOrigin : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly ConcurrentBag<TcpClient> _clients = new ConcurrentBag<TcpClient>();
            private readonly Func<int, CancellationToken, Task> _onHandshake;
            private int _accepted;
            private int _handshakes;

            public ColdBurstOrigin(Func<int, CancellationToken, Task> onHandshake, int maxConnectionsPerHost)
            {
                _onHandshake = onHandshake;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = AcceptLoopAsync(); // ends when Dispose stops the listener
                Pool = new TcpConnectionPool(
                    _ => new CountingTlsProvider(this),
                    TlsBackend.SslStream,
                    maxConnectionsPerHost);
                Transport = new RawSocketTransport(
                    Pool,
                    TlsBackend.SslStream,
                    new Http2Options { PingInterval = TimeSpan.Zero });
            }

            public int Port { get; }
            public TcpConnectionPool Pool { get; }
            public RawSocketTransport Transport { get; }
            public List<RequestContext> Contexts { get; } = new List<RequestContext>();
            public int Handshakes => Volatile.Read(ref _handshakes);
            public int AcceptedConnections => Volatile.Read(ref _accepted);

            public int AwaitingDial()
            {
                return Contexts.Sum(c => c.Timeline.Count(e => e.Name == "TransportH2AwaitDial"));
            }

            public int Connecting()
            {
                return Contexts.Sum(c => c.Timeline.Count(e => e.Name == "TransportConnecting"));
            }

            public Task<UHttpResponse> SendAsync(string path, CancellationToken ct)
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri($"https://127.0.0.1:{Port}{path}"));
                var context = new RequestContext(request);
                Contexts.Add(context);
                return TransportDispatchHelper.CollectResponseAsync(Transport.DispatchAsync, request, context, ct);
            }

            private async Task<TlsResult> WrapAsync(Stream inner, CancellationToken ct)
            {
                int handshake = Interlocked.Increment(ref _handshakes);
                await _onHandshake(handshake, ct);
                return new TlsResult(inner, "h2", "1.3", providerName: "ColdBurstTls");
            }

            private async Task AcceptLoopAsync()
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception)
                    {
                        return; // listener stopped
                    }

                    Interlocked.Increment(ref _accepted);
                    _clients.Add(client);
                    _ = ServeClientAsync(client);
                }
            }

            private async Task ServeClientAsync(TcpClient client)
            {
                try
                {
                    var token = _cts.Token;
                    var codec = await CompleteHandshakeAsync(client.GetStream(), null, token);
                    await ServeAsync(codec, (c, request) => RespondAsync(c, request, token), token);
                }
                catch (Exception)
                {
                    // Dial failed or was dropped by the client, or the test is over.
                }
            }

            public void Dispose()
            {
                Transport.Dispose();
                Pool.Dispose();
                _cts.Cancel();
                _listener.Stop();
                foreach (var client in _clients)
                {
                    try { client.Close(); } catch { }
                }

                _cts.Dispose();
            }

            private sealed class CountingTlsProvider : ITlsProvider
            {
                private readonly ColdBurstOrigin _origin;

                public CountingTlsProvider(ColdBurstOrigin origin)
                {
                    _origin = origin;
                }

                public string ProviderName => "ColdBurstTls";

                public bool IsAlpnSupported() => true;

                public Task<TlsResult> WrapAsync(Stream innerStream, string host, string[] alpnProtocols, CancellationToken ct)
                {
                    return _origin.WrapAsync(innerStream, ct);
                }
            }
        }
    }
}
