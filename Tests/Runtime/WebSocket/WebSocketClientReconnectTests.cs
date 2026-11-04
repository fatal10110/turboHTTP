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
    /// #208: reconnecting the same <see cref="WebSocketClient"/> must not let the previous receive
    /// listener complete the new message queue. The old listener is held inside <c>OnMessage</c>
    /// while the client reconnects, then released: on the old code it completes the new queue as
    /// it exits, so the new connection's message never reaches <c>ReceiveAsync</c>.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketClientReconnectTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/reconnect");

        // Guard only: every await finishes long before this, it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        [Test]
        public void ReconnectFromOnClosed_NewConnectionReceivesMessages()
        {
            AssertAsync.Run(async () =>
            {
                using var transport = new PeerPerConnectTransport();
                await using var client = new WebSocketClient(transport);
                using var listener = new BlockingFirstMessageHandler();
                client.OnMessage += listener.Handle;

                var options = CreateOptions();
                var reconnect = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnClosed += (code, reason) =>
                {
                    if (!reconnect.Task.IsCompleted)
                        reconnect.TrySetResult(client.ConnectAsync(TestUri, options, CancellationToken.None));
                };

                await client.ConnectAsync(TestUri, options, CancellationToken.None);
                await transport.Peer(0).SendTextAsync("old");
                await AwaitGuarded(listener.FirstMessageEntered, "the first listener never delivered its message");

                // The peer closes while the first listener is still inside OnMessage.
                await transport.Peer(0).SendFrameAsync(WebSocketOpcode.Close, new byte[] { 0x03, 0xE8 }, fin: true);
                var reconnectTask = await AwaitGuarded(reconnect.Task, "OnClosed was not raised for the peer close");

                await listener.ReleaseAsync();
                await AwaitGuarded(reconnectTask, "reconnect from OnClosed did not complete");

                await AssertNewConnectionUsableAsync(client, transport.Peer(1));
            });
        }

        [Test]
        public void AbortThenConnect_NewConnectionReceivesMessages()
        {
            AssertAsync.Run(async () =>
            {
                using var transport = new PeerPerConnectTransport();
                await using var client = new WebSocketClient(transport);
                using var listener = new BlockingFirstMessageHandler();
                client.OnMessage += listener.Handle;
                var options = CreateOptions();

                await client.ConnectAsync(TestUri, options, CancellationToken.None);
                await transport.Peer(0).SendTextAsync("old");
                await AwaitGuarded(listener.FirstMessageEntered, "the first listener never delivered its message");

                client.Abort();
                Assert.AreEqual(WebSocketState.Closed, client.State);

                var reconnectTask = client.ConnectAsync(TestUri, options, CancellationToken.None);
                await listener.ReleaseAsync();
                await AwaitGuarded(reconnectTask, "reconnect after Abort did not complete");

                await AssertNewConnectionUsableAsync(client, transport.Peer(1));
            });
        }

        private static async Task AssertNewConnectionUsableAsync(WebSocketClient client, InMemoryWebSocketPeer peer)
        {
            Assert.AreEqual(WebSocketState.Open, client.State);

            await peer.SendTextAsync("new");
            using var message = await AwaitGuarded(
                client.ReceiveAsync(CancellationToken.None).AsTask(),
                "the new connection's message was not received");

            Assert.AreEqual("new", message.Text);
            Assert.AreEqual(WebSocketState.Open, client.State);
        }

        private static async Task AwaitGuarded(Task task, string message)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Guard));
            Assert.AreSame(task, winner, message);
            await task;
        }

        private static async Task<T> AwaitGuarded<T>(Task<T> task, string message)
        {
            await AwaitGuarded((Task)task, message);
            return task.Result;
        }

        private static WebSocketConnectionOptions CreateOptions()
        {
            return new WebSocketConnectionOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(5),
                CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                PingInterval = TimeSpan.Zero,
                PongTimeout = TimeSpan.FromMilliseconds(200),
                ReceiveQueueCapacity = 4
            };
        }

        /// <summary>Holds the listener inside the first <c>OnMessage</c> call until released.</summary>
        private sealed class BlockingFirstMessageHandler : IDisposable
        {
            private readonly TaskCompletionSource<bool> _entered =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _exited =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
            private int _calls;

            public Task FirstMessageEntered => _entered.Task;

            public void Handle(WebSocketMessage message)
            {
                if (Interlocked.Increment(ref _calls) != 1)
                    return;

                _entered.TrySetResult(true);

                // Bounded: the test releases this right after starting the reconnect.
                _release.Wait(Guard);

                // The listener goes on synchronously from here (the old connection is closed, so its
                // next receive fails at once), while the test resumes on another thread.
                _exited.TrySetResult(true);
            }

            /// <summary>Releases the held listener and waits until its OnMessage call returned.</summary>
            public Task ReleaseAsync()
            {
                _release.Set();
                return AwaitGuarded(_exited.Task, "the held listener did not return from OnMessage");
            }

            public void Dispose()
            {
                _release.Set();
                _release.Dispose();
            }
        }

        /// <summary>A fresh <see cref="InMemoryWebSocketPeer"/> per connect.</summary>
        private sealed class PeerPerConnectTransport : IWebSocketTransport
        {
            private readonly object _gate = new object();
            private readonly List<InMemoryWebSocketPeer> _peers = new List<InMemoryWebSocketPeer>();

            public InMemoryWebSocketPeer Peer(int index)
            {
                lock (_gate)
                    return _peers[index];
            }

            public Task<Stream> ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)
            {
                var peer = new InMemoryWebSocketPeer();
                lock (_gate)
                    _peers.Add(peer);
                return peer.ConnectAsync(uri, options, ct);
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    foreach (var peer in _peers)
                        peer.Dispose();
                    _peers.Clear();
                }
            }
        }
    }
}
