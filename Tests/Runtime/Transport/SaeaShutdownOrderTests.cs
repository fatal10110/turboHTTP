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
    /// The receive's error code differs per runtime (#410), so the order is checked through a hook
    /// that runs when the receive ends, before Dispose may close the socket.
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

                // The order is asserted where it is decided: at the moment the receive ends, the
                // channel's socket must still be open. Its error code differs per runtime.
                int endedWith = NotEnded;
                int openWhenEnded = NotEnded;
                channel.TestHookReceiveEnded = error =>
                {
                    Volatile.Write(ref openWhenEnded, IsOpen(pair.Client) ? 1 : 0);
                    Volatile.Write(ref endedWith, (int)error);
                };

                var receive = channel.ReceiveAsync(new byte[16], 0, 16, CancellationToken.None).AsTask();
                Assert.IsFalse(receive.IsCompleted, "nothing was sent: the receive is pending");

                var dispose = Task.Run(() => channel.Dispose());

                // The shutdown reaches the peer before anything is closed.
                var fin = pair.Server.ReceiveAsync(new ArraySegment<byte>(new byte[16]), SocketFlags.None);
                await AssertCompletesAsync(fin, "the peer never saw the shutdown");
                Assert.AreEqual(0, await fin);

                // A selector platform ends the receive on the shutdown alone; Windows reports the
                // local shutdown (WSAESHUTDOWN), at the latest once the peer closes.
                pair.Server.Shutdown(SocketShutdown.Both);
                await AssertCompletesAsync(receive, "the receive never ended");
                await AssertReceiveEndedByShutdownAsync(receive);

                TestContext.WriteLine($"receive ended with {(SocketError)Volatile.Read(ref endedWith)}, " +
                                      $"socket open then: {Volatile.Read(ref openWhenEnded) == 1}");
                Assert.AreEqual(1, Volatile.Read(ref openWhenEnded),
                    $"the socket was closed before the receive ended ({(SocketError)Volatile.Read(ref endedWith)})");
                Assert.That((SocketError)Volatile.Read(ref endedWith),
                    Is.EqualTo(SocketError.Success).Or.EqualTo(SocketError.Shutdown).Or.EqualTo(SocketError.Interrupted),
                    "closing first aborts the receive (OperationAborted)");

                await AssertCompletesAsync(dispose, "Dispose never returned");
                await dispose;
                Assert.IsFalse(IsOpen(pair.Client), "the socket is closed after the drain");
                Assert.IsTrue(channel.BuffersReleased, "the drained receive's buffer returns to the pool");
                Assert.AreEqual(leakedBefore, SaeaSocketChannel.BuffersLeakedOnDispose);
            }, Guard + Guard);
        }

        private const int NotEnded = -1;

        /// <summary>
        /// The receive's result for each way a shutdown ends it while the descriptor is open: end of
        /// stream; WSAESHUTDOWN on Windows; Interrupted on Mono and IL2CPP, which turn a 0-byte read
        /// after a local SHUT_RD into EINTR and which the channel reports as a cancellation (#410).
        /// </summary>
        private static async Task AssertReceiveEndedByShutdownAsync(Task<int> receive)
        {
            try
            {
                Assert.AreEqual(0, await receive, "the receive ends with end of stream");
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.Shutdown)
            {
                // WSAESHUTDOWN (Windows): ended by the shutdown while the descriptor was open.
            }
            catch (OperationCanceledException)
            {
                // Interrupted; the hook's open-socket check tells it apart from a close-first abort.
            }
        }

        /// <summary>
        /// False once the socket was closed. A call that reaches the operating system (even one that
        /// fails there) proves the descriptor is still open.
        /// </summary>
        private static bool IsOpen(Socket socket)
        {
            try
            {
                _ = socket.Available;
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (SocketException)
            {
                return true;
            }
        }

        private static async Task AssertCompletesAsync(Task task, string message)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Guard));
            Assert.AreSame(task, winner, message);
        }
    }
}
