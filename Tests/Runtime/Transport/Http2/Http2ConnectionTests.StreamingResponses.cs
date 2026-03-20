using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    public partial class Http2ConnectionTests
    {
        [Test]
        public void SendStreamingRequest_ResponseBodyCanBeReadIncrementally_AndTrailersReturned()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/streaming"));
                var context = new RequestContext(request);
                var responseTask = conn.SendStreamingRequestAsync(request, context, cts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200),
                    cts.Token);

                await using var response = await responseTask;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = Encoding.UTF8.GetBytes("ab"),
                    Length = 2
                }, cts.Token);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = Encoding.UTF8.GetBytes("c"),
                    Length = 1
                }, cts.Token);

                await serverCodec.WriteFrameAsync(
                    BuildTrailingHeadersFrame(
                        streamId,
                        new Dictionary<string, string> { { "x-trailer", "ok" } }),
                    cts.Token);

                var buffer = new byte[2];
                var firstRead = await response.Body.ReadAsync(buffer, cts.Token);
                Assert.AreEqual(2, firstRead);
                Assert.AreEqual("ab", Encoding.UTF8.GetString(buffer, 0, firstRead));

                var secondRead = await response.Body.ReadAsync(buffer, cts.Token);
                Assert.AreEqual(1, secondRead);
                Assert.AreEqual("c", Encoding.UTF8.GetString(buffer, 0, secondRead));

                var eofRead = await response.Body.ReadAsync(buffer, cts.Token);
                Assert.AreEqual(0, eofRead);

                var trailers = await response.GetTrailersAsync(cts.Token);
                Assert.AreEqual("ok", trailers.Get("x-trailer"));

                conn.Dispose();
            });
        }

        [Test]
        public void SendStreamingRequest_StreamWindowUpdate_IsDeferredUntilBodyConsumed()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/flow"));
                var context = new RequestContext(request);
                var responseTask = conn.SendStreamingRequestAsync(request, context, cts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200),
                    cts.Token);

                await using var response = await responseTask;

                var payload = new byte[16384];
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = payload,
                    Length = payload.Length
                }, cts.Token);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = payload,
                    Length = payload.Length
                }, cts.Token);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = payload,
                    Length = payload.Length
                }, cts.Token);

                var connectionUpdate = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.WindowUpdate, connectionUpdate.Type);
                Assert.AreEqual(0, connectionUpdate.StreamId);

                var unexpected = await TryReadFrameAsync(serverCodec, timeoutMs: 250);
                Assert.IsNull(unexpected, "Stream-level WINDOW_UPDATE should not be sent before consumption.");

                var readBuffer = new byte[16384];
                var read = await response.Body.ReadAsync(readBuffer, cts.Token);
                Assert.AreEqual(16384, read);

                var streamUpdate = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.WindowUpdate, streamUpdate.Type);
                Assert.AreEqual(streamId, streamUpdate.StreamId);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, cts.Token);

                while (await response.Body.ReadAsync(readBuffer, cts.Token) != 0)
                {
                }

                conn.Dispose();
            });
        }

        [Test]
        public void SendStreamingRequest_DrainAsync_AbortsStreamWithCancel()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/drain"));
                var context = new RequestContext(request);
                var responseTask = conn.SendStreamingRequestAsync(request, context, cts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200),
                    cts.Token);

                await using var response = await responseTask;

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = Encoding.UTF8.GetBytes("drain"),
                    Length = 5
                }, cts.Token);

                var bodySourceField = typeof(UHttpStreamingResponse).GetField(
                    "_bodySource",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(bodySourceField);
                var bodySource = (IResponseBodySource)bodySourceField.GetValue(response);
                await bodySource.DrainAsync(cts.Token);

                var rst = await ReadMatchingFrameAsync(
                    serverCodec,
                    frame => frame.Type == Http2FrameType.RstStream && frame.StreamId == streamId,
                    timeoutMs: 2000);
                Assert.AreEqual(Http2FrameType.RstStream, rst.Type);
                Assert.AreEqual(streamId, rst.StreamId);
                Assert.AreEqual((uint)Http2ErrorCode.Cancel, ReadErrorCode(rst));

                conn.Dispose();
            });
        }

        [Test]
        public void SendStreamingRequest_DisposeBeforeEndStream_SendsRst_AndPostResetDataIsIgnored()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/abort"));
                var context = new RequestContext(request);
                var responseTask = conn.SendStreamingRequestAsync(request, context, cts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200),
                    cts.Token);

                var response = await responseTask;

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = Encoding.UTF8.GetBytes("abc"),
                    Length = 3
                }, cts.Token);

                var readBuffer = new byte[1];
                var read = await response.Body.ReadAsync(readBuffer, cts.Token);
                Assert.AreEqual(1, read);
                Assert.AreEqual("a", Encoding.UTF8.GetString(readBuffer, 0, read));

                await response.DisposeAsync();

                var rst = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.RstStream, rst.Type);
                Assert.AreEqual(streamId, rst.StreamId);

                uint errorCode = ((uint)rst.Payload[0] << 24)
                    | ((uint)rst.Payload[1] << 16)
                    | ((uint)rst.Payload[2] << 8)
                    | rst.Payload[3];
                Assert.AreEqual((uint)Http2ErrorCode.Cancel, errorCode);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = Encoding.UTF8.GetBytes("z"),
                    Length = 1
                }, cts.Token);

                var redundantFrame = await TryReadFrameAsync(serverCodec, timeoutMs: 250);
                Assert.IsNull(redundantFrame, "Post-reset DATA should not trigger a redundant RST_STREAM.");

                var request2 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/after-abort"));
                var context2 = new RequestContext(request2);
                var responseTask2 = conn.SendRequestAsync(request2, context2, cts.Token);

                var requestHeaders2 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(requestHeaders2.StreamId, 200, endStream: true),
                    cts.Token);

                var response2 = await responseTask2;
                Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void SendStreamingRequest_RequestCancellationAfterHeaders_DoesNotAbortBody()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                using var requestCts = new CancellationTokenSource();
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/header-cancel"));
                var context = new RequestContext(request);
                var responseTask = conn.SendStreamingRequestAsync(request, context, requestCts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200),
                    cts.Token);

                await using var response = await responseTask;
                requestCts.Cancel();

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = Encoding.UTF8.GetBytes("body"),
                    Length = 4
                }, cts.Token);

                var buffer = new byte[8];
                var read = await response.Body.ReadAsync(buffer, cts.Token);
                Assert.AreEqual(4, read);
                Assert.AreEqual("body", Encoding.UTF8.GetString(buffer, 0, read));
                Assert.AreEqual(0, await response.Body.ReadAsync(buffer, cts.Token));

                conn.Dispose();
            });
        }

        [Test]
        public void SendStreamingRequest_ConnectionWindowUpdate_IsSuppressedWhileAggregateBufferedBytesExceedLimit()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                var options = CreateTestHttp2Options(maxConnectionBufferedBytes: 47 * 1024);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token, options);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/connection-cap"));
                var context = new RequestContext(request);
                var responseTask = conn.SendStreamingRequestAsync(request, context, cts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200),
                    cts.Token);

                await using var response = await responseTask;

                var payload = new byte[16384];
                for (int i = 0; i < 3; i++)
                {
                    await serverCodec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Data,
                        Flags = Http2FrameFlags.None,
                        StreamId = streamId,
                        Payload = payload,
                        Length = payload.Length
                    }, cts.Token);
                }

                var unexpected = await TryReadFrameAsync(serverCodec, timeoutMs: 250);
                Assert.IsNull(unexpected, "Connection-level WINDOW_UPDATE should stay suppressed while buffered bytes exceed the cap.");

                var readBuffer = new byte[16384];
                var read = await response.Body.ReadAsync(readBuffer, cts.Token);
                Assert.AreEqual(16384, read);

                var connectionUpdate = await ReadMatchingFrameAsync(
                    serverCodec,
                    frame => frame.Type == Http2FrameType.WindowUpdate && frame.StreamId == 0,
                    timeoutMs: 2000);
                Assert.IsNotNull(connectionUpdate);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, cts.Token);

                while (await response.Body.ReadAsync(readBuffer, cts.Token) != 0)
                {
                }

                conn.Dispose();
            });
        }

        [Test]
        public void SendStreamingRequest_SlowConsumerOnOneStream_DoesNotBlockAnotherStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request1 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/slow"));
                var request2 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/fast"));
                var responseTask1 = conn.SendStreamingRequestAsync(request1, new RequestContext(request1), cts.Token).AsTask();
                var headers1 = await serverCodec.ReadFrameAsync(16384, cts.Token);
                var responseTask2 = conn.SendStreamingRequestAsync(request2, new RequestContext(request2), cts.Token).AsTask();
                var headers2 = await serverCodec.ReadFrameAsync(16384, cts.Token);

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(headers1.StreamId, 200), cts.Token);
                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(headers2.StreamId, 200), cts.Token);

                var response1 = await responseTask1;
                await using var response2 = await responseTask2;

                var slowPayload = new byte[16384];
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = headers1.StreamId,
                    Payload = slowPayload,
                    Length = slowPayload.Length
                }, cts.Token);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = headers1.StreamId,
                    Payload = slowPayload,
                    Length = slowPayload.Length
                }, cts.Token);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = headers2.StreamId,
                    Payload = Encoding.UTF8.GetBytes("fast"),
                    Length = 4
                }, cts.Token);

                var buffer = new byte[8];
                var read = await response2.Body.ReadAsync(buffer, cts.Token);
                Assert.AreEqual(4, read);
                Assert.AreEqual("fast", Encoding.UTF8.GetString(buffer, 0, read));
                Assert.AreEqual(0, await response2.Body.ReadAsync(buffer, cts.Token));

                await response1.DisposeAsync();
                conn.Dispose();
            });
        }

        [Test]
        public void SendStreamingRequest_ZeroBodyResponse_IsImmediatelyCompleted()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/empty"));
                var context = new RequestContext(request);
                var responseTask = conn.SendStreamingRequestAsync(request, context, cts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(requestHeaders.StreamId, 200, endStream: true),
                    cts.Token);

                await using var response = await responseTask;

                var buffer = new byte[1];
                Assert.AreEqual(0, await response.Body.ReadAsync(buffer, cts.Token));
                Assert.AreSame(HttpHeaders.Empty, await response.GetTrailersAsync(cts.Token));

                conn.Dispose();
            });
        }

        [Test]
        public void SendStreamingRequest_PerStreamBufferFull_SendsCancelRstAndFaultsBody()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                var options = CreateTestHttp2Options(perStreamReceiveBufferBytes: 1024);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token, options);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/buffer-full"));
                var context = new RequestContext(request);
                var responseTask = conn.SendStreamingRequestAsync(request, context, cts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200),
                    cts.Token);

                await using var response = await responseTask;

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = new byte[2048],
                    Length = 2048
                }, cts.Token);

                var rst = await ReadMatchingFrameAsync(
                    serverCodec,
                    frame => frame.Type == Http2FrameType.RstStream && frame.StreamId == streamId,
                    timeoutMs: 2000);
                Assert.AreEqual(Http2FrameType.RstStream, rst.Type);
                Assert.AreEqual(streamId, rst.StreamId);
                Assert.AreEqual((uint)Http2ErrorCode.Cancel, ReadErrorCode(rst));

                var buffer = new byte[32];
                Assert.ThrowsAsync<UHttpException>(async () =>
                {
                    await response.Body.ReadAsync(buffer, cts.Token);
                });

                conn.Dispose();
            });
        }

        [Test]
        public void SendStreamingRequest_StalledConsumer_SendsRstStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                var options = CreateTestHttp2Options(
                    stallTimeoutMilliseconds: 250,
                    maintenanceIntervalMilliseconds: 50);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token, options);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/stall"));
                var context = new RequestContext(request);
                var responseTask = conn.SendStreamingRequestAsync(request, context, cts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200),
                    cts.Token);

                var response = await responseTask;

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = Encoding.UTF8.GetBytes("stall"),
                    Length = 5
                }, cts.Token);

                var rst = await ReadMatchingFrameAsync(
                    serverCodec,
                    frame => frame.Type == Http2FrameType.RstStream && frame.StreamId == streamId,
                    timeoutMs: (int)TimeSpan.FromSeconds(7).TotalMilliseconds);
                Assert.AreEqual(Http2FrameType.RstStream, rst.Type);
                Assert.AreEqual(streamId, rst.StreamId);

                await response.DisposeAsync();
                conn.Dispose();
            });
        }

        private static Http2Frame BuildTrailingHeadersFrame(
            int streamId,
            Dictionary<string, string> trailers)
        {
            var encoder = new HpackEncoder();
            var headerList = new List<(string, string)>();
            foreach (var trailer in trailers)
                headerList.Add((trailer.Key, trailer.Value));

            var payload = encoder.Encode(headerList).ToArray();
            return new Http2Frame
            {
                Type = Http2FrameType.Headers,
                Flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
                StreamId = streamId,
                Payload = payload,
                Length = payload.Length
            };
        }

        private static async Task<Http2Frame> TryReadFrameAsync(Http2FrameCodec codec, int timeoutMs)
        {
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            try
            {
                return await codec.ReadFrameAsync(16384, timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        private static async Task<Http2Frame> ReadMatchingFrameAsync(
            Http2FrameCodec codec,
            Func<Http2Frame, bool> predicate,
            int timeoutMs)
        {
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            while (true)
            {
                var frame = await codec.ReadFrameAsync(16384, timeoutCts.Token);
                if (predicate(frame))
                    return frame;
            }
        }

        private static uint ReadErrorCode(Http2Frame frame)
        {
            return ((uint)frame.Payload[0] << 24)
                | ((uint)frame.Payload[1] << 16)
                | ((uint)frame.Payload[2] << 8)
                | frame.Payload[3];
        }
    }
}
