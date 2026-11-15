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
    /// #1105: an app-initiated CloseAsync or Abort while the resilient client is reconnecting ends the
    /// connection for good. The backoff delay and the replacement connect are cancelled, CloseAsync returns
    /// without waiting for the retry budget, no replacement is published and OnReconnected never fires.
    /// Before the fix the loop kept going: CloseAsync blocked for the whole backoff (forever with an
    /// unreachable server) and a late replacement went live after OnClosed.
    /// </summary>
    [TestFixture]
    public sealed class ResilientCloseDuringBackoffTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/close-during-backoff");

        // Guard only: every await finishes long before this, it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        // Longer than the guard: a client that waits for this backoff fails the test instead of passing late.
        private static readonly TimeSpan LongBackoff = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan ShortBackoff = TimeSpan.FromMilliseconds(1);

        private static readonly FieldInfo ReceivePumpField =
            typeof(ResilientWebSocketClient).GetField("_receivePumpTask", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo ClientField =
            typeof(ResilientWebSocketClient).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo ManualCloseField =
            typeof(ResilientWebSocketClient).GetField("_manualClose", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo ReconnectCtsField =
            typeof(ResilientWebSocketClient).GetField("_reconnectCts", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo ChildDisposedField =
            typeof(WebSocketClient).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);

        // 1001 Going Away is a reconnect close code.
        private static readonly byte[] GoingAway = { 0x03, 0xE9 };

        [Test]
        public void CloseAsync_DuringBackoffDelay_ReturnsWithoutWaitingAndDoesNotReconnect()
        {
            AssertAsync.Run(async () =>
            {
                using var transport = new CountingTransport();
                await using var client = new ResilientWebSocketClient(transport);
                var events = new EventLog(client);
                var reconnecting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnReconnecting += (_, _) => reconnecting.TrySetResult(true);

                await client.ConnectAsync(TestUri, CreateOptions(LongBackoff), CancellationToken.None)
                    .ConfigureAwait(false);
                var initialPump = CurrentReceivePump(client);

                await transport.Peer(0).SendFrameAsync(WebSocketOpcode.Close, GoingAway, fin: true).ConfigureAwait(false);
                await WebSocketReceiveBackpressureTests.WaitWithTimeout(reconnecting.Task, Guard,
                    "The client did not enter its reconnect backoff.").ConfigureAwait(false);

                await WebSocketReceiveBackpressureTests.WaitWithTimeout(client.CloseAsync(), Guard,
                    "CloseAsync waited for the reconnect backoff instead of cancelling it.").ConfigureAwait(false);
                await WaitForReconnectLoopExit(initialPump).ConfigureAwait(false);

                events.AssertClosedOnce(WebSocketCloseCode.NormalClosure);
                AssertNoReconnect(client, transport, events, expectedConnects: 1);
            });
        }

        [Test]
        public void CloseAsync_DuringBackoffOnNonDefaultSynchronizationContext_ClosesOnceAndEndsReceive()
        {
            // Under a non-default SynchronizationContext (the Unity main thread) the cancelled backoff resumes on
            // the thread pool instead of inline, so the loop exits while CloseAsync is still closing the child it
            // took. The loop used to dispose that child too, and CloseAsync then failed with
            // ObjectDisposedException before completing the queue and raising OnClosed. The race is timing and
            // runtime dependent (it may not reproduce at all), so this covers the end-to-end contract; the
            // deterministic regression check is the ownership test below.
            AssertAsync.Run(async () =>
            {
                for (int iteration = 0; iteration < 20; iteration++)
                {
                    using var transport = new CountingTransport();
                    await using var client = new ResilientWebSocketClient(transport);
                    var events = new EventLog(client);
                    var reconnecting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    client.OnReconnecting += (_, _) => reconnecting.TrySetResult(true);

                    await client.ConnectAsync(TestUri, CreateOptions(LongBackoff), CancellationToken.None)
                        .ConfigureAwait(false);
                    var initialPump = CurrentReceivePump(client);
                    var receive = client.ReceiveAsync().AsTask();

                    await transport.Peer(0).SendFrameAsync(WebSocketOpcode.Close, GoingAway, fin: true)
                        .ConfigureAwait(false);
                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(reconnecting.Task, Guard,
                        "The client did not enter its reconnect backoff.").ConfigureAwait(false);

                    Task close;
                    var previousContext = SynchronizationContext.Current;
                    SynchronizationContext.SetSynchronizationContext(new NonInliningSynchronizationContext());
                    try
                    {
                        close = client.CloseAsync();
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(previousContext);
                    }

                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(close, Guard,
                        "CloseAsync did not complete.").ConfigureAwait(false);
                    await WaitForReconnectLoopExit(initialPump).ConfigureAwait(false);

                    events.AssertClosedOnce(WebSocketCloseCode.NormalClosure);
                    try
                    {
                        await WebSocketReceiveBackpressureTests.WaitWithTimeout(receive, Guard,
                            "The parked ReceiveAsync did not end after CloseAsync.").ConfigureAwait(false);
                        Assert.Fail("Expected ConnectionClosed from the parked ReceiveAsync, got a message.");
                    }
                    catch (WebSocketException ex)
                    {
                        Assert.AreEqual(WebSocketError.ConnectionClosed, ex.Error);
                    }
                    AssertNoReconnect(client, transport, events, expectedConnects: 1);
                }
            });
        }

        [Test]
        public void ReconnectLoop_EndedByCallerClose_DoesNotDisposeTheChildTheCloserTook()
        {
            // Deterministic form of the race above: the closer's first steps (flag, cancel, take the child) are
            // replayed and the child is held as a CloseAsync in flight would hold it. The reconnect loop exits
            // and must leave that child to its owner.
            AssertAsync.Run(async () =>
            {
                using var transport = new CountingTransport();
                await using var client = new ResilientWebSocketClient(transport);
                var reconnecting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnReconnecting += (_, _) => reconnecting.TrySetResult(true);

                await client.ConnectAsync(TestUri, CreateOptions(LongBackoff), CancellationToken.None)
                    .ConfigureAwait(false);
                var initialPump = CurrentReceivePump(client);

                await transport.Peer(0).SendFrameAsync(WebSocketOpcode.Close, GoingAway, fin: true).ConfigureAwait(false);
                await WebSocketReceiveBackpressureTests.WaitWithTimeout(reconnecting.Task, Guard,
                    "The client did not enter its reconnect backoff.").ConfigureAwait(false);

                ManualCloseField.SetValue(client, 1);
                var reconnectCts = (CancellationTokenSource)ReconnectCtsField.GetValue(client);
                Assert.IsNotNull(reconnectCts, "The reconnect loop must be in its backoff.");
                var child = (WebSocketClient)ClientField.GetValue(client);
                Assert.IsNotNull(child, "The dropped child must still be the current connection.");
                ClientField.SetValue(client, null);
                reconnectCts.Cancel();

                try
                {
                    await WaitForReconnectLoopExit(initialPump).ConfigureAwait(false);
                    Assert.AreEqual(0, (int)ChildDisposedField.GetValue(child),
                        "The reconnect loop disposed a child that the closer had taken and still owned.");
                }
                finally
                {
                    child.Dispose();
                }
            });
        }

        [Test]
        public void CloseAsync_InsideOnReconnecting_DoesNotReconnect()
        {
            AssertAsync.Run(async () =>
            {
                using var transport = new CountingTransport();
                await using var client = new ResilientWebSocketClient(transport);
                var events = new EventLog(client);
                var closeStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnReconnecting += (_, _) => closeStarted.TrySetResult(client.CloseAsync());

                await client.ConnectAsync(TestUri, CreateOptions(ShortBackoff), CancellationToken.None)
                    .ConfigureAwait(false);
                var initialPump = CurrentReceivePump(client);

                await transport.Peer(0).SendFrameAsync(WebSocketOpcode.Close, GoingAway, fin: true).ConfigureAwait(false);
                await WebSocketReceiveBackpressureTests.WaitWithTimeout(closeStarted.Task, Guard,
                    "The client did not enter its reconnect backoff.").ConfigureAwait(false);
                await WebSocketReceiveBackpressureTests.WaitWithTimeout(closeStarted.Task.Result, Guard,
                    "CloseAsync called from OnReconnecting did not complete.").ConfigureAwait(false);
                await WaitForReconnectLoopExit(initialPump).ConfigureAwait(false);

                events.AssertClosedOnce(WebSocketCloseCode.NormalClosure);
                AssertNoReconnect(client, transport, events, expectedConnects: 1);
            });
        }

        [Test]
        public void Abort_InsideOnReconnecting_DoesNotReconnect()
        {
            AssertAsync.Run(async () =>
            {
                using var transport = new CountingTransport();
                await using var client = new ResilientWebSocketClient(transport);
                var events = new EventLog(client);
                client.OnReconnecting += (_, _) => client.Abort();

                await client.ConnectAsync(TestUri, CreateOptions(ShortBackoff), CancellationToken.None)
                    .ConfigureAwait(false);
                var initialPump = CurrentReceivePump(client);

                await transport.Peer(0).SendFrameAsync(WebSocketOpcode.Close, GoingAway, fin: true).ConfigureAwait(false);
                await WaitForReconnectLoopExit(initialPump).ConfigureAwait(false);

                Assert.AreEqual(1, events.ReconnectingCount, "The loop must have reached its backoff.");
                events.AssertClosedOnce(WebSocketCloseCode.AbnormalClosure);
                AssertNoReconnect(client, transport, events, expectedConnects: 1);
            });
        }

        [Test]
        public void CloseAsync_DuringReplacementConnect_CancelsTheConnectAndReturns()
        {
            AssertAsync.Run(() => CloseDuringReplacementConnect(abort: false));
        }

        [Test]
        public void Abort_DuringReplacementConnect_CancelsTheConnect()
        {
            AssertAsync.Run(() => CloseDuringReplacementConnect(abort: true));
        }

        private static async Task CloseDuringReplacementConnect(bool abort)
        {
            // The replacement's server never answers: only cancellation ends the connect.
            using var transport = new CountingTransport { HoldReconnectsUntilCancelled = true };
            await using var client = new ResilientWebSocketClient(transport);
            var events = new EventLog(client);

            await client.ConnectAsync(TestUri, CreateOptions(ShortBackoff), CancellationToken.None)
                .ConfigureAwait(false);
            var initialPump = CurrentReceivePump(client);

            await transport.Peer(0).SendFrameAsync(WebSocketOpcode.Close, GoingAway, fin: true).ConfigureAwait(false);
            await WebSocketReceiveBackpressureTests.WaitWithTimeout(transport.ReconnectStarted.Task, Guard,
                "The replacement connect did not start.").ConfigureAwait(false);

            if (abort)
            {
                client.Abort();
            }
            else
            {
                await WebSocketReceiveBackpressureTests.WaitWithTimeout(client.CloseAsync(), Guard,
                    "CloseAsync waited for the replacement connect instead of cancelling it.").ConfigureAwait(false);
            }

            await WebSocketReceiveBackpressureTests.WaitWithTimeout(transport.ReconnectCancelled.Task, Guard,
                "The replacement connect was not cancelled.").ConfigureAwait(false);
            await WaitForReconnectLoopExit(initialPump).ConfigureAwait(false);

            events.AssertClosedOnce(abort ? WebSocketCloseCode.AbnormalClosure : WebSocketCloseCode.NormalClosure);
            AssertNoReconnect(client, transport, events, expectedConnects: 2);
        }

        private static void AssertNoReconnect(
            ResilientWebSocketClient client,
            CountingTransport transport,
            EventLog events,
            int expectedConnects)
        {
            Assert.AreEqual(0, events.ReconnectedCount, "OnReconnected must not fire after a caller's close.");
            Assert.AreEqual(expectedConnects, transport.ConnectCount, "No connection may be opened after the close.");
            Assert.AreEqual(1, events.ReconnectingCount, "No further reconnect attempt may start after the close.");

            var state = client.State;
            Assert.AreNotEqual(WebSocketState.Open, state, "No live connection may remain after the close.");
            Assert.AreNotEqual(WebSocketState.Connecting, state, "The client must not stay in its reconnecting state.");
            var send = client.SendAsync("after-close");
            Assert.IsTrue(send.IsFaulted && send.Exception?.InnerException is InvalidOperationException,
                "SendAsync must not reach a connection after the close.");
        }

        private static Task CurrentReceivePump(ResilientWebSocketClient client)
        {
            var pump = (Task)ReceivePumpField.GetValue(client);
            Assert.IsNotNull(pump, "The receive pump must be running after ConnectAsync.");
            return pump;
        }

        // The reconnect loop runs on the initial connection's pump; it has fully exited once that pump completes.
        private static Task WaitForReconnectLoopExit(Task initialPump)
        {
            return WebSocketReceiveBackpressureTests.WaitWithTimeout(initialPump, Guard,
                "The reconnect loop did not exit after the caller's close.");
        }

        private static WebSocketConnectionOptions CreateOptions(TimeSpan backoff)
        {
            var options = WebSocketReceiveBackpressureTests.CreateOptions(capacity: 32);
            options.CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500);
            return options.WithReconnection(new WebSocketReconnectPolicy(
                maxRetries: -1,
                initialDelay: backoff,
                maxDelay: backoff,
                backoffMultiplier: 1.0,
                jitterFactor: 0.0));
        }

        /// <summary>
        /// Any SynchronizationContext subclass stops awaits with ConfigureAwait(false) from resuming inline on the
        /// thread that completes the awaited task, as on the Unity main thread.
        /// </summary>
        private sealed class NonInliningSynchronizationContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state)
            {
                ThreadPool.QueueUserWorkItem(_ => d(state));
            }
        }

        private sealed class EventLog
        {
            private int _reconnecting;
            private int _reconnected;
            private int _closed;
            private int _closedCode = -1;

            public EventLog(ResilientWebSocketClient client)
            {
                client.OnReconnecting += (_, _) => Interlocked.Increment(ref _reconnecting);
                client.OnReconnected += () => Interlocked.Increment(ref _reconnected);
                client.OnClosed += (code, _) =>
                {
                    if (Interlocked.Increment(ref _closed) == 1)
                        Volatile.Write(ref _closedCode, (int)code);
                };
            }

            public int ReconnectingCount => Volatile.Read(ref _reconnecting);

            public int ReconnectedCount => Volatile.Read(ref _reconnected);

            public void AssertClosedOnce(WebSocketCloseCode expected)
            {
                Assert.AreEqual(1, Volatile.Read(ref _closed), "OnClosed must be raised exactly once.");
                Assert.AreEqual((int)expected, Volatile.Read(ref _closedCode),
                    "OnClosed must carry the caller's close, not the dropped connection's.");
            }
        }

        private sealed class CountingTransport : IWebSocketTransport
        {
            private readonly object _gate = new object();
            private readonly List<InMemoryWebSocketPeer> _peers = new List<InMemoryWebSocketPeer>();
            private int _connectCount;

            public bool HoldReconnectsUntilCancelled { get; set; }

            public TaskCompletionSource<bool> ReconnectStarted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<bool> ReconnectCancelled { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public int ConnectCount => Volatile.Read(ref _connectCount);

            public InMemoryWebSocketPeer Peer(int index)
            {
                lock (_gate)
                    return _peers[index];
            }

            public async Task<Stream> ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)
            {
                int index = Interlocked.Increment(ref _connectCount) - 1;
                if (index > 0 && HoldReconnectsUntilCancelled)
                {
                    var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (ct.Register(() => cancelled.TrySetResult(true)))
                    {
                        ReconnectStarted.TrySetResult(true);
                        await cancelled.Task.ConfigureAwait(false);
                    }

                    ReconnectCancelled.TrySetResult(true);
                    ct.ThrowIfCancellationRequested();
                }

                var peer = new InMemoryWebSocketPeer();
                lock (_gate)
                    _peers.Add(peer);
                return await peer.ConnectAsync(uri, options, ct).ConfigureAwait(false);
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
