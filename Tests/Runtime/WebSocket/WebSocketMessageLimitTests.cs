using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #80: MaxMessageSize applies to single-frame messages before the payload is buffered, the default
    /// MaxFrameSize fits inside MaxMessageSize, and the receive queues have a byte budget.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketMessageLimitTests
    {
        private const int SmallLimit = 16 * 1024;
        private static readonly Uri TestUri = new Uri("ws://localhost/limits");

        [Test]
        public void Reader_SingleFrameOverMaxMessageSize_RejectedBeforePayloadIsRead()
        {
            // Header only: a reader that tried to buffer the payload would fail with an IOException (EOF).
            using var stream = new MemoryStream(BuildBinaryHeader(payloadLength: 2000, fin: true));
            var reader = new WebSocketFrameReader(maxFrameSize: 1 << 20, maxMessageSize: 1024);

            var ex = AssertAsync.ThrowsAsync<WebSocketProtocolException>(async () =>
                await reader.ReadAsync(stream, fragmentedMessageInProgress: false, CancellationToken.None)
                    .ConfigureAwait(false));

            Assert.AreEqual(WebSocketError.MessageTooLarge, ex.Error);
            Assert.AreEqual(WebSocketCloseCode.MessageTooBig, ex.CloseCode);
        }

        [Test]
        public void Reader_FirstFragmentOverMaxMessageSize_Rejected()
        {
            using var stream = new MemoryStream(BuildBinaryHeader(payloadLength: 2000, fin: false));
            var reader = new WebSocketFrameReader(maxFrameSize: 1 << 20, maxMessageSize: 1024);

            var ex = AssertAsync.ThrowsAsync<WebSocketProtocolException>(async () =>
                await reader.ReadAsync(stream, fragmentedMessageInProgress: false, CancellationToken.None)
                    .ConfigureAwait(false));

            Assert.AreEqual(WebSocketCloseCode.MessageTooBig, ex.CloseCode);
        }

        [Test]
        public void Assembler_SingleFinalFrameOverMaxMessageSize_Rejected()
        {
            AssertAsync.Run(async () =>
            {
                var frame = new byte[4 + 2000];
                Array.Copy(BuildBinaryHeader(2000, fin: true), frame, 4);
                using var stream = new MemoryStream(frame);
                var reader = new WebSocketFrameReader(maxFrameSize: 1 << 20, maxMessageSize: 1 << 20);
                var assembler = new MessageAssembler(maxMessageSize: 1024, maxFragmentCount: 4);

                var lease = await reader.ReadAsync(stream, fragmentedMessageInProgress: false, CancellationToken.None)
                    .ConfigureAwait(false);

                var ex = Assert.Throws<WebSocketProtocolException>(() => assembler.TryAssemble(lease, out _));
                Assert.AreEqual(WebSocketError.MessageTooLarge, ex.Error);
                Assert.AreEqual(WebSocketCloseCode.MessageTooBig, ex.CloseCode);
                Assert.IsFalse(assembler.FragmentedMessageInProgress);
            });
        }

        [Test]
        public void Options_Defaults_MaxFrameSizeWithinMaxMessageSizeAndValid()
        {
            var options = new WebSocketConnectionOptions();

            Assert.LessOrEqual(options.MaxFrameSize, options.MaxMessageSize);
            Assert.LessOrEqual(options.FragmentationThreshold, options.MaxFrameSize);
            Assert.Greater(options.ReceiveQueueMaxBytes, 0);
            Assert.DoesNotThrow(options.Validate);
        }

        [Test]
        public void Options_Validate_RejectsMaxFrameSizeAboveMaxMessageSize()
        {
            var options = new WebSocketConnectionOptions { MaxMessageSize = 1024 * 1024 };

            Assert.Throws<ArgumentException>(options.Validate);

            options.MaxFrameSize = 1024 * 1024;
            Assert.DoesNotThrow(options.Validate);
        }

        [Test]
        public void Options_ReceiveQueueMaxBytes_ValidatedAndCloned()
        {
            var options = new WebSocketConnectionOptions { ReceiveQueueMaxBytes = 0 };
            Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

            options.ReceiveQueueMaxBytes = 12345;
            Assert.AreEqual(12345, options.Clone().ReceiveQueueMaxBytes);
        }

        [Test]
        public void Queue_TryEnqueue_RespectsByteBudget_AndAdmitsOneOversizeItemWhenEmpty()
        {
            var queue = new BoundedAsyncQueue<byte[]>(capacity: 10, maxBytes: 100, sizeOf: b => b.Length);

            Assert.IsTrue(queue.TryEnqueue(new byte[60]));
            Assert.IsFalse(queue.TryEnqueue(new byte[60]), "Second item would exceed the byte budget.");
            Assert.IsTrue(queue.TryEnqueue(new byte[40]));
            Assert.AreEqual(100, queue.QueuedBytes);

            queue.Drain(_ => { });
            Assert.AreEqual(0, queue.QueuedBytes);

            Assert.IsTrue(queue.TryEnqueue(new byte[500]), "An empty queue must admit an item larger than the budget.");
            Assert.IsFalse(queue.TryEnqueue(new byte[1]));
        }

        [Test]
        public void Queue_EnqueueAsync_WaitsForBytesAndResumesOnDequeue()
        {
            AssertAsync.Run(async () =>
            {
                var queue = new BoundedAsyncQueue<byte[]>(capacity: 10, maxBytes: 100, sizeOf: b => b.Length);
                Assert.IsTrue(queue.TryEnqueue(new byte[80]));

                var pending = queue.EnqueueAsync(new byte[50], CancellationToken.None).AsTask();
                Assert.IsFalse(pending.IsCompleted, "Enqueue must wait while the byte budget is exhausted.");

                var first = await queue.DequeueAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(80, first.Length);

                await WaitWithTimeout(pending, "Writer was not woken after bytes were freed.").ConfigureAwait(false);
                Assert.AreEqual(50, queue.QueuedBytes);
            });
        }

        [Test]
        public void Queue_Complete_WakesWriterWaitingForBytes()
        {
            AssertAsync.Run(async () =>
            {
                var queue = new BoundedAsyncQueue<byte[]>(capacity: 10, maxBytes: 100, sizeOf: b => b.Length);
                Assert.IsTrue(queue.TryEnqueue(new byte[80]));

                var pending = queue.EnqueueAsync(new byte[50], CancellationToken.None).AsTask();
                queue.Complete(null);

                var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                Assert.AreSame(pending, completed, "Writer stayed blocked after Complete.");
                Assert.IsInstanceOf<AsyncQueueCompletedException>(pending.Exception?.InnerException);
            });
        }

        [Test]
        public void Queue_EnqueueDropOldest_DropsUntilTheItemFitsTheByteBudget()
        {
            var queue = new BoundedAsyncQueue<byte[]>(capacity: 10, maxBytes: 100, sizeOf: b => b.Length);
            var a = new byte[30];
            var b = new byte[30];
            var c = new byte[30];
            queue.TryEnqueue(a);
            queue.TryEnqueue(b);
            queue.TryEnqueue(c);

            var dropped = new System.Collections.Generic.List<byte[]>();
            int count = queue.EnqueueDropOldest(new byte[70], dropped.Add);

            Assert.AreEqual(2, count);
            CollectionAssert.AreEqual(new[] { a, b }, dropped);
            Assert.AreEqual(2, queue.Count);
            Assert.AreEqual(100, queue.QueuedBytes);
        }

        [Test]
        public void Connection_FloodOfMaxSizeFrames_QueueStaysWithinByteBudget_AndDeliversAll()
        {
            AssertAsync.Run(async () =>
            {
                const int frameCount = 20;
                var options = CreateSmallLimitOptions();
                options.ReceiveQueueCapacity = 100;
                options.ReceiveQueueMaxBytes = 2 * SmallLimit;

                using var peer = new InMemoryWebSocketPeer();
                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None).ConfigureAwait(false);

                    var send = SendMaxSizeFramesAsync(peer, frameCount);

                    // Without a byte budget the 100-slot queue would take all 20 frames and the read loop would
                    // never park.
                    await WaitUntilAsync(() => connection.IsReceiveLoopParked, "Read loop never parked on the byte budget.")
                        .ConfigureAwait(false);

                    Assert.LessOrEqual(connection.ReceiveQueueBytes, options.ReceiveQueueMaxBytes);
                    Assert.AreEqual(2, connection.ReceiveQueueCount);

                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    for (int i = 0; i < frameCount; i++)
                    {
                        using var message = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                        Assert.AreEqual(SmallLimit, message.Length);
                        Assert.AreEqual((byte)i, message.Data.Span[0], "Message lost or reordered.");
                        Assert.LessOrEqual(connection.ReceiveQueueBytes, options.ReceiveQueueMaxBytes);
                    }

                    await WaitWithTimeout(send, "Peer writes did not complete.").ConfigureAwait(false);
                    Assert.AreEqual(WebSocketState.Open, connection.State);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void WebSocketClient_PullOnly_FloodOfMaxSizeFrames_DeliveredLosslessly()
        {
            AssertAsync.Run(async () =>
            {
                const int frameCount = 12;
                var options = CreateSmallLimitOptions();
                options.ReceiveQueueMaxBytes = SmallLimit;

                using var peer = new InMemoryWebSocketPeer();
                await using var client = new WebSocketClient(peer);
                await client.ConnectAsync(TestUri, options, CancellationToken.None).ConfigureAwait(false);

                var send = SendMaxSizeFramesAsync(peer, frameCount);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                for (int i = 0; i < frameCount; i++)
                {
                    using var message = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    Assert.AreEqual((byte)i, message.Data.Span[0], "Message lost or reordered.");
                }

                await WaitWithTimeout(send, "Peer writes did not complete.").ConfigureAwait(false);
            });
        }

        [Test]
        public void Connection_SingleFrameOverMaxMessageSize_ClosesWith1009WithoutPayload()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, CreateSmallLimitOptions(), CancellationToken.None)
                        .ConfigureAwait(false);

                    // The header announces a frame one byte over the limit; no payload follows.
                    await peer.SendRawAsync(BuildBinaryHeader(SmallLimit + 1, fin: true)).ConfigureAwait(false);

                    var frame = await peer.ReadFrameAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketOpcode.Close, frame.Opcode);
                    Assert.AreEqual((int)WebSocketCloseCode.MessageTooBig, (frame.Payload[0] << 8) | frame.Payload[1]);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        private static WebSocketConnectionOptions CreateSmallLimitOptions()
        {
            var options = WebSocketReceiveBackpressureTests.CreateOptions(capacity: 100);
            options.MaxMessageSize = SmallLimit;
            options.MaxFrameSize = SmallLimit;
            options.FragmentationThreshold = SmallLimit;
            return options;
        }

        private static async Task SendMaxSizeFramesAsync(InMemoryWebSocketPeer peer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var payload = new byte[SmallLimit];
                payload[0] = (byte)i;
                await peer.SendFrameAsync(WebSocketOpcode.Binary, payload, fin: true).ConfigureAwait(false);
            }
        }

        // Unmasked binary frame header with a 16-bit extended length (payloadLength 126..65535).
        private static byte[] BuildBinaryHeader(int payloadLength, bool fin)
        {
            return new[]
            {
                (byte)((fin ? 0x80 : 0x00) | (byte)WebSocketOpcode.Binary),
                (byte)126,
                (byte)(payloadLength >> 8),
                (byte)payloadLength
            };
        }

        private static Task WaitWithTimeout(Task task, string message)
        {
            return WebSocketReceiveBackpressureTests.WaitWithTimeout(task, TimeSpan.FromSeconds(10), message);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, string message)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!predicate())
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                    Assert.Fail(message);

                await Task.Yield();
                await Task.Delay(5).ConfigureAwait(false);
            }
        }
    }
}
