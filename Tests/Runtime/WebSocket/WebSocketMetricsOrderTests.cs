using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// A received message is counted in <see cref="WebSocketMetrics.MessagesReceived"/> before
    /// <see cref="WebSocketConnection.ReceiveAsync"/> can return it (#1271). The receive loop is held right after
    /// the enqueue by <see cref="WebSocketConnection.MessageEnqueuedTestHook"/>, so a metric recorded only after
    /// the enqueue is deterministically missing when the consumer reads it.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketMetricsOrderTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/metrics-order");
        private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

        [Test]
        public void ReceiveAsync_ReturnedMessage_IsAlreadyCountedInMetrics()
        {
            AssertAsync.Run(async () =>
            {
                var gate = new EnqueueGate(blockFromCall: 1);
                using var peer = new InMemoryWebSocketPeer();
                var connection = new WebSocketConnection { MessageEnqueuedTestHook = gate.OnEnqueued };
                try
                {
                    await connection.ConnectAsync(TestUri, peer, CreateOptions(), CancellationToken.None)
                        .ConfigureAwait(false);

                    await peer.SendTextAsync("counted").ConfigureAwait(false);

                    using (var timeout = new CancellationTokenSource(WaitTimeout))
                    using (var message = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false))
                    {
                        Assert.AreEqual("counted", message.Text);

                        // The receive loop is still held after the enqueue: only a count taken before it shows up.
                        var metrics = connection.Metrics;
                        Assert.AreEqual(1, metrics.MessagesReceived,
                            "ReceiveAsync returned a message that MessagesReceived does not count yet.");
                        Assert.GreaterOrEqual(metrics.FramesReceived, 1);
                        Assert.Greater(metrics.BytesReceived, 0);
                    }
                }
                finally
                {
                    gate.Release();
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void ReceiveAsync_FragmentedMessage_IsAlreadyCountedInMetrics()
        {
            AssertAsync.Run(async () =>
            {
                var gate = new EnqueueGate(blockFromCall: 1);
                using var peer = new InMemoryWebSocketPeer();
                var connection = new WebSocketConnection { MessageEnqueuedTestHook = gate.OnEnqueued };
                try
                {
                    await connection.ConnectAsync(TestUri, peer, CreateOptions(), CancellationToken.None)
                        .ConfigureAwait(false);

                    await peer.SendFrameAsync(WebSocketOpcode.Text, Encoding.UTF8.GetBytes("frag-"), fin: false)
                        .ConfigureAwait(false);
                    await peer.SendFrameAsync(WebSocketOpcode.Continuation, Encoding.UTF8.GetBytes("ment"), fin: true)
                        .ConfigureAwait(false);

                    using (var timeout = new CancellationTokenSource(WaitTimeout))
                    using (var message = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false))
                    {
                        Assert.AreEqual("frag-ment", message.Text);

                        var metrics = connection.Metrics;
                        Assert.AreEqual(1, metrics.MessagesReceived,
                            "ReceiveAsync returned a fragmented message that MessagesReceived does not count yet.");
                        Assert.GreaterOrEqual(metrics.FramesReceived, 2);
                    }
                }
                finally
                {
                    gate.Release();
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void ReceiveAsync_MessageDeliveredAfterParking_IsAlreadyCountedInMetrics()
        {
            AssertAsync.Run(async () =>
            {
                // The first message passes the hook; the second one, delivered through the parked (full queue)
                // path, is held after its enqueue.
                var gate = new EnqueueGate(blockFromCall: 2);
                var options = CreateOptions();
                options.ReceiveQueueCapacity = 1;

                using var peer = new InMemoryWebSocketPeer();
                var connection = new WebSocketConnection { MessageEnqueuedTestHook = gate.OnEnqueued };
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    await peer.SendManyTextAsync(2).ConfigureAwait(false);
                    await WaitUntilAsync(() => connection.IsReceiveLoopParked,
                        "The receive loop did not park on the full delivery queue.").ConfigureAwait(false);

                    using (var timeout = new CancellationTokenSource(WaitTimeout))
                    {
                        using (var first = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false))
                        {
                            Assert.AreEqual("m0", first.Text);
                        }

                        using (var second = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false))
                        {
                            Assert.AreEqual("m1", second.Text);
                            Assert.AreEqual(2, connection.Metrics.MessagesReceived,
                                "ReceiveAsync returned a parked message that MessagesReceived does not count yet.");
                        }
                    }
                }
                finally
                {
                    gate.Release();
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

        /// <summary>Holds the receive loop after the enqueue from the <c>blockFromCall</c>-th message on.</summary>
        private sealed class EnqueueGate
        {
            private readonly TaskCompletionSource<bool> _release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly int _blockFromCall;
            private int _calls;

            public EnqueueGate(int blockFromCall)
            {
                _blockFromCall = blockFromCall;
            }

            public Task OnEnqueued()
            {
                return Interlocked.Increment(ref _calls) >= _blockFromCall ? _release.Task : Task.CompletedTask;
            }

            public void Release()
            {
                _release.TrySetResult(true);
            }
        }
    }
}
