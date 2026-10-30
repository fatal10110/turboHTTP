using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #78: event-only consumers must not stall on the bounded pull queue, and control frames must keep
    /// being processed while data messages pile up.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketReceiveBackpressureTests
    {
        private const int FloodCount = 600;
        private static readonly Uri TestUri = new Uri("ws://localhost/backpressure");

        [Test]
        public void WebSocketClient_EventOnly_ReceivesFloodWithoutStalling()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                await using var client = new WebSocketClient(peer);
                var counter = new MessageCounter(FloodCount);
                client.OnMessage += counter.Handle;

                await client.ConnectAsync(TestUri, CreateOptions(capacity: 4), CancellationToken.None)
                    .ConfigureAwait(false);
                await peer.SendManyTextAsync(FloodCount).ConfigureAwait(false);

                await WaitWithTimeout(counter.Completed, TimeSpan.FromSeconds(10),
                    "Event-only consumer stalled after " + counter.Count + " messages.").ConfigureAwait(false);

                Assert.AreEqual(FloodCount, counter.Count);
                Assert.AreEqual(0, counter.OutOfOrder, "Messages were delivered out of order.");
                Assert.AreEqual(WebSocketState.Open, client.State);
            });
        }

        [Test]
        public void ResilientWebSocketClient_EventOnly_ReceivesFloodWithoutStalling()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                await using var client = new ResilientWebSocketClient(peer);
                var counter = new MessageCounter(FloodCount);
                client.OnMessage += counter.Handle;

                await client.ConnectAsync(TestUri, CreateOptions(capacity: 4), CancellationToken.None)
                    .ConfigureAwait(false);
                await peer.SendManyTextAsync(FloodCount).ConfigureAwait(false);

                await WaitWithTimeout(counter.Completed, TimeSpan.FromSeconds(10),
                    "Resilient event-only consumer stalled after " + counter.Count + " messages.").ConfigureAwait(false);

                Assert.AreEqual(FloodCount, counter.Count);
                Assert.AreEqual(0, counter.OutOfOrder, "Messages were delivered out of order.");
                Assert.AreEqual(WebSocketState.Open, client.State);
            });
        }

        [Test]
        public void WebSocketClient_EventOnly_AnswersPingAndProcessesCloseBehindFlood()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                await using var client = new WebSocketClient(peer);
                var counter = new MessageCounter(50);
                var closed = new TaskCompletionSource<WebSocketCloseCode>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnMessage += counter.Handle;
                client.OnClosed += (code, _) => closed.TrySetResult(code);

                await client.ConnectAsync(TestUri, CreateOptions(capacity: 2), CancellationToken.None)
                    .ConfigureAwait(false);

                // Far more messages than both queues hold, then control frames behind them.
                await peer.SendManyTextAsync(50).ConfigureAwait(false);
                await peer.SendFrameAsync(WebSocketOpcode.Ping, Encoding.ASCII.GetBytes("probe"), fin: true)
                    .ConfigureAwait(false);

                var pong = await ReadUntilAsync(peer, WebSocketOpcode.Pong).ConfigureAwait(false);
                Assert.AreEqual("probe", Encoding.ASCII.GetString(pong.Payload));

                await peer.SendFrameAsync(WebSocketOpcode.Close, new byte[] { 0x03, 0xE8 }, fin: true)
                    .ConfigureAwait(false);

                await WaitWithTimeout(closed.Task, TimeSpan.FromSeconds(5), "Close frame was not processed.")
                    .ConfigureAwait(false);

                Assert.AreEqual(WebSocketCloseCode.NormalClosure, closed.Task.Result);
                Assert.AreEqual(50, counter.Count);
            });
        }

        [Test]
        public void ResilientWebSocketClient_EventOnly_AnswersPingBehindFlood()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                await using var client = new ResilientWebSocketClient(peer);
                var counter = new MessageCounter(50);
                client.OnMessage += counter.Handle;

                await client.ConnectAsync(TestUri, CreateOptions(capacity: 2), CancellationToken.None)
                    .ConfigureAwait(false);

                await peer.SendManyTextAsync(50).ConfigureAwait(false);
                await peer.SendFrameAsync(WebSocketOpcode.Ping, Encoding.ASCII.GetBytes("probe"), fin: true)
                    .ConfigureAwait(false);

                var pong = await ReadUntilAsync(peer, WebSocketOpcode.Pong).ConfigureAwait(false);
                Assert.AreEqual("probe", Encoding.ASCII.GetString(pong.Payload));

                await WaitWithTimeout(counter.Completed, TimeSpan.FromSeconds(5), "Messages were not all raised.")
                    .ConfigureAwait(false);
            });
        }

        [Test]
        public void WebSocketClient_PullOnly_ReceivesEveryMessageInOrder()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                await using var client = new WebSocketClient(peer);

                await client.ConnectAsync(TestUri, CreateOptions(capacity: 4), CancellationToken.None)
                    .ConfigureAwait(false);
                await peer.SendManyTextAsync(50).ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                for (int i = 0; i < 50; i++)
                {
                    using var message = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    Assert.AreEqual("m" + i, message.Text, "Pull-only consumer lost or reordered a message.");
                }
            });
        }

        [Test]
        public void WebSocketClient_MixedMode_RaisesEveryMessageAndPullQueueKeepsMostRecent()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                await using var client = new WebSocketClient(peer);
                var counter = new MessageCounter(20);
                client.OnMessage += counter.Handle;

                await client.ConnectAsync(TestUri, CreateOptions(capacity: 4), CancellationToken.None)
                    .ConfigureAwait(false);
                await peer.SendManyTextAsync(20).ConfigureAwait(false);

                await WaitWithTimeout(counter.Completed, TimeSpan.FromSeconds(5), "Mixed-mode OnMessage stalled.")
                    .ConfigureAwait(false);

                // The pull queue was never drained: it holds the newest ReceiveQueueCapacity messages.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                for (int i = 16; i < 20; i++)
                {
                    using var message = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    Assert.AreEqual("m" + i, message.Text);
                }

                await peer.SendTextAsync("late").ConfigureAwait(false);
                using (var late = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false))
                {
                    Assert.AreEqual("late", late.Text);
                }
            });
        }

        [Test]
        public void WebSocketConnection_PullConsumerNotReading_KeepAliveDoesNotAbort()
        {
            AssertAsync.Run(async () =>
            {
                var options = CreateOptions(capacity: 1);
                options.PingInterval = TimeSpan.FromMilliseconds(25);
                options.PongTimeout = TimeSpan.FromMilliseconds(100);

                using var peer = new InMemoryWebSocketPeer();
                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    // The queue holds one message; the read loop parks on the second one and stops reading
                    // the socket, so pongs to the keep-alive pings stay unread.
                    await peer.SendManyTextAsync(5).ConfigureAwait(false);

                    // Deterministic signal: the keep-alive loop records every iteration it skips (or every pong
                    // timeout it forgives) because the read loop is parked. Eight skips are at least seven
                    // PingIntervals (175 ms), more than one PongTimeout window, so a false 1006 would already have
                    // ended the keep-alive loop and the counter would stop short of the target.
                    const long targetSkips = 8;
                    await WaitUntilAsync(
                        () => connection.KeepAliveBackpressureSkips >= targetSkips ||
                            connection.State != WebSocketState.Open,
                        TimeSpan.FromSeconds(5),
                        "Keep-alive loop never observed the parked read loop.").ConfigureAwait(false);

                    Assert.AreEqual(WebSocketState.Open, connection.State,
                        "Local backpressure was treated as a dead peer: " + connection.TerminalError?.Message);
                    Assert.IsNull(connection.TerminalError);

                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    for (int i = 0; i < 5; i++)
                    {
                        using var message = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                        Assert.AreEqual("m" + i, message.Text);
                    }

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

        internal static WebSocketConnectionOptions CreateOptions(int capacity)
        {
            return new WebSocketConnectionOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(5),
                CloseHandshakeTimeout = TimeSpan.FromSeconds(2),
                PingInterval = TimeSpan.Zero,
                IdleTimeout = TimeSpan.Zero,
                ReceiveQueueCapacity = capacity
            };
        }

        internal static async Task WaitWithTimeout(Task task, TimeSpan timeout, string message)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != task)
                Assert.Fail(message);

            await task.ConfigureAwait(false);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string message)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!predicate())
            {
                if (deadline.Elapsed > timeout)
                    Assert.Fail(message);

                // Polling a state predicate under a deadline, not a sleep used as synchronization.
                await Task.Delay(5).ConfigureAwait(false);
            }
        }

        private static async Task<InMemoryWebSocketPeer.ReceivedFrame> ReadUntilAsync(
            InMemoryWebSocketPeer peer,
            WebSocketOpcode opcode)
        {
            while (true)
            {
                InMemoryWebSocketPeer.ReceivedFrame frame;
                try
                {
                    frame = await peer.ReadFrameAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Assert.Fail("Client did not send " + opcode + ": its read loop is blocked behind data messages.");
                    throw;
                }

                if (frame.Opcode == opcode)
                    return frame;
            }
        }

        private sealed class MessageCounter
        {
            private readonly int _target;
            private readonly TaskCompletionSource<bool> _completed =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            private int _count;
            private int _outOfOrder;

            public MessageCounter(int target)
            {
                _target = target;
            }

            public int Count => Volatile.Read(ref _count);

            public int OutOfOrder => Volatile.Read(ref _outOfOrder);

            public Task Completed => _completed.Task;

            public void Handle(WebSocketMessage message)
            {
                try
                {
                    int index = Volatile.Read(ref _count);
                    if (message.Text != "m" + index)
                        Interlocked.Increment(ref _outOfOrder);

                    if (Interlocked.Increment(ref _count) == _target)
                        _completed.TrySetResult(true);
                }
                finally
                {
                    message.Dispose();
                }
            }
        }
    }
}
