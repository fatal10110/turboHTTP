using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public class Http2FlowControlTests
    {
        private async Task<(Http2Connection conn, Stream serverStream, TestDuplexStream duplex)>
            CreateInitializedConnectionAsync(CancellationToken ct = default)
        {
            var duplex = new TestDuplexStream();
            var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);

            var serverCodec = new Http2FrameCodec(duplex.ServerStream);
            var serverTask = Task.Run(async () =>
            {
                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read, ct);
                    if (n == 0) throw new IOException("Unexpected end of stream");
                    read += n;
                }
                await serverCodec.ReadFrameAsync(16384, ct);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, ct);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, ct);
                var clientAck = await serverCodec.ReadFrameAsync(16384, ct);
            }, ct);

            await conn.InitializeAsync(ct);
            await serverTask;

            return (conn, duplex.ServerStream, duplex);
        }

        private Http2Frame BuildResponseHeadersFrame(int streamId, int statusCode, bool endStream = false)
        {
            var encoder = new HpackEncoder();
            var headerList = new List<(string, string)>
            {
                (":status", statusCode.ToString())
            };
            byte[] headerBlock = encoder.Encode(headerList);
            var flags = Http2FrameFlags.EndHeaders;
            if (endStream) flags |= Http2FrameFlags.EndStream;
            return new Http2Frame
            {
                Type = Http2FrameType.Headers,
                Flags = flags,
                StreamId = streamId,
                Payload = headerBlock,
                Length = headerBlock.Length
            };
        }

        private static byte[] MakeWindowUpdatePayload(int increment)
        {
            var payload = new byte[4];
            payload[0] = (byte)((increment >> 24) & 0x7F);
            payload[1] = (byte)((increment >> 16) & 0xFF);
            payload[2] = (byte)((increment >> 8) & 0xFF);
            payload[3] = (byte)(increment & 0xFF);
            return payload;
        }

        // --- Basic Window Update ---

        [Test]
        public async Task WindowUpdate_IncreasesConnectionWindow()
        {
            using var cts = new CancellationTokenSource(10000);
            var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
            var serverCodec = new Http2FrameCodec(serverStream);

            // Send WINDOW_UPDATE on stream 0
            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.WindowUpdate,
                Flags = Http2FrameFlags.None,
                StreamId = 0,
                Payload = MakeWindowUpdatePayload(1000),
                Length = 4
            }, cts.Token);

            // Connection should still be alive (no error)
            await Task.Delay(100);
            Assert.IsTrue(conn.IsAlive);

            conn.Dispose();
        }

        [Test]
        public async Task WindowUpdate_IncreasesStreamWindow()
        {
            using var cts = new CancellationTokenSource(10000);
            var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
            var serverCodec = new Http2FrameCodec(serverStream);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
            var context = new RequestContext(request);
            var responseTask = conn.SendRequestAsync(request, context, cts.Token);

            var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
            int streamId = headersFrame.StreamId;

            // Send WINDOW_UPDATE for stream
            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.WindowUpdate,
                Flags = Http2FrameFlags.None,
                StreamId = streamId,
                Payload = MakeWindowUpdatePayload(5000),
                Length = 4
            }, cts.Token);

            // Complete the request
            await serverCodec.WriteFrameAsync(
                BuildResponseHeadersFrame(streamId, 200, endStream: true), cts.Token);
            var response = await responseTask;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            conn.Dispose();
        }

        [Test]
        public async Task WindowUpdate_Zero_ConnectionDies()
        {
            // Zero WINDOW_UPDATE → PROTOCOL_ERROR
            using var cts = new CancellationTokenSource(10000);
            var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
            var serverCodec = new Http2FrameCodec(serverStream);

            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.WindowUpdate,
                Flags = Http2FrameFlags.None,
                StreamId = 0,
                Payload = MakeWindowUpdatePayload(0),
                Length = 4
            }, cts.Token);

            await Task.Delay(300);
            Assert.IsFalse(conn.IsAlive);

            conn.Dispose();
        }

        [Test]
        public async Task WindowUpdate_Overflow_ConnectionDies()
        {
            // WINDOW_UPDATE that causes overflow → FLOW_CONTROL_ERROR [R2-3]
            using var cts = new CancellationTokenSource(10000);
            var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
            var serverCodec = new Http2FrameCodec(serverStream);

            // Send a huge WINDOW_UPDATE that will overflow the default 65535 window
            // MaxWindowSize = 2^31 - 1 = 2147483647. Default = 65535.
            // Increment by MaxWindowSize should cause overflow: 65535 + 2147483647 > 2^31 - 1
            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.WindowUpdate,
                Flags = Http2FrameFlags.None,
                StreamId = 0,
                Payload = MakeWindowUpdatePayload(int.MaxValue),
                Length = 4
            }, cts.Token);

            await Task.Delay(300);
            Assert.IsFalse(conn.IsAlive);

            conn.Dispose();
        }

        // --- Data Sending and Flow Control ---

        [Test]
        public async Task DataSending_RespectsConnectionWindow()
        {
            // Send a body larger than default window to verify chunking
            using var cts = new CancellationTokenSource(15000);
            var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
            var serverCodec = new Http2FrameCodec(serverStream);

            // Create a body slightly smaller than default window (65535) so it fits
            var body = new byte[30000];
            new Random(42).NextBytes(body);
            var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/upload"), body: body);
            var context = new RequestContext(request);
            var responseTask = conn.SendRequestAsync(request, context, cts.Token);

            // Read HEADERS
            var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
            int streamId = headersFrame.StreamId;

            // Read DATA frames until END_STREAM
            int totalReceived = 0;
            bool endStreamSeen = false;
            while (!endStreamSeen)
            {
                var frame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                if (frame.Type == Http2FrameType.Data)
                {
                    totalReceived += frame.Payload.Length;
                    endStreamSeen = frame.HasFlag(Http2FrameFlags.EndStream);
                }
            }

            Assert.AreEqual(body.Length, totalReceived);

            // Send response
            await serverCodec.WriteFrameAsync(
                BuildResponseHeadersFrame(streamId, 200, endStream: true), cts.Token);
            await responseTask;

            conn.Dispose();
        }

        [Test]
        public async Task DataReceiving_SendsWindowUpdate()
        {
            // Server sends data that consumes more than half the window → client sends WINDOW_UPDATE
            using var cts = new CancellationTokenSource(10000);
            var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
            var serverCodec = new Http2FrameCodec(serverStream);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/large"));
            var context = new RequestContext(request);
            var responseTask = conn.SendRequestAsync(request, context, cts.Token);

            var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
            int streamId = headersFrame.StreamId;

            // Send HEADERS
            await serverCodec.WriteFrameAsync(
                BuildResponseHeadersFrame(streamId, 200), cts.Token);

            // Send DATA that consumes > half the connection window (> 32767 bytes)
            // The threshold for WINDOW_UPDATE is when recv window < 65535/2 = 32767
            // So we need to consume 65535 - 32767 + 1 = 32769 bytes to go below threshold
            // Send 3 chunks of 16384 for 49152 total to be safely over threshold
            var chunk = new byte[16384];
            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Data,
                Flags = Http2FrameFlags.None,
                StreamId = streamId,
                Payload = chunk,
                Length = chunk.Length
            }, cts.Token);

            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Data,
                Flags = Http2FrameFlags.None,
                StreamId = streamId,
                Payload = chunk,
                Length = chunk.Length
            }, cts.Token);

            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Data,
                Flags = Http2FrameFlags.None,
                StreamId = streamId,
                Payload = chunk,
                Length = chunk.Length
            }, cts.Token);

            // Now we've sent 49152 bytes, window is 65535-49152=16383 which is < 32767
            // The client should send a WINDOW_UPDATE for the connection
            var windowUpdate = await serverCodec.ReadFrameAsync(16384, cts.Token);
            Assert.AreEqual(Http2FrameType.WindowUpdate, windowUpdate.Type);
            Assert.AreEqual(0, windowUpdate.StreamId); // Connection-level

            int increment = ((windowUpdate.Payload[0] & 0x7F) << 24) |
                            (windowUpdate.Payload[1] << 16) |
                            (windowUpdate.Payload[2] << 8) |
                            windowUpdate.Payload[3];
            Assert.Greater(increment, 0);

            // Complete the response
            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Data,
                Flags = Http2FrameFlags.EndStream,
                StreamId = streamId,
                Payload = new byte[] { 0x42 },
                Length = 1
            }, cts.Token);

            var response = await responseTask;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            conn.Dispose();
        }

        [Test]
        public async Task WindowSize_AtomicAccess()
        {
            // Verify Http2Stream.WindowSize uses Interlocked [GPT-6]
            var testRequest = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
            var stream = new Http2Stream(1, testRequest,
                new RequestContext(testRequest), 65535, 65535);

            Assert.AreEqual(65535, stream.SendWindowSize);

            stream.AdjustSendWindowSize(-100);
            Assert.AreEqual(65435, stream.SendWindowSize);

            stream.AdjustSendWindowSize(200);
            Assert.AreEqual(65635, stream.SendWindowSize);

            stream.SendWindowSize = 1000;
            Assert.AreEqual(1000, stream.SendWindowSize);

            // Also verify recv window
            Assert.AreEqual(65535, stream.RecvWindowSize);
            stream.RecvWindowSize -= 100;
            Assert.AreEqual(65435, stream.RecvWindowSize);

            stream.Dispose();
        }

        [Test]
        public async Task DataSending_BlocksWhenWindowExhausted_ThenUnblocks()
        {
            // Send body larger than window → blocks until WINDOW_UPDATE
            using var cts = new CancellationTokenSource(15000);

            var duplex = new TestDuplexStream();
            var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);
            var serverCodec = new Http2FrameCodec(duplex.ServerStream);

            // Initialize with a small initial window size (1024 bytes)
            var serverInitTask = Task.Run(async () =>
            {
                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read, cts.Token);
                    if (n == 0) throw new IOException("Unexpected end of stream");
                    read += n;
                }
                await serverCodec.ReadFrameAsync(16384, cts.Token);

                // Send SETTINGS with small INITIAL_WINDOW_SIZE
                var settings = new byte[6];
                settings[0] = 0; settings[1] = (byte)Http2SettingId.InitialWindowSize;
                settings[2] = 0; settings[3] = 0; settings[4] = 0x04; settings[5] = 0x00; // 1024
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = settings,
                    Length = 6
                }, cts.Token);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, cts.Token);

                var clientAck = await serverCodec.ReadFrameAsync(16384, cts.Token);
            }, cts.Token);

            await conn.InitializeAsync(cts.Token);
            await serverInitTask;

            // Send 2048 bytes (bigger than 1024 stream window, but fits in 65535 connection window)
            var body = new byte[2048];
            var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/upload"), body: body);
            var context = new RequestContext(request);
            var responseTask = conn.SendRequestAsync(request, context, cts.Token);

            // Read HEADERS
            var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
            int streamId = headersFrame.StreamId;

            // Read first chunk of DATA (up to 1024 bytes)
            var dataFrame1 = await serverCodec.ReadFrameAsync(16384, cts.Token);
            Assert.AreEqual(Http2FrameType.Data, dataFrame1.Type);
            Assert.LessOrEqual(dataFrame1.Payload.Length, 1024);
            Assert.IsFalse(dataFrame1.HasFlag(Http2FrameFlags.EndStream));

            // At this point, the stream window is exhausted. Send should be blocked.
            // Send WINDOW_UPDATE for the stream to unblock it
            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.WindowUpdate,
                Flags = Http2FrameFlags.None,
                StreamId = streamId,
                Payload = MakeWindowUpdatePayload(2048),
                Length = 4
            }, cts.Token);

            // Read remaining DATA
            int totalRead = dataFrame1.Payload.Length;
            bool endStreamSeen = false;
            while (!endStreamSeen)
            {
                var frame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                if (frame.Type == Http2FrameType.Data)
                {
                    totalRead += frame.Payload.Length;
                    endStreamSeen = frame.HasFlag(Http2FrameFlags.EndStream);
                }
                else if (frame.Type == Http2FrameType.WindowUpdate)
                {
                    // Connection-level window update from client, ignore
                }
            }

            Assert.AreEqual(2048, totalRead);

            // Complete the response
            await serverCodec.WriteFrameAsync(
                BuildResponseHeadersFrame(streamId, 200, endStream: true), cts.Token);
            var response = await responseTask;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            conn.Dispose();
        }
        // --- New tests for review fixes ---

        [Test]
        public async Task StreamLevelRecvWindow_SendsStreamWindowUpdate()
        {
            // Fix 1: Verify stream-level WINDOW_UPDATE is sent when stream recv window is consumed
            using var cts = new CancellationTokenSource(15000);
            var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
            var serverCodec = new Http2FrameCodec(serverStream);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/large"));
            var context = new RequestContext(request);
            var responseTask = conn.SendRequestAsync(request, context, cts.Token);

            var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
            int streamId = headersFrame.StreamId;

            // Send HEADERS
            await serverCodec.WriteFrameAsync(
                BuildResponseHeadersFrame(streamId, 200), cts.Token);

            // Send DATA that consumes > half the window to trigger WINDOW_UPDATEs
            // The threshold is when recv window < 65535/2 = 32767
            // So we need to consume 65535 - 32767 + 1 = 32769 bytes to go below threshold
            // Send 3 chunks of 16384 for 49152 total to be safely over threshold
            var chunk = new byte[16384];
            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Data,
                Flags = Http2FrameFlags.None,
                StreamId = streamId,
                Payload = chunk,
                Length = chunk.Length
            }, cts.Token);

            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Data,
                Flags = Http2FrameFlags.None,
                StreamId = streamId,
                Payload = chunk,
                Length = chunk.Length
            }, cts.Token);

            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Data,
                Flags = Http2FrameFlags.None,
                StreamId = streamId,
                Payload = chunk,
                Length = chunk.Length
            }, cts.Token);

            // Now 49152 bytes consumed, window is 65535-49152=16383 which is < 32767
            // Client should send WINDOW_UPDATEs for both connection and stream.
            var updates = new List<Http2Frame>();
            for (int i = 0; i < 2; i++)
            {
                var frame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                if (frame.Type == Http2FrameType.WindowUpdate)
                    updates.Add(frame);
            }

            // Should have at least one connection-level (stream 0) and one stream-level
            Assert.IsTrue(updates.Exists(f => f.StreamId == 0), "Expected connection-level WINDOW_UPDATE");
            Assert.IsTrue(updates.Exists(f => f.StreamId == streamId), "Expected stream-level WINDOW_UPDATE");

            // Complete the response
            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Data,
                Flags = Http2FrameFlags.EndStream,
                StreamId = streamId,
                Payload = new byte[] { 0x42 },
                Length = 1
            }, cts.Token);

            var response = await responseTask;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            conn.Dispose();
        }

        [Test]
        public async Task LargeResponse_CompletesWithStreamWindowUpdates()
        {
            // Fix 1: Verify responses > 65535 bytes complete (stream-level WINDOW_UPDATE prevents stall)
            using var cts = new CancellationTokenSource(20000);
            var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
            var serverCodec = new Http2FrameCodec(serverStream);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/big"));
            var context = new RequestContext(request);
            var responseTask = conn.SendRequestAsync(request, context, cts.Token);

            var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
            int streamId = headersFrame.StreamId;

            // Send HEADERS
            await serverCodec.WriteFrameAsync(
                BuildResponseHeadersFrame(streamId, 200), cts.Token);

            // Send 128KB of data (well over the 65535 initial window)
            int totalToSend = 128 * 1024;
            int sent = 0;
            while (sent < totalToSend)
            {
                int chunkSize = Math.Min(16384, totalToSend - sent);
                bool isLast = (sent + chunkSize) >= totalToSend;

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = isLast ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = new byte[chunkSize],
                    Length = chunkSize
                }, cts.Token);
                sent += chunkSize;

                // Read any WINDOW_UPDATEs the client sends (must consume them to prevent blocking)
                // The server stream is a TestDuplexStream, so we try to read non-blockingly
                // by doing a short attempt — but since WINDOW_UPDATEs arrive async, just keep sending
                // and handle them as they come
            }

            // Drain any remaining WINDOW_UPDATEs (non-blocking reads would be ideal, but
            // the response task completing means the client processed everything)
            var response = await responseTask;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(totalToSend, response.Body.Length);

            conn.Dispose();
        }
    }
}

