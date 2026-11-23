using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using NUnit.Framework;

namespace TurboHTTP.Tests
{
    /// <summary>#1259: the refusing port stays refused, and reserved, for the helper's whole lifetime.</summary>
    [TestFixture]
    public class RefusingLoopbackPortTests
    {
        // Safety bound only: a refused loopback connect ends in about 2 s on Windows (#226), at once
        // elsewhere; a connect that is accepted or hangs fails the test instead of the run.
        private static readonly TimeSpan ConnectBound = TimeSpan.FromSeconds(15);

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
                TestContext.WriteLine($"Refused after {watch.ElapsedMilliseconds} ms: {ex.SocketErrorCode}");
                Assert.AreEqual(SocketError.ConnectionRefused, ex.SocketErrorCode);
            });
        }

        [Test]
        public void Port_CannotBeBoundByAnotherSocketWhileHeld()
        {
            // The old pattern (start and stop a listener on port 0) releases the port, so another
            // socket can bind it before the connect; the held port stays reserved.
            using var port = new RefusingLoopbackPort();
            using var other = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            other.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);

            var ex = Assert.Throws<SocketException>(() => other.Bind(new IPEndPoint(IPAddress.Loopback, port.Port)));
            Assert.AreEqual(SocketError.AddressAlreadyInUse, ex.SocketErrorCode);
        }
    }
}
