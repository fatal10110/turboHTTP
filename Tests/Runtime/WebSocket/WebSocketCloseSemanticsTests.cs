using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #86: close-handshake semantics (RFC 6455 sections 5.4, 5.5.1, 7.1.1): the reply echoes the received
    /// code, the client lets the server close TCP first (bounded), a canceled CloseAsync aborts, and a pong
    /// is not held behind a large fragmented message.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketCloseSemanticsTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/close-semantics");

        // Guard only: every await finishes long before this, it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        [Test]
        public void ServerClose_ReplyEchoesReceivedCode()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, CreateOptions(), CancellationToken.None)
                        .ConfigureAwait(false);

                    // 4001 with a reason: the reply carries the code (RFC 6455 section 5.5.1).
                    await peer.SendFrameAsync(
                        WebSocketOpcode.Close,
                        new byte[] { 0x0F, 0xA1, (byte)'b', (byte)'y', (byte)'e' },
                        fin: true).ConfigureAwait(false);

                    var reply = await peer.ReadFrameAsync(Guard).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketOpcode.Close, reply.Opcode);
                    Assert.GreaterOrEqual(reply.Payload.Length, 2);
                    Assert.AreEqual(4001, (reply.Payload[0] << 8) | reply.Payload[1]);

                    await WaitUntilAsync(() => connection.State == WebSocketState.Closed, "Connection did not close.")
                        .ConfigureAwait(false);
                    Assert.AreEqual((WebSocketCloseCode)4001, connection.CloseStatus.Value.Code);
                    Assert.IsNull(connection.TerminalError);
                }
                finally
                {
                    connection.Dispose();
                }
            });
        }

        [Test]
        public void ServerCloseWithoutBody_ReplyHasNoBody()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, CreateOptions(), CancellationToken.None)
                        .ConfigureAwait(false);

                    await peer.SendFrameAsync(WebSocketOpcode.Close, Array.Empty<byte>(), fin: true)
                        .ConfigureAwait(false);

                    // 1005 is local only (RFC 6455 section 7.4.1): the reply is an empty Close.
                    var reply = await peer.ReadFrameAsync(Guard).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketOpcode.Close, reply.Opcode);
                    Assert.AreEqual(0, reply.Payload.Length);
                }
                finally
                {
                    connection.Dispose();
                }
            });
        }

        [Test]
        public void CloseAsync_WaitsForServerToCloseTransport()
        {
            AssertAsync.Run(async () =>
            {
                ControlledClientStream clientStream = null;
                using var peer = new InMemoryWebSocketPeer(inner => clientStream = new ControlledClientStream(inner))
                {
                    CloseTransportAfterCloseHandshake = false
                };

                var options = CreateOptions();
                options.CloseHandshakeTimeout = TimeSpan.FromSeconds(60);

                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    var closeTask = connection.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None);

                    // The peer echoed the Close and the client processed it, but the server keeps TCP open.
                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(peer.ReaderCompleted, Guard,
                        "Peer did not receive the client Close.").ConfigureAwait(false);
                    await WaitUntilAsync(() => connection.CloseStatus.HasValue, "The peer's Close was not processed.")
                        .ConfigureAwait(false);

                    // Negative window: the old code disposed the stream right after the Close reply.
                    await Task.WhenAny(closeTask, Task.Delay(200)).ConfigureAwait(false);
                    Assert.IsFalse(closeTask.IsCompleted, "CloseAsync closed TCP before the server did.");
                    Assert.IsFalse(clientStream.IsDisposed, "The client closed the transport first.");
                    Assert.AreEqual(WebSocketState.Closing, connection.State);

                    peer.CloseTransport();

                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(closeTask, Guard,
                        "CloseAsync did not finish after the server closed TCP.").ConfigureAwait(false);
                    Assert.AreEqual(WebSocketState.Closed, connection.State);
                    Assert.AreEqual(WebSocketCloseCode.NormalClosure, connection.CloseStatus.Value.Code);
                    Assert.IsNull(connection.TerminalError);
                    Assert.IsTrue(clientStream.IsDisposed);
                }
                finally
                {
                    connection.Dispose();
                }
            });
        }

        [Test]
        public void CloseAsync_ServerNeverClosesTransport_ClosesAfterTimeout()
        {
            AssertAsync.Run(async () =>
            {
                ControlledClientStream clientStream = null;
                using var peer = new InMemoryWebSocketPeer(inner => clientStream = new ControlledClientStream(inner))
                {
                    CloseTransportAfterCloseHandshake = false
                };

                var options = CreateOptions();
                options.CloseHandshakeTimeout = TimeSpan.FromMilliseconds(300);

                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    var closeTask = connection.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None);
                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(closeTask, Guard,
                        "CloseAsync did not give up waiting for the server FIN.").ConfigureAwait(false);

                    // The Close exchange completed: a slow FIN is not an abnormal closure.
                    Assert.AreEqual(WebSocketState.Closed, connection.State);
                    Assert.AreEqual(WebSocketCloseCode.NormalClosure, connection.CloseStatus.Value.Code);
                    Assert.IsNull(connection.TerminalError);
                    Assert.IsTrue(clientStream.IsDisposed);
                }
                finally
                {
                    connection.Dispose();
                }
            });
        }

        [Test]
        public void CloseAsync_CanceledByCaller_LeavesConnectionClosed()
        {
            AssertAsync.Run(async () =>
            {
                ControlledClientStream clientStream = null;
                using var peer = new InMemoryWebSocketPeer(inner => clientStream = new ControlledClientStream(inner));

                var options = CreateOptions();
                options.CloseHandshakeTimeout = TimeSpan.FromSeconds(60);

                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    // The peer never sees the Close, so it never answers.
                    clientStream.DropWrites();
                    using var cts = new CancellationTokenSource();
                    var closeTask = connection.CloseAsync(WebSocketCloseCode.NormalClosure, "bye", cts.Token);

                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(clientStream.FirstDroppedWrite, Guard,
                        "The Close frame was not written.").ConfigureAwait(false);
                    Assert.AreEqual(WebSocketState.Closing, connection.State);

                    cts.Cancel();

                    var completed = await Task.WhenAny(closeTask, Task.Delay(Guard)).ConfigureAwait(false);
                    Assert.AreSame(closeTask, completed, "CloseAsync ignored the caller's cancellation.");
                    AssertAsync.ThrowsAsync<OperationCanceledException>(() => closeTask);

                    Assert.AreEqual(WebSocketState.Closed, connection.State, "A canceled close left the connection Closing.");
                    Assert.AreEqual(WebSocketCloseCode.AbnormalClosure, connection.CloseStatus.Value.Code);
                    Assert.IsInstanceOf<WebSocketException>(connection.TerminalError);
                    Assert.IsTrue(clientStream.IsDisposed, "The transport was not released.");
                }
                finally
                {
                    connection.Dispose();
                }
            });
        }

        [Test]
        public void PingDuringLargeFragmentedSend_PongGoesBetweenFragments()
        {
            AssertAsync.Run(async () =>
            {
                ControlledClientStream clientStream = null;
                using var peer = new InMemoryWebSocketPeer(inner => clientStream = new ControlledClientStream(inner));

                var options = CreateOptions();
                options.FragmentationThreshold = 1024;

                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    // The first fragment's flush stalls (a full socket buffer) with the message in flight.
                    var stalled = clientStream.StallNextFlush();
                    var sendTask = connection.SendBinaryAsync(new byte[8 * 1024], CancellationToken.None);
                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(stalled, Guard,
                        "The first fragment was not written.").ConfigureAwait(false);

                    await peer.SendFrameAsync(WebSocketOpcode.Ping, new byte[] { 1, 2, 3 }, fin: true)
                        .ConfigureAwait(false);
                    await WaitUntilAsync(() => connection.ControlFramesWaitingForSendLock > 0,
                        "The pong reply never queued for the frame lock.").ConfigureAwait(false);

                    clientStream.ReleaseStall();
                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(sendTask, Guard,
                        "The fragmented send did not finish.").ConfigureAwait(false);

                    var first = await peer.ReadFrameAsync(Guard).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketOpcode.Binary, first.Opcode);
                    Assert.IsFalse(first.IsFinal);

                    var second = await peer.ReadFrameAsync(Guard).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketOpcode.Pong, second.Opcode, "The pong waited behind the whole message.");
                    CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, second.Payload);

                    int continuations = 0;
                    bool sawFinal = false;
                    while (!sawFinal)
                    {
                        var frame = await peer.ReadFrameAsync(Guard).ConfigureAwait(false);
                        Assert.AreEqual(WebSocketOpcode.Continuation, frame.Opcode);
                        continuations++;
                        sawFinal = frame.IsFinal;
                    }

                    Assert.AreEqual(7, continuations);
                    Assert.AreEqual(0, peer.FramingViolations);
                    Assert.AreEqual(WebSocketState.Open, connection.State);
                }
                finally
                {
                    connection.Dispose();
                }
            });
        }

        internal static WebSocketConnectionOptions CreateOptions()
        {
            return WebSocketReceiveBackpressureTests.CreateOptions(capacity: 8);
        }

        internal static async Task WaitUntilAsync(Func<bool> condition, string message)
        {
            // Polls an observable condition; the deadline is an upper bound, not synchronization.
            var deadline = DateTime.UtcNow + Guard;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail(message);

                await Task.Delay(5).ConfigureAwait(false);
            }
        }
    }
}
