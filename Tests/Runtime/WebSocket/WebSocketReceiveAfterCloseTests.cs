using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #217: the Autobahn fuzzing server answers /getCaseCount with a text frame and its close frame in one
    /// write. Messages that arrive ahead of the peer's close frame stay readable after the connection
    /// reaches Closed; only then does ReceiveAsync report ConnectionClosed with the peer's close code.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketReceiveAfterCloseTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/receive-after-close");

        // Server frames (unmasked): text "m0", text "m1", text "m2", close 1000.
        private static readonly byte[] MessagesThenClose =
        {
            0x81, 0x02, (byte)'m', (byte)'0',
            0x81, 0x02, (byte)'m', (byte)'1',
            0x81, 0x02, (byte)'m', (byte)'2',
            0x88, 0x02, 0x03, 0xE8,
        };

        [Test]
        public void ReceiveAsync_MessagesAheadOfPeerClose_AreDeliveredAfterClosed()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                await using var client = new WebSocketClient(peer);
                var closed = new TaskCompletionSource<WebSocketCloseCode>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnClosed += (code, _) => closed.TrySetResult(code);

                await client.ConnectAsync(TestUri, new WebSocketConnectionOptions(), CancellationToken.None)
                    .ConfigureAwait(false);
                await peer.SendRawAsync(MessagesThenClose).ConfigureAwait(false);

                var closeCode = await WaitAsync(closed.Task, "The peer's close frame was not processed.")
                    .ConfigureAwait(false);
                Assert.AreEqual(WebSocketCloseCode.NormalClosure, closeCode);
                Assert.AreEqual(WebSocketState.Closed, client.State);

                for (int i = 0; i < 3; i++)
                {
                    using var message = await WaitAsync(client.ReceiveAsync().AsTask(), "Message m" + i + " was lost.")
                        .ConfigureAwait(false);
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
                    Assert.AreEqual(WebSocketCloseCode.NormalClosure, ex.CloseCode);
                }
            });
        }

        private static async Task<T> WaitAsync<T>(Task<T> task, string failure)
        {
            var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            if (winner != task)
                Assert.Fail(failure);
            return await task.ConfigureAwait(false);
        }
    }
}
