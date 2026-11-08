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
        private bool _savedHoldsPendingIo;

        [SetUp]
        public void SetUp()
        {
            _savedHold = SocketHandleHold.HoldTimeout;
            _savedHoldsPendingIo = SocketHandleHold.HoldsPendingIo;
            _savedSettle = AbandonedSocketIo.SettleTimeout;
            AssertAsync.Run(async () =>
                await AbandonedSocketIoTests.WaitUntilAsync(
                    () => AbandonedSocketIo.PendingCount == 0, "earlier abandoned operations never settled"),
                Guard);
            SocketHandleHold.HoldTimeout = LongHold;

            // The selector-platform behavior under test, on every host (Windows does not hold).
            SocketHandleHold.HoldsPendingIo = true;
        }

        [TearDown]
        public void TearDown()
        {
            SocketHandleHold.HoldTimeout = _savedHold;
            SocketHandleHold.HoldsPendingIo = _savedHoldsPendingIo;
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

                    Assert.IsTrue(hold.EnterIo());
                    hold.ExitIo(true, read.Task, write: false);
                    hold.Close();

                    Assert.AreEqual(0, await ReceiveAsync(server), "the peer must see the close at once");
                    Assert.IsFalse(hold.IsReleased, "the handle must stay open while the read is pending");
                    Assert.IsFalse(IsClosed(client), "the descriptor must not be released before the read ended");
                    Assert.AreEqual(SocketHandleHold.NotReleased, hold.ReleaseKind);

                    read.SetResult(0);

                    Assert.IsTrue(hold.IsReleased, "the handle is released once the read ended");
                    Assert.IsTrue(IsClosed(client));
                    Assert.AreEqual(SocketHandleHold.ReleasedAfterEnd, hold.ReleaseKind);
                }
            }, Guard);
        }

        [Test]
        public void Close_ReadPending_PlatformThatDoesNotHold_ReleasesTheHandleAtOnce()
        {
            // Windows: a shutdown does not end the pending receive and the close aborts it, so a hold
            // would keep the abandoned read, and the connection gate waiting on it, until the bound.
            AssertAsync.Run(async () =>
            {
                SocketHandleHold.HoldsPendingIo = false;
                var (client, server) = await ConnectPairAsync();
                using (server)
                {
                    var hold = new SocketHandleHold(client);
                    var read = new TaskCompletionSource<int>();

                    Assert.IsTrue(hold.EnterIo());
                    hold.ExitIo(true, read.Task, write: false);
                    hold.Close();

                    Assert.IsTrue(hold.IsReleased, "the handle is released at close");
                    Assert.IsTrue(IsClosed(client));
                    Assert.AreEqual(0, await ReceiveAsync(server), "the peer sees the close");
                    Assert.AreEqual(SocketHandleHold.ReleasedAtClose, hold.ReleaseKind, "nothing was held");

                    // An operation that reaches the hold after the close holds nothing either.
                    Assert.IsFalse(hold.EnterIo());
                    hold.ExitIo(false, null, write: true);
                    read.SetResult(0);
                    Assert.AreEqual(SocketHandleHold.ReleasedAtClose, hold.ReleaseKind);
                }
            }, Guard);
        }

        [Test]
        public void Close_ReadPending_AnOperationStartedAfterTheClose_DoesNotReleaseTheHandle()
        {
            // A caller that still touches the disposed stream fails inside the bracket; the close
            // never counted that call, so it must not count down the held read's claim.
            AssertAsync.Run(async () =>
            {
                var (client, server) = await ConnectPairAsync();
                using (server)
                {
                    var hold = new SocketHandleHold(client);
                    var read = new TaskCompletionSource<int>();

                    Assert.IsTrue(hold.EnterIo());
                    hold.ExitIo(true, read.Task, write: false);
                    hold.Close();

                    bool counted = hold.EnterIo();
                    Assert.IsFalse(counted, "an operation after the close is not counted");
                    hold.ExitIo(counted, null, write: false);
                    Assert.IsFalse(hold.IsReleased, "the held read is still pending");
                    Assert.IsFalse(IsClosed(client));

                    read.SetResult(0);
                    Assert.IsTrue(hold.IsReleased);
                    Assert.AreEqual(SocketHandleHold.ReleasedAfterEnd, hold.ReleaseKind);
                    Assert.IsTrue(IsClosed(client));
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

                    Assert.IsTrue(hold.EnterIo());
                    hold.ExitIo(true, read.Task, write: false);
                    hold.Close();
                    Assert.IsFalse(hold.IsReleased);

                    await AbandonedSocketIoTests.WaitUntilAsync(() => hold.IsReleased, "the hold bound never released the handle");
                    Assert.IsTrue(IsClosed(client));
                    Assert.AreEqual(SocketHandleHold.ReleasedAtBound, hold.ReleaseKind);

                    // The read ending late releases nothing twice.
                    read.SetException(new ObjectDisposedException("socket"));
                    Assert.AreEqual(SocketHandleHold.ReleasedAtBound, hold.ReleaseKind);
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

                    // A completed operation leaves nothing to wait for.
                    Assert.IsTrue(hold.EnterIo());
                    hold.ExitIo(true, Task.FromResult(5), write: false);
                    hold.Close();

                    Assert.IsTrue(hold.IsReleased);
                    Assert.IsTrue(IsClosed(client));
                    Assert.AreEqual(SocketHandleHold.ReleasedAtClose, hold.ReleaseKind);
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
                    Assert.IsTrue(hold.EnterIo());
                    hold.ExitIo(true, write.Task, write: true);
                    Assert.IsTrue(hold.EnterIo());
                    hold.Close();
                    Assert.IsFalse(hold.IsReleased);

                    hold.ExitIo(true, read.Task, write: false);
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

                    Assert.IsTrue(hold.EnterIo());
                    hold.Close();
                    Assert.IsFalse(hold.IsReleased);

                    hold.ExitIo(true, null, write: false);
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
