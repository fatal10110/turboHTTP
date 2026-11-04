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
    /// <summary>
    /// Address-family interleaving (RFC 8305 section 4, #362). On the Android 6 emulator
    /// www.google.com resolves to eight IPv6 and eight IPv4 addresses and IPv6 has no route (SYNs
    /// are dropped). Listing every IPv6 address before the first IPv4 one let two dead IPv6
    /// attempts fill <see cref="HappyEyeballsOptions.MaxConcurrentAttempts"/>, so IPv4 was never
    /// tried and the connect hit its 10 s deadline.
    /// </summary>
    [TestFixture]
    public class HappyEyeballsInterleaveTests
    {
        private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

        [Test]
        public void ManyUnroutableIpv6_DefaultOptions_ConnectsOverIpv4()
        {
            var ipv6 = new List<IPAddress>();
            var ipv4 = new List<IPAddress>();
            for (int i = 1; i <= 8; i++)
            {
                ipv6.Add(IPAddress.Parse("2001:db8::" + i));
                ipv4.Add(IPAddress.Parse("192.0.2." + i));
            }

            var addresses = new List<IPAddress>(ipv4); // the resolver order on the emulator
            addresses.AddRange(ipv6);
            var dialer = new RecordingDialer(blackholed: ipv6, healthy: ipv4[0]);

            AssertAsync.Run(async () =>
            {
                using var watchdog = new CancellationTokenSource(Watchdog);
                using var winner = await HappyEyeballsConnector.ConnectAsync(
                    addresses.ToArray(),
                    443,
                    TimeSpan.FromSeconds(5), // the old schedule ran into this deadline
                    new HappyEyeballsOptions(), // defaults: prefer IPv6, 2 attempts in flight
                    watchdog.Token,
                    dialer.DialAsync);

                Assert.AreSame(dialer.HealthySocket, winner);
                Assert.IsFalse(watchdog.IsCancellationRequested);
            });

            var order = dialer.Order;
            Assert.AreEqual(AddressFamily.InterNetworkV6, order[0].AddressFamily, "IPv6 stays preferred");
            Assert.AreEqual(ipv4[0], order[1], "the second attempt must be the first IPv4 address");
        }

        [Test]
        public void Schedule_AlternatesFamiliesThenDrainsTheLongerOne()
        {
            var v6a = IPAddress.Parse("2001:db8::a");
            var v6b = IPAddress.Parse("2001:db8::b");
            var v4a = IPAddress.Parse("192.0.2.1");
            var v4b = IPAddress.Parse("192.0.2.2");
            var v4c = IPAddress.Parse("192.0.2.3");
            var v4d = IPAddress.Parse("192.0.2.4");
            var dialer = new RecordingDialer(blackholed: Array.Empty<IPAddress>(), healthy: null);
            var options = new HappyEyeballsOptions
            {
                FamilyStaggerDelay = TimeSpan.Zero,
                AttemptSpacingDelay = TimeSpan.Zero,
                MaxConcurrentAttempts = 1,
            };

            AssertAsync.ThrowsAsync<AggregateException>(async () =>
            {
                using var watchdog = new CancellationTokenSource(Watchdog);
                using var _ = await HappyEyeballsConnector.ConnectAsync(
                    new[] { v4a, v4b, v6a, v4c, v6b, v4d },
                    443,
                    TimeSpan.FromSeconds(10),
                    options,
                    watchdog.Token,
                    dialer.DialAsync);
            });

            CollectionAssert.AreEqual(new[] { v6a, v4a, v6b, v4b, v4c, v4d }, dialer.Order);
        }

        /// <summary>
        /// Records the dial order. Blackholed addresses wait for cancellation, the healthy one
        /// connects, every other address fails at once.
        /// </summary>
        private sealed class RecordingDialer
        {
            private readonly ICollection<IPAddress> _blackholed;
            private readonly IPAddress _healthy;
            private readonly List<IPAddress> _order = new List<IPAddress>();

            public RecordingDialer(ICollection<IPAddress> blackholed, IPAddress healthy)
            {
                _blackholed = blackholed;
                _healthy = healthy;
            }

            public Socket HealthySocket { get; private set; }

            public IPAddress[] Order
            {
                get
                {
                    lock (_order)
                        return _order.ToArray();
                }
            }

            public async Task<Socket> DialAsync(IPAddress address, int port, CancellationToken ct)
            {
                lock (_order)
                    _order.Add(address);

                if (_blackholed.Contains(address))
                    await Task.Delay(Timeout.Infinite, ct);

                if (!address.Equals(_healthy))
                    throw new SocketException((int)SocketError.HostUnreachable);

                await Task.Yield();
                HealthySocket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                return HealthySocket;
            }
        }
    }
}
