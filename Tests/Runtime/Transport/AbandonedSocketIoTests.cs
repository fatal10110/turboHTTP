using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Tests.Transport.Tls;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Connection;
using TurboHTTP.Transport.Http1;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// #369: on IL2CPP a socket closed with a pending operation keeps that operation's job in the
    /// I/O selector under its descriptor number, and a new socket that reuses the number can lose its
    /// own receive. Every path that abandons a pending read (or handshake, or connect) records it, and
    /// no socket is created until the abandoned operation ended or its bound expired. Mono removes
    /// the job on close, so the Editor cannot show the loss itself: these tests assert the order (the
    /// next socket is created only after the abandoned read ended), with reads that ignore their token
    /// and <see cref="Stream.Dispose()"/> as on IL2CPP.
    /// </summary>
    [TestFixture]
    public class AbandonedSocketIoTests
    {
        // Guard only: every awaited step ends long before this; it never synchronizes a test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        // Longer than any test: only the end of the abandoned read can open the gate.
        private static readonly TimeSpan LongSettle = TimeSpan.FromSeconds(30);

        private static readonly string[] Http11 = { "http/1.1" };

        private TimeSpan _savedSettle;

        [SetUp]
        public void SetUp()
        {
            _savedSettle = AbandonedSocketIo.SettleTimeout;

            // Operations other fixtures abandoned expire within their own (default) bound.
            AssertAsync.Run(async () =>
                await WaitUntilAsync(() => AbandonedSocketIo.PendingCount == 0, "earlier abandoned operations never settled"),
                Guard);
            AbandonedSocketIo.SettleTimeout = LongSettle;
        }

        [TearDown]
        public void TearDown()
        {
            AbandonedSocketIo.SettleTimeout = _savedSettle;
        }

        [Test]
        public void WaitForSettled_NothingAbandoned_CompletesSynchronously()
        {
            Assert.IsTrue(AbandonedSocketIo.WaitForSettledAsync(CancellationToken.None).IsCompleted);
        }

        [Test]
        public void AbandonOnCancel_CompletedTask_IsNotRecorded()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                Assert.AreEqual(7, await AbandonOnCancel.WaitAsync(Task.FromResult(7), cts.Token));
                Assert.AreEqual(0, AbandonedSocketIo.PendingCount);
            }, Guard);
        }

        [Test]
        public void AbandonOnCancel_AbandonedTask_HoldsTheGateUntilItEnds()
        {
            var operation = new TaskCompletionSource<int>();
            try
            {
                AssertAsync.Run(async () =>
                {
                    using var cts = new CancellationTokenSource();
                    var wait = AbandonOnCancel.WaitAsync(operation.Task, cts.Token);
                    cts.Cancel();
                    Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(wait));
                    Assert.AreEqual(1, AbandonedSocketIo.PendingCount, "the abandoned task is recorded");

                    var gate = AbandonedSocketIo.WaitForSettledAsync(CancellationToken.None);
                    Assert.IsFalse(gate.IsCompleted, "the gate opened while the abandoned task was pending");

                    operation.SetException(new IOException("socket disposed"));
                    await AssertCompletesAsync(gate, "the gate stayed closed after the abandoned task ended");
                    Assert.AreEqual(0, AbandonedSocketIo.PendingCount);
                }, Guard);
            }
            finally
            {
                operation.TrySetResult(0);
            }
        }

        [Test]
        public void AbandonedOperationNeverEnds_EntryExpiresAtTheSettleBound()
        {
            AbandonedSocketIo.SettleTimeout = TimeSpan.FromMilliseconds(100);
            var operation = new TaskCompletionSource<int>();
            try
            {
                AssertAsync.Run(async () =>
                {
                    AbandonedSocketIo.Track(operation.Task);
                    Assert.AreEqual(1, AbandonedSocketIo.PendingCount);

                    // The platform never ends the operation (#293): the gate opens at the bound.
                    await AssertCompletesAsync(
                        AbandonedSocketIo.WaitForSettledAsync(CancellationToken.None),
                        "the gate never opened for an operation that never ends");
                    await WaitUntilAsync(() => AbandonedSocketIo.PendingCount == 0, "the entry never expired");
                }, Guard);
            }
            finally
            {
                operation.TrySetResult(0);
            }
        }

        [Test]
        public void WaitForSettled_TokenFires_ThrowsOperationCanceled()
        {
            var operation = new TaskCompletionSource<int>();
            try
            {
                AssertAsync.Run(async () =>
                {
                    AbandonedSocketIo.Track(operation.Task);
                    using var cts = new CancellationTokenSource();
                    var gate = AbandonedSocketIo.WaitForSettledAsync(cts.Token);
                    cts.Cancel();
                    Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(gate));
                }, Guard);
            }
            finally
            {
                operation.TrySetResult(0);
            }
        }

        [Test]
        public void HeadRead_Abandoned_NextSocketWaitsForTheRead()
        {
            var stream = new AbandonedReadStream();
            RunOrderingTest(stream, async () =>
            {
                using var cts = new CancellationTokenSource();
                var parse = Http11ResponseParser.ParseHeadAsync(stream, HttpMethod.GET, cts.Token);
                await AssertCompletesAsync(stream.ReadPending, "the head read never started");
                cts.Cancel();
                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(parse));
            });
        }

        [Test]
        public void BodyRead_Abandoned_NextSocketWaitsForTheRead()
        {
            var stream = new AbandonedReadStream();
            RunOrderingTest(stream, async () =>
            {
                var reader = new Http11ResponseParser.BufferedStreamReader(stream);
                reader.MarkHeadComplete();
                using var cts = new CancellationTokenSource();
                var read = reader.EnsureBufferedAsync(cts.Token);
                await AssertCompletesAsync(stream.ReadPending, "the body read never started");
                cts.Cancel();
                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(read));
                reader.Dispose();
            });
        }

        [Test]
        public void ProxyChallengeDrain_Abandoned_NextSocketWaitsForTheRead()
        {
            var stream = new AbandonedReadStream();
            RunOrderingTest(stream, async () =>
            {
                using var cts = new CancellationTokenSource();
                var head = new ParsedResponseHead(new Http11ResponseParser.BufferedStreamReader(stream))
                {
                    StatusCode = (HttpStatusCode)407,
                    Headers = HttpHeaders.Empty,
                    KeepAlive = true,
                    BodyKind = Http11ResponseBodyKind.ContentLength,
                    ContentLength = 32 * 1024
                };

                var drain = RawSocketTransport.TryDrainProxyChallengeBodyAsync(head, cts.Token);
                await AssertCompletesAsync(stream.ReadPending, "the 407 drain read never started");
                cts.Cancel();
                await AssertCompletesAsync(drain, "the 407 drain outlived its cancellation");
                Assert.IsFalse(await drain);
                head.Dispose();
            });
        }

        [Test]
        public void ConnectReply_Abandoned_NextSocketWaitsForTheRead()
        {
            var stream = new AbandonedReadStream();
            RunOrderingTest(stream, async () =>
            {
                using var cts = new CancellationTokenSource();
                var read = ConnectReplyReader.ReadAsync(stream, cts.Token);
                await AssertCompletesAsync(stream.ReadPending, "the CONNECT reply read never started");
                cts.Cancel();
                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(read));
            });
        }

        [Test]
        public void WebSocketUpgradeRead_Abandoned_NextSocketWaitsForTheRead()
        {
            var stream = new AbandonedReadStream();
            RunOrderingTest(stream, async () =>
            {
                await using var client = new WebSocketClient(new StreamTransport(stream));
                using var cts = new CancellationTokenSource();
                var connect = client.ConnectAsync(
                    new Uri("ws://127.0.0.1/abandoned-read"),
                    new WebSocketConnectionOptions
                    {
                        HandshakeTimeout = TimeSpan.FromSeconds(30),
                        CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                        PingInterval = TimeSpan.Zero,
                        PongTimeout = TimeSpan.FromMilliseconds(200),
                        ReceiveQueueCapacity = 4,
                        AllowCleartext = true
                    },
                    cts.Token);
                await AssertCompletesAsync(stream.ReadPending, "the upgrade response read never started");
                cts.Cancel();
                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(connect));
            });
        }

        [Test]
        public void BouncyCastleHandshake_Abandoned_NextSocketWaitsForTheRead()
        {
            var provider = ClientHelloCapture.CreateBouncyCastleProvider(30000);
            var stream = new AbandonedReadStream();
            RunOrderingTest(stream, async () =>
            {
                using var cts = new CancellationTokenSource();
                var wrap = provider.WrapAsync(stream, "localhost", Http11, cts.Token);
                await AssertCompletesAsync(stream.ReadPending, "the handshake read never started");
                cts.Cancel();
                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(wrap));
            });
        }

        /// <summary>
        /// Runs <paramref name="abandon"/>, which must leave one read of <paramref name="stream"/>
        /// abandoned, then starts a real connect: it must not create its socket before the test ends
        /// that read. The read is ended on every path, so no entry outlives the test.
        /// </summary>
        private static void RunOrderingTest(AbandonedReadStream stream, Func<Task> abandon)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var readEnded = false;
            try
            {
                AssertAsync.Run(async () =>
                {
                    await abandon();
                    Assert.Greater(
                        AbandonedSocketIo.PendingCount,
                        0,
                        "the abandoned read must be recorded before the caller sees the cancellation");

                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var connect = HappyEyeballsConnector.ConnectAsync(
                        new[] { IPAddress.Loopback },
                        port,
                        Timeout.InfiniteTimeSpan,
                        new HappyEyeballsOptions(),
                        CancellationToken.None);

                    await WaitUntilAsync(
                        () => AbandonedSocketIo.HasWaiters || connect.IsCompleted,
                        "the connect neither waited nor completed");
                    Assert.IsFalse(connect.IsCompleted, "a socket was created while the abandoned read was pending");
                    Assert.IsFalse(listener.Pending(), "a connection arrived while the abandoned read was pending");

                    readEnded = true;
                    stream.EndRead(0);

                    await AssertCompletesAsync(connect, "the connect never ran after the abandoned read ended");
                    using var socket = await connect;
                    Assert.IsTrue(socket.Connected);
                    Assert.AreEqual(0, AbandonedSocketIo.PendingCount);
                }, Guard);
            }
            finally
            {
                if (!readEnded)
                {
                    try { stream.EndRead(0); } catch { /* no read pending */ }
                }

                listener.Stop();
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow + Guard;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail(message);

                await Task.Delay(1);
            }
        }

        private static async Task AssertCompletesAsync(Task task, string message)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Guard));
            Assert.AreSame(task, winner, message);
        }

        private static async Task<Exception> CaptureAsync(Task task)
        {
            try
            {
                await task;
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private sealed class StreamTransport : IWebSocketTransport
        {
            private readonly Stream _stream;

            public StreamTransport(Stream stream)
            {
                _stream = stream;
            }

            public Task<Stream> ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct) =>
                Task.FromResult(_stream);

            public void Dispose() => _stream.Dispose();
        }
    }
}
