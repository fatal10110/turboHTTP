using System;
using System.Buffers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tcp;
using LoopbackPair = TurboHTTP.Tests.Transport.SocketChannelLifecycleTests.LoopbackPair;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// #415: a pending operation can fail with a plain socket error after the caller cancelled it
    /// (PollSelect's poll thread met the socket the cancellation had shut down but not yet closed).
    /// The caller's cancellation must still win. Each test makes the channel's own socket call fail,
    /// and cancels the token from a hook that runs after that failure and before it completes the
    /// operation.
    /// </summary>
    [TestFixture]
    public class SocketChannelCancellationWinsTests
    {
        // Guard only: every step ends long before this; it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        [Test]
        public void SendAsync_PollThreadHitsShutDownSocketAfterCancel_CompletesCanceledWithTheCallersToken()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                var channel = new PollSelectSocketChannel(pair.Client);
                using var cts = new CancellationTokenSource();
                try
                {
                    // The cancellation's teardown, frozen half way: the socket is shut down, not closed.
                    pair.Client.Shutdown(SocketShutdown.Send);
                    channel.TestHookSendFailing = () => cts.Cancel();

                    // Memory that is not array-backed skips the inline attempt, so the poll thread
                    // makes the send call and meets the shut-down socket (WSAESHUTDOWN / EPIPE).
                    using var payload = new NonArrayMemory(1024);
                    var send = channel.SendAsync(payload.Memory, cts.Token).AsTask();

                    await AssertEndsAsync(send, "the send never ended");
                    var oce = await AssertCanceledAsync(send);
                    Assert.AreEqual(cts.Token, oce.CancellationToken, "cancelled with the caller's token");
                    Assert.IsInstanceOf<SocketException>(oce.InnerException, "the poll thread's error is kept as the cause");
                    StringAssert.StartsWith("Socket I/O was cancelled", oce.Message, "the cancellation's message, not the socket error's");
                }
                finally
                {
                    channel.Dispose();
                }
            });
        }

        [Test]
        public void SendAsync_InlineSendHitsShutDownSocketAfterCancel_CompletesCanceledWithTheCallersToken()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                var channel = new PollSelectSocketChannel(pair.Client);
                using var cts = new CancellationTokenSource();
                try
                {
                    // The socket is shut down, not closed (as by another operation's cancellation on
                    // the same token), and the token is cancelled while the inline send fails.
                    pair.Client.Shutdown(SocketShutdown.Send);
                    channel.TestHookSendFailing = () => cts.Cancel();

                    // Array-backed memory: the caller's thread makes the send call (inline attempt),
                    // and its failure is thrown from SendAsync itself; the async lambda captures it.
                    Func<Task> start = async () => await channel.SendAsync(new byte[1024], cts.Token);
                    var send = start();

                    await AssertEndsAsync(send, "the send never ended");
                    var oce = await AssertCanceledAsync(send);
                    Assert.AreEqual(cts.Token, oce.CancellationToken, "cancelled with the caller's token");
                    Assert.IsInstanceOf<SocketException>(oce.InnerException, "the inline send's error is kept as the cause");
                }
                finally
                {
                    channel.Dispose();
                }
            });
        }

        [Test]
        public void ReceiveAsync_PollThreadHitsResetAfterCancel_CompletesCanceledWithTheCallersToken()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                var channel = new PollSelectSocketChannel(pair.Client);
                using var cts = new CancellationTokenSource();
                try
                {
                    int failures = 0;
                    channel.TestHookReceiveFailing = () =>
                    {
                        Interlocked.Increment(ref failures);
                        cts.Cancel();
                    };
                    var receive = channel.ReceiveAsync(new byte[16], 0, 16, cts.Token).AsTask();

                    // An abortive close (RST): the poll thread's receive fails with ConnectionReset.
                    pair.Server.LingerState = new LingerOption(true, 0);
                    pair.Server.Close();

                    await AssertEndsAsync(receive, "the receive never ended");
                    if (await EndedWithoutFailureAsync(receive))
                    {
                        Assert.AreEqual(0, Volatile.Read(ref failures), "a receive that ended normally never failed");
                        return;
                    }

                    var oce = await AssertCanceledAsync(receive);
                    Assert.AreEqual(cts.Token, oce.CancellationToken, "cancelled with the caller's token");
                    Assert.IsInstanceOf<SocketException>(oce.InnerException, "the poll thread's error is kept as the cause");
                }
                finally
                {
                    channel.Dispose();
                }
            });
        }

        [Test]
        public void Saea_ReceiveEndsWithResetAfterCancel_CompletesCanceledWithTheCallersToken()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                var channel = new SaeaSocketChannel(pair.Client);
                using var cts = new CancellationTokenSource();
                try
                {
                    channel.TestHookReceiveEnded = _ => cts.Cancel();
                    var receive = channel.ReceiveAsync(new byte[16], 0, 16, cts.Token).AsTask();
                    Assert.IsFalse(receive.IsCompleted, "nothing was sent: the receive is pending");

                    // An abortive close (RST): the receive completes with ConnectionReset.
                    pair.Server.LingerState = new LingerOption(true, 0);
                    pair.Server.Close();

                    await AssertEndsAsync(receive, "the receive never ended");
                    if (await EndedWithoutFailureAsync(receive))
                        return;

                    var oce = await AssertCanceledAsync(receive);
                    Assert.AreEqual(cts.Token, oce.CancellationToken, "cancelled with the caller's token");
                    Assert.IsInstanceOf<SocketException>(oce.InnerException, "the socket error is kept as the cause");
                }
                finally
                {
                    channel.Dispose();
                }
            });
        }

        private static async Task AssertEndsAsync(Task task, string message)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Guard));
            Assert.AreSame(task, winner, message);
        }

        /// <summary>
        /// True when the receive ended with end of stream instead of failing. On the Android IL2CPP
        /// player (emulator, API 33) the peer's abortive close ends the receive with end of stream (a
        /// FIN ahead of the RST), so there is no failure to race there. The wrapper under test is the
        /// same managed code on every runtime, and the Editor and the Mono players drive the reset.
        /// </summary>
        private static async Task<bool> EndedWithoutFailureAsync(Task<int> receive)
        {
            if (receive.Status != TaskStatus.RanToCompletion)
                return false;

            Assert.AreEqual(0, await receive, "only the peer's close ends this receive");
            TestContext.WriteLine("the peer's FIN arrived before its reset: no failure to race on this runtime");
            return true;
        }

        private static async Task<OperationCanceledException> AssertCanceledAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException e)
            {
                return e;
            }
            catch (Exception e)
            {
                Assert.Fail($"expected a cancelled operation, got {task.Status}: {e}");
            }

            Assert.Fail("the operation did not fail");
            return null;
        }

        /// <summary>Memory with no backing array (MemoryMarshal.TryGetArray is false).</summary>
        private sealed class NonArrayMemory : MemoryManager<byte>
        {
            private readonly byte[] _buffer;

            public NonArrayMemory(int length) => _buffer = new byte[length];

            public override Span<byte> GetSpan() => _buffer;

            public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

            public override void Unpin()
            {
            }

            protected override void Dispose(bool disposing)
            {
            }
        }
    }
}
