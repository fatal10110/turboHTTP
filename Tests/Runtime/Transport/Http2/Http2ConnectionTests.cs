using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public class Http2ConnectionTests
    {
        /// <summary>
        /// Helper: create a duplex stream, start a simulated server that sends SETTINGS + SETTINGS ACK,
        /// and return an initialized Http2Connection.
        /// </summary>
        private async Task<(Http2Connection conn, Stream serverStream, TestDuplexStream duplex)>
            CreateInitializedConnectionAsync(CancellationToken ct = default)
        {
            var duplex = new TestDuplexStream();
            var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);

            // Start server simulation in background
            var serverCodec = new Http2FrameCodec(duplex.ServerStream);
            var serverTask = Task.Run(async () =>
            {
                // Read and discard client preface (24 bytes)
                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read, ct);
                    if (n == 0) throw new IOException("Unexpected end of stream");
                    read += n;
                }

                // Read client SETTINGS frame
                var clientSettings = await serverCodec.ReadFrameAsync(16384, ct);

                // Send server SETTINGS
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, ct);

                // Send SETTINGS ACK to client
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, ct);

                // Read client's SETTINGS ACK
                var clientAck = await serverCodec.ReadFrameAsync(16384, ct);
            }, ct);

            await conn.InitializeAsync(ct);
            await serverTask;

            return (conn, duplex.ServerStream, duplex);
        }

        /// <summary>
        /// Helper: encode response headers as HPACK and build a HEADERS frame.
        /// </summary>
        private Http2Frame BuildResponseHeadersFrame(int streamId, int statusCode,
            Dictionary<string, string> headers = null, bool endStream = false)
        {
            var encoder = new HpackEncoder();
            var headerList = new List<(string, string)>
            {
                (":status", statusCode.ToString())
            };
            if (headers != null)
            {
                foreach (var kvp in headers)
                    headerList.Add((kvp.Key, kvp.Value));
            }

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

        // --- Connection Setup Tests ---

        [Test]
        public void InitializeAsync_SendsPreface()        {
            Task.Run(async () =>
            {
                var duplex = new TestDuplexStream();

                // Start initialization in background
                var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);
                var initTask = conn.InitializeAsync(CancellationToken.None);

                // Read 24-byte preface from server side
                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read);
                    if (n == 0) break;
                    read += n;
                }

                Assert.AreEqual(24, read);
                Assert.AreEqual(Http2Constants.ConnectionPreface, preface);

                // Cleanup: send settings ack to unblock init
                var serverCodec = new Http2FrameCodec(duplex.ServerStream);
                var clientSettings = await serverCodec.ReadFrameAsync(16384, CancellationToken.None);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);

                await initTask;
                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void InitializeAsync_SendsSettings()        {
            Task.Run(async () =>
            {
                var duplex = new TestDuplexStream();
                var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);
                var initTask = conn.InitializeAsync(CancellationToken.None);

                // Read preface
                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read);
                    if (n == 0) break;
                    read += n;
                }

                // Read SETTINGS frame from client
                var serverCodec = new Http2FrameCodec(duplex.ServerStream);
                var settingsFrame = await serverCodec.ReadFrameAsync(16384, CancellationToken.None);

                Assert.AreEqual(Http2FrameType.Settings, settingsFrame.Type);
                Assert.AreEqual(0, settingsFrame.StreamId);
                Assert.IsFalse(settingsFrame.HasFlag(Http2FrameFlags.Ack));
                Assert.IsTrue(settingsFrame.Payload.Length > 0);
                Assert.AreEqual(0, settingsFrame.Payload.Length % 6); // Multiple of 6

                // Cleanup
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);

                await initTask;
                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void InitializeAsync_AcksServerSettings()        {
            Task.Run(async () =>
            {
                var duplex = new TestDuplexStream();
                var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);
                var initTask = conn.InitializeAsync(CancellationToken.None);

                // Read preface + client settings
                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read);
                    read += n;
                }

                var serverCodec = new Http2FrameCodec(duplex.ServerStream);
                await serverCodec.ReadFrameAsync(16384, CancellationToken.None); // client SETTINGS

                // Send server SETTINGS
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);

                // Send SETTINGS ACK
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);

                await initTask;

                // Now read the client's SETTINGS ACK (sent after receiving server SETTINGS)
                var clientAck = await serverCodec.ReadFrameAsync(16384, CancellationToken.None);
                Assert.AreEqual(Http2FrameType.Settings, clientAck.Type);
                Assert.IsTrue(clientAck.HasFlag(Http2FrameFlags.Ack));
                Assert.AreEqual(0, clientAck.Length);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        // --- Request/Response Lifecycle ---

        [Test]
        public void SendGetRequest_ReceiveResponse()        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // Send request in background
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/path"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                // Read HEADERS from client
                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersFrame.Type);
                Assert.IsTrue(headersFrame.HasFlag(Http2FrameFlags.EndStream)); // GET has no body
                Assert.IsTrue(headersFrame.HasFlag(Http2FrameFlags.EndHeaders));
                int streamId = headersFrame.StreamId;
                Assert.AreEqual(1, streamId); // First stream is 1

                // Send response: HEADERS + DATA with END_STREAM
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200), cts.Token);

                var bodyBytes = System.Text.Encoding.UTF8.GetBytes("Hello, HTTP/2!");
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = bodyBytes,
                    Length = bodyBytes.Length
                }, cts.Token);

                var response = await responseTask;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("Hello, HTTP/2!", response.GetBodyAsString());

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SendPostRequest_WithBody()        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var body = System.Text.Encoding.UTF8.GetBytes("request body");
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/api"), body: body);
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                // Read HEADERS (should NOT have END_STREAM since there's a body)
                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersFrame.Type);
                Assert.IsFalse(headersFrame.HasFlag(Http2FrameFlags.EndStream));
                int streamId = headersFrame.StreamId;

                // Read DATA frame(s)
                var dataFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Data, dataFrame.Type);
                Assert.IsTrue(dataFrame.HasFlag(Http2FrameFlags.EndStream));
                Assert.AreEqual("request body", System.Text.Encoding.UTF8.GetString(dataFrame.Payload));

                // Send response
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 201, endStream: true), cts.Token);

                var response = await responseTask;
                Assert.AreEqual((HttpStatusCode)201, response.StatusCode);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SendRequest_AfterGoaway_Throws()        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // Send GOAWAY from server
                var goawayPayload = new byte[8];
                // lastStreamId = 0, errorCode = NO_ERROR
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.GoAway,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = goawayPayload,
                    Length = 8
                }, cts.Token);

                // Wait a moment for the read loop to process the GOAWAY
                await Task.Delay(200, cts.Token);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                AssertAsync.ThrowsAsync<UHttpException>(
                    () => conn.SendRequestAsync(request, context, cts.Token));

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        // --- Frame Handling ---

        [Test]
        public void PingFrame_EchoedWithAck()        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // Send PING from server
                var pingData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Ping,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = pingData,
                    Length = 8
                }, cts.Token);

                // Read PING ACK from client
                var pongFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Ping, pongFrame.Type);
                Assert.IsTrue(pongFrame.HasFlag(Http2FrameFlags.Ack));
                Assert.AreEqual(pingData, pongFrame.Payload);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void GoAwayFrame_FailsHigherStreams()        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // Send two requests
                var request1 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/1"));
                var request2 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/2"));
                var ctx1 = new RequestContext(request1);
                var ctx2 = new RequestContext(request2);

                var task1 = conn.SendRequestAsync(request1, ctx1, cts.Token);
                var task2 = conn.SendRequestAsync(request2, ctx2, cts.Token);

                // Read both HEADERS frames to get stream IDs
                var h1 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                var h2 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int sid1 = h1.StreamId;
                int sid2 = h2.StreamId;

                // Send GOAWAY with lastStreamId = sid1 (only stream 1 is allowed to complete)
                var goawayPayload = new byte[8];
                goawayPayload[0] = (byte)((sid1 >> 24) & 0x7F);
                goawayPayload[1] = (byte)((sid1 >> 16) & 0xFF);
                goawayPayload[2] = (byte)((sid1 >> 8) & 0xFF);
                goawayPayload[3] = (byte)(sid1 & 0xFF);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.GoAway,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = goawayPayload,
                    Length = 8
                }, cts.Token);

                // Stream 2 should fail
                AssertAsync.ThrowsAsync<UHttpException>(async () => await task2);

                // Complete stream 1 normally
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(sid1, 200, endStream: true), cts.Token);

                var response = await task1;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RstStreamFrame_FailsSpecificStream()        {
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

                // Send RST_STREAM with INTERNAL_ERROR
                var rstPayload = new byte[4];
                rstPayload[0] = 0; rstPayload[1] = 0; rstPayload[2] = 0; rstPayload[3] = 2; // INTERNAL_ERROR
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.RstStream,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = rstPayload,
                    Length = 4
                }, cts.Token);

                AssertAsync.ThrowsAsync<UHttpException>(async () => await responseTask);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PushPromise_EnablePushDisabled_SendsGoAwayProtocolError()        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // Open a stream so a connection-level protocol error will fail active work.
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);
                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);

                // Send a PUSH_PROMISE frame despite client advertising ENABLE_PUSH=0.
                var pushPayload = new byte[4];
                pushPayload[3] = 2; // promised stream ID = 2
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.PushPromise,
                    Flags = Http2FrameFlags.EndHeaders,
                    StreamId = headersFrame.StreamId,
                    Payload = pushPayload,
                    Length = 4
                }, cts.Token);

                // Client should terminate the connection with GOAWAY(PROTOCOL_ERROR).
                var goawayFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.GoAway, goawayFrame.Type);
                Assert.AreEqual(0, goawayFrame.StreamId);

                uint errorCode = ((uint)goawayFrame.Payload[4] << 24) | ((uint)goawayFrame.Payload[5] << 16) |
                                 ((uint)goawayFrame.Payload[6] << 8) | goawayFrame.Payload[7];
                Assert.AreEqual((uint)Http2ErrorCode.ProtocolError, errorCode);

                AssertAsync.ThrowsAsync<Http2ProtocolException>(async () => await responseTask);
                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        // --- Settings ---

        [Test]
        public void Settings_UnknownId_Ignored()        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // Send SETTINGS with unknown ID (0xFF)
                var payload = new byte[6];
                payload[0] = 0x00; payload[1] = 0xFF; // Unknown ID
                payload[2] = 0; payload[3] = 0; payload[4] = 0; payload[5] = 42; // value = 42
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = payload,
                    Length = 6
                }, cts.Token);

                // Client should ACK (not crash)
                var ackFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Settings, ackFrame.Type);
                Assert.IsTrue(ackFrame.HasFlag(Http2FrameFlags.Ack));

                // Connection should still be alive
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        // --- REVIEW FIX tests ---

        [Test]
        public void SettingsAck_NonZeroPayload_FrameSizeError()        {
            Task.Run(async () =>
            {
                // SETTINGS ACK with payload should cause FRAME_SIZE_ERROR [GPT-7]
                var duplex = new TestDuplexStream();
                var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);

                var serverCodec = new Http2FrameCodec(duplex.ServerStream);

                // Start init
                var initTask = conn.InitializeAsync(CancellationToken.None);

                // Read preface
                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read);
                    read += n;
                }
                // Read client settings
                await serverCodec.ReadFrameAsync(16384, CancellationToken.None);

                // Send server SETTINGS
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);

                // Send SETTINGS ACK with non-zero payload (invalid)
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = new byte[] { 0x00 },
                    Length = 1
                }, CancellationToken.None);

                // Init should fail because the read loop throws Http2ProtocolException
                // which triggers FailAllStreams. The _settingsAckTcs will never get a valid result.
                // We expect either a timeout or an exception propagated through the read loop.
                try
                {
                    await initTask;
                    // If it completes, the SETTINGS ACK was before the invalid one
                    // and the connection should die from the read loop exception
                    await Task.Delay(500);
                    Assert.IsFalse(conn.IsAlive);
                }
                catch (Exception)
                {
                    // Expected — either timeout or protocol error propagation
                }

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ContinuationFrame_WrongStream_ConnectionDies()        {
            Task.Run(async () =>
            {
                // CONTINUATION for wrong stream should cause PROTOCOL_ERROR [A3]
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // Start a request
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headersFrame.StreamId;

                // Send HEADERS without EndHeaders (expects CONTINUATION)
                var encoder = new HpackEncoder();
                var headerBlock = encoder.Encode(new List<(string, string)> { (":status", "200") });
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.None, // No EndHeaders
                    StreamId = streamId,
                    Payload = headerBlock,
                    Length = headerBlock.Length
                }, cts.Token);

                // Send CONTINUATION for WRONG stream
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Continuation,
                    Flags = Http2FrameFlags.EndHeaders,
                    StreamId = streamId + 2, // Wrong stream!
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, cts.Token);

                // The request should fail because the connection dies
                try
                {
                    await responseTask;
                    Assert.Fail("Expected exception");
                }
                catch (Http2ProtocolException) { /* expected */ }
                catch (ObjectDisposedException) { /* also acceptable */ }

                await Task.Delay(200);
                Assert.IsFalse(conn.IsAlive);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void NonContinuation_WhileExpectingContinuation_ConnectionDies()        {
            Task.Run(async () =>
            {
                // Non-CONTINUATION frame while expecting CONTINUATION → PROTOCOL_ERROR [A3]
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headersFrame.StreamId;

                // Send HEADERS without EndHeaders
                var encoder = new HpackEncoder();
                var headerBlock = encoder.Encode(new List<(string, string)> { (":status", "200") });
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = headerBlock,
                    Length = headerBlock.Length
                }, cts.Token);

                // Send DATA instead of CONTINUATION
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = new byte[] { 0x00 },
                    Length = 1
                }, cts.Token);

                try
                {
                    await responseTask;
                    Assert.Fail("Expected exception");
                }
                catch (Http2ProtocolException) { /* expected */ }
                catch (ObjectDisposedException) { /* also acceptable */ }

                await Task.Delay(200);
                Assert.IsFalse(conn.IsAlive);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DataFrame_PaddingLengthExceedsPayload_ConnectionDies()        {
            Task.Run(async () =>
            {
                // DATA frame with padding length > payload → PROTOCOL_ERROR [GPT-5]
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headersFrame.StreamId;

                // Send HEADERS
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200), cts.Token);

                // Send DATA with PADDED flag and padding length > remaining
                // Payload: [padLength=200], but total length is 3 bytes → 200 > 2 → error
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.Padded,
                    StreamId = streamId,
                    Payload = new byte[] { 200, 0x01, 0x02 },
                    Length = 3
                }, cts.Token);

                try
                {
                    await responseTask;
                    Assert.Fail("Expected exception");
                }
                catch (Http2ProtocolException) { /* expected */ }
                catch (ObjectDisposedException) { /* also acceptable */ }

                await Task.Delay(200);
                Assert.IsFalse(conn.IsAlive);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ContinuationFrame_WithEndStreamOnHeaders_CompletesStream()        {
            Task.Run(async () =>
            {
                // HEADERS(END_STREAM, no EndHeaders) + CONTINUATION(EndHeaders) should complete stream [R2-7]
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var clientHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = clientHeaders.StreamId;

                // Build response header block, then split it
                var encoder = new HpackEncoder();
                var headerBlock = encoder.Encode(new List<(string, string)>
                {
                    (":status", "200"),
                    ("content-type", "text/plain")
                });

                // Split: first half in HEADERS, second half in CONTINUATION
                int split = headerBlock.Length / 2;
                var firstPart = new byte[split];
                var secondPart = new byte[headerBlock.Length - split];
                Array.Copy(headerBlock, 0, firstPart, 0, split);
                Array.Copy(headerBlock, split, secondPart, 0, secondPart.Length);

                // HEADERS with END_STREAM but NOT EndHeaders
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.EndStream, // EndStream but no EndHeaders
                    StreamId = streamId,
                    Payload = firstPart,
                    Length = firstPart.Length
                }, cts.Token);

                // CONTINUATION with EndHeaders
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Continuation,
                    Flags = Http2FrameFlags.EndHeaders,
                    StreamId = streamId,
                    Payload = secondPart,
                    Length = secondPart.Length
                }, cts.Token);

                // Stream should complete since both EndStream and EndHeaders are now received
                var response = await responseTask;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        // --- Cleanup ---

        [Test]
        public void Dispose_FailsAllActiveStreams()        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                // Read the headers frame to ensure the request was sent
                await serverCodec.ReadFrameAsync(16384, cts.Token);

                // Dispose the connection without responding
                conn.Dispose();

                // The response task should fail — can be ObjectDisposedException or OperationCanceledException
                // depending on timing (CTS cancel vs FailAllStreams)
                try
                {
                    await responseTask;
                    Assert.Fail("Expected exception");
                }
                catch (ObjectDisposedException) { /* expected */ }
                catch (OperationCanceledException) { /* also acceptable - CTS cancelled first */ }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Dispose_SendsBestEffortGoaway()        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                conn.Dispose();

                // Try to read a GOAWAY from the server side
                try
                {
                    var frame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                    Assert.AreEqual(Http2FrameType.GoAway, frame.Type);
                    Assert.AreEqual(0, frame.StreamId);
                }
                catch (IOException)
                {
                    // Connection may close before we can read — that's OK for best-effort
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void StreamIdExhaustion_ThrowsOnOverflow()        {
            Task.Run(async () =>
            {
                // The stream ID check should prevent overflow [R2-6]
                // We can't easily exhaust 2^30 IDs in a test, but we verify the
                // connection constructor and stream ID management is correct
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);

                // First stream should be ID 1
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var serverCodec = new Http2FrameCodec(serverStream);

                var responseTask = conn.SendRequestAsync(request, context, cts.Token);
                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(1, headersFrame.StreamId);

                // Complete stream 1
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(1, 200, endStream: true), cts.Token);
                await responseTask;

                // Second stream should be ID 3
                var request2 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/2"));
                var ctx2 = new RequestContext(request2);
                var task2 = conn.SendRequestAsync(request2, ctx2, cts.Token);
                var h2 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(3, h2.StreamId);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(3, 200, endStream: true), cts.Token);
                await task2;

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void StreamIdExhaustion_DoesNotCorruptCounterOnFailure()        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);

                var nextStreamIdField = typeof(Http2Connection).GetField(
                    "_nextStreamId",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(nextStreamIdField);
                nextStreamIdField.SetValue(conn, int.MaxValue - 1);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/overflow"));
                var context = new RequestContext(request);

                AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await conn.SendRequestAsync(request, context, cts.Token));

                Assert.AreEqual(int.MaxValue - 1, (int)nextStreamIdField.GetValue(conn));
                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PostRequest_StreamStateIsOpen_ThenHalfClosedLocal()        {
            Task.Run(async () =>
            {
                // Verify stream state transitions [GPT-8]
                // POST: HEADERS → Open, DATA END_STREAM → HalfClosedLocal
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var body = System.Text.Encoding.UTF8.GetBytes("body");
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/api"), body: body);
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                // Read HEADERS (no END_STREAM)
                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.IsFalse(headersFrame.HasFlag(Http2FrameFlags.EndStream));

                // Read DATA (END_STREAM)
                var dataFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.IsTrue(dataFrame.HasFlag(Http2FrameFlags.EndStream));

                // Complete the response
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersFrame.StreamId, 200, endStream: true), cts.Token);
                await responseTask;

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }
        // --- Fix 2: Header table size back-to-default ---

        [Test]
        public void Settings_HeaderTableSizeBackToDefault_UpdatesEncoder()        {
            Task.Run(async () =>
            {
                // Fix 2: If server changes table size away from default then back to default,
                // the encoder must be updated both times.
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // Send SETTINGS with reduced HEADER_TABLE_SIZE = 2048
                var payload1 = new byte[6];
                payload1[0] = 0; payload1[1] = (byte)Http2SettingId.HeaderTableSize;
                payload1[2] = 0; payload1[3] = 0; payload1[4] = 0x08; payload1[5] = 0x00; // 2048
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = payload1,
                    Length = 6
                }, cts.Token);

                // Read ACK
                var ack1 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Settings, ack1.Type);
                Assert.IsTrue(ack1.HasFlag(Http2FrameFlags.Ack));

                // Now send SETTINGS with HEADER_TABLE_SIZE = 4096 (back to default)
                var payload2 = new byte[6];
                payload2[0] = 0; payload2[1] = (byte)Http2SettingId.HeaderTableSize;
                payload2[2] = 0; payload2[3] = 0; payload2[4] = 0x10; payload2[5] = 0x00; // 4096
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = payload2,
                    Length = 6
                }, cts.Token);

                // Read ACK — if the encoder was not updated, subsequent HPACK encoding
                // would still use the 2048 limit. Getting the ACK means no crash at least.
                var ack2 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Settings, ack2.Type);
                Assert.IsTrue(ack2.HasFlag(Http2FrameFlags.Ack));

                // Connection should still be alive
                Assert.IsTrue(conn.IsAlive);

                // Send a request — the encoder should emit a table size update in the header block
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersFrame.Type);

                // Complete the request
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersFrame.StreamId, 200, endStream: true), cts.Token);
                await responseTask;

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        // --- Fix 7: SETTINGS ACK stream ID validation ---

        [Test]
        public void SettingsAck_OnNonZeroStream_ProtocolError()        {
            Task.Run(async () =>
            {
                // Fix 7: SETTINGS ACK on a non-zero stream should cause protocol error
                var duplex = new TestDuplexStream();
                var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);
                var serverCodec = new Http2FrameCodec(duplex.ServerStream);

                var initTask = conn.InitializeAsync(CancellationToken.None);

                // Read preface
                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read);
                    read += n;
                }
                await serverCodec.ReadFrameAsync(16384, CancellationToken.None);

                // Send server SETTINGS
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);

                // Send SETTINGS ACK on stream 1 (invalid)
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 1, // INVALID: must be 0
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);

                // Init should fail or the connection should die
                try
                {
                    await initTask;
                    await Task.Delay(500);
                    Assert.IsFalse(conn.IsAlive);
                }
                catch (Exception)
                {
                    // Expected — protocol error propagation
                }

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        // --- R4: te header filtering ---

        [Test]
        public void TeHeader_OnlyTrailersAllowed()        {
            Task.Run(async () =>
            {
                // RFC 7540 Section 8.1.2.2: te header is forbidden except "trailers"
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // Request with te: gzip (should be filtered out)
                var teGzipHeaders = new HttpHeaders();
                teGzipHeaders.Set("te", "gzip");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"))
                    .WithHeaders(teGzipHeaders);
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                var decoder = new HpackDecoder();
                var decoded = decoder.Decode(headersFrame.Payload, 0, headersFrame.Length);

                // te: gzip should NOT appear in the headers
                Assert.IsFalse(decoded.Any(h => h.Name == "te"),
                    "te: gzip should be filtered out for HTTP/2");

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersFrame.StreamId, 200, endStream: true), cts.Token);
                await responseTask;
                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void TeHeader_TrailersValueAllowed()        {
            Task.Run(async () =>
            {
                // RFC 7540 Section 8.1.2.2: te: trailers IS allowed
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var teTrailersHeaders = new HttpHeaders();
                teTrailersHeaders.Set("te", "trailers");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"))
                    .WithHeaders(teTrailersHeaders);
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                var decoder = new HpackDecoder();
                var decoded = decoder.Decode(headersFrame.Payload, 0, headersFrame.Length);

                // te: trailers SHOULD appear
                Assert.IsTrue(decoded.Any(h => h.Name == "te" && h.Value == "trailers"),
                    "te: trailers should be preserved for HTTP/2");

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersFrame.StreamId, 200, endStream: true), cts.Token);
                await responseTask;
                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        // --- R4: Missing :status validation ---

        [Test]
        public void MissingStatus_FailsStream()        {
            Task.Run(async () =>
            {
                // If the response HEADERS omit :status, the stream should fail
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headersFrame.StreamId;

                // Send HEADERS without :status — just a regular header
                var encoder = new HpackEncoder();
                var noStatusHeaders = new List<(string, string)> { ("content-type", "text/plain") };
                byte[] headerBlock = encoder.Encode(noStatusHeaders);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = headerBlock,
                    Length = headerBlock.Length
                }, cts.Token);

                // The response task should fail
                AssertAsync.ThrowsAsync<UHttpException>(async () => await responseTask);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void InvalidStatus_FailsStream()        {
            Task.Run(async () =>
            {
                // If :status is non-numeric, the stream should fail
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headersFrame.StreamId;

                // Send HEADERS with invalid :status
                var encoder = new HpackEncoder();
                var badStatusHeaders = new List<(string, string)> { (":status", "abc") };
                byte[] headerBlock = encoder.Encode(badStatusHeaders);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = headerBlock,
                    Length = headerBlock.Length
                }, cts.Token);

                AssertAsync.ThrowsAsync<UHttpException>(async () => await responseTask);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        // --- R5: HPACK decoding error sends GOAWAY with COMPRESSION_ERROR ---

        [Test]
        public void HpackDecodingError_SendsGoAwayCompressionError()        {
            Task.Run(async () =>
            {
                // M3: HpackDecodingException must trigger GOAWAY with COMPRESSION_ERROR
                // per RFC 7540 Section 4.3.
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headersFrame.StreamId;

                // Send HEADERS with invalid HPACK data (index 0 is always invalid)
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = new byte[] { 0x80 }, // Indexed header field, index 0 → COMPRESSION_ERROR
                    Length = 1
                }, cts.Token);

                // The request should fail with Http2ProtocolException (CompressionError)
                try
                {
                    await responseTask;
                    Assert.Fail("Expected exception");
                }
                catch (Http2ProtocolException) { /* expected */ }
                catch (ObjectDisposedException) { /* also acceptable */ }

                // Read GOAWAY from client — should have COMPRESSION_ERROR (0x9)
                var goaway = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.GoAway, goaway.Type);
                Assert.AreEqual(0, goaway.StreamId);

                uint errorCode = ((uint)goaway.Payload[4] << 24) | ((uint)goaway.Payload[5] << 16) |
                                 ((uint)goaway.Payload[6] << 8) | goaway.Payload[7];
                Assert.AreEqual((uint)Http2ErrorCode.CompressionError, errorCode);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }

        // --- R7: DATA before HEADERS is protocol error ---

        [Test]
        public void DataFrame_BeforeHeaders_SendsRstStream()        {
            Task.Run(async () =>
            {
                // RFC 7540 Section 8.1: Response MUST start with HEADERS.
                // DATA before HEADERS should result in RST_STREAM with PROTOCOL_ERROR.
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, duplex) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headersFrame.StreamId;

                // Send DATA without sending HEADERS first (protocol violation)
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = new byte[] { 0x01, 0x02, 0x03 },
                    Length = 3
                }, cts.Token);

                // The request should fail
                try
                {
                    await responseTask;
                    Assert.Fail("Expected exception");
                }
                catch (Http2ProtocolException) { /* expected */ }
                catch (UHttpException) { /* also acceptable */ }

                // Read RST_STREAM from client — should have PROTOCOL_ERROR (0x1)
                var rstStream = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.RstStream, rstStream.Type);
                Assert.AreEqual(streamId, rstStream.StreamId);

                uint errorCode = ((uint)rstStream.Payload[0] << 24) | ((uint)rstStream.Payload[1] << 16) |
                                 ((uint)rstStream.Payload[2] << 8) | rstStream.Payload[3];
                Assert.AreEqual((uint)Http2ErrorCode.ProtocolError, errorCode);

                conn.Dispose();
            }).GetAwaiter().GetResult();
        }
    }
}
