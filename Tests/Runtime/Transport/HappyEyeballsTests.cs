using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Connection;

namespace TurboHTTP.Tests.Transport
{
    [TestFixture]
    public class HappyEyeballsTests
    {
        [Test]
        public void IPv6Healthy_WinsWhenFaster()
        {
            Task.Run(async () =>
            {
                var options = new HappyEyeballsOptions
                {
                    Enable = true,
                    FamilyStaggerDelay = TimeSpan.FromMilliseconds(20),
                    AttemptSpacingDelay = TimeSpan.FromMilliseconds(10),
                    MaxConcurrentAttempts = 2,
                    PreferIpv6 = true
                };

                var address6 = IPAddress.Parse("2001:db8::1");
                var address4 = IPAddress.Parse("192.0.2.10");

                var dialer = CreateDialer(new Dictionary<string, DialerPlan>
                {
                    [address6.ToString()] = DialerPlan.Success(TimeSpan.FromMilliseconds(40)),
                    [address4.ToString()] = DialerPlan.Success(TimeSpan.FromMilliseconds(90))
                }, out _);

                using var winner = await HappyEyeballsConnector.ConnectAsync(
                    new[] { address6, address4 },
                    443,
                    TimeSpan.FromSeconds(2),
                    options,
                    CancellationToken.None,
                    dialer);

                Assert.AreEqual(AddressFamily.InterNetworkV6, winner.AddressFamily);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void IPv6Stalled_IPv4Fallback()
        {
            Task.Run(async () =>
            {
                var options = new HappyEyeballsOptions
                {
                    Enable = true,
                    FamilyStaggerDelay = TimeSpan.FromMilliseconds(20),
                    AttemptSpacingDelay = TimeSpan.FromMilliseconds(10),
                    MaxConcurrentAttempts = 2,
                    PreferIpv6 = true
                };

                var address6 = IPAddress.Parse("2001:db8::2");
                var address4 = IPAddress.Parse("192.0.2.20");

                var dialer = CreateDialer(new Dictionary<string, DialerPlan>
                {
                    [address6.ToString()] = DialerPlan.Failure(TimeSpan.FromMilliseconds(800), new SocketException((int)SocketError.TimedOut)),
                    [address4.ToString()] = DialerPlan.Success(TimeSpan.FromMilliseconds(30))
                }, out _);

                using var winner = await HappyEyeballsConnector.ConnectAsync(
                    new[] { address6, address4 },
                    443,
                    TimeSpan.FromSeconds(2),
                    options,
                    CancellationToken.None,
                    dialer);

                Assert.AreEqual(AddressFamily.InterNetwork, winner.AddressFamily);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void BothFamiliesFail_AggregatesErrors()
        {
            Task.Run(async () =>
            {
                var options = new HappyEyeballsOptions
                {
                    Enable = true,
                    FamilyStaggerDelay = TimeSpan.FromMilliseconds(10),
                    AttemptSpacingDelay = TimeSpan.FromMilliseconds(10),
                    MaxConcurrentAttempts = 2,
                    PreferIpv6 = true
                };

                var address6 = IPAddress.Parse("2001:db8::3");
                var address4 = IPAddress.Parse("192.0.2.30");

                var dialer = CreateDialer(new Dictionary<string, DialerPlan>
                {
                    [address6.ToString()] = DialerPlan.Failure(TimeSpan.FromMilliseconds(20), new SocketException((int)SocketError.HostUnreachable)),
                    [address4.ToString()] = DialerPlan.Failure(TimeSpan.FromMilliseconds(30), new SocketException((int)SocketError.ConnectionRefused))
                }, out _);

                var ex = await TestHelpers.AssertThrowsAsync<AggregateException>(async () =>
                {
                    await HappyEyeballsConnector.ConnectAsync(
                        new[] { address6, address4 },
                        443,
                        TimeSpan.FromSeconds(2),
                        options,
                        CancellationToken.None,
                        dialer);
                });

                Assert.IsTrue(ex.InnerExceptions.Count >= 2);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Cancellation_StopsAllAttempts()
        {
            Task.Run(async () =>
            {
                var options = new HappyEyeballsOptions
                {
                    Enable = true,
                    FamilyStaggerDelay = TimeSpan.FromMilliseconds(10),
                    AttemptSpacingDelay = TimeSpan.FromMilliseconds(10),
                    MaxConcurrentAttempts = 2
                };

                var address6 = IPAddress.Parse("2001:db8::4");
                var address4 = IPAddress.Parse("192.0.2.40");

                var dialer = CreateDialer(new Dictionary<string, DialerPlan>
                {
                    [address6.ToString()] = DialerPlan.Success(TimeSpan.FromMilliseconds(500)),
                    [address4.ToString()] = DialerPlan.Success(TimeSpan.FromMilliseconds(500))
                }, out _);

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await HappyEyeballsConnector.ConnectAsync(
                        new[] { address6, address4 },
                        443,
                        TimeSpan.FromSeconds(5),
                        options,
                        cts.Token,
                        dialer);
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SingleFamily_NoRaceRegression()
        {
            Task.Run(async () =>
            {
                var options = new HappyEyeballsOptions
                {
                    Enable = true,
                    PreferIpv6 = true
                };

                var address4a = IPAddress.Parse("198.51.100.1");
                var address4b = IPAddress.Parse("198.51.100.2");

                var dialer = CreateDialer(new Dictionary<string, DialerPlan>
                {
                    [address4a.ToString()] = DialerPlan.Failure(TimeSpan.FromMilliseconds(10), new SocketException((int)SocketError.HostUnreachable)),
                    [address4b.ToString()] = DialerPlan.Success(TimeSpan.FromMilliseconds(10))
                }, out var stats);

                using var winner = await HappyEyeballsConnector.ConnectAsync(
                    new[] { address4a, address4b },
                    443,
                    TimeSpan.FromSeconds(2),
                    options,
                    CancellationToken.None,
                    dialer);

                Assert.AreEqual(AddressFamily.InterNetwork, winner.AddressFamily);
                Assert.LessOrEqual(stats.MaxConcurrentAttempts, 1);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void LargeDnsSet_BoundedConcurrency()
        {
            Task.Run(async () =>
            {
                var options = new HappyEyeballsOptions
                {
                    Enable = true,
                    FamilyStaggerDelay = TimeSpan.FromMilliseconds(20),
                    AttemptSpacingDelay = TimeSpan.FromMilliseconds(5),
                    MaxConcurrentAttempts = 2,
                    PreferIpv6 = true
                };

                var addresses = new List<IPAddress>();
                var plan = new Dictionary<string, DialerPlan>();

                for (int i = 1; i <= 10; i++)
                {
                    var ipv6 = IPAddress.Parse("2001:db8::" + i);
                    addresses.Add(ipv6);
                    plan[ipv6.ToString()] = DialerPlan.Failure(
                        TimeSpan.FromMilliseconds(40),
                        new SocketException((int)SocketError.HostUnreachable));
                }

                for (int i = 1; i <= 10; i++)
                {
                    var ipv4 = IPAddress.Parse("203.0.113." + i);
                    addresses.Add(ipv4);
                    plan[ipv4.ToString()] = i == 4
                        ? DialerPlan.Success(TimeSpan.FromMilliseconds(35))
                        : DialerPlan.Failure(TimeSpan.FromMilliseconds(35), new SocketException((int)SocketError.ConnectionRefused));
                }

                var dialer = CreateDialer(plan, out var stats);

                using var winner = await HappyEyeballsConnector.ConnectAsync(
                    addresses.ToArray(),
                    443,
                    TimeSpan.FromSeconds(5),
                    options,
                    CancellationToken.None,
                    dialer);

                Assert.AreEqual(AddressFamily.InterNetwork, winner.AddressFamily);
                Assert.LessOrEqual(stats.MaxConcurrentAttempts, 2);
            }).GetAwaiter().GetResult();
        }

        private static Func<IPAddress, int, CancellationToken, Task<Socket>> CreateDialer(
            IReadOnlyDictionary<string, DialerPlan> plan,
            out DialerStats stats)
        {
            var state = new DialerStats();
            stats = state;

            return async (address, port, ct) =>
            {
                if (!plan.TryGetValue(address.ToString(), out var entry))
                    throw new SocketException((int)SocketError.HostNotFound);

                state.IncrementActive();
                try
                {
                    await Task.Delay(entry.Delay, ct);
                    if (entry.Exception != null)
                        throw entry.Exception;
                    return new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                }
                finally
                {
                    state.DecrementActive();
                }
            };
        }

        private readonly struct DialerPlan
        {
            public DialerPlan(TimeSpan delay, Exception exception)
            {
                Delay = delay;
                Exception = exception;
            }

            public TimeSpan Delay { get; }
            public Exception Exception { get; }

            public static DialerPlan Success(TimeSpan delay) => new DialerPlan(delay, null);
            public static DialerPlan Failure(TimeSpan delay, Exception exception) => new DialerPlan(delay, exception);
        }

        private sealed class DialerStats
        {
            private int _active;
            private int _maxActive;

            public int MaxConcurrentAttempts => Volatile.Read(ref _maxActive);

            public void IncrementActive()
            {
                var current = Interlocked.Increment(ref _active);
                while (true)
                {
                    var max = Volatile.Read(ref _maxActive);
                    if (current <= max)
                        return;
                    if (Interlocked.CompareExchange(ref _maxActive, current, max) == max)
                        return;
                }
            }

            public void DecrementActive()
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
