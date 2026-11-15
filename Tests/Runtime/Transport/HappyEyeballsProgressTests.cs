using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Connection;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// The race keeps making progress (RFC 8305 section 5): at the
    /// <see cref="HappyEyeballsOptions.MaxConcurrentAttempts"/> cap a due address replaces a stalled
    /// attempt (#814), and when every attempt in flight has failed the next address starts at once
    /// instead of at its due time (#815).
    /// </summary>
    [TestFixture]
    public class HappyEyeballsProgressTests
    {
        private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

        private static readonly IPAddress V6 = IPAddress.Parse("2001:db8::1");
        private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.1");
        private static readonly IPAddress V4B = IPAddress.Parse("192.0.2.2");

        [Test]
        public void TwoBlackholedThenHealthy_DefaultOptions_ConnectsToHealthy()
        {
            // Before #814 v6 and v4a filled both slots and nothing was dialled until the deadline.
            var dialer = new ScriptedDialer();
            dialer.Blackhole(V6);
            dialer.Blackhole(V4A);
            dialer.Healthy(V4B);

            AssertAsync.Run(async () =>
            {
                using var watchdog = new CancellationTokenSource(Watchdog);
                using var winner = await HappyEyeballsConnector.ConnectAsync(
                    new[] { V6, V4A, V4B },
                    443,
                    TimeSpan.FromSeconds(5),
                    new HappyEyeballsOptions(), // defaults: prefer IPv6, 2 attempts in flight
                    watchdog.Token,
                    dialer.DialAsync);

                Assert.AreSame(dialer.HealthySocket, winner);
                Assert.IsFalse(watchdog.IsCancellationRequested);
            });

            CollectionAssert.AreEqual(new[] { V6, V4A, V4B }, dialer.Order);
            Assert.IsTrue(dialer.WasCancelled(V6), "the oldest stalled attempt makes room for the due address");
            Assert.LessOrEqual(dialer.MaxInFlight, 2, "the cap still bounds the attempts in flight");
        }

        [Test]
        public void AtTheCap_YoungAttemptIsNotReplaced()
        {
            // v4b is due 5 ms in, but v4a has not stalled yet (two attempt delays: 400 ms here), so
            // it keeps its slot and wins.
            var dialer = new ScriptedDialer();
            dialer.Healthy(V4A, TimeSpan.FromMilliseconds(30));
            dialer.Blackhole(V4B);
            var options = new HappyEyeballsOptions
            {
                FamilyStaggerDelay = TimeSpan.FromMilliseconds(200),
                AttemptSpacingDelay = TimeSpan.FromMilliseconds(5),
                MaxConcurrentAttempts = 1,
            };

            AssertAsync.Run(async () =>
            {
                using var watchdog = new CancellationTokenSource(Watchdog);
                using var winner = await HappyEyeballsConnector.ConnectAsync(
                    new[] { V4A, V4B },
                    443,
                    TimeSpan.FromSeconds(5),
                    options,
                    watchdog.Token,
                    dialer.DialAsync);

                Assert.AreSame(dialer.HealthySocket, winner);
            });

            CollectionAssert.AreEqual(new[] { V4A }, dialer.Order);
            Assert.IsFalse(dialer.WasCancelled(V4A));
        }

        [Test]
        public void OnlyAttemptRefused_NextStartsAtOnceAfterTheFloor()
        {
            // Before #815 the loop slept until the IPv4 due time (30 s here) and the 5 s deadline fired.
            var dialer = new ScriptedDialer();
            dialer.Refuse(V6);
            dialer.Healthy(V4A);
            var options = new HappyEyeballsOptions { FamilyStaggerDelay = TimeSpan.FromSeconds(30) };

            AssertAsync.Run(async () =>
            {
                using var watchdog = new CancellationTokenSource(Watchdog);
                using var winner = await HappyEyeballsConnector.ConnectAsync(
                    new[] { V6, V4A },
                    443,
                    TimeSpan.FromSeconds(5),
                    options,
                    watchdog.Token,
                    dialer.DialAsync);

                Assert.AreSame(dialer.HealthySocket, winner);
            });

            CollectionAssert.AreEqual(new[] { V6, V4A }, dialer.Order);
            // RFC 8305 section 5: no start within 10 ms of the previous one (1 ms of timer slack).
            var gap = dialer.StartOf(V4A) - dialer.StartOf(V6);
            Assert.GreaterOrEqual(gap.TotalMilliseconds, 9.0, "the early start keeps the 10 ms floor");
        }

        [Test]
        public void Localhost_Ipv4OnlyListener_DoesNotWaitForTheFamilyStagger()
        {
            // localhost resolves to [::1, 127.0.0.1]; a server bound to IPv4 only refuses ::1 (or
            // ::1 is unavailable). The real dialer must then go to 127.0.0.1 without waiting for
            // the stagger (30 s here, beyond the deadline).
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var options = new HappyEyeballsOptions { FamilyStaggerDelay = TimeSpan.FromSeconds(30) };

                AssertAsync.Run(async () =>
                {
                    using var watchdog = new CancellationTokenSource(Watchdog);
                    using var socket = await HappyEyeballsConnector.ConnectAsync(
                        new[] { IPAddress.IPv6Loopback, IPAddress.Loopback },
                        port,
                        TimeSpan.FromSeconds(10), // Windows takes about 2 s to report the ::1 refusal
                        options,
                        watchdog.Token);

                    Assert.IsTrue(socket.Connected);
                    Assert.AreEqual(IPAddress.Loopback, ((IPEndPoint)socket.RemoteEndPoint).Address);
                    using var accepted = await listener.AcceptSocketAsync();
                });
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// Per-address dial behavior: blackholed (waits for cancellation), refused (fails at once),
        /// or healthy (optionally after a delay). Records the dial order, start times, cancellations
        /// and the most attempts in flight at once.
        /// </summary>
        private sealed class ScriptedDialer
        {
            private readonly object _lock = new object();
            private readonly Dictionary<IPAddress, TimeSpan?> _healthy = new Dictionary<IPAddress, TimeSpan?>();
            private readonly HashSet<IPAddress> _blackholed = new HashSet<IPAddress>();
            private readonly HashSet<IPAddress> _refused = new HashSet<IPAddress>();
            private readonly HashSet<IPAddress> _cancelled = new HashSet<IPAddress>();
            private readonly List<IPAddress> _order = new List<IPAddress>();
            private readonly Dictionary<IPAddress, TimeSpan> _starts = new Dictionary<IPAddress, TimeSpan>();
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private int _inFlight;
            private int _maxInFlight;

            public Socket HealthySocket { get; private set; }

            public int MaxInFlight => Volatile.Read(ref _maxInFlight);

            public IPAddress[] Order
            {
                get
                {
                    lock (_lock)
                        return _order.ToArray();
                }
            }

            public void Blackhole(IPAddress address) => _blackholed.Add(address);

            public void Refuse(IPAddress address) => _refused.Add(address);

            public void Healthy(IPAddress address, TimeSpan? delay = null) => _healthy[address] = delay;

            public bool WasCancelled(IPAddress address)
            {
                lock (_lock)
                    return _cancelled.Contains(address);
            }

            public TimeSpan StartOf(IPAddress address)
            {
                lock (_lock)
                    return _starts[address];
            }

            public async Task<Socket> DialAsync(IPAddress address, int port, CancellationToken ct)
            {
                lock (_lock)
                {
                    _order.Add(address);
                    _starts[address] = _clock.Elapsed;
                    int now = ++_inFlight;
                    if (now > _maxInFlight)
                        _maxInFlight = now;
                }

                try
                {
                    if (_refused.Contains(address))
                        throw new SocketException((int)SocketError.ConnectionRefused);

                    if (_blackholed.Contains(address))
                    {
                        try
                        {
                            await Task.Delay(Timeout.Infinite, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            lock (_lock)
                                _cancelled.Add(address);
                            throw;
                        }
                    }

                    if (!_healthy.TryGetValue(address, out var delay))
                        throw new SocketException((int)SocketError.HostUnreachable);

                    if (delay.HasValue)
                        await Task.Delay(delay.Value, ct);
                    else
                        await Task.Yield();

                    HealthySocket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    return HealthySocket;
                }
                finally
                {
                    lock (_lock)
                        _inFlight--;
                }
            }
        }
    }
}
