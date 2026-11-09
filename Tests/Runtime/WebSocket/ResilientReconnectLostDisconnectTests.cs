using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #417: the replacement connection drops while the reconnect loop that created it is still finishing
    /// (here: inside OnReconnected, after the replacement's receive pump has started). That disconnect must
    /// not be lost: the client reconnects again, or completes the receive queue and raises OnClosed once
    /// the retries are exhausted. Before the fix the client hung with no reconnect and no close.
    /// </summary>
    [TestFixture]
    public sealed class ResilientReconnectLostDisconnectTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/reconnect-lost-disconnect");

        // Guard only: every await finishes long before this, it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        private static readonly FieldInfo ReceivePumpField =
            typeof(ResilientWebSocketClient).GetField("_receivePumpTask", BindingFlags.Instance | BindingFlags.NonPublic);

        [Test]
        public void ReplacementDropsBeforeReconnectLoopEnds_RetriesExhausted_CompletesQueueAndRaisesClosed()
        {
            AssertAsync.Run(async () =>
            {
                using var transport = new PeerPerConnectTransport();
                await using var client = new ResilientWebSocketClient(transport);

                var closed = new TaskCompletionSource<WebSocketCloseCode>(TaskCreationOptions.RunContinuationsAsynchronously);
                int closedCount = 0;
                client.OnClosed += (code, _) =>
                {
                    Interlocked.Increment(ref closedCount);
                    closed.TrySetResult(code);
                };

                var reconnectingAttempts = new List<int>();
                client.OnReconnecting += (attempt, _) =>
                {
                    lock (reconnectingAttempts)
                        reconnectingAttempts.Add(attempt);
                };

                int reconnectedCount = 0;
                client.OnReconnected += () =>
                {
                    if (Interlocked.Increment(ref reconnectedCount) == 1)
                        DropReplacementInsideReconnectLoop(client, transport.Peer(1));
                };

                await client.ConnectAsync(TestUri, CreateOptions(maxRetries: 1), CancellationToken.None)
                    .ConfigureAwait(false);

                await using var enumerator = client.ReceiveAllAsync(CancellationToken.None).GetAsyncEnumerator();
                var moveNext = enumerator.MoveNextAsync().AsTask();

                // 1001 Going Away is a reconnect close code: attempt 1 connects the replacement (peer 1).
                await transport.Peer(0).SendFrameAsync(WebSocketOpcode.Close, new byte[] { 0x03, 0xE9 }, fin: true)
                    .ConfigureAwait(false);

                await WebSocketReceiveBackpressureTests.WaitWithTimeout(closed.Task, Guard,
                    "The replacement's disconnect was lost: no OnClosed after the retries were exhausted.")
                    .ConfigureAwait(false);
                await WebSocketReceiveBackpressureTests.WaitWithTimeout(moveNext, Guard,
                    "ReceiveAllAsync did not complete after the retries were exhausted.").ConfigureAwait(false);

                Assert.IsFalse(moveNext.Result, "The receive stream must end once the retries are exhausted.");
                Assert.AreEqual(1, Volatile.Read(ref closedCount), "OnClosed must be raised exactly once.");
                Assert.AreEqual(1, Volatile.Read(ref reconnectedCount));
                lock (reconnectingAttempts)
                    CollectionAssert.AreEqual(new[] { 1 }, reconnectingAttempts,
                        "The replacement's drop counts as attempt 2, which maxRetries 1 refuses.");
                Assert.AreNotEqual(WebSocketState.Connecting, client.State,
                    "The client must not stay in its reconnecting state.");
            });
        }

        [Test]
        public void ReplacementDropsBeforeReconnectLoopEnds_RetriesLeft_ReconnectsAgain()
        {
            AssertAsync.Run(async () =>
            {
                using var transport = new PeerPerConnectTransport();
                await using var client = new ResilientWebSocketClient(transport);

                var reconnectingAttempts = new List<int>();
                client.OnReconnecting += (attempt, _) =>
                {
                    lock (reconnectingAttempts)
                        reconnectingAttempts.Add(attempt);
                };

                var reconnectedTwice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int reconnectedCount = 0;
                client.OnReconnected += () =>
                {
                    int count = Interlocked.Increment(ref reconnectedCount);
                    if (count == 1)
                        DropReplacementInsideReconnectLoop(client, transport.Peer(1));
                    else if (count == 2)
                        reconnectedTwice.TrySetResult(true);
                };

                await client.ConnectAsync(TestUri, CreateOptions(maxRetries: 3), CancellationToken.None)
                    .ConfigureAwait(false);

                await transport.Peer(0).SendFrameAsync(WebSocketOpcode.Close, new byte[] { 0x03, 0xE9 }, fin: true)
                    .ConfigureAwait(false);

                await WebSocketReceiveBackpressureTests.WaitWithTimeout(reconnectedTwice.Task, Guard,
                    "The replacement's disconnect was lost: the client did not reconnect again.").ConfigureAwait(false);

                // The second replacement (peer 2) is live: its messages reach the caller.
                await transport.Peer(2).SendTextAsync("after-second-reconnect").ConfigureAwait(false);
                var receive = client.ReceiveAsync(CancellationToken.None).AsTask();
                await WebSocketReceiveBackpressureTests.WaitWithTimeout(receive, Guard,
                    "No message arrived over the second replacement.").ConfigureAwait(false);
                using var message = receive.Result;

                Assert.AreEqual("after-second-reconnect", message.Text);
                Assert.AreEqual(WebSocketState.Open, client.State);
                lock (reconnectingAttempts)
                    CollectionAssert.AreEqual(new[] { 1, 2 }, reconnectingAttempts,
                        "A replacement that drops right after its reconnect counts as the next attempt.");
            });
        }

        /// <summary>
        /// Runs inside OnReconnected, which the reconnect loop raises after the replacement's receive pump has
        /// started and before the loop finishes. Closes the replacement's transport (EOF) and waits until that
        /// pump has handled the disconnect, so the drop deterministically lands inside the loop's window.
        /// </summary>
        private static void DropReplacementInsideReconnectLoop(ResilientWebSocketClient client, InMemoryWebSocketPeer replacementPeer)
        {
            var replacementPump = (Task)ReceivePumpField.GetValue(client);
            Assert.IsNotNull(replacementPump, "The replacement's receive pump must be running before OnReconnected.");

            replacementPeer.CloseTransport();

            if (!replacementPump.Wait(Guard))
                Assert.Fail("The replacement's receive pump did not observe the dropped transport.");
        }

        private static WebSocketConnectionOptions CreateOptions(int maxRetries)
        {
            var options = WebSocketReceiveBackpressureTests.CreateOptions(capacity: 32);
            options.CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500);
            return options.WithReconnection(new WebSocketReconnectPolicy(
                maxRetries: maxRetries,
                initialDelay: TimeSpan.FromMilliseconds(1),
                maxDelay: TimeSpan.FromMilliseconds(5),
                backoffMultiplier: 1.0,
                jitterFactor: 0.0));
        }

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
