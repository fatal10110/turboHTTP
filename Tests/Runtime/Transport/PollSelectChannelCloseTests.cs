using System;
using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tcp;
using LoopbackPair = TurboHTTP.Tests.Transport.SocketChannelLifecycleTests.LoopbackPair;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// PollSelect close and buffer ownership: a dispose from an inline continuation on the poll
    /// thread never joins that thread (#840), and token cancellation shuts the socket down and
    /// leaves the close to the poll thread once its socket call ended (#846).
    /// </summary>
    [TestFixture]
    public class PollSelectChannelCloseTests
    {
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        [Test]
        public void Dispose_FromContinuationOnPollThread_ReturnsAtOnceAndTheExitReturnsTheBuffers()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                var channel = new PollSelectSocketChannel(pair.Client);

                async Task<(string Thread, long ElapsedMs, int StateAfterDispose)> ReceiveThenDispose()
                {
                    await channel.ReceiveAsync(new byte[16], 0, 16, CancellationToken.None).ConfigureAwait(false);
                    string thread = Thread.CurrentThread.Name;
                    var watch = Stopwatch.StartNew();
                    channel.Dispose();
                    watch.Stop();
                    return (thread, watch.ElapsedMilliseconds, channel.BufferState);
                }

                var run = ReceiveThenDispose();
                pair.Server.Send(new byte[] { 1 });
                var (thread, elapsedMs, stateAfterDispose) = await run;

                StringAssert.StartsWith("TurboHTTP.PollSelect-", thread,
                    "precondition: the continuation runs inline on the poll thread");
                // The former Dispose joined its own thread for the whole 500 ms bound.
                Assert.Less(elapsedMs, PollSelectSocketChannel.DisposeJoinTimeoutMs / 2,
                    "Dispose on the poll thread must not wait for itself");
                Assert.AreNotEqual(PollSelectSocketChannel.BuffersAbandoned, stateAfterDispose,
                    "Dispose on the poll thread must not give up buffers its own exit returns");

                await AbandonedSocketIoTests.WaitUntilAsync(
                    () => channel.BufferState != PollSelectSocketChannel.BuffersOwned,
                    "the poll thread's exit never returned the buffers");
                Assert.AreEqual(PollSelectSocketChannel.BuffersReturned, channel.BufferState);
                await AbandonedSocketIoTests.WaitUntilAsync(() => !IsOpen(pair.Client),
                    "the poll thread never closed the socket");
            }, Guard);
        }

        [Test]
        public void Dispose_FromAnotherThread_WaitsForTheExitWhichClosesAndReturnsTheBuffers()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                var channel = new PollSelectSocketChannel(pair.Client);
                var pending = channel.ReceiveAsync(new byte[16], 0, 16, CancellationToken.None).AsTask();

                // The assertion below must not depend on the poll thread exiting within the
                // production join bound on a loaded machine.
                channel.SetDisposeJoinTimeoutForTest((int)Guard.TotalMilliseconds);
                channel.Dispose();

                Assert.AreEqual(PollSelectSocketChannel.BuffersReturned, channel.BufferState,
                    "Dispose waits for the poll thread, whose exit returns the buffers");
                Assert.IsFalse(IsOpen(pair.Client), "the socket is closed when Dispose returns");
                await AssertCanceledWithAsync(pending, null);
            }, Guard);
        }

        [Test]
        public void Dispose_PollThreadOutlivesTheBound_ClosesAndAbandonsTheBuffersOnce()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                using var gate = new ManualResetEventSlim(false);
                var channel = new PollSelectSocketChannel(pair.Client);
                channel.TestHookBeforeClose = () => gate.Wait(Guard);
                channel.SetDisposeJoinTimeoutForTest(50);
                var pending = channel.ReceiveAsync(new byte[16], 0, 16, CancellationToken.None).AsTask();

                try
                {
                    channel.Dispose();

                    Assert.AreEqual(PollSelectSocketChannel.BuffersAbandoned, channel.BufferState,
                        "a poll thread held past the bound must not get its buffers returned under it");
                    Assert.IsFalse(IsOpen(pair.Client), "Dispose closes the socket itself past the bound");
                }
                finally
                {
                    gate.Set();
                }

                // The released thread exits; its exit must not return the abandoned buffers too.
                await AssertCanceledWithAsync(pending, null);
                Assert.AreEqual(PollSelectSocketChannel.BuffersAbandoned, channel.BufferState);
            }, Guard);
        }

        [Test]
        public void Cancel_ReceivePending_ShutsDownAndThePollThreadClosesAfterItsCallEnded()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                using var gate = new ManualResetEventSlim(false);
                using var cts = new CancellationTokenSource();
                using var channel = new PollSelectSocketChannel(pair.Client);
                var atClose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.TestHookBeforeClose = () =>
                {
                    atClose.TrySetResult(IsOpen(pair.Client));
                    gate.Wait(Guard);
                };

                var receive = channel.ReceiveAsync(new byte[16], 0, 16, cts.Token).AsTask();
                try
                {
                    cts.Cancel();

                    // The former registration disposed the socket right here, under the poll thread.
                    Assert.IsTrue(IsOpen(pair.Client), "cancellation must not close the socket itself");
                    Assert.IsTrue(await atClose.Task, "the descriptor is still open when the poll thread closes it");
                    Assert.IsFalse(receive.IsCompleted, "the receive ends only once the poll thread left the socket");
                }
                finally
                {
                    gate.Set();
                }

                await AssertCanceledWithAsync(receive, cts.Token);
                Assert.IsFalse(IsOpen(pair.Client), "the poll thread closes the socket before it fails the receive");
                Assert.AreEqual(PollSelectSocketChannel.BuffersReturned, channel.BufferState);
            }, Guard);
        }

        [Test]
        public void Cancel_SendPending_ShutsDownAndThePollThreadClosesAfterItsCallEnded()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync(bufferSize: 4096);
                using var gate = new ManualResetEventSlim(false);
                using var cts = new CancellationTokenSource();
                using var channel = new PollSelectSocketChannel(pair.Client);
                var atClose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.TestHookBeforeClose = () =>
                {
                    atClose.TrySetResult(IsOpen(pair.Client));
                    gate.Wait(Guard);
                };

                // Memory that is not array-backed goes straight to the poll thread, which sends it in
                // 16 KB chunks (Winsock may accept one large array-backed send whole). The peer never
                // reads, so the chunks stall once the kernel buffers are full.
                using var payload = new NonArrayMemory(8 * 1024 * 1024);
                long start = channel.PollIterations;
                var send = channel.SendAsync(payload.Memory, cts.Token).AsTask();
                await AbandonedSocketIoTests.WaitUntilAsync(
                    () => send.IsCompleted || channel.PollIterations >= start + 3,
                    "the poll thread never serviced the send");
                Assert.IsFalse(send.IsCompleted, "precondition: the send is stalled on the poll thread");
                try
                {
                    cts.Cancel();

                    Assert.IsTrue(IsOpen(pair.Client), "cancellation must not close the socket itself");
                    Assert.IsTrue(await atClose.Task, "the descriptor is still open when the poll thread closes it");
                    Assert.IsFalse(send.IsCompleted, "the send ends only once the poll thread left the socket");
                }
                finally
                {
                    gate.Set();
                }

                await AssertCanceledWithAsync(send, cts.Token);
                Assert.IsFalse(IsOpen(pair.Client));
                Assert.AreEqual(PollSelectSocketChannel.BuffersReturned, channel.BufferState);
            }, Guard);
        }

        private static async Task AssertCanceledWithAsync(Task task, CancellationToken? expected)
        {
            try
            {
                var finished = await Task.WhenAny(task, Task.Delay(Guard));
                Assert.AreSame(task, finished, "the operation neither completed nor was cancelled");
                await task;
            }
            catch (OperationCanceledException ex)
            {
                if (expected.HasValue)
                    Assert.AreEqual(expected.Value, ex.CancellationToken, "OCE must carry the caller's token");
                return;
            }

            Assert.Fail("Expected OperationCanceledException, but the operation completed.");
        }

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

        /// <summary>False once the socket was closed; a call that reaches the OS proves it open.</summary>
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
