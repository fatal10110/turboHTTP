using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// #384: on IL2CPP the gate's settle bound could expire before an abandoned read ended, and the
    /// next socket then reused the descriptor behind the read's stale selector job. The socket stream
    /// now shuts the socket down at once but keeps its handle (the descriptor number) until every
    /// operation pending at disposal ended, or until <see cref="SocketHandleHold.HoldTimeout"/> for one
    /// that never ends. The operation doubles here are completion sources that ignore token and
    /// disposal, as an IL2CPP read can.
    /// </summary>
    [TestFixture]
    public class AbandonedHandleHoldTests
    {
        // Guard only: every awaited step ends long before this; it never synchronizes a test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        // Longer than any test: only the end of the pending operation can release the handle.
        private static readonly TimeSpan LongHold = TimeSpan.FromSeconds(30);

        private TimeSpan _savedHold;
        private TimeSpan _savedSettle;

        [SetUp]
        public void SetUp()
        {
            _savedHold = SocketHandleHold.HoldTimeout;
            _savedSettle = AbandonedSocketIo.SettleTimeout;
            AssertAsync.Run(async () =>
                await AbandonedSocketIoTests.WaitUntilAsync(
                    () => AbandonedSocketIo.PendingCount == 0, "earlier abandoned operations never settled"),
                Guard);
            SocketHandleHold.HoldTimeout = LongHold;
        }

        [TearDown]
        public void TearDown()
        {
            SocketHandleHold.HoldTimeout = _savedHold;
            AbandonedSocketIo.SettleTimeout = _savedSettle;
        }

        [Test]
        public void Close_ReadPending_ShutsDownAtOnceButHoldsTheHandleUntilTheReadEnds()
        {
            AssertAsync.Run(async () =>
            {
                var (client, server) = await ConnectPairAsync();
                using (server)
                {
                    var hold = new SocketHandleHold(client);
                    var read = new TaskCompletionSource<int>();
                    long releasedAfterEnd = SocketHandleHold.ReleasedAfterEndCount;
                    int held = SocketHandleHold.HeldCount;

                    hold.EnterIo();
                    hold.ExitIo(read.Task, write: false);
                    hold.Close();

                    Assert.AreEqual(0, await ReceiveAsync(server), "the peer must see the close at once");
                    Assert.IsFalse(hold.IsReleased, "the handle must stay open while the read is pending");
                    Assert.IsFalse(IsClosed(client), "the descriptor must not be released before the read ended");
                    Assert.AreEqual(held + 1, SocketHandleHold.HeldCount);

                    read.SetResult(0);

                    Assert.IsTrue(hold.IsReleased, "the handle is released once the read ended");
                    Assert.IsTrue(IsClosed(client));
                    Assert.AreEqual(held, SocketHandleHold.HeldCount);
                    Assert.AreEqual(releasedAfterEnd + 1, SocketHandleHold.ReleasedAfterEndCount);
                }
            }, Guard);
        }

        [Test]
        public void Close_ReadNeverEnds_HandleIsReleasedAtTheHoldBound()
        {
            AssertAsync.Run(async () =>
            {
                SocketHandleHold.HoldTimeout = TimeSpan.FromMilliseconds(100);
                var (client, server) = await ConnectPairAsync();
                using (server)
                {
                    var hold = new SocketHandleHold(client);
                    var read = new TaskCompletionSource<int>();
                    long releasedAtBound = SocketHandleHold.ReleasedAtBoundCount;
                    long releasedAfterEnd = SocketHandleHold.ReleasedAfterEndCount;
                    int held = SocketHandleHold.HeldCount;

                    hold.EnterIo();
                    hold.ExitIo(read.Task, write: false);
                    hold.Close();
                    Assert.IsFalse(hold.IsReleased);

                    await AbandonedSocketIoTests.WaitUntilAsync(() => hold.IsReleased, "the hold bound never released the handle");
                    Assert.IsTrue(IsClosed(client));
                    Assert.AreEqual(held, SocketHandleHold.HeldCount);
                    Assert.AreEqual(releasedAtBound + 1, SocketHandleHold.ReleasedAtBoundCount);

                    // The read ending late releases nothing twice.
                    read.SetException(new ObjectDisposedException("socket"));
                    Assert.AreEqual(releasedAtBound + 1, SocketHandleHold.ReleasedAtBoundCount);
                    Assert.AreEqual(releasedAfterEnd, SocketHandleHold.ReleasedAfterEndCount);
                    Assert.AreEqual(held, SocketHandleHold.HeldCount);
                }
            }, Guard);
        }

        [Test]
        public void Close_NothingPending_ReleasesTheHandleAtOnce()
        {
            AssertAsync.Run(async () =>
            {
                var (client, server) = await ConnectPairAsync();
                using (server)
                {
                    var hold = new SocketHandleHold(client);
                    int held = SocketHandleHold.HeldCount;

                    // A completed operation leaves nothing to wait for.
                    hold.EnterIo();
                    hold.ExitIo(Task.FromResult(5), write: false);
                    hold.Close();

                    Assert.IsTrue(hold.IsReleased);
                    Assert.IsTrue(IsClosed(client));
                    Assert.AreEqual(held, SocketHandleHold.HeldCount);
                    Assert.AreEqual(0, await ReceiveAsync(server));
                }
            }, Guard);
        }

        [Test]
        public void Close_WhileAReadIsStarting_HoldsUntilThatReadEnds()
        {
            AssertAsync.Run(async () =>
            {
                var (client, server) = await ConnectPairAsync();
                using (server)
                {
                    var hold = new SocketHandleHold(client);
                    var read = new TaskCompletionSource<int>();
                    var write = new TaskCompletionSource<bool>();

                    // A write is pending and a read is between its start and its return when the
                    // stream is disposed: both hold the handle.
                    hold.EnterIo();
                    hold.ExitIo(write.Task, write: true);
                    hold.EnterIo();
                    hold.Close();
                    Assert.IsFalse(hold.IsReleased);

                    hold.ExitIo(read.Task, write: false);
                    Assert.IsFalse(hold.IsReleased);

                    write.SetResult(true);
                    Assert.IsFalse(hold.IsReleased, "the read still holds the handle");

                    read.SetException(new ObjectDisposedException("socket"));
                    Assert.IsTrue(hold.IsReleased);
                    Assert.IsTrue(IsClosed(client));
                }
            }, Guard);
        }

        [Test]
        public void Close_WhileASyncReadIsInProgress_ReleasesWhenItReturns()
        {
            AssertAsync.Run(async () =>
            {
                var (client, server) = await ConnectPairAsync();
                using (server)
                {
                    var hold = new SocketHandleHold(client);

                    hold.EnterIo();
                    hold.Close();
                    Assert.IsFalse(hold.IsReleased);

                    hold.ExitIo(null, write: false);
                    Assert.IsTrue(hold.IsReleased);
                    Assert.IsTrue(IsClosed(client));
                }
            }, Guard);
        }

        [Test]
        public void HoldingNetworkStream_DisposedUnderAPendingRead_ReleasesTheSocketOnlyAfterTheReadEnded()
        {
            AssertAsync.Run(async () =>
            {
                var (client, server) = await ConnectPairAsync();
                using (server)
                {
                    var stream = new HoldingNetworkStream(client);
                    var read = stream.ReadAsync(new byte[16].AsMemory(), CancellationToken.None).AsTask();
                    Assert.IsFalse(read.IsCompleted, "nothing was sent: the read is pending");

                    stream.Dispose();
                    Assert.AreEqual(0, await ReceiveAsync(server), "the peer must see the close at once");

                    // The platform may end the read on the shutdown alone; until it does, the
                    // descriptor stays open. The peer's close ends it on every platform.
                    if (!read.IsCompleted)
                        Assert.IsFalse(IsClosed(client), "the descriptor must not be released before the read ended");

                    server.Close();
                    await AbandonedSocketIoTests.AssertCompletesAsync(CaptureAsync(read), "the read never ended");
                    await AbandonedSocketIoTests.WaitUntilAsync(() => stream.Hold.IsReleased, "the handle was never released");
                    Assert.IsTrue(IsClosed(client));
                }
            }, Guard);
        }

        [Test]
        public void HoldingNetworkStream_DisposedIdle_ClosesTheSocketAtOnce()
        {
            AssertAsync.Run(async () =>
            {
                var (client, server) = await ConnectPairAsync();
                using (server)
                {
                    var stream = new HoldingNetworkStream(client);
                    var payload = new byte[] { 1, 2, 3 };
                    await stream.WriteAsync(payload, 0, payload.Length);
                    var echo = new byte[3];
                    Assert.AreEqual(3, await server.ReceiveAsync(new ArraySegment<byte>(echo), SocketFlags.None));

                    stream.Dispose();

                    Assert.IsTrue(stream.Hold.IsReleased);
                    Assert.IsTrue(IsClosed(client));
                    Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
                }
            }, Guard);
        }

        [Test]
        public void ConnectionPool_NetworkStreamMode_WrapsTheSocketInAHoldingStream()
        {
            AssertAsync.Run(async () =>
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var accept = listener.AcceptSocketAsync();
                    using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                    using var lease = await pool.GetNewConnectionAsync("127.0.0.1", port, false, CancellationToken.None);
                    using var accepted = await accept;

                    Assert.IsInstanceOf<HoldingNetworkStream>(lease.Connection.Stream);
                }
                finally
                {
                    listener.Stop();
                }
            }, Guard);
        }

        [Test]
        public void AbandonedEntry_EndedByItsOperation_CountsAsSettled()
        {
            AbandonedSocketIo.SettleTimeout = LongHold;
            long settled = AbandonedSocketIo.SettledCount;
            long expired = AbandonedSocketIo.ExpiredCount;

            var entry = AbandonedSocketIo.Begin();
            entry.End();
            entry.End();

            Assert.AreEqual(settled + 1, AbandonedSocketIo.SettledCount);
            Assert.AreEqual(expired, AbandonedSocketIo.ExpiredCount);
            Assert.AreEqual(0, AbandonedSocketIo.PendingCount);
        }

        [Test]
        public void AbandonedEntry_ReachingTheSettleBound_CountsAsExpired()
        {
            AssertAsync.Run(async () =>
            {
                AbandonedSocketIo.SettleTimeout = TimeSpan.FromMilliseconds(50);
                long settled = AbandonedSocketIo.SettledCount;
                long expired = AbandonedSocketIo.ExpiredCount;
                var operation = new TaskCompletionSource<int>();

                AbandonedSocketIo.Track(operation.Task);
                await AbandonedSocketIoTests.WaitUntilAsync(
                    () => AbandonedSocketIo.PendingCount == 0, "the entry never expired");

                Assert.AreEqual(expired + 1, AbandonedSocketIo.ExpiredCount);
                Assert.AreEqual(settled, AbandonedSocketIo.SettledCount);

                // The late end of an expired operation is not counted again.
                operation.SetResult(1);
                Assert.AreEqual(expired + 1, AbandonedSocketIo.ExpiredCount);
                Assert.AreEqual(settled, AbandonedSocketIo.SettledCount);
            }, Guard);
        }

        private static async Task<(Socket Client, Socket Server)> ConnectPairAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var accept = listener.AcceptSocketAsync();
                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                return (client, await accept);
            }
            finally
            {
                listener.Stop();
            }
        }

        private static Task<int> ReceiveAsync(Socket socket) =>
            socket.ReceiveAsync(new ArraySegment<byte>(new byte[16]), SocketFlags.None);

        private static bool IsClosed(Socket socket)
        {
            try
            {
                _ = socket.Available;
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private static async Task CaptureAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
                // The read's outcome after disposal is platform-specific; only its end matters.
            }
        }
    }
}
