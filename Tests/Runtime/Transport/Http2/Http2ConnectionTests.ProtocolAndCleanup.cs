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
using TurboHTTP.Tests;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    public partial class Http2ConnectionTests
    {
        // --- Cleanup ---

        [Test]
        public void Dispose_FailsAllActiveStreams()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                await serverCodec.ReadFrameAsync(16384, cts.Token);

                conn.Dispose();

                try
                {
                    await responseTask;
                    Assert.Fail("Expected exception");
                }
                catch (UHttpException ex) when (ex.InnerException is ObjectDisposedException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        [Test]
        public void Dispose_SendsBestEffortGoaway()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                conn.Dispose();

                try
                {
                    var frame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                    Assert.AreEqual(Http2FrameType.GoAway, frame.Type);
                    Assert.AreEqual(0, frame.StreamId);
                    int lastStreamId = ((frame.Payload[0] & 0x7F) << 24)
                        | (frame.Payload[1] << 16)
                        | (frame.Payload[2] << 8)
                        | frame.Payload[3];
                    Assert.AreEqual(0, lastStreamId);
                }
                catch (IOException)
                {
                }
            });
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, _, _) = await CreateInitializedConnectionAsync(cts.Token);

                Assert.DoesNotThrow(() => conn.Dispose());
                Assert.DoesNotThrow(() => conn.Dispose());
            });
        }

        [Test]
        public void StreamIdExhaustion_ThrowsOnOverflow()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var serverCodec = new Http2FrameCodec(serverStream);

                var responseTask = conn.SendRequestAsync(request, context, cts.Token);
                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(1, headersFrame.StreamId);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(1, 200, endStream: true),
                    cts.Token);
                await responseTask;

                var request2 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/2"));
                var ctx2 = new RequestContext(request2);
                var task2 = conn.SendRequestAsync(request2, ctx2, cts.Token);
                var h2 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(3, h2.StreamId);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(3, 200, endStream: true),
                    cts.Token);
                await task2;

                conn.Dispose();
            });
        }

        [Test]
        public void StreamIdExhaustion_DoesNotCorruptCounterOnFailure()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, _, _) = await CreateInitializedConnectionAsync(cts.Token);

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
            });
        }

        [Test]
        public void PostRequest_StreamStateIsOpen_ThenHalfClosedLocal()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var body = System.Text.Encoding.UTF8.GetBytes("body");
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("https://test.example.com/api"),
                    body: body);
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.IsFalse(headersFrame.HasFlag(Http2FrameFlags.EndStream));

                var dataFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.IsTrue(dataFrame.HasFlag(Http2FrameFlags.EndStream));

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersFrame.StreamId, 200, endStream: true),
                    cts.Token);
                await responseTask;

                conn.Dispose();
            });
        }

        // --- Fix 2: Header table size back-to-default ---

        [Test]
        public void Settings_HeaderTableSizeBackToDefault_UpdatesEncoder()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var payload1 = new byte[6];
                payload1[1] = (byte)Http2SettingId.HeaderTableSize;
                payload1[4] = 0x08;
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = payload1,
                    Length = 6
                }, cts.Token);

                var ack1 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Settings, ack1.Type);
                Assert.IsTrue(ack1.HasFlag(Http2FrameFlags.Ack));

                var payload2 = new byte[6];
                payload2[1] = (byte)Http2SettingId.HeaderTableSize;
                payload2[4] = 0x10;
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = payload2,
                    Length = 6
                }, cts.Token);

                var ack2 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Settings, ack2.Type);
                Assert.IsTrue(ack2.HasFlag(Http2FrameFlags.Ack));
                Assert.IsTrue(conn.IsAlive);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersFrame.Type);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersFrame.StreamId, 200, endStream: true),
                    cts.Token);
                await responseTask;

                conn.Dispose();
            });
        }

        // --- Fix 7: SETTINGS ACK stream ID validation ---

        [Test]
        public void SettingsAck_OnNonZeroStream_ProtocolError()
        {
            AssertAsync.Run(async () =>
            {
                var duplex = new TestDuplexStream();
                var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);
                var serverCodec = new Http2FrameCodec(duplex.ServerStream);

                var initTask = conn.InitializeAsync(CancellationToken.None);

                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read);
                    read += n;
                }

                await serverCodec.ReadFrameAsync(16384, CancellationToken.None);

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
                    StreamId = 1,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);

                try
                {
                    await initTask;
                    await Task.Delay(500);
                    Assert.IsFalse(conn.IsAlive);
                }
                catch (Exception)
                {
                }

                conn.Dispose();
            });
        }

        // --- R4: te header filtering ---

        [Test]
        public void TeHeader_OnlyTrailersAllowed()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var teGzipHeaders = new HttpHeaders();
                teGzipHeaders.Set("te", "gzip");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"))
                    .WithHeaders(teGzipHeaders);
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                var decoder = new HpackDecoder();
                var decoded = decoder.Decode(headersFrame.Payload, 0, headersFrame.Length);

                Assert.IsFalse(decoded.Any(h => h.Name == "te"), "te: gzip should be filtered out for HTTP/2");

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersFrame.StreamId, 200, endStream: true),
                    cts.Token);
                await responseTask;
                conn.Dispose();
            });
        }

        [Test]
        public void TeHeader_TrailersValueAllowed()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
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

                Assert.IsTrue(
                    decoded.Any(h => h.Name == "te" && h.Value == "trailers"),
                    "te: trailers should be preserved for HTTP/2");

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersFrame.StreamId, 200, endStream: true),
                    cts.Token);
                await responseTask;
                conn.Dispose();
            });
        }

        // --- R4: Missing :status validation ---

        [Test]
        public void MissingStatus_FailsStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headersFrame.StreamId;

                var encoder = new HpackEncoder();
                byte[] headerBlock = encoder.Encode(
                    new List<(string, string)> { ("content-type", "text/plain") }).ToArray();

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
            });
        }

        [Test]
        public void InvalidStatus_FailsStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headersFrame.StreamId;

                var encoder = new HpackEncoder();
                byte[] headerBlock = encoder.Encode(
                    new List<(string, string)> { (":status", "abc") }).ToArray();

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
            });
        }

        [Test]
        public void UnexpectedResponsePseudoHeader_FailsOnlyAffectedStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/illegal-pseudo"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                var encoder = new HpackEncoder();
                byte[] headerBlock = encoder.Encode(
                    new List<(string, string)>
                    {
                        (":status", "200"),
                        (":path", "/illegal")
                    }).ToArray();

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = headerBlock,
                    Length = headerBlock.Length
                }, cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.That(ex.Message, Does.Contain("Unexpected pseudo-header"));
                Assert.IsTrue(conn.IsAlive);

                var followUpRequest = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/follow-up"));
                var followUpContext = new RequestContext(followUpRequest);
                var followUpTask = conn.SendRequestAsync(followUpRequest, followUpContext, cts.Token);

                var followUpHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(followUpHeaders.StreamId, 200, endStream: true),
                    cts.Token);

                using var followUpResponse = await followUpTask;
                Assert.AreEqual(HttpStatusCode.OK, followUpResponse.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void TrailingHeaders_WithoutStatus_AreAccepted()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200, endStream: false),
                    cts.Token);

                var bodyChunk = System.Text.Encoding.UTF8.GetBytes("hello");
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = bodyChunk,
                    Length = bodyChunk.Length
                }, cts.Token);

                var trailerEncoder = new HpackEncoder();
                var trailerBlock = trailerEncoder.Encode(new List<(string, string)>
                {
                    ("grpc-status", "0")
                }).ToArray();

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = trailerBlock,
                    Length = trailerBlock.Length
                }, cts.Token);

                var response = await responseTask;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("hello", response.GetBodyAsString());
                Assert.IsNull(response.Headers.Get("grpc-status"));

                conn.Dispose();
            });
        }

        [Test]
        public void TrailingHeaders_WithoutEndStream_FailsOnlyAffectedStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/trailing-no-endstream"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200, endStream: false),
                    cts.Token);

                var trailerEncoder = new HpackEncoder();
                var trailerBlock = trailerEncoder.Encode(new List<(string, string)>
                {
                    ("grpc-status", "0")
                }).ToArray();

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.EndHeaders,
                    StreamId = streamId,
                    Payload = trailerBlock,
                    Length = trailerBlock.Length
                }, cts.Token);

                var completed = await Task.WhenAny(responseTask, Task.Delay(1000, cts.Token));
                Assert.AreSame(responseTask, completed);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.That(ex.Message, Does.Contain("without END_STREAM"));
                Assert.IsTrue(conn.IsAlive);

                var followUpRequest = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/follow-up-after-trailer-failure"));
                var followUpContext = new RequestContext(followUpRequest);
                var followUpTask = conn.SendRequestAsync(followUpRequest, followUpContext, cts.Token);

                var followUpHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(followUpHeaders.StreamId, 200, endStream: true),
                    cts.Token);

                using var followUpResponse = await followUpTask;
                Assert.AreEqual(HttpStatusCode.OK, followUpResponse.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void HandlerFault_FailsOnlyAffectedStream_AndKeepsConnectionAlive()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request1 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/fail"));
                var context1 = new RequestContext(request1);
                var throwingHandler = new ThrowingResponseStartHandler();
                var task1 = conn.DispatchAsync(request1, throwingHandler, context1, cts.Token);

                var requestHeaders1 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId1 = requestHeaders1.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId1, 200, endStream: false),
                    cts.Token);

                var rst = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.RstStream, rst.Type);
                Assert.AreEqual(streamId1, rst.StreamId);

                var ex = await TestHelpers.AssertThrowsAsync<InvalidOperationException>(async () => await task1);
                Assert.AreEqual("handler-start-failure", ex.Message);
                Assert.IsFalse(throwingHandler.ResponseErrorCalled);
                Assert.IsNull(throwingHandler.LastError);

                var request2 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/success"));
                var context2 = new RequestContext(request2);
                var task2 = conn.SendRequestAsync(request2, context2, cts.Token);

                var requestHeaders2 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId2 = requestHeaders2.StreamId;
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId2, 200, endStream: true),
                    cts.Token);

                using var response2 = await task2;
                Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            });
        }

        [Test]
        public void PostHeaderBufferedFailure_ReportsNetworkError()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(
                    cts.Token,
                    new Http2Options { MaxResponseBodySize = 1 });
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("https://test.example.com/post-header-failure"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200, endStream: false),
                    cts.Token);

                var bodyBytes = System.Text.Encoding.UTF8.GetBytes("too-large");
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = bodyBytes,
                    Length = bodyBytes.Length
                }, cts.Token);

                var error = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, error.HttpError.Type);

                conn.Dispose();
            });
        }

        [Test]
        public void ContentLengthPreallocation_CappedByMaxResponseBodySize()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var localSettingsField = typeof(Http2Connection).GetField(
                    "_localSettings",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(localSettingsField);
                var localSettings = (Http2Settings)localSettingsField.GetValue(conn);
                localSettings.MaxResponseBodySize = 4096;

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(
                        streamId,
                        200,
                        new Dictionary<string, string>
                        {
                            { "content-length", int.MaxValue.ToString() }
                        },
                        endStream: false),
                    cts.Token);

                await Task.Delay(100, cts.Token);

                var activeStreamsField = typeof(Http2Connection).GetField(
                    "_activeStreams",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(activeStreamsField);
                var activeStreams =
                    (System.Collections.Concurrent.ConcurrentDictionary<int, Http2Stream>)
                    activeStreamsField.GetValue(conn);
                Assert.IsTrue(activeStreams.TryGetValue(streamId, out var stream));
                var perStreamCapacityProperty = typeof(Http2Connection).GetProperty(
                    "PerStreamReceiveBufferBytes",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(perStreamCapacityProperty);
                Assert.AreEqual(
                    (int)perStreamCapacityProperty.GetValue(conn),
                    stream.ResponseBodyCapacity);

                var bodyBytes = System.Text.Encoding.UTF8.GetBytes("ok");
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
                conn.Dispose();
            });
        }

        // --- R5: HPACK decoding error sends GOAWAY with COMPRESSION_ERROR ---

        [Test]
        public void HpackDecodingError_SendsGoAwayCompressionError()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headersFrame.StreamId;

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = new byte[] { 0x80 },
                    Length = 1
                }, cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.IsInstanceOf<Http2ProtocolException>(ex.HttpError.InnerException);

                var goaway = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.GoAway, goaway.Type);
                Assert.AreEqual(0, goaway.StreamId);

                uint errorCode = ((uint)goaway.Payload[4] << 24)
                    | ((uint)goaway.Payload[5] << 16)
                    | ((uint)goaway.Payload[6] << 8)
                    | goaway.Payload[7];
                Assert.AreEqual((uint)Http2ErrorCode.CompressionError, errorCode);

                conn.Dispose();
            });
        }

        // --- R7: DATA before HEADERS is protocol error ---

        [Test]
        public void DataFrame_BeforeHeaders_SendsRstStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headersFrame.StreamId;

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = new byte[] { 0x01, 0x02, 0x03 },
                    Length = 3
                }, cts.Token);

                try
                {
                    await responseTask;
                    Assert.Fail("Expected exception");
                }
                catch (Http2ProtocolException)
                {
                }
                catch (UHttpException)
                {
                }

                var rstStream = await ReadMatchingFrameAsync(
                    serverCodec,
                    frame => frame.Type == Http2FrameType.RstStream && frame.StreamId == streamId,
                    timeoutMs: 1000);
                Assert.AreEqual(Http2FrameType.RstStream, rstStream.Type);
                Assert.AreEqual(streamId, rstStream.StreamId);

                uint errorCode = ((uint)rstStream.Payload[0] << 24)
                    | ((uint)rstStream.Payload[1] << 16)
                    | ((uint)rstStream.Payload[2] << 8)
                    | rstStream.Payload[3];
                Assert.AreEqual((uint)Http2ErrorCode.ProtocolError, errorCode);

                conn.Dispose();
            });
        }
    }
}
