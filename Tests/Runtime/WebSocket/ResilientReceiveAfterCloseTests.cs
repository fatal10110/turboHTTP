using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #1106: the resilient client keeps the #217 contract of <see cref="WebSocketReceiveAfterCloseTests"/>.
    /// When the connection ends on its own (the peer closes and no reconnect follows, or the retries run
    /// out), messages already queued for a pull-only consumer stay readable; ReceiveAsync reports
    /// ConnectionClosed only after them. Before the fix the terminal path drained and discarded them.
    /// </summary>
    [TestFixture]
    public sealed class ResilientReceiveAfterCloseTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/resilient-receive-after-close");

        private const int MessageCount = 5;

        // Guard only: every await finishes long before this, it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        [Test]
        public void ReceiveAsync_MessagesAheadOfPeerClose_AreDeliveredAfterClosed()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                await using var client = new ResilientWebSocketClient(peer);
                var closed = WatchClosed(client);

                await client.ConnectAsync(TestUri, new WebSocketConnectionOptions(), CancellationToken.None)
                    .ConfigureAwait(false);

                // Nobody pulls until OnClosed: every message is queued when the connection ends.
                await peer.SendRawAsync(MessagesThenClose(0x03, 0xE8)).ConfigureAwait(false);

                await WebSocketReceiveBackpressureTests.WaitWithTimeout(closed.Task, Guard,
                    "The peer's close frame was not processed.").ConfigureAwait(false);
                Assert.AreEqual(WebSocketCloseCode.NormalClosure, closed.Task.Result);

                await AssertAllMessagesThenClosed(client).ConfigureAwait(false);
            });
        }

        [Test]
        public void ReceiveAsync_MessagesAheadOfDrop_AreDeliveredAfterRetriesExhausted()
        {
            AssertAsync.Run(async () =>
            {
                using var transport = new FirstConnectOnlyTransport();
                await using var client = new ResilientWebSocketClient(transport);
                var closed = WatchClosed(client);

                var options = WebSocketReceiveBackpressureTests.CreateOptions(capacity: 32);
                options.CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500);
                options = options.WithReconnection(new WebSocketReconnectPolicy(
                    maxRetries: 1,
                    initialDelay: TimeSpan.FromMilliseconds(1),
                    maxDelay: TimeSpan.FromMilliseconds(1),
                    backoffMultiplier: 1.0,
                    jitterFactor: 0.0));

                await client.ConnectAsync(TestUri, options, CancellationToken.None).ConfigureAwait(false);

                // 1001 Going Away is a reconnect close code; the one allowed reconnect fails.
                await transport.FirstPeer.SendRawAsync(MessagesThenClose(0x03, 0xE9)).ConfigureAwait(false);

                await WebSocketReceiveBackpressureTests.WaitWithTimeout(closed.Task, Guard,
                    "OnClosed was not raised after the retries were exhausted.").ConfigureAwait(false);
                Assert.AreEqual(2, transport.ConnectCount, "Exactly one reconnect attempt is allowed.");

                await AssertAllMessagesThenClosed(client).ConfigureAwait(false);
            });
        }

        private static TaskCompletionSource<WebSocketCloseCode> WatchClosed(ResilientWebSocketClient client)
        {
            var closed = new TaskCompletionSource<WebSocketCloseCode>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnClosed += (code, _) => closed.TrySetResult(code);
            return closed;
        }

        private static async Task AssertAllMessagesThenClosed(ResilientWebSocketClient client)
        {
            for (int i = 0; i < MessageCount; i++)
            {
                var receive = client.ReceiveAsync().AsTask();
                await WebSocketReceiveBackpressureTests.WaitWithTimeout(receive, Guard, "Message m" + i + " was lost.")
                    .ConfigureAwait(false);
                using var message = receive.Result;
                Assert.AreEqual("m" + i, message.Text);
            }

            try
            {
                using var extra = await client.ReceiveAsync().ConfigureAwait(false);
                Assert.Fail("Expected ConnectionClosed after the queued messages, got a message.");
            }
            catch (WebSocketException ex)
            {
                Assert.AreEqual(WebSocketError.ConnectionClosed, ex.Error);
            }
        }

        // Server frames (unmasked): text "m0".."m4", then a close frame with the given code.
        private static byte[] MessagesThenClose(byte codeHigh, byte codeLow)
        {
            var bytes = new List<byte>();
            for (int i = 0; i < MessageCount; i++)
            {
                bytes.Add(0x81);
                bytes.Add(0x02);
                bytes.Add((byte)'m');
                bytes.Add((byte)('0' + i));
            }

            bytes.Add(0x88);
            bytes.Add(0x02);
            bytes.Add(codeHigh);
            bytes.Add(codeLow);
            return bytes.ToArray();
        }

        /// <summary>The first connect reaches an in-memory peer; every reconnect fails like an unreachable server.</summary>
        private sealed class FirstConnectOnlyTransport : IWebSocketTransport
        {
            private int _connectCount;

            public InMemoryWebSocketPeer FirstPeer { get; } = new InMemoryWebSocketPeer();

            public int ConnectCount => Volatile.Read(ref _connectCount);

            public Task<Stream> ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)
            {
                if (Interlocked.Increment(ref _connectCount) == 1)
                    return FirstPeer.ConnectAsync(uri, options, ct);

                return Task.FromException<Stream>(new IOException("Connection refused (test)."));
            }

            public void Dispose()
            {
                FirstPeer.Dispose();
            }
        }
    }
}
