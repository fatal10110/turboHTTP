using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    // Http2Options.MaxResponseBodySize is enforced by the connection read loop (#157): these tests drive a
    // real Http2Connection over the duplex-stream harness, so removing the guard in
    // Http2Connection.ReadLoop.cs makes them fail (the response completes with the oversize body instead).
    public partial class Http2ConnectionTests
    {
        [Test]
        public void ResponseBodyCap_DataOverCap_FailsStreamAndSendsRstStreamCancel()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(
                    cts.Token,
                    new Http2Options { MaxResponseBodySize = 10 });
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/body-cap"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200, endStream: false),
                    cts.Token);
                await WriteDataFrameAsync(serverCodec, streamId, "This is much longer than ten bytes.", endStream: true, cts.Token);

                var error = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, error.HttpError.Type);
                StringAssert.Contains("Response body exceeds maximum size limit", error.HttpError.Message);

                // RFC 9113 Section 6.4: the client abandons the stream with RST_STREAM(CANCEL).
                var reset = await ReadMatchingFrameAsync(serverCodec, f => f.Type == Http2FrameType.RstStream, 5000);
                Assert.AreEqual(streamId, reset.StreamId);
                Assert.AreEqual((uint)Http2ErrorCode.Cancel, ReadErrorCode(reset));

                conn.Dispose();
            });
        }

        [Test]
        public void ResponseBodyCap_CumulativeDataOverCap_FailsOnTheFrameThatCrossesIt()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(
                    cts.Token,
                    new Http2Options { MaxResponseBodySize = 10 });
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/body-cap-cumulative"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200, endStream: false),
                    cts.Token);
                // 6 + 6 bytes: each frame fits the cap alone, the second crosses it.
                await WriteDataFrameAsync(serverCodec, streamId, "abcdef", endStream: false, cts.Token);
                await WriteDataFrameAsync(serverCodec, streamId, "ghijkl", endStream: true, cts.Token);

                var error = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, error.HttpError.Type);

                var reset = await ReadMatchingFrameAsync(serverCodec, f => f.Type == Http2FrameType.RstStream, 5000);
                Assert.AreEqual(streamId, reset.StreamId);
                Assert.AreEqual((uint)Http2ErrorCode.Cancel, ReadErrorCode(reset));

                conn.Dispose();
            });
        }

        [Test]
        public void ResponseBodyCap_BodyExactlyAtCap_Completes()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(
                    cts.Token,
                    new Http2Options { MaxResponseBodySize = 10 });
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/body-at-cap"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token).AsTask();

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200, endStream: false),
                    cts.Token);
                await WriteDataFrameAsync(serverCodec, streamId, "0123456789", endStream: true, cts.Token);

                using var response = await responseTask;
                Assert.AreEqual("0123456789", response.GetBodyAsString());

                conn.Dispose();
            });
        }

        private static Task WriteDataFrameAsync(
            Http2FrameCodec codec,
            int streamId,
            string body,
            bool endStream,
            CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            return codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Data,
                Flags = endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                StreamId = streamId,
                Payload = bytes,
                Length = bytes.Length
            }, ct);
        }
    }
}
