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
    /// PollSelect idle cost and full-duplex progress (#38). The idle tests count poll-loop
    /// iterations over a measurement window; the window is a sample period, not synchronization.
    /// </summary>
    [TestFixture]
    public class PollSelectSocketChannelTests
    {
        private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

        [Test]
        public void Idle_NoPendingOperation_PollThreadBlocks()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                using var channel = new PollSelectSocketChannel(pair.Client);

                long before = channel.PollIterations;
                await Task.Delay(Window);
                long iterations = channel.PollIterations - before;

                // The former loop spun through Thread.Sleep(0) (hundreds of thousands per window).
                Assert.LessOrEqual(iterations, 2, "idle poll thread must block, not spin");
            });
        }

        [Test]
        public void Idle_PendingReceive_PollsInSlicesNotSpins()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                var channel = new PollSelectSocketChannel(pair.Client);
                var pending = channel.ReceiveAsync(new byte[64], 0, 64, CancellationToken.None).AsTask();

                long before = channel.PollIterations;
                await Task.Delay(Window);
                long iterations = channel.PollIterations - before;

                // 20 ms slices: about 25 per 500 ms. The former 1 ms poll gave about 500.
                Assert.Less(iterations, 120, "a pending receive must not poll in 1 ms steps");
                Assert.IsFalse(pending.IsCompleted);

                // Data still arrives promptly while the thread waits in a long slice.
                pair.Server.Send(new byte[] { 1, 2, 3 });
                var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.AreSame(pending, finished);
                Assert.AreEqual(3, await pending);

                channel.Dispose();
            });
        }

        [Test]
        public void FullDuplex_LargeConcurrentSendAndReceive_CompletesWithoutDeadlock()
        {
            AssertAsync.Run(async () =>
            {
                const int payload = 4 * 1024 * 1024;
                using var pair = await LoopbackPair.CreateAsync(bufferSize: 32 * 1024);
                using var channel = new PollSelectSocketChannel(pair.Client);

                var outgoing = Pattern(payload, 7);
                var incomingExpected = Pattern(payload, 13);

                // The peer writes its whole payload before it reads anything. If the client stops
                // servicing receives while its own send is blocked on a full kernel buffer, both
                // sides wait on each other forever.
                var peer = Task.Run(() =>
                {
                    pair.Server.Blocking = true;
                    int sent = 0;
                    while (sent < payload)
                        sent += pair.Server.Send(incomingExpected, sent, payload - sent, SocketFlags.None);

                    var received = new byte[payload];
                    int read = 0;
                    while (read < payload)
                    {
                        int n = pair.Server.Receive(received, read, payload - read, SocketFlags.None);
                        if (n == 0) throw new InvalidOperationException("client closed early");
                        read += n;
                    }

                    return received;
                });

                var send = channel.SendAsync(outgoing, CancellationToken.None).AsTask();
                var receive = Task.Run(async () =>
                {
                    var received = new byte[payload];
                    int read = 0;
                    while (read < payload)
                    {
                        int n = await channel.ReceiveAsync(received, read, Math.Min(16 * 1024, payload - read),
                            CancellationToken.None);
                        if (n == 0) throw new InvalidOperationException("peer closed early");
                        read += n;
                    }

                    return received;
                });

                var all = Task.WhenAll(send, receive, peer);
                var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)));
                if (!ReferenceEquals(all, finished))
                {
                    // Unblock the stuck tasks before failing.
                    channel.Dispose();
                    pair.Dispose();
                    Assert.Fail("full-duplex transfer deadlocked");
                }

                await all;
                CollectionAssert.AreEqual(incomingExpected, await receive);
                CollectionAssert.AreEqual(outgoing, await peer);
            });
        }

        private static byte[] Pattern(int length, int seed)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
                data[i] = (byte)((i * seed + (i >> 10)) & 0xFF);
            return data;
        }
    }
}
