using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tcp;
using LoopbackPair = TurboHTTP.Tests.Transport.SocketChannelLifecycleTests.LoopbackPair;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// PollSelect sends on a full kernel send buffer (#329). Mono's and IL2CPP's
    /// <c>Socket.Send(byte[], int, int, SocketFlags, out SocketError)</c> retries <c>WouldBlock</c>
    /// until the whole count is written, so on a non-blocking socket whose peer does not read it
    /// never returns. Every call under test runs on its own thread behind a watchdog, so a call that
    /// does not return fails the test instead of stalling the run.
    /// </summary>
    [TestFixture]
    public class PollSelectSocketChannelSendTests
    {
        private const int Payload = 4 * 1024 * 1024;
        private const int KernelBuffer = 32 * 1024;

        /// <summary>Bound for a call that must not block (generous for slow emulators).</summary>
        private static readonly TimeSpan CallBound = TimeSpan.FromSeconds(5);

        private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

        [Test]
        public void SendAsync_PeerNotReading_ReturnsWithoutBlocking()
        {
            AssertAsync.Run(async () =>
            {
                var watchdog = Task.Delay(Watchdog);
                LoopbackPair pair = null;
                PollSelectSocketChannel channel = null;
                try
                {
                    pair = await Within(LoopbackPair.CreateAsync(KernelBuffer), watchdog, "loopback connect");
                    channel = new PollSelectSocketChannel(pair.Client);
                    var payload = Pattern(Payload, 7);

                    // 4 MB is far more than the send and receive buffers hold while the peer does
                    // not read, so the call must return with the send still pending (or, where the
                    // kernel buffers it all, completed). It must not wait for the peer.
                    var ch = channel;
                    var call = StartThread(() => ch.SendAsync(payload, CancellationToken.None).AsTask());
                    await Within(call, Task.Delay(CallBound), "SendAsync on a full send buffer");
                    var send = await call;

                    var drain = StartThread(() => ReadExactly(pair.Server, Payload));
                    await Within(Task.WhenAll(send, drain), watchdog, "send after the peer started reading");
                    AssertBytes(payload, await drain);
                }
                finally
                {
                    channel?.Dispose();
                    pair?.Dispose();
                }
            });
        }

        [Test]
        public void SendAsync_CancelWhileSendBufferFull_CompletesCanceled()
        {
            AssertAsync.Run(async () =>
            {
                var watchdog = Task.Delay(Watchdog);
                LoopbackPair pair = null;
                PollSelectSocketChannel channel = null;
                try
                {
                    pair = await Within(LoopbackPair.CreateAsync(KernelBuffer), watchdog, "loopback connect");
                    channel = new PollSelectSocketChannel(pair.Client);
                    var payload = Pattern(Payload, 11);
                    using var cts = new CancellationTokenSource();

                    var ch = channel;
                    var call = StartThread(() => ch.SendAsync(payload, cts.Token).AsTask());
                    await Within(call, Task.Delay(CallBound), "SendAsync on a full send buffer");
                    var send = await call;
                    if (send.IsCompleted)
                        Assert.Inconclusive("the kernel accepted the whole payload; nothing left to cancel");

                    cts.Cancel();
                    await Within(Swallow(send), watchdog, "cancelled send");
                    Assert.IsTrue(send.IsCanceled || send.Exception?.GetBaseException() is OperationCanceledException,
                        $"expected a cancelled send, got {send.Status}: {send.Exception?.GetBaseException()}");
                }
                finally
                {
                    channel?.Dispose();
                    pair?.Dispose();
                }
            });
        }

        /// <summary>
        /// Pins the class-library behavior the channel relies on: the list overload of
        /// <c>Socket.Send</c> makes one native call and returns a short count (or WouldBlock) on a
        /// non-blocking socket instead of retrying until everything is written.
        /// </summary>
        [Test]
        public void ListSend_NonBlockingPeerNotReading_ReturnsPromptly()
        {
            AssertAsync.Run(async () =>
            {
                var watchdog = Task.Delay(Watchdog);
                LoopbackPair pair = null;
                try
                {
                    pair = await Within(LoopbackPair.CreateAsync(KernelBuffer), watchdog, "loopback connect");
                    var client = pair.Client;
                    client.Blocking = false;
                    var segments = new[] { new ArraySegment<byte>(new byte[Payload]) };

                    var call = StartThread(() =>
                    {
                        int n = client.Send(segments, SocketFlags.None, out var err);
                        return (n, err);
                    });
                    await Within(call, Task.Delay(CallBound), "Socket.Send(IList) on a full send buffer");
                    var (sent, error) = await call;

                    Assert.That(error, Is.EqualTo(SocketError.Success).Or.EqualTo(SocketError.WouldBlock));
                    Assert.That(sent, Is.InRange(0, Payload));
                }
                finally
                {
                    pair?.Dispose();
                }
            });
        }

        // ── Helpers (shared with PollSelectSocketChannelTests) ─────────────────

        /// <summary>
        /// Runs <paramref name="body"/> on a dedicated thread, so a call that does not return
        /// holds neither the test thread nor a pool thread.
        /// </summary>
        internal static Task<T> StartThread<T>(Func<T> body) =>
            Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        /// <summary>Fails the test when <paramref name="watchdog"/> finishes first.</summary>
        internal static async Task<T> Within<T>(Task<T> task, Task watchdog, string stage)
        {
            await Within((Task)task, watchdog, stage);
            return await task;
        }

        /// <inheritdoc cref="Within{T}(Task{T}, Task, string)"/>
        internal static async Task Within(Task task, Task watchdog, string stage)
        {
            if (!ReferenceEquals(await Task.WhenAny(task, watchdog), task))
                Assert.Fail($"{stage} did not finish within the watchdog");
            await task;
        }

        internal static byte[] ReadExactly(Socket socket, int count)
        {
            socket.Blocking = true;
            var received = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = socket.Receive(received, read, count - read, SocketFlags.None);
                if (n == 0) throw new InvalidOperationException("connection closed early");
                read += n;
            }

            return received;
        }

        internal static byte[] Pattern(int length, int seed)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
                data[i] = (byte)((i * seed + (i >> 10)) & 0xFF);
            return data;
        }

        /// <summary>Byte-wise compare without boxing 4 MB through CollectionAssert.</summary>
        internal static void AssertBytes(byte[] expected, byte[] actual)
        {
            Assert.AreEqual(expected.Length, actual.Length, "length");
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                    Assert.Fail($"byte {i} differs: expected {expected[i]}, got {actual[i]}");
            }
        }

        private static async Task Swallow(Task task)
        {
            try { await task; }
            catch { /* inspected by the caller */ }
        }
    }
}
