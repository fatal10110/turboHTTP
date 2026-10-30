using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #79: a send cancelled after its first byte must never leave an Open connection with a partial
    /// frame or message on the wire (RFC 6455 sections 5.2 and 5.4).
    /// </summary>
    [TestFixture]
    public sealed class WebSocketSendCancellationTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/send-cancel");

        [Test]
        public void CancelAfterFirstFragment_AbortsConnection_AndStrictPeerSeesNoInterleavedMessage()
        {
            AssertAsync.Run(async () =>
            {
                CancelAfterWriteStream clientStream = null;
                using var peer = new InMemoryWebSocketPeer(inner => clientStream = new CancelAfterWriteStream(inner));
                var options = WebSocketReceiveBackpressureTests.CreateOptions(capacity: 4);
                options.FragmentationThreshold = 1024;

                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    using var cts = new CancellationTokenSource();
                    clientStream.Arm(cts, afterFlushes: 1);

                    var payload = new byte[8 * 1024];
                    var sendError = AssertAsync.ThrowsAsync<WebSocketException>(
                        () => connection.SendBinaryAsync(payload, cts.Token));

                    Assert.AreEqual(WebSocketError.SendFailed, sendError.Error);
                    Assert.IsInstanceOf<OperationCanceledException>(sendError.InnerException);
                    AssertAborted(connection);

                    // The next send must not start a new data frame after the unfinished fragment.
                    Assert.Throws<InvalidOperationException>(
                        () => connection.SendTextAsync("next", CancellationToken.None).GetAwaiter().GetResult());

                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(peer.ReaderCompleted,
                        TimeSpan.FromSeconds(5), "Peer did not observe the aborted connection.").ConfigureAwait(false);

                    var first = await peer.ReadFrameAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketOpcode.Binary, first.Opcode);
                    Assert.IsFalse(first.IsFinal);
                    Assert.AreEqual(0, peer.FramingViolations, "Strict peer would have closed with 1002.");
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void CancelMidFrame_TransitionsConnectionToClosed()
        {
            AssertAsync.Run(async () =>
            {
                CancelAfterWriteStream clientStream = null;
                using var peer = new InMemoryWebSocketPeer(inner => clientStream = new CancelAfterWriteStream(inner));
                var options = WebSocketReceiveBackpressureTests.CreateOptions(capacity: 4);

                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    // One unfragmented frame: the header goes out, then the payload write is cancelled.
                    using var cts = new CancellationTokenSource();
                    clientStream.Arm(cts, afterWrites: 1);

                    var payload = new byte[32 * 1024];
                    var sendError = AssertAsync.ThrowsAsync<WebSocketException>(
                        () => connection.SendBinaryAsync(payload, cts.Token));

                    Assert.AreEqual(WebSocketError.SendFailed, sendError.Error);
                    AssertAborted(connection);
                    Assert.AreSame(sendError, connection.TerminalError);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void CancelBeforeFirstByte_KeepsConnectionOpen_AndNextSendIsIntact()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                var options = WebSocketReceiveBackpressureTests.CreateOptions(capacity: 4);

                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    using var cancelled = new CancellationTokenSource();
                    cancelled.Cancel();

                    AssertAsync.ThrowsAsync<OperationCanceledException>(
                        () => connection.SendTextAsync("never", cancelled.Token));

                    Assert.AreEqual(WebSocketState.Open, connection.State);
                    Assert.IsNull(connection.TerminalError);

                    await connection.SendTextAsync("ok", CancellationToken.None).ConfigureAwait(false);

                    var frame = await peer.ReadFrameAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketOpcode.Text, frame.Opcode);
                    Assert.IsTrue(frame.IsFinal);
                    Assert.AreEqual("ok", Encoding.UTF8.GetString(frame.Payload));
                    Assert.AreEqual(0, peer.FramingViolations);

                    await connection.CloseAsync(WebSocketCloseCode.NormalClosure, "done", CancellationToken.None)
                        .ConfigureAwait(false);
                    Assert.AreEqual(WebSocketState.Closed, connection.State);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void WebSocketClient_CancelAfterFirstFragment_ClosesClientWithAbnormalClosure()
        {
            AssertAsync.Run(async () =>
            {
                CancelAfterWriteStream clientStream = null;
                using var peer = new InMemoryWebSocketPeer(inner => clientStream = new CancelAfterWriteStream(inner));
                await using var client = new WebSocketClient(peer);
                var closed = new TaskCompletionSource<WebSocketCloseCode>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnClosed += (code, _) => closed.TrySetResult(code);

                var options = WebSocketReceiveBackpressureTests.CreateOptions(capacity: 4);
                options.FragmentationThreshold = 1024;
                await client.ConnectAsync(TestUri, options, CancellationToken.None).ConfigureAwait(false);

                using var cts = new CancellationTokenSource();
                clientStream.Arm(cts, afterFlushes: 1);

                var sendError = AssertAsync.ThrowsAsync<WebSocketException>(
                    () => client.SendAsync(new byte[8 * 1024], cts.Token));
                Assert.AreEqual(WebSocketError.SendFailed, sendError.Error);

                await WebSocketReceiveBackpressureTests.WaitWithTimeout(closed.Task, TimeSpan.FromSeconds(5),
                    "OnClosed was not raised after the aborted send.").ConfigureAwait(false);

                Assert.AreEqual(WebSocketCloseCode.AbnormalClosure, closed.Task.Result);
                Assert.AreEqual(WebSocketState.Closed, client.State);
            });
        }

        private static void AssertAborted(WebSocketConnection connection)
        {
            Assert.AreEqual(WebSocketState.Closed, connection.State, "A partial message left the connection Open.");
            Assert.IsTrue(connection.CloseStatus.HasValue);
            Assert.AreEqual(WebSocketCloseCode.AbnormalClosure, connection.CloseStatus.Value.Code);
        }
    }
}
