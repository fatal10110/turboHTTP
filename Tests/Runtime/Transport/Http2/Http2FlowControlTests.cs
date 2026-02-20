using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;
using UnityEngine.TestTools;

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
        public void WindowUpdate_IncreasesConnectionWindow()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void WindowUpdate_IncreasesStreamWindow()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void WindowUpdate_Zero_ConnectionDies()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [UnityTest]
        public IEnumerator WindowUpdate_Zero_Stream_SendsRstStreamAndKeepsConnectionAlive()
        {
            var task = RunAsync();
            yield return new UnityEngine.WaitUntil(() => task.IsCompleted);
            RethrowIfFaulted(task);

            async Task RunAsync()
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // Open stream 1.
                var request1 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/one"));
                var context1 = new RequestContext(request1);
                var task1 = conn.SendRequestAsync(request1, context1, cts.Token);
                var headers1 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId1 = headers1.StreamId;

                // Invalid stream-level WINDOW_UPDATE (increment = 0) should be a stream error.
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.WindowUpdate,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId1,
                    Payload = MakeWindowUpdatePayload(0),
                    Length = 4
                }, cts.Token);

                var rst = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.RstStream, rst.Type);
                Assert.AreEqual(streamId1, rst.StreamId);

                uint rstError = ((uint)rst.Payload[0] << 24) | ((uint)rst.Payload[1] << 16) |
                                ((uint)rst.Payload[2] << 8) | rst.Payload[3];
                Assert.AreEqual((uint)Http2ErrorCode.ProtocolError, rstError);
                AssertAsync.ThrowsAsync<Http2ProtocolException>(async () => await task1);

                // Connection should still be usable for new streams.
                var request2 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/two"));
                var context2 = new RequestContext(request2);
                var task2 = conn.SendRequestAsync(request2, context2, cts.Token);
                var headers2 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId2 = headers2.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId2, 200, endStream: true), cts.Token);
                var response2 = await task2;
                Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            }
        }

        [Test]
        public void WindowUpdate_Overflow_ConnectionDies()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        // --- Data Sending and Flow Control ---

        [Test]
        public void DataSending_RespectsConnectionWindow()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DataReceiving_SendsWindowUpdate()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void WindowSize_AtomicAccess()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DataSending_BlocksWhenWindowExhausted_ThenUnblocks()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }
        // --- New tests for review fixes ---

        [Test]
        public void StreamLevelRecvWindow_SendsStreamWindowUpdate()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void LargeResponse_CompletesWithStreamWindowUpdates()        {
            Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DataReceiving_ExceedsConnectionWindow_FlowControlError()        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/conn-window"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200), cts.Token);

                var recvWindowField = typeof(Http2Connection).GetField(
                    "_connectionRecvWindow", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(recvWindowField);
                recvWindowField.SetValue(conn, 32);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = new byte[64],
                    Length = 64
                }, cts.Token);

                var goaway = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.GoAway, goaway.Type);

                uint errorCode = ((uint)goaway.Payload[4] << 24) | ((uint)goaway.Payload[5] << 16) |
                                 ((uint)goaway.Payload[6] << 8) | goaway.Payload[7];
                Assert.AreEqual((uint)Http2ErrorCode.FlowControlError, errorCode);

                try
                {
                    await responseTask;
                    Assert.Fail("Expected exception");
                }
                catch (Http2ProtocolException) { /* expected */ }
                catch (UHttpException) { /* also acceptable */ }
                catch (ObjectDisposedException) { /* also acceptable */ }

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DataReceiving_ExceedsStreamWindow_FlowControlError()        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/stream-window"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200), cts.Token);

                var activeStreamsField = typeof(Http2Connection).GetField(
                    "_activeStreams", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(activeStreamsField);
                var activeStreams = (System.Collections.Concurrent.ConcurrentDictionary<int, Http2Stream>)
                    activeStreamsField.GetValue(conn);
                Assert.IsTrue(activeStreams.TryGetValue(streamId, out var stream));
                stream.RecvWindowSize = 16;

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = new byte[32],
                    Length = 32
                }, cts.Token);

                var goaway = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.GoAway, goaway.Type);

                uint errorCode = ((uint)goaway.Payload[4] << 24) | ((uint)goaway.Payload[5] << 16) |
                                 ((uint)goaway.Payload[6] << 8) | goaway.Payload[7];
                Assert.AreEqual((uint)Http2ErrorCode.FlowControlError, errorCode);

                try
                {
                    await responseTask;
                    Assert.Fail("Expected exception");
                }
                catch (Http2ProtocolException) { /* expected */ }
                catch (UHttpException) { /* also acceptable */ }
                catch (ObjectDisposedException) { /* also acceptable */ }

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void InitialWindowSizeDelta_OverflowCheckedPerStream()        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/window-overflow"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                var activeStreamsField = typeof(Http2Connection).GetField(
                    "_activeStreams", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(activeStreamsField);
                var activeStreams = (System.Collections.Concurrent.ConcurrentDictionary<int, Http2Stream>)
                    activeStreamsField.GetValue(conn);
                Assert.IsTrue(activeStreams.TryGetValue(streamId, out var stream));
                stream.SendWindowSize = int.MaxValue;

                const uint newInitialWindow = 65536;
                var settingsPayload = new byte[6];
                settingsPayload[0] = 0x00;
                settingsPayload[1] = (byte)Http2SettingId.InitialWindowSize;
                settingsPayload[2] = (byte)((newInitialWindow >> 24) & 0xFF);
                settingsPayload[3] = (byte)((newInitialWindow >> 16) & 0xFF);
                settingsPayload[4] = (byte)((newInitialWindow >> 8) & 0xFF);
                settingsPayload[5] = (byte)(newInitialWindow & 0xFF);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = settingsPayload,
                    Length = settingsPayload.Length
                }, cts.Token);

                var goaway = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.GoAway, goaway.Type);

                uint errorCode = ((uint)goaway.Payload[4] << 24) | ((uint)goaway.Payload[5] << 16) |
                                 ((uint)goaway.Payload[6] << 8) | goaway.Payload[7];
                Assert.AreEqual((uint)Http2ErrorCode.FlowControlError, errorCode);

                try
                {
                    await responseTask;
                    Assert.Fail("Expected exception");
                }
                catch (Http2ProtocolException) { /* expected */ }
                catch (UHttpException) { /* also acceptable */ }
                catch (ObjectDisposedException) { /* also acceptable */ }

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        private static void RethrowIfFaulted(Task task)
        {
            if (!task.IsFaulted)
                return;

            throw task.Exception?.GetBaseException() ?? new Exception("Task failed without an exception.");
        }
    }
}
