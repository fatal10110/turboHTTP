using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;
using TurboHTTP.Unity.Mobile;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// <see cref="NetworkQualitySample.BytesTransferred"/> counts every response body shape the
    /// adaptive handler sees, not only single-segment buffered views (#261).
    /// </summary>
    [TestFixture]
    public class AdaptiveByteCountTests
    {
        [Test]
        public void DecodedBufferedBody_LargerThanOneSegment_IsCounted()
        {
            AssertAsync.Run(async () =>
            {
                var payload = CreatePayload(64 * 1024);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                var transport = new MockTransport(HttpStatusCode.OK, headers, Gzip(payload));
                var detector = new NetworkQualityDetector();
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[]
                    {
                        new AdaptiveInterceptor(new AdaptivePolicy { Enable = true }, detector),
                        new DecompressionInterceptor()
                    },
                    transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/large.json"));

                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(payload.Length, response.Body.Length, "the body is decoded");
                Assert.AreEqual(1, detector.GetSnapshot().SampleCount);
                Assert.IsTrue(detector.TryGetLatestSample(out var sample));
                Assert.AreEqual(payload.Length, sample.BytesTransferred);
                Assert.IsTrue(sample.WasSuccess);
            });
        }

        [Test]
        public void SingleSegmentBufferedBody_IsCountedAtTheHead()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.OK, new HttpHeaders(), new byte[1000]);
                var detector = new NetworkQualityDetector();
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { new AdaptiveInterceptor(new AdaptivePolicy { Enable = true }, detector) },
                    transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/small"));

                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.IsTrue(detector.TryGetLatestSample(out var sample));
                Assert.AreEqual(1000, sample.BytesTransferred);
                Assert.AreEqual(1, detector.GetSnapshot().SampleCount);
            });
        }

        [Test]
        public void StreamedBody_IsCountedAsItIsRead_AndSampledOnce()
        {
            AssertAsync.Run(async () =>
            {
                var chunks = new List<ReadOnlyMemory<byte>>();
                for (var i = 0; i < 5; i++)
                    chunks.Add(new byte[8000]);
                var body = new MockResponseBodySource(chunks);
                var detector = new NetworkQualityDetector();
                var interceptor = new AdaptiveInterceptor(new AdaptivePolicy { Enable = true }, detector);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/stream"));

                using var response = await TransportDispatchHelper.CollectResponseAsync(
                    interceptor.Wrap(async (req, handler, ctx, ct) =>
                    {
                        handler.OnRequestStart(req, ctx);
                        await handler.OnResponseStartAsync(200, new HttpHeaders(), body, ctx);
                    }),
                    request,
                    new RequestContext(request),
                    CancellationToken.None);

                Assert.AreEqual(40000, response.Body.Length);
                Assert.AreEqual(1, detector.GetSnapshot().SampleCount);
                Assert.IsTrue(detector.TryGetLatestSample(out var sample));
                Assert.AreEqual(40000, sample.BytesTransferred);
                Assert.IsTrue(sample.WasSuccess);
            });
        }

        [Test]
        public void ErrorAfterHead_RecordsOneFailureSample()
        {
            AssertAsync.Run(async () =>
            {
                var detector = new NetworkQualityDetector();
                var inner = new CapturingHandler();
                var handler = new AdaptiveHandler(inner, detector, requestBytes: 10, started: TimeSpan.Zero);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/error"));
                var context = new RequestContext(request);

                await handler.OnResponseStartAsync(
                    200,
                    new HttpHeaders(),
                    new MockResponseBodySource(new List<ReadOnlyMemory<byte>> { new byte[100] }),
                    context);
                Assert.AreEqual(0, detector.GetSnapshot().SampleCount, "a streamed body is sampled when it completes");

                handler.OnResponseError(new UHttpException(new UHttpError(UHttpErrorType.NetworkError, "reset")), context);
                await inner.Body.DisposeAsync();

                Assert.AreEqual(1, detector.GetSnapshot().SampleCount);
                Assert.IsTrue(detector.TryGetLatestSample(out var sample));
                Assert.IsFalse(sample.WasSuccess);
                Assert.IsTrue(sample.WasTransportFailure);
            });
        }

        [Test]
        public void DrainAsync_IsForwardedToTheInnerSource_NotReadThrough()
        {
            AssertAsync.Run(async () =>
            {
                var inner = new DrainOnlyBody();
                var completions = 0;
                long counted = -1;
                var body = new CountingResponseBodySource(inner, n =>
                {
                    completions++;
                    counted = n;
                });

                await body.DrainAsync(CancellationToken.None);
                Assert.AreEqual(1, inner.DrainCalls, "a discard stays a discard (HTTP/2 resets the stream)");
                Assert.AreEqual(0, inner.ReadCalls);
                Assert.AreEqual(1, completions);
                Assert.AreEqual(0, counted, "drained bytes are not counted");

                await body.DisposeAsync();
                Assert.AreEqual(1, completions);
                Assert.AreEqual(1, inner.DisposeCalls);
            });
        }

        private static byte[] CreatePayload(int length)
        {
            var payload = new byte[length];
            for (var i = 0; i < payload.Length; i++)
                payload[i] = (byte)('a' + (i * 7 % 26));
            return payload;
        }

        private static byte[] Gzip(byte[] input)
        {
            using (var stream = new MemoryStream())
            {
                using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
                    gzip.Write(input, 0, input.Length);
                return stream.ToArray();
            }
        }

        private sealed class DrainOnlyBody : IResponseBodySource
        {
            public int DrainCalls;
            public int ReadCalls;
            public int DisposeCalls;

            public long? Length => null;

            public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
            {
                data = default;
                return false;
            }

            public bool TryDetachBufferedBody(out DetachedBufferedBody body)
            {
                body = default;
                return false;
            }

            public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
            {
                ReadCalls++;
                throw new InvalidOperationException("DrainAsync must not read the body through");
            }

            public ValueTask DrainAsync(CancellationToken ct)
            {
                DrainCalls++;
                return default;
            }

            public void Abort()
            {
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                return new ValueTask<HttpHeaders>(new HttpHeaders());
            }

            public ValueTask DisposeAsync()
            {
                DisposeCalls++;
                return default;
            }
        }

        private sealed class CapturingHandler : IHttpHandler
        {
            public IResponseBodySource Body { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                Body = body;
                return default;
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
            }
        }
    }
}
