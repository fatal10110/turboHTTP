using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// #846: token cancellation of a pending socket operation only shuts the socket down and leaves
    /// the close to the owner of the operation. Step by step for the SAEA channel: the cancel fails
    /// the caller and shuts the socket down without closing it, the peer sees that shutdown while the
    /// descriptor is still open, the operation ends at the OS, and only then is the descriptor closed
    /// and the buffers returned. A cancellation that closed the socket fails at the first step, one
    /// that did not shut it down at the second (the peer never sees the end), and a release before the
    /// operation ended at the third. The PollSelect channel's poll thread closes on its way out, after
    /// the peer saw the shutdown.
    /// </summary>
    /// <remarks>
    /// The SAEA shutdown-first order is forced, so the sequence also runs on a Windows host, whose
    /// default is close first (#405, #843). Each step is awaited on an event, never on elapsed time.
    /// </remarks>
    [TestFixture]
    public class SocketCancelCloseSequenceTests
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
        public void Saea_CancelReceivePending_PeerSeesTheShutdownBeforeTheReceiveEndsAndTheSocketCloses()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await SocketChannelLifecycleTests.LoopbackPair.CreateAsync();
                var channel = new SaeaSocketChannel(pair.Client);
                // Longer than the test: only the end of the receive lets the channel close the socket.
                channel.SetDrainTimeoutForTest((int)(Guard + Guard).TotalMilliseconds);

                // Not disposed: a hook still running after a failed assertion must not throw on it.
                var receiveMayEnd = new ManualResetEventSlim(false);
                var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.TestHookReceiveEnded = _ =>
                {
                    ended.TrySetResult(IsOpen(pair.Client));
                    receiveMayEnd.Wait(Guard);
                };

                using var cts = new CancellationTokenSource();
                var receive = channel.ReceiveAsync(new byte[16], 0, 16, cts.Token).AsTask();
                Assert.IsFalse(receive.IsCompleted, "precondition: nothing was sent, the receive is pending");

                try
                {
                    // 1. The cancel fails the caller with its token and leaves the descriptor open.
                    cts.Cancel();
                    Assert.IsTrue(IsOpen(pair.Client), "the cancellation closed the socket itself");
                    await AssertCanceledWithAsync(receive, cts.Token, "receive");

                    // 2. The peer sees the shutdown while the descriptor is still open.
                    await AssertPeerSeesEndOfStreamAsync(pair.Server);
                    Assert.IsTrue(IsOpen(pair.Client), "the descriptor was closed before the peer saw the shutdown");
                    Assert.IsFalse(channel.IsReleased, "the channel released while its receive was in flight");

                    // 3. The receive ends with the descriptor open. The local shutdown ends it on the
                    // selector platforms; the peer's shutdown ends it where that does not (#405).
                    pair.Server.Shutdown(SocketShutdown.Both);
                    Assert.AreSame(ended.Task, await Task.WhenAny(ended.Task, Task.Delay(Guard)), "the receive never ended");
                    Assert.IsTrue(await ended.Task, "the descriptor was closed before the receive ended");
                    Assert.IsFalse(channel.IsReleased, "the channel released before the receive's completion returned");
                }
                finally
                {
                    receiveMayEnd.Set();
                }

                // 4. Only the ended receive closes the socket and returns the buffers.
                // Wait on BuffersReleased, TryRelease's last write, not IsReleased, its first (#1207).
                await AbandonedSocketIoTests.WaitUntilAsync(() => channel.BuffersReleased, "the ended receive never closed the socket and returned its buffers");
                Assert.IsTrue(channel.IsReleased);
                Assert.IsFalse(IsOpen(pair.Client));
            }, Guard + Guard + Guard);
        }

        [Test]
        public void Saea_CancelSendPending_PeerSeesTheShutdownBeforeTheSendEndsAndTheSocketCloses()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await SocketChannelLifecycleTests.LoopbackPair.CreateAsync(bufferSize: 4096);
                var channel = new SaeaSocketChannel(pair.Client);
                channel.SetDrainTimeoutForTest((int)(Guard + Guard).TotalMilliseconds);

                // Every OS completion of a send is held back on its own thread: once a chunk went
                // pending it stays in flight for the channel until the hook returns.
                // Not disposed: a hook still running after a failed assertion must not throw on it.
                var sendMayEnd = new ManualResetEventSlim(false);
                var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.TestHookSendEnded = _ =>
                {
                    ended.TrySetResult(IsOpen(pair.Client));
                    sendMayEnd.Wait(Guard);
                };

                // More than the socket buffers hold and the server does not read yet, so a chunk goes
                // pending and the task cannot complete.
                using var cts = new CancellationTokenSource();
                var send = channel.SendAsync(new byte[16 * 1024 * 1024], cts.Token).AsTask();
                Assert.IsFalse(send.IsCompleted, "precondition: the server does not read, a send chunk is pending");

                try
                {
                    // 1. The cancel fails the caller with its token and leaves the descriptor open.
                    cts.Cancel();
                    Assert.IsTrue(IsOpen(pair.Client), "the cancellation closed the socket itself");
                    await AssertCanceledWithAsync(send, cts.Token, "send");

                    // 2. The peer reads what was queued and then sees the shutdown, while the
                    // descriptor is still open: the held completion keeps the send in flight.
                    await AssertPeerSeesEndOfStreamAsync(pair.Server);
                    Assert.IsTrue(IsOpen(pair.Client), "the descriptor was closed before the peer saw the shutdown");
                    Assert.IsFalse(channel.IsReleased, "the channel released while its send was in flight");

                    // 3. The send ends with the descriptor open; the peer's close resets the connection
                    // if the shutdown did not already end it.
                    pair.Server.Close();
                    var winner = await Task.WhenAny(ended.Task, Task.Delay(Guard));
                    if (winner != ended.Task)
                    {
                        Assert.IsTrue(IsOpen(pair.Client), "the socket was closed while the send was still pending");
                        Assert.IsFalse(channel.IsReleased);
                        TestRequirements.IgnoreEnvironment(
                            "The pending send did not end on shutdown or reset on this runtime; its descriptor is held (#384).");
                    }

                    Assert.IsTrue(await ended.Task, "the descriptor was closed before the send ended");
                    Assert.IsFalse(channel.IsReleased, "the channel released before the send's completion returned");
                }
                finally
                {
                    sendMayEnd.Set();
                }

                // 4. Only the ended send closes the socket and returns the buffers (#1207).
                await AbandonedSocketIoTests.WaitUntilAsync(() => channel.BuffersReleased, "the ended send never closed the socket and returned its buffers");
                Assert.IsTrue(channel.IsReleased);
                Assert.IsFalse(IsOpen(pair.Client));
            }, Guard + Guard + Guard);
        }

        [Test]
        public void PollSelect_CancelReceivePending_PeerSeesTheShutdownBeforeThePollThreadCloses()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await SocketChannelLifecycleTests.LoopbackPair.CreateAsync();
                using var cts = new CancellationTokenSource();
                using var channel = new PollSelectSocketChannel(pair.Client);
                // Not disposed: a hook still running after a failed assertion must not throw on it.
                var mayClose = new ManualResetEventSlim(false);
                var atClose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.TestHookBeforeClose = () =>
                {
                    atClose.TrySetResult(IsOpen(pair.Client));
                    mayClose.Wait(Guard);
                };

                var receive = channel.ReceiveAsync(new byte[16], 0, 16, cts.Token).AsTask();
                try
                {
                    // 1. The cancel leaves the descriptor open.
                    cts.Cancel();
                    Assert.IsTrue(IsOpen(pair.Client), "the cancellation closed the socket itself");

                    // 2. The peer sees the shutdown while the poll thread is still held before its close.
                    await AssertPeerSeesEndOfStreamAsync(pair.Server);
                    Assert.IsTrue(IsOpen(pair.Client), "the descriptor was closed before the peer saw the shutdown");

                    // 3. The poll thread, whose call on the socket ended, closes it with the descriptor open.
                    Assert.AreSame(atClose.Task, await Task.WhenAny(atClose.Task, Task.Delay(Guard)), "the poll thread never reached its close");
                    Assert.IsTrue(await atClose.Task, "the descriptor was closed before the poll thread closed it");
                    Assert.IsFalse(receive.IsCompleted, "the receive ends only once the poll thread left the socket");
                }
                finally
                {
                    mayClose.Set();
                }

                // 4. The receive fails with the cancellation after the close; the buffers went back.
                await AssertCanceledWithAsync(receive, cts.Token, "receive");
                Assert.IsFalse(IsOpen(pair.Client));
                Assert.AreEqual(PollSelectSocketChannel.BuffersReturned, channel.BufferState);
            }, Guard + Guard + Guard);
        }

        /// <summary>
        /// Reads on the peer until end of stream; anything queued before the shutdown is discarded.
        /// A reset instead of end of stream means the channel closed (or aborted) before shutting down.
        /// </summary>
        private static async Task AssertPeerSeesEndOfStreamAsync(Socket peer)
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = peer.ReceiveAsync(new ArraySegment<byte>(buffer), SocketFlags.None);
                Assert.AreSame(read, await Task.WhenAny(read, Task.Delay(Guard)), "the peer never saw the shutdown");
                int n;
                try
                {
                    n = await read;
                }
                catch (SocketException e)
                {
                    Assert.Fail($"the peer saw {e.SocketErrorCode} instead of the shutdown's end of stream");
                    return;
                }

                if (n == 0)
                    return;
            }
        }

        private static async Task AssertCanceledWithAsync(Task operation, CancellationToken token, string what)
        {
            Assert.AreSame(operation, await Task.WhenAny(operation, Task.Delay(Guard)), $"the cancelled {what} did not fail at once");
            try
            {
                await operation;
                Assert.Fail($"the cancelled {what} completed");
            }
            catch (OperationCanceledException e)
            {
                Assert.AreEqual(token, e.CancellationToken);
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
    }
}
