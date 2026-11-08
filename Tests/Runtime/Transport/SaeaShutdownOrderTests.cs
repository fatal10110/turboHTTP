using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// #384: on the selector platforms the SAEA channel shuts its socket down, waits for a pending
    /// receive to end and only then closes the descriptor, so IL2CPP cannot hand the number to a new
    /// socket while its selector still holds the receive's job. The order is forced here, so the
    /// selector-platform path also runs on a Windows host (where the default is close first).
    /// </summary>
    [TestFixture]
    public class SaeaShutdownOrderTests
    {
        // Guard only: every awaited step ends long before this; it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        private bool _savedOrder;

        [SetUp]
        public void SetUp()
        {
            _savedOrder = SaeaSocketChannel.ShutdownBeforeClose;
            SaeaSocketChannel.ShutdownBeforeClose = true;
        }

        [TearDown]
        public void TearDown() => SaeaSocketChannel.ShutdownBeforeClose = _savedOrder;

        [Test]
        public void Dispose_ReceivePending_ShutsDownFirstAndClosesOnlyAfterTheReceiveEnded()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await SocketChannelLifecycleTests.LoopbackPair.CreateAsync();
                var channel = new SaeaSocketChannel(pair.Client);

                // Longer than the test: only the end of the receive lets Dispose close the socket.
                channel.SetDrainTimeoutForTest((int)Guard.TotalMilliseconds);
                long leakedBefore = SaeaSocketChannel.BuffersLeakedOnDispose;

                var receive = channel.ReceiveAsync(new byte[16], 0, 16, CancellationToken.None).AsTask();
                Assert.IsFalse(receive.IsCompleted, "nothing was sent: the receive is pending");

                var dispose = Task.Run(() => channel.Dispose());

                // The shutdown reaches the peer before anything is closed.
                var fin = pair.Server.ReceiveAsync(new ArraySegment<byte>(new byte[16]), SocketFlags.None);
                await AssertCompletesAsync(fin, "the peer never saw the shutdown");
                Assert.AreEqual(0, await fin);

                // A selector platform ends the receive on the shutdown alone (end of stream); Windows
                // reports the local shutdown (WSAESHUTDOWN), at the latest once the peer closes.
                // Closing first would abort it instead (OperationAborted or ObjectDisposedException).
                pair.Server.Shutdown(SocketShutdown.Both);
                await AssertCompletesAsync(receive, "the receive never ended");
                try
                {
                    Assert.AreEqual(0, await receive, "the receive ends with end of stream");
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.Shutdown)
                {
                    // Ended by the shutdown while the descriptor was still open.
                }

                await AssertCompletesAsync(dispose, "Dispose never returned");
                await dispose;
                Assert.Throws<ObjectDisposedException>(() => _ = pair.Client.Available, "the socket is closed after the drain");
                Assert.IsTrue(channel.BuffersReleased, "the drained receive's buffer returns to the pool");
                Assert.AreEqual(leakedBefore, SaeaSocketChannel.BuffersLeakedOnDispose);
            }, Guard + Guard);
        }

        private static async Task AssertCompletesAsync(Task task, string message)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Guard));
            Assert.AreSame(task, winner, message);
        }
    }
}
