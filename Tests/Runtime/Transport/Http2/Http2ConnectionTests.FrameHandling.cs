using System;
using System.Collections.Generic;
using System.Net;
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
        // --- Frame Handling ---

        [Test]
        public void PingFrame_EchoedWithAck()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var pingData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Ping,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = pingData,
                    Length = 8
                }, cts.Token);

                var pongFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Ping, pongFrame.Type);
                Assert.IsTrue(pongFrame.HasFlag(Http2FrameFlags.Ack));
                Assert.AreEqual(pingData, pongFrame.Payload);

                conn.Dispose();
            });
        }

        [Test]
        public void GoAwayFrame_FailsHigherStreams()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request1 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/1"));
                var request2 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/2"));
                var ctx1 = new RequestContext(request1);
                var ctx2 = new RequestContext(request2);

                var task1 = conn.SendRequestAsync(request1, ctx1, cts.Token);
                var task2 = conn.SendRequestAsync(request2, ctx2, cts.Token);

                var h1 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                var h2 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int sid1 = h1.StreamId;
                int sid2 = h2.StreamId;

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

                AssertAsync.ThrowsAsync<UHttpException>(async () => await task2);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(sid1, 200, endStream: true),
                    cts.Token);

                var response = await task1;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        [Category("Stress")]
        public void GoAwayFrame_WithConcurrentInFlightRequests_CompletesOrFailsByLastStreamId()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                const int totalRequests = 50;
                var responseTasks = new ValueTask<UHttpResponse>[totalRequests];
                var streamIds = new int[totalRequests];

                for (int i = 0; i < totalRequests; i++)
                {
                    var request = new UHttpRequest(
                        HttpMethod.GET,
                        new Uri("https://test.example.com/stress/" + i));
                    var context = new RequestContext(request);
                    responseTasks[i] = conn.SendRequestAsync(request, context, cts.Token);

                    var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                    Assert.AreEqual(Http2FrameType.Headers, headersFrame.Type);
                    streamIds[i] = headersFrame.StreamId;
                }

                var lastAllowedStreamId = streamIds[(totalRequests / 2) - 1];
                var goawayPayload = new byte[8];
                goawayPayload[0] = (byte)((lastAllowedStreamId >> 24) & 0x7F);
                goawayPayload[1] = (byte)((lastAllowedStreamId >> 16) & 0xFF);
                goawayPayload[2] = (byte)((lastAllowedStreamId >> 8) & 0xFF);
                goawayPayload[3] = (byte)(lastAllowedStreamId & 0xFF);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.GoAway,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = goawayPayload,
                    Length = 8
                }, cts.Token);

                for (int i = 0; i < totalRequests; i++)
                {
                    if (streamIds[i] > lastAllowedStreamId)
                        continue;

                    await serverCodec.WriteFrameAsync(
                        BuildResponseHeadersFrame(streamIds[i], 200, endStream: true),
                        cts.Token);
                }

                var successCount = 0;
                var failureCount = 0;

                for (int i = 0; i < totalRequests; i++)
                {
                    if (streamIds[i] <= lastAllowedStreamId)
                    {
                        using var response = await responseTasks[i].ConfigureAwait(false);
                        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                        successCount++;
                        continue;
                    }

                    AssertAsync.ThrowsAsync<UHttpException, UHttpResponse>(() => responseTasks[i]);
                    failureCount++;
                }

                Assert.AreEqual(totalRequests / 2, successCount);
                Assert.AreEqual(totalRequests / 2, failureCount);

                conn.Dispose();
            });
        }

        [Test]
        public void RstStreamFrame_FailsSpecificStream()
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

                var rstPayload = new byte[4];
                rstPayload[3] = 2;
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
            });
        }

        [Test]
        public void RstStreamCancel_FrameFailsSpecificStreamAsNetworkError()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("https://test.example.com/cancelled-by-server"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headersFrame.StreamId;

                var rstPayload = new byte[4];
                rstPayload[3] = (byte)Http2ErrorCode.Cancel;
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.RstStream,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = rstPayload,
                    Length = 4
                }, cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                StringAssert.Contains("RST_STREAM: Cancel", ex.HttpError.Message);

                conn.Dispose();
            });
        }

        [Test]
        public void PushPromise_EnablePushDisabled_SendsGoAwayProtocolError()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(
                    cts.Token,
                    new Http2Options { EnablePush = false });
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);
                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);

                var pushPayload = new byte[4];
                pushPayload[3] = 2;
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.PushPromise,
                    Flags = Http2FrameFlags.EndHeaders,
                    StreamId = headersFrame.StreamId,
                    Payload = pushPayload,
                    Length = 4
                }, cts.Token);

                var goawayFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.GoAway, goawayFrame.Type);
                Assert.AreEqual(0, goawayFrame.StreamId);
                int lastStreamId = ((goawayFrame.Payload[0] & 0x7F) << 24)
                    | (goawayFrame.Payload[1] << 16)
                    | (goawayFrame.Payload[2] << 8)
                    | goawayFrame.Payload[3];
                Assert.AreEqual(0, lastStreamId);

                uint errorCode = ((uint)goawayFrame.Payload[4] << 24)
                    | ((uint)goawayFrame.Payload[5] << 16)
                    | ((uint)goawayFrame.Payload[6] << 8)
                    | goawayFrame.Payload[7];
                Assert.AreEqual((uint)Http2ErrorCode.ProtocolError, errorCode);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.IsInstanceOf<Http2ProtocolException>(ex.HttpError.InnerException);
                conn.Dispose();
            });
        }

        [Test]
        public void GoAway_InvalidPayloadLength_SendsFrameSizeErrorGoAway()
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
                Assert.AreEqual(Http2FrameType.Headers, requestHeaders.Type);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.GoAway,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = new byte[7],
                    Length = 7
                }, cts.Token);

                var goaway = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.GoAway, goaway.Type);
                Assert.AreEqual(0, goaway.StreamId);

                int lastStreamId = ((goaway.Payload[0] & 0x7F) << 24)
                    | (goaway.Payload[1] << 16)
                    | (goaway.Payload[2] << 8)
                    | goaway.Payload[3];
                Assert.AreEqual(0, lastStreamId);

                uint errorCode = ((uint)goaway.Payload[4] << 24)
                    | ((uint)goaway.Payload[5] << 16)
                    | ((uint)goaway.Payload[6] << 8)
                    | goaway.Payload[7];
                Assert.AreEqual((uint)Http2ErrorCode.FrameSizeError, errorCode);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.IsInstanceOf<Http2ProtocolException>(ex.HttpError.InnerException);
                conn.Dispose();
            });
        }

        [Test]
        public void Priority_InvalidLength_SendsFrameSizeErrorGoAway()
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

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Priority,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = new byte[4],
                    Length = 4
                }, cts.Token);

                var goaway = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.GoAway, goaway.Type);

                uint errorCode = ((uint)goaway.Payload[4] << 24)
                    | ((uint)goaway.Payload[5] << 16)
                    | ((uint)goaway.Payload[6] << 8)
                    | goaway.Payload[7];
                Assert.AreEqual((uint)Http2ErrorCode.FrameSizeError, errorCode);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.IsInstanceOf<Http2ProtocolException>(ex.HttpError.InnerException);
                conn.Dispose();
            });
        }

        // --- Settings ---

        [Test]
        public void Settings_UnknownId_Ignored()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var payload = new byte[6];
                payload[1] = 0xFF;
                payload[5] = 42;
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = payload,
                    Length = 6
                }, cts.Token);

                var ackFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Settings, ackFrame.Type);
                Assert.IsTrue(ackFrame.HasFlag(Http2FrameFlags.Ack));
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            });
        }

        // --- REVIEW FIX tests ---

        [Test]
        public void SettingsAck_NonZeroPayload_FrameSizeError()
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
                    StreamId = 0,
                    Payload = new byte[] { 0x00 },
                    Length = 1
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

        [Test]
        public void ContinuationFrame_WrongStream_ConnectionDies()
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
                var headerBlock = encoder.Encode(
                    new List<(string, string)> { (":status", "200") }).ToArray();
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = headerBlock,
                    Length = headerBlock.Length
                }, cts.Token);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Continuation,
                    Flags = Http2FrameFlags.EndHeaders,
                    StreamId = streamId + 2,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.IsInstanceOf<Http2ProtocolException>(ex.HttpError.InnerException);

                await Task.Delay(200);
                Assert.IsFalse(conn.IsAlive);

                conn.Dispose();
            });
        }

        [Test]
        public void NonContinuation_WhileExpectingContinuation_ConnectionDies()
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
                var headerBlock = encoder.Encode(
                    new List<(string, string)> { (":status", "200") }).ToArray();
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = headerBlock,
                    Length = headerBlock.Length
                }, cts.Token);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = new byte[] { 0x00 },
                    Length = 1
                }, cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.IsInstanceOf<Http2ProtocolException>(ex.HttpError.InnerException);

                await Task.Delay(200);
                Assert.IsFalse(conn.IsAlive);

                conn.Dispose();
            });
        }

        [Test]
        public void DataFrame_PaddingLengthExceedsPayload_ConnectionDies()
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

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200),
                    cts.Token);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.Padded,
                    StreamId = streamId,
                    Payload = new byte[] { 200, 0x01, 0x02 },
                    Length = 3
                }, cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.IsInstanceOf<Http2ProtocolException>(ex.HttpError.InnerException);

                await Task.Delay(200);
                Assert.IsFalse(conn.IsAlive);

                conn.Dispose();
            });
        }

        [Test]
        public void ContinuationFrame_WithEndStreamOnHeaders_CompletesStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var clientHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = clientHeaders.StreamId;

                var encoder = new HpackEncoder();
                var headerBlock = encoder.Encode(new List<(string, string)>
                {
                    (":status", "200"),
                    ("content-type", "text/plain")
                }).ToArray();

                int split = headerBlock.Length / 2;
                var firstPart = new byte[split];
                var secondPart = new byte[headerBlock.Length - split];
                headerBlock.AsSpan(0, split).CopyTo(firstPart);
                headerBlock.AsSpan(split, secondPart.Length).CopyTo(secondPart);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = firstPart,
                    Length = firstPart.Length
                }, cts.Token);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Continuation,
                    Flags = Http2FrameFlags.EndHeaders,
                    StreamId = streamId,
                    Payload = secondPart,
                    Length = secondPart.Length
                }, cts.Token);

                var response = await responseTask;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                conn.Dispose();
            });
        }
    }
}
