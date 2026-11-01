using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Connection;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// Connect deadline and same-family racing (#163). A watchdog token on every call turns a
    /// hang on the old code into a failure instead of a stuck run.
    /// </summary>
    [TestFixture]
    public class HappyEyeballsSameFamilyTests
    {
        private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

        [Test]
        public void SameFamily_FirstBlackholed_ConnectsToSecondWithinAttemptDelay()
        {
            AssertAsync.Run(async () =>
            {
                var blackholed = IPAddress.Parse("198.51.100.1");
                var healthy = IPAddress.Parse("198.51.100.2");
                var dialer = new FakeDialer(blackholed, healthy);
                var options = new HappyEyeballsOptions(); // defaults: 125 ms spacing, 2 concurrent

                using var watchdog = new CancellationTokenSource(Watchdog);
                var elapsed = Stopwatch.StartNew();
                using var winner = await HappyEyeballsConnector.ConnectAsync(
                    new[] { blackholed, healthy },
                    443,
                    TimeSpan.FromSeconds(30),
                    options,
                    watchdog.Token,
                    dialer.DialAsync);
                elapsed.Stop();

                Assert.AreSame(dialer.HealthySocket, winner);
                Assert.IsFalse(watchdog.IsCancellationRequested, "connect only finished through the watchdog");
                // attempt delay (125 ms) + fake RTT (10 ms); generous bound for loaded CI machines.
                Assert.Less(elapsed.Elapsed, TimeSpan.FromSeconds(5));
                Assert.AreEqual(2, dialer.MaxConcurrent, "the second address must race, not wait");
                await dialer.BlackholeCancelled.Task;
            });
        }

        [Test]
        public void SingleFamily_AllBlackholed_ConnectTimeoutThrowsTimeoutException()
        {
            AssertConnectTimesOut(
                new HappyEyeballsOptions(),
                IPAddress.Parse("198.51.100.1"),
                IPAddress.Parse("198.51.100.3"));
        }

        [Test]
        public void SequentialMode_ConnectTimeoutHonoured()
        {
            AssertConnectTimesOut(
                new HappyEyeballsOptions { Enable = false },
                IPAddress.Parse("198.51.100.1"));
        }

        [Test]
        public void DualStack_AllBlackholed_ConnectTimeoutThrowsTimeoutException()
        {
            AssertConnectTimesOut(
                new HappyEyeballsOptions
                {
                    FamilyStaggerDelay = TimeSpan.FromMilliseconds(10),
                    AttemptSpacingDelay = TimeSpan.FromMilliseconds(10),
                },
                IPAddress.Parse("2001:db8::1"),
                IPAddress.Parse("198.51.100.1"));
        }

        [Test]
        public void CallerCancellation_SurfacesAsCancellationNotTimeout()
        {
            using var caller = new CancellationTokenSource();
            var dialer = new FakeDialer(IPAddress.Parse("198.51.100.1"), null);
            dialer.OnBlackholeStarted = () => caller.Cancel();

            Assert.Catch<OperationCanceledException>(() => AssertAsync.Run(async () =>
            {
                using var _ = await HappyEyeballsConnector.ConnectAsync(
                    new[] { IPAddress.Parse("198.51.100.1") },
                    443,
                    TimeSpan.FromSeconds(30),
                    new HappyEyeballsOptions(),
                    caller.Token,
                    dialer.DialAsync);
            }));
        }

        [Test]
        public void Pool_ConnectTimeout_SurfacesUHttpTimeout()
        {
            var dialer = new FakeDialer(IPAddress.Parse("198.51.100.1"), null);
            using var watchdog = new CancellationTokenSource(Watchdog);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
            {
                using var _ = await TcpConnectionPool.ConnectWithDeadlineAsync(
                    new[] { IPAddress.Parse("198.51.100.1") },
                    443,
                    TimeSpan.FromMilliseconds(200),
                    new HappyEyeballsOptions(),
                    watchdog.Token,
                    dialer.DialAsync);
            });

            Assert.AreEqual(UHttpErrorType.Timeout, ex.HttpError.Type);
            Assert.IsFalse(watchdog.IsCancellationRequested);
        }

        private static void AssertConnectTimesOut(HappyEyeballsOptions options, params IPAddress[] blackholed)
        {
            var dialer = new FakeDialer(blackholed, null);
            using var watchdog = new CancellationTokenSource(Watchdog);

            AssertAsync.ThrowsAsync<TimeoutException>(async () =>
            {
                using var _ = await HappyEyeballsConnector.ConnectAsync(
                    blackholed,
                    443,
                    TimeSpan.FromMilliseconds(200),
                    options,
                    watchdog.Token,
                    dialer.DialAsync);
            });

            Assert.IsFalse(watchdog.IsCancellationRequested, "the connect deadline did not fire");
        }

        /// <summary>
        /// Blackholed addresses never answer (they wait for cancellation); the healthy one connects
        /// after 10 ms. Tracks peak concurrency.
        /// </summary>
        private sealed class FakeDialer
        {
            private readonly IPAddress[] _blackholed;
            private readonly IPAddress _healthy;
            private int _active;
            private int _maxConcurrent;

            public FakeDialer(IPAddress blackholed, IPAddress healthy)
                : this(new[] { blackholed }, healthy)
            {
            }

            public FakeDialer(IPAddress[] blackholed, IPAddress healthy)
            {
                _blackholed = blackholed;
                _healthy = healthy;
            }

            public Socket HealthySocket { get; private set; }

            public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

            public Action OnBlackholeStarted { get; set; }

            public TaskCompletionSource<bool> BlackholeCancelled { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<Socket> DialAsync(IPAddress address, int port, CancellationToken ct)
            {
                int now = Interlocked.Increment(ref _active);
                int seen;
                while (now > (seen = Volatile.Read(ref _maxConcurrent)) &&
                       Interlocked.CompareExchange(ref _maxConcurrent, now, seen) != seen)
                {
                }

                try
                {
                    if (Array.IndexOf(_blackholed, address) >= 0)
                    {
                        OnBlackholeStarted?.Invoke();
                        try
                        {
                            await Task.Delay(Timeout.Infinite, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            BlackholeCancelled.TrySetResult(true);
                            throw;
                        }
                    }

                    if (!address.Equals(_healthy))
                        throw new SocketException((int)SocketError.HostUnreachable);

                    await Task.Delay(10, ct);
                    HealthySocket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    return HealthySocket;
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }
        }
    }
}
