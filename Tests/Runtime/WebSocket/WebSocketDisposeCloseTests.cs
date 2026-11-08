using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #301: DisposeAsync on an Open or Closing connection starts the closing handshake with a 1000 Close
    /// frame (RFC 6455 section 7.1.2) instead of dropping the transport, and a silent peer cannot hold it
    /// past its budget.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketDisposeCloseTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/dispose-close");

        // Guard only: every await finishes long before this, it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        [Test]
        public void DisposeAsync_Open_SendsNormalClosureFrame()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(
                        TestUri, peer, WebSocketCloseSemanticsTests.CreateOptions(), CancellationToken.None)
                        .ConfigureAwait(false);

                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(
                        connection.DisposeAsync().AsTask(), Guard, "DisposeAsync did not complete.")
                        .ConfigureAwait(false);

                    var frame = await peer.ReadFrameAsync(Guard).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketOpcode.Close, frame.Opcode);
                    Assert.GreaterOrEqual(frame.Payload.Length, 2);
                    Assert.AreEqual(1000, (frame.Payload[0] << 8) | frame.Payload[1]);

                    // The peer echoed the Close and closed the transport: a clean handshake, not an abort.
                    Assert.AreEqual(WebSocketState.Closed, connection.State);
                    Assert.AreEqual(WebSocketCloseCode.NormalClosure, connection.CloseStatus.Value.Code);
                    Assert.IsNull(connection.TerminalError);
                }
                finally
                {
                    connection.Dispose();
                }
            });
        }

        [Test]
        public void DisposeAsync_SilentPeer_ReturnsWithinBudgetAndCloses()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer { ReplyToClose = false };

                // Without the dispose budget the close would wait for this timeout, far past the guard.
                var options = WebSocketCloseSemanticsTests.CreateOptions();
                options.CloseHandshakeTimeout = TimeSpan.FromSeconds(60);

                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(
                        connection.DisposeAsync().AsTask(), Guard,
                        "DisposeAsync waited for a peer that never answers.")
                        .ConfigureAwait(false);

                    Assert.AreEqual(WebSocketState.Closed, connection.State);

                    var frame = await peer.ReadFrameAsync(Guard).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketOpcode.Close, frame.Opcode);
                    Assert.AreEqual(1000, (frame.Payload[0] << 8) | frame.Payload[1]);
                }
                finally
                {
                    connection.Dispose();
                }
            });
        }

        [Test]
        public void DisposeAsync_WhileCloseInFlight_CompletesBothAndCloses()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer { ReplyToClose = false };
                var options = WebSocketCloseSemanticsTests.CreateOptions();
                options.CloseHandshakeTimeout = TimeSpan.FromSeconds(60);

                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    // The user's close sent its frame and is waiting (Closing) for a reply that never comes.
                    var closeTask = connection.CloseAsync(WebSocketCloseCode.NormalClosure, "bye", CancellationToken.None);
                    var frame = await peer.ReadFrameAsync(Guard).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketOpcode.Close, frame.Opcode);
                    Assert.AreEqual(WebSocketState.Closing, connection.State);

                    // Dispose sends no second Close frame, gives up after its budget and releases the close.
                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(
                        connection.DisposeAsync().AsTask(), Guard, "DisposeAsync did not complete.")
                        .ConfigureAwait(false);
                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(
                        closeTask, Guard, "CloseAsync was not released by DisposeAsync.")
                        .ConfigureAwait(false);

                    Assert.AreEqual(WebSocketState.Closed, connection.State);

                    // The silent peer keeps reading until the transport closes, so a second Close would be here.
                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(
                        peer.ReaderCompleted, Guard, "The peer reader did not see the transport close.")
                        .ConfigureAwait(false);
                    try
                    {
                        await peer.ReadFrameAsync(TimeSpan.Zero).ConfigureAwait(false);
                        Assert.Fail("DisposeAsync sent a second Close frame.");
                    }
                    catch (TimeoutException)
                    {
                    }
                }
                finally
                {
                    connection.Dispose();
                }
            });
        }
    }
}
