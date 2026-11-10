using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// #384: on the selector platforms the SAEA channel shuts its socket down, fails a pending
    /// receive's caller at once, and closes the descriptor only once the receive ended at the OS (or
    /// at the bound), so IL2CPP cannot hand the number to a new socket while its selector still holds
    /// the receive's job. The order is forced here, so the
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

                // Longer than the test: only the end of the receive lets the channel close the socket.
                channel.SetDrainTimeoutForTest((int)(Guard + Guard).TotalMilliseconds);
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

                // Dispose neither waits for the receive nor leaves its caller waiting (#384).
                channel.Dispose();
                await AssertCompletesAsync(receive, "the pending receive was not failed by Dispose");
                await AssertReceiveEndedByShutdownAsync(receive);

                // The shutdown reaches the peer before anything is closed.
                var fin = pair.Server.ReceiveAsync(new ArraySegment<byte>(new byte[16]), SocketFlags.None);
                await AssertCompletesAsync(fin, "the peer never saw the shutdown");
                Assert.AreEqual(0, await fin);

                // A selector platform ends the receive on the shutdown alone; Windows reports the
                // local shutdown (WSAESHUTDOWN), at the latest once the peer closes.
                pair.Server.Shutdown(SocketShutdown.Both);
                var ended = await WaitAsync(() => Volatile.Read(ref endedWith) != NotEnded);
                if (!ended)
                {
                    // IL2CPP did not end some receives at all (#384): the descriptor must still be held.
                    Assert.IsTrue(IsOpen(pair.Client), "the socket was closed while the receive was still pending");
                    Assert.IsFalse(channel.IsReleased);
                    TestRequirements.IgnoreEnvironment(
                        "The pending receive did not end on shutdown on this runtime; its descriptor is held (#384).");
                }

                TestContext.WriteLine($"receive ended with {(SocketError)Volatile.Read(ref endedWith)}, " +
                                      $"socket open then: {Volatile.Read(ref openWhenEnded) == 1}");
                Assert.AreEqual(1, Volatile.Read(ref openWhenEnded),
                    $"the socket was closed before the receive ended ({(SocketError)Volatile.Read(ref endedWith)})");
                Assert.That((SocketError)Volatile.Read(ref endedWith),
                    Is.EqualTo(SocketError.Success).Or.EqualTo(SocketError.Shutdown).Or.EqualTo(SocketError.Interrupted),
                    "closing first aborts the receive (OperationAborted)");

                await AbandonedSocketIoTests.WaitUntilAsync(() => channel.IsReleased, "the socket was never closed after the receive ended");
                Assert.IsFalse(IsOpen(pair.Client), "the socket is closed after the receive ended");
                Assert.IsTrue(channel.BuffersReleased, "the ended receive's buffer returns to the pool");
                Assert.AreEqual(leakedBefore, SaeaSocketChannel.BuffersLeakedOnDispose);
            }, Guard + Guard + Guard);
        }

        [Test]
        public void Cancel_ReceivePending_FailsAtOnceAndHoldsTheDescriptorUntilTheReceiveEnded()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await SocketChannelLifecycleTests.LoopbackPair.CreateAsync();
                var channel = new SaeaSocketChannel(pair.Client);
                channel.SetDrainTimeoutForTest((int)(Guard + Guard).TotalMilliseconds);

                // The OS completion is held back on its own thread: a receive that has not ended yet,
                // as IL2CPP leaves some after a dispose (#384).
                using var receiveMayEnd = new ManualResetEventSlim(false);
                int ended = 0;
                channel.TestHookReceiveEnded = _ =>
                {
                    Volatile.Write(ref ended, 1);
                    receiveMayEnd.Wait(Guard);
                };

                using var cts = new CancellationTokenSource();
                var receive = channel.ReceiveAsync(new byte[16], 0, 16, cts.Token).AsTask();
                Assert.IsFalse(receive.IsCompleted, "nothing was sent: the receive is pending");

                cts.Cancel();
                await AssertCompletesAsync(receive, "the cancelled receive did not fail at once");
                try
                {
                    await receive;
                    Assert.Fail("the cancelled receive completed");
                }
                catch (OperationCanceledException e)
                {
                    Assert.AreEqual(cts.Token, e.CancellationToken);
                }

                // The receive ends on the shutdown (or the peer's close), but its completion is held.
                pair.Server.Shutdown(SocketShutdown.Both);
                await AbandonedSocketIoTests.WaitUntilAsync(() => Volatile.Read(ref ended) != 0, "the receive never reached its completion");
                Assert.IsTrue(IsOpen(pair.Client), "the descriptor was closed before the receive ended");
                Assert.IsFalse(channel.IsReleased);

                receiveMayEnd.Set();
                await AbandonedSocketIoTests.WaitUntilAsync(() => channel.IsReleased, "the socket was never closed after the receive ended");
                Assert.IsFalse(IsOpen(pair.Client));
                Assert.IsTrue(channel.BuffersReleased);
            }, Guard + Guard + Guard);
        }

        [Test]
        public void Dispose_ReceiveNeverEnds_HoldsTheDescriptorUntilTheBound()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await SocketChannelLifecycleTests.LoopbackPair.CreateAsync();
                var channel = new SaeaSocketChannel(pair.Client);
                channel.SimulateStuckReceiveForTest(drainTimeoutMs: 200);
                long leakedBefore = SaeaSocketChannel.BuffersLeakedOnDispose;

                TurboDiagnostics.SetSink(null);
                try
                {
                    channel.Dispose();
                    Assert.IsTrue(IsOpen(pair.Client), "the descriptor was closed under a pending receive");
                    Assert.IsFalse(channel.IsReleased);

                    await AbandonedSocketIoTests.WaitUntilAsync(() => channel.IsReleased, "the bound never closed the socket");
                }
                finally
                {
                    TurboDiagnostics.ResetSink();
                }

                Assert.IsFalse(IsOpen(pair.Client), "the bound closes the socket");
                Assert.IsFalse(channel.BuffersReleased, "buffers must not return to the pool while the OS may write");
                Assert.Greater(SaeaSocketChannel.BuffersLeakedOnDispose, leakedBefore);
            });
        }

        private static async Task<bool> WaitAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Guard;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    return false;
                await Task.Delay(1);
            }

            return true;
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
