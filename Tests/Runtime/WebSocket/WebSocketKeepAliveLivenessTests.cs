using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// Keep-alive liveness: a write stalled by TCP backpressure must not starve the Ping and the protocol-error
    /// Close (#1129), any inbound bytes count as liveness (#1131), and a pong timeout gets a grace re-check and
    /// honours an application resume (#1132).
    /// </summary>
    [TestFixture]
    public sealed class WebSocketKeepAliveLivenessTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/keepalive");
        private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

        [Test]
        public void KeepAlive_DataWriteStalledByBackpressure_AbortsWithPongTimeout()
        {
            AssertAsync.Run(async () =>
            {
                var options = CreateOptions();
                options.PingInterval = TimeSpan.FromMilliseconds(50);
                options.PongTimeout = TimeSpan.FromMilliseconds(200);

                StallingWriteStream stalling = null;
                using var peer = new InMemoryWebSocketPeer(inner => stalling = new StallingWriteStream(inner));
                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    // The peer stops reading: the next write never completes and ignores its token, like a socket
                    // send blocked on a zero TCP window.
                    stalling.Stall();
                    var sendTask = connection.SendBinaryAsync(new byte[256], CancellationToken.None);

                    await WaitUntilAsync(() => stalling.StalledWrites > 0, "The data write never stalled.")
                        .ConfigureAwait(false);
                    await WaitUntilAsync(
                        () => connection.State == WebSocketState.Closed,
                        "A stalled write starved the keep-alive Ping: the connection was never aborted.")
                        .ConfigureAwait(false);

                    var terminal = connection.TerminalError as WebSocketException;
                    Assert.IsNotNull(terminal, "Expected a WebSocketException, got " + connection.TerminalError);
                    Assert.AreEqual(WebSocketError.PongTimeout, terminal.Error);

                    await AssertFailsWithWebSocketExceptionAsync(sendTask, "stalled send").ConfigureAwait(false);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void ProtocolError_CloseBehindStalledWrite_IsBoundedByCloseHandshakeTimeout()
        {
            AssertAsync.Run(async () =>
            {
                var options = CreateOptions();
                options.CloseHandshakeTimeout = TimeSpan.FromMilliseconds(200);

                StallingWriteStream stalling = null;
                using var peer = new InMemoryWebSocketPeer(inner => stalling = new StallingWriteStream(inner));
                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    stalling.Stall();
                    var sendTask = connection.SendBinaryAsync(new byte[256], CancellationToken.None);
                    await WaitUntilAsync(() => stalling.StalledWrites > 0, "The data write never stalled.")
                        .ConfigureAwait(false);

                    // Reserved opcode 0x3 (RFC 6455 5.2): the Close (1002) queues behind the stalled message.
                    await peer.SendRawAsync(new byte[] { 0x83, 0x00 }).ConfigureAwait(false);

                    await WaitUntilAsync(
                        () => connection.State == WebSocketState.Closed,
                        "The protocol-error Close waited on the stalled write without a deadline.")
                        .ConfigureAwait(false);

                    var terminal = connection.TerminalError as WebSocketException;
                    Assert.IsNotNull(terminal, "Expected a WebSocketException, got " + connection.TerminalError);
                    Assert.AreEqual(WebSocketError.ReservedOpcode, terminal.Error);
                    Assert.AreEqual(WebSocketCloseCode.ProtocolError, connection.CloseStatus?.Code);

                    await AssertFailsWithWebSocketExceptionAsync(sendTask, "stalled send").ConfigureAwait(false);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void KeepAlive_LargeFrameArrivingSlowly_ExtendsPongWaitWhileBytesArrive()
        {
            AssertAsync.Run(async () =>
            {
                var options = CreateOptions();
                options.PingInterval = TimeSpan.FromMilliseconds(50);
                options.PongTimeout = TimeSpan.FromSeconds(1);

                using var peer = new InMemoryWebSocketPeer { AnswerPings = false };
                // A generous grace keeps the test deterministic on a slow machine: a chunk read late only delays it.
                var connection = new WebSocketConnection { KeepAliveGracePeriodOverride = TimeSpan.FromSeconds(30) };
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    var ping = await ReadPingAsync(peer).ConfigureAwait(false);

                    // One 3000-byte binary frame, delivered one chunk per PongTimeout window. The Pong cannot
                    // overtake the frame (RFC 6455 5.4), so only the bytes show the peer is alive.
                    var payload = new byte[3000];
                    for (int i = 0; i < payload.Length; i++)
                        payload[i] = (byte)(i * 7);

                    var first = new byte[4 + 1000];
                    first[0] = 0x82;
                    first[1] = 126;
                    first[2] = (byte)(payload.Length >> 8);
                    first[3] = (byte)payload.Length;
                    Buffer.BlockCopy(payload, 0, first, 4, 1000);
                    await peer.SendRawAsync(first).ConfigureAwait(false);

                    await WaitForExtensionsAsync(connection, 1).ConfigureAwait(false);
                    await peer.SendRawAsync(Slice(payload, 1000, 1000)).ConfigureAwait(false);

                    await WaitForExtensionsAsync(connection, 2).ConfigureAwait(false);
                    await peer.SendRawAsync(Slice(payload, 2000, 1000)).ConfigureAwait(false);
                    await peer.SendFrameAsync(WebSocketOpcode.Pong, ping.Payload, fin: true).ConfigureAwait(false);

                    using (var timeout = new CancellationTokenSource(WaitTimeout))
                    using (var message = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false))
                    {
                        Assert.IsTrue(message.IsBinary);
                        CollectionAssert.AreEqual(payload, message.Data.ToArray());
                    }

                    // The keep-alive moved on to the next Ping instead of closing the connection.
                    await ReadPingAsync(peer).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketState.Open, connection.State,
                        "A frame in transit was treated as a dead peer: " + connection.TerminalError?.Message);
                    Assert.IsNull(connection.TerminalError);

                    await connection.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void KeepAlive_SilentPeer_IsStillClosedWithPongTimeout()
        {
            AssertAsync.Run(async () =>
            {
                var options = CreateOptions();
                options.PingInterval = TimeSpan.FromMilliseconds(50);
                options.PongTimeout = TimeSpan.FromMilliseconds(100);

                using var peer = new InMemoryWebSocketPeer { AnswerPings = false };
                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    // A frame that starts and then stops: bytes before the Ping are not liveness after it.
                    await peer.SendRawAsync(new byte[] { 0x82, 126, 0x0B, 0xB8, 1, 2, 3, 4 }).ConfigureAwait(false);
                    await ReadPingAsync(peer).ConfigureAwait(false);

                    await WaitUntilAsync(
                        () => connection.State == WebSocketState.Closed,
                        "A silent peer was never closed.").ConfigureAwait(false);

                    var terminal = connection.TerminalError as WebSocketException;
                    Assert.IsNotNull(terminal, "Expected a WebSocketException, got " + connection.TerminalError);
                    Assert.AreEqual(WebSocketError.PongTimeout, terminal.Error);
                    Assert.GreaterOrEqual(connection.KeepAlivePongGraceWaits, 1,
                        "The pong timeout skipped the grace re-check.");
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void KeepAlive_PongArrivingDuringGracePeriod_KeepsConnectionOpen()
        {
            AssertAsync.Run(async () =>
            {
                var options = CreateOptions();
                options.PingInterval = TimeSpan.FromMilliseconds(50);
                options.PongTimeout = TimeSpan.FromMilliseconds(200);

                using var peer = new InMemoryWebSocketPeer { AnswerPings = false };
                var connection = new WebSocketConnection { KeepAliveGracePeriodOverride = TimeSpan.FromSeconds(30) };
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    var ping = await ReadPingAsync(peer).ConfigureAwait(false);

                    // The PongTimeout window elapsed: the keep-alive re-checks before declaring the peer dead,
                    // the way a timer that fired on resume races the read completion carrying the Pong.
                    await WaitUntilAsync(
                        () => connection.KeepAlivePongGraceWaits > 0 || connection.State != WebSocketState.Open,
                        "The pong timeout never entered the grace re-check.").ConfigureAwait(false);
                    Assert.AreEqual(WebSocketState.Open, connection.State,
                        "Closed without a grace re-check: " + connection.TerminalError?.Message);

                    await peer.SendFrameAsync(WebSocketOpcode.Pong, ping.Payload, fin: true).ConfigureAwait(false);

                    await ReadPingAsync(peer).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketState.Open, connection.State, connection.TerminalError?.Message);
                    Assert.IsNull(connection.TerminalError);

                    await connection.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void KeepAlive_ApplicationResumeDuringPongWindow_RestartsDeadline()
        {
            AssertAsync.Run(async () =>
            {
                var options = CreateOptions();
                options.PingInterval = TimeSpan.FromMilliseconds(50);
                options.PongTimeout = TimeSpan.FromMilliseconds(300);

                using var peer = new InMemoryWebSocketPeer { AnswerPings = false };
                var connection = new WebSocketConnection { KeepAliveGracePeriodOverride = TimeSpan.FromSeconds(30) };
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    var ping = await ReadPingAsync(peer).ConfigureAwait(false);

                    // The platform layer reports a resume while the Pong window runs.
                    WebSocketConnection.NotifyApplicationResumed();

                    await WaitForExtensionsAsync(connection, 1).ConfigureAwait(false);
                    Assert.AreEqual(0, connection.KeepAlivePongGraceWaits,
                        "A resumed window must restart, not fall through to the grace re-check.");

                    await peer.SendFrameAsync(WebSocketOpcode.Pong, ping.Payload, fin: true).ConfigureAwait(false);

                    await ReadPingAsync(peer).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketState.Open, connection.State, connection.TerminalError?.Message);
                    Assert.IsNull(connection.TerminalError);

                    await connection.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        private static WebSocketConnectionOptions CreateOptions()
        {
            return new WebSocketConnectionOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(5),
                CloseHandshakeTimeout = TimeSpan.FromSeconds(2),
                PingInterval = TimeSpan.Zero,
                IdleTimeout = TimeSpan.Zero
            };
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(source, offset, slice, 0, count);
            return slice;
        }

        private static async Task<InMemoryWebSocketPeer.ReceivedFrame> ReadPingAsync(InMemoryWebSocketPeer peer)
        {
            var frame = await peer.ReadFrameAsync(WaitTimeout).ConfigureAwait(false);
            Assert.AreEqual(WebSocketOpcode.Ping, frame.Opcode, "Expected a keep-alive Ping.");
            return frame;
        }

        private static async Task WaitForExtensionsAsync(WebSocketConnection connection, long target)
        {
            await WaitUntilAsync(
                () => connection.KeepAliveLivenessExtensions >= target || connection.State != WebSocketState.Open,
                "The pong wait was never extended.").ConfigureAwait(false);
            Assert.AreEqual(WebSocketState.Open, connection.State,
                "Closed instead of extending the pong wait: " + connection.TerminalError?.Message);
        }

        private static async Task AssertFailsWithWebSocketExceptionAsync(Task task, string what)
        {
            var finished = await Task.WhenAny(task, Task.Delay(WaitTimeout)).ConfigureAwait(false);
            Assert.AreSame(task, finished, "The " + what + " never completed.");
            try
            {
                await task.ConfigureAwait(false);
                Assert.Fail("The " + what + " succeeded on an aborted connection.");
            }
            catch (WebSocketException)
            {
                // Expected: the aborted connection failed the send.
            }
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, string message)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!predicate())
            {
                if (deadline.Elapsed > WaitTimeout)
                    Assert.Fail(message);

                // Polling a state predicate under a deadline, not a sleep used as synchronization.
                await Task.Delay(5).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Client stream whose writes, once <see cref="Stall"/> is called, never complete and ignore their token
        /// (a socket send blocked on a zero TCP window); disposing the stream faults them.
        /// </summary>
        private sealed class StallingWriteStream : Stream
        {
            private readonly Stream _inner;
            private readonly TaskCompletionSource<bool> _disposed =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _stalled;
            private int _stalledWrites;

            public StallingWriteStream(Stream inner)
            {
                _inner = inner;
            }

            public int StalledWrites => Volatile.Read(ref _stalledWrites);

            public void Stall() => Interlocked.Exchange(ref _stalled, 1);

            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return _inner.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Volatile.Read(ref _stalled) != 0)
                    throw new NotSupportedException("Synchronous writes are not expected.");

                _inner.Write(buffer, offset, count);
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (Volatile.Read(ref _stalled) != 0)
                {
                    await StallUntilDisposedAsync().ConfigureAwait(false);
                    return;
                }

                await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (Volatile.Read(ref _stalled) != 0)
                {
                    await StallUntilDisposedAsync().ConfigureAwait(false);
                    return;
                }

                await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            public override void Flush() => _inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _disposed.TrySetResult(true);
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }

            private async Task StallUntilDisposedAsync()
            {
                Interlocked.Increment(ref _stalledWrites);
                await _disposed.Task.ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(StallingWriteStream));
            }
        }
    }
}
