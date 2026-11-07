using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core.Internal;
using TurboHTTP.Tests.WebSocket;
using TurboHTTP.Transport.Http2;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// #375: the HTTP/2 read loop and the WebSocket receive loop await a read on the stream their
    /// owner closes on teardown. On IL2CPP that read keeps its I/O job keyed by the descriptor number
    /// until it ends (#369), so the owner records the loop before closing the stream and no socket is
    /// created until the loop ended. The reads here ignore their token and
    /// <see cref="Stream.Dispose()"/>, as on IL2CPP; the tests assert the order, which the Editor can
    /// show (Mono removes the job on close, so the loss itself cannot be shown here).
    /// </summary>
    [TestFixture]
    public class AbandonedLoopGateTests
    {
        // Guard only: every awaited step ends long before this; it never synchronizes a test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        // Longer than any test: only the end of the loop's read can open the gate.
        private static readonly TimeSpan LongSettle = TimeSpan.FromSeconds(30);

        private static readonly Uri WsUri = new Uri("ws://127.0.0.1/abandoned-loop");

        // Server preface (an empty SETTINGS frame), then the ACK of the client's SETTINGS (RFC 9113 3.4).
        private const string ServerSettings = "\0\0\0\u0004\0\0\0\0\0";
        private const string ServerSettingsAck = "\0\0\0\u0004\u0001\0\0\0\0";

        private TimeSpan _savedSettle;

        [SetUp]
        public void SetUp()
        {
            _savedSettle = AbandonedSocketIo.SettleTimeout;

            // Operations other fixtures abandoned expire within their own (default) bound.
            AssertAsync.Run(async () =>
                await AbandonedSocketIoTests.WaitUntilAsync(
                    () => AbandonedSocketIo.PendingCount == 0,
                    "earlier abandoned operations never settled"),
                Guard);
            AbandonedSocketIo.SettleTimeout = LongSettle;
        }

        [TearDown]
        public void TearDown()
        {
            AbandonedSocketIo.SettleTimeout = _savedSettle;
        }

        [Test]
        public void Http2Dispose_ReadLoopPending_NextSocketWaitsForTheLoop()
        {
            var stream = new AbandonedReadStream(ServerSettings, ServerSettingsAck);
            AbandonedSocketIoTests.RunOrderingTest(stream, async () =>
            {
                var connection = new Http2Connection(stream, "test.example.com", 443);
                await connection.InitializeAsync(CancellationToken.None);
                await AbandonedSocketIoTests.AssertCompletesAsync(
                    stream.ReadPending,
                    "the read loop never awaited its next frame");

                connection.Dispose();
                connection.Dispose();

                Assert.IsTrue(stream.IsDisposed, "the teardown must close the stream under the loop's read");
                Assert.AreEqual(1, AbandonedSocketIo.PendingCount, "the read loop must be recorded exactly once");
            });
        }

        [Test]
        public void WebSocketAbort_ReceiveLoopPending_NextSocketWaitsForTheLoop()
        {
            var stream = new AbandonedReadStream();
            stream.OnWrite = written => AnswerUpgrade(stream, written);
            AbandonedSocketIoTests.RunOrderingTest(stream, async () =>
            {
                var connection = new WebSocketConnection();
                await connection.ConnectAsync(
                    WsUri,
                    new SingleStreamTransport(stream),
                    CreateOptions(TimeSpan.FromSeconds(30)),
                    CancellationToken.None);
                await AbandonedSocketIoTests.AssertCompletesAsync(
                    stream.ReadPending,
                    "the receive loop never awaited its next frame");

                connection.Abort();
                connection.Dispose();

                Assert.AreEqual(WebSocketState.Closed, connection.State);
                Assert.IsTrue(stream.IsDisposed, "the abort must close the stream under the loop's read");
                Assert.AreEqual(1, AbandonedSocketIo.PendingCount, "the receive loop must be recorded exactly once");
            });
        }

        [Test]
        public void WebSocketCloseHandshakeTimeout_ReceiveLoopPending_NextSocketWaitsForTheLoop()
        {
            // Once the connection is open the server sends a Close frame (FIN + opcode 0x8, no payload)
            // and then never closes the TCP connection: the loop's read for the transport close stays
            // pending until CloseHandshakeTimeout closes the stream under it.
            var stream = new AbandonedReadStream();
            stream.OnWrite = written => AnswerUpgrade(stream, written);
            AbandonedSocketIoTests.RunOrderingTest(stream, async () =>
            {
                var connection = new WebSocketConnection();
                await connection.ConnectAsync(
                    WsUri,
                    new SingleStreamTransport(stream),
                    CreateOptions(TimeSpan.FromMilliseconds(50)),
                    CancellationToken.None);
                await AbandonedSocketIoTests.AssertCompletesAsync(
                    stream.ReadPending,
                    "the receive loop never awaited its next frame");

                stream.ReadBuffer[stream.ReadOffset] = 0x88;
                stream.ReadBuffer[stream.ReadOffset + 1] = 0x00;
                stream.EndRead(2);

                await AbandonedSocketIoTests.WaitUntilAsync(
                    () => stream.IsDisposed,
                    "the close-handshake timeout never closed the stream");

                Assert.AreEqual(1, AbandonedSocketIo.PendingCount, "the receive loop must be recorded at the timeout");

                // A teardown racing the pending read does not record the loop a second time.
                connection.Abort();
                connection.Dispose();
                Assert.AreEqual(1, AbandonedSocketIo.PendingCount, "the receive loop must be recorded exactly once");
            });
        }

        private static WebSocketConnectionOptions CreateOptions(TimeSpan closeHandshakeTimeout) =>
            new WebSocketConnectionOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(30),
                CloseHandshakeTimeout = closeHandshakeTimeout,
                PingInterval = TimeSpan.Zero,
                PongTimeout = TimeSpan.FromMilliseconds(200),
                ReceiveQueueCapacity = 4,
                AllowCleartext = true
            };

        /// <summary>Queues the 101 reply when <paramref name="written"/> is the upgrade request.</summary>
        private static bool AnswerUpgrade(AbandonedReadStream stream, string written)
        {
            const string KeyHeader = "Sec-WebSocket-Key:";
            int start = written.IndexOf(KeyHeader, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return false;

            start += KeyHeader.Length;
            int end = written.IndexOf("\r\n", start, StringComparison.Ordinal);
            string key = written.Substring(start, end - start).Trim();
            stream.Answer(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + WebSocketConstants.ComputeAcceptKey(key) + "\r\n\r\n"));
            return true;
        }
    }
}
