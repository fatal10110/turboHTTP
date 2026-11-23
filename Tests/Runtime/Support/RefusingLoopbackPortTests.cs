using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core.Internal;
using TurboHTTP.Tests.Transport;

namespace TurboHTTP.Tests
{
    /// <summary>#1259: the refusing port stays refused, and reserved, for the helper's whole lifetime.</summary>
    [TestFixture]
    public class RefusingLoopbackPortTests
    {
        // Safety bound only: a refused loopback connect ends in about 2 s on Windows (#226), at once
        // elsewhere; a connect that is accepted or hangs fails the test instead of the run.
        private static readonly TimeSpan ConnectBound = TimeSpan.FromSeconds(15);

        // Safety bound of the off-loopback probes: a dropped SYN or a silent resolver ends here.
        private static readonly TimeSpan ProbeBound = TimeSpan.FromSeconds(5);

        [Test]
        public void Connect_IsRefused()
        {
            AssertAsync.Run(async () =>
            {
                using var port = new RefusingLoopbackPort();
                using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var watch = Stopwatch.StartNew();
                var connect = client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port.Port));
                if (await Task.WhenAny(connect, Task.Delay(ConnectBound)).ConfigureAwait(false) != connect)
                    Assert.Fail($"The connect to the refusing port neither failed nor completed within {ConnectBound.TotalSeconds} s.");

                var ex = Assert.Throws<SocketException>(() => connect.GetAwaiter().GetResult());
                TestContext.WriteLine($"Refused after {watch.ElapsedMilliseconds} ms: {Describe(ex)}");
                // IL2CPP players report the raw errno instead of the mapped value: ECONNREFUSED 111 on
                // Android (#1290), 61 on Darwin (#1287). The product compares through the normalizer too.
                Assert.AreEqual(SocketError.ConnectionRefused, SocketErrorNormalizer.Normalize(ex));
            });
        }

        /// <summary>
        /// #1290 probe, no assertion on the codes: records what the runtime reports for a refused
        /// loopback connect, so a player run (Android, iOS) shows whether it maps the errno.
        /// </summary>
        [Test]
        public void Probe_RefusedConnect_RecordsSocketErrorCodes()
        {
            AssertAsync.Run(async () =>
            {
                using var port = new RefusingLoopbackPort();
                var outcome = await ProbeConnectAsync(new IPEndPoint(IPAddress.Loopback, port.Port), ConnectBound)
                    .ConfigureAwait(false);
                TestContext.WriteLine($"[#1290 probe] {DescribePlatform()} refused connect: {outcome}");
            });
        }

        /// <summary>
        /// #1290 probe, no assertion: records what the runtime reports for an unreachable connect
        /// (TEST-NET-1, RFC 5737, never routed) and a resolver failure (<c>.invalid</c>, RFC 6761). Both
        /// leave the loopback interface (the default route and the configured resolver), hence the
        /// category; either may also end in the bound instead of an error.
        /// </summary>
        [Test]
        [Category("ExternalNetwork")]
        public void Probe_UnreachableAndDnsFailure_RecordSocketErrorCodes()
        {
            AssertAsync.Run(async () =>
            {
                var unreachable = await ProbeConnectAsync(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9), ProbeBound)
                    .ConfigureAwait(false);
                TestContext.WriteLine($"[#1290 probe] {DescribePlatform()} unreachable connect: {unreachable}");

                string dns;
                var resolve = Dns.GetHostAddressesAsync("turbohttp-errno-probe.invalid");
                if (await Task.WhenAny(resolve, Task.Delay(ProbeBound)).ConfigureAwait(false) != resolve)
                {
                    dns = $"no result within {ProbeBound.TotalSeconds} s";
                }
                else
                {
                    try
                    {
                        dns = "resolved " + (await resolve.ConfigureAwait(false)).Length + " address(es)";
                    }
                    catch (SocketException ex)
                    {
                        dns = Describe(ex);
                    }
                    catch (Exception ex)
                    {
                        dns = ex.GetType().Name;
                    }
                }

                TestContext.WriteLine($"[#1290 probe] {DescribePlatform()} DNS failure: {dns}");
            });
        }

        private static async Task<string> ProbeConnectAsync(IPEndPoint endPoint, TimeSpan bound)
        {
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var connect = client.ConnectAsync(endPoint);
            if (await Task.WhenAny(connect, Task.Delay(bound)).ConfigureAwait(false) != connect)
            {
                client.Dispose(); // ends the pending connect
                _ = connect.ContinueWith(t => t.Exception, TaskScheduler.Default); // observe its fault
                return $"no result within {bound.TotalSeconds} s";
            }

            try
            {
                await connect.ConfigureAwait(false);
                return "connected";
            }
            catch (SocketException ex)
            {
                return Describe(ex);
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }

        private static string Describe(SocketException ex) =>
            $"SocketErrorCode={ex.SocketErrorCode} ({(int)ex.SocketErrorCode}), ErrorCode={ex.ErrorCode}, " +
            $"NativeErrorCode={ex.NativeErrorCode}, normalized={SocketErrorNormalizer.Normalize(ex)}";

        private static string DescribePlatform() =>
            $"{RuntimeInformation.OSDescription} (errno table {SocketErrorNormalizer.CurrentTable})";

        [Test]
        public void Port_CannotBeBoundByAnotherSocketWhileHeld()
        {
            // The old pattern (start and stop a listener on port 0) releases the port, so another
            // socket can bind it before the connect; the held port stays reserved. Only Windows
            // guarantees that for every runtime that runs these tests: .NET on Unix sets
            // SO_REUSEADDR on every socket, so Linux lets two non-listening sockets share a port.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                TestRequirements.IgnoreEnvironment("Exclusive port binding is only guaranteed on Windows (#1259).");

            using var port = new RefusingLoopbackPort();
            using var other = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            other.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);

            var ex = Assert.Throws<SocketException>(() => other.Bind(new IPEndPoint(IPAddress.Loopback, port.Port)));
            Assert.AreEqual(SocketError.AddressAlreadyInUse, ex.SocketErrorCode);
        }
    }
}
