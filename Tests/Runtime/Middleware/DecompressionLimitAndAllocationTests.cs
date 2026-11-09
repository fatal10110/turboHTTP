using System;
using System.Buffers;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;
using TurboHTTP.Tests.Transport;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.Middleware
{
    /// <summary>
    /// #74: the default decoded-size limit is 32 MB, and the buffered path decodes into pooled segments that the
    /// response takes over without a flattening copy, so a body costs about its decoded size and a bomb fails
    /// before the allocation grows past the limit.
    /// </summary>
    public class DecompressionLimitAndAllocationTests
    {
        private const long DefaultLimitBytes = 32L * 1024 * 1024;
        private const long FixedOverheadBytes = 64 * 1024;

        [Test]
        public void DefaultLimit_RejectsBodyOneBytePast32Mb()
        {
            AssertAsync.Run(async () =>
            {
                var wire = GzipCompress(new byte[DefaultLimitBytes + 1]);
                var consumer = await DispatchStreamingAsync(new DecompressionInterceptor(), wire).ConfigureAwait(false);

                AssertDecompressionFailure(consumer.Failure);
                StringAssert.Contains(DefaultLimitBytes.ToString(), consumer.Failure.InnerException?.Message ?? string.Empty);
            });
        }

        [Test]
        public void DefaultLimit_AcceptsBodyOf32Mb()
        {
            AssertAsync.Run(async () =>
            {
                var wire = GzipCompress(new byte[DefaultLimitBytes]);
                var consumer = await DispatchStreamingAsync(new DecompressionInterceptor(), wire).ConfigureAwait(false);

                Assert.IsNull(consumer.Failure);
                Assert.AreEqual(DefaultLimitBytes, consumer.DecodedLength);
            });
        }

        [Test]
        public void Buffered_BodyExactlyAtLimit_Decodes()
        {
            var payload = CreatePayload(40000);
            var outcome = DispatchBuffered(new DecompressionInterceptor(maxDecompressedBodySizeBytes: payload.Length), GzipCompress(payload));

            Assert.IsNull(outcome.Failure);
            CollectionAssert.AreEqual(payload, outcome.Detached.Sequence.ToArray());
            outcome.Detached.DisposeOwnedResources();
        }

        [Test]
        public void Buffered_LargeBody_IsReadableWithoutDetaching()
        {
            var payload = CreatePayload(100 * 1024);
            var outcome = DispatchBuffered(new DecompressionInterceptor(), GzipCompress(payload), detach: false);

            Assert.IsNull(outcome.Failure);
            CollectionAssert.AreEqual(payload, outcome.Read);
        }

        [Test]
        public void BufferedGzip_10MbBody_StaysInPooledSegments()
        {
            // Runs everywhere (no allocation counter needed): the decoded body reaches the response as the
            // decoder's 16 KB segments, not as one flattened array.
            const int bodyBytes = 10 * 1024 * 1024;
            var payload = CreatePayload(bodyBytes);
            var outcome = DispatchBuffered(new DecompressionInterceptor(), GzipCompress(payload));

            Assert.IsNull(outcome.Failure);
            try
            {
                var sequence = outcome.Detached.Sequence;
                Assert.AreEqual(bodyBytes, sequence.Length);
                Assert.IsFalse(sequence.IsSingleSegment, "A 10 MB body must not be flattened into one array.");
                foreach (var segment in sequence)
                    Assert.LessOrEqual(segment.Length, SegmentedBuffer.DefaultSegmentSize);

                CollectionAssert.AreEqual(payload, sequence.ToArray());
            }
            finally
            {
                outcome.Detached.DisposeOwnedResources();
            }
        }

        [UnityTest]
        public IEnumerator BufferedGzip_10MbBody_AllocatesAboutTheBodySize()
        {
            const int bodyBytes = 10 * 1024 * 1024;
            var meter = new AllocationMeter();
            try
            {
                yield return meter.Calibrate();

                var payload = CreatePayload(bodyBytes);
                var wire = GzipCompress(payload);

                // Warm-up (JIT, static tables) outside the measured frame.
                DispatchBuffered(new DecompressionInterceptor(), wire).Detached.DisposeOwnedResources();
                var interceptor = new DecompressionInterceptor();
                var body = new MockResponseBodySource(wire);

                yield return null;
                meter.Begin();
                var outcome = DispatchBuffered(interceptor, body);
                meter.End();
                yield return null;

                var allocated = meter.Allocated;
                TestContext.WriteLine($"Allocated {allocated} bytes ({meter.Name}).");
                Assert.IsNull(outcome.Failure);
                try
                {
                    var budget = (long)(bodyBytes * 1.2) + FixedOverheadBytes;
                    Assert.LessOrEqual(
                        allocated,
                        budget,
                        $"Buffered gzip decode of {bodyBytes} bytes allocated {allocated} bytes (budget {budget}, {meter.Name}).");
                    CollectionAssert.AreEqual(payload, outcome.Detached.Sequence.ToArray());
                }
                finally
                {
                    outcome.Detached.DisposeOwnedResources();
                }
            }
            finally
            {
                meter.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator BufferedGzipBomb_FailsBeforeAllocatingPastTheLimit()
        {
            const long limit = 512 * 1024;
            var meter = new AllocationMeter();
            try
            {
                yield return meter.Calibrate();

                var wire = GzipCompress(new byte[1024 * 1024], CompressionLevel.Optimal);
                Assert.Less(wire.Length, 4 * 1024, "The bomb is a small gzip body.");

                DispatchBuffered(new DecompressionInterceptor(maxDecompressedBodySizeBytes: limit), wire);
                var interceptor = new DecompressionInterceptor(maxDecompressedBodySizeBytes: limit);
                var body = new MockResponseBodySource(wire);

                yield return null;
                meter.Begin();
                var outcome = DispatchBuffered(interceptor, body);
                meter.End();
                yield return null;

                var allocated = meter.Allocated;
                TestContext.WriteLine($"Allocated {allocated} bytes ({meter.Name}).");
                AssertDecompressionFailure(outcome.Failure);
                var budget = limit + SegmentedBuffer.DefaultSegmentSize + FixedOverheadBytes;
                Assert.LessOrEqual(
                    allocated,
                    budget,
                    $"Bomb decode with a {limit}-byte limit allocated {allocated} bytes (budget {budget}, {meter.Name}).");
            }
            finally
            {
                meter.Dispose();
            }
        }

        private static BufferedConsumer DispatchBuffered(DecompressionInterceptor interceptor, byte[] wire, bool detach = true)
        {
            return DispatchBuffered(interceptor, new MockResponseBodySource(wire), detach);
        }

        /// <summary>
        /// Runs the buffered decompression path synchronously on this thread (every step completes inline with
        /// the mock body), so the per-thread allocation counter sees all of it.
        /// </summary>
        private static BufferedConsumer DispatchBuffered(
            DecompressionInterceptor interceptor,
            MockResponseBodySource body,
            bool detach = true)
        {
            var transport = new CallbackTransport((req, handler, ctx, ct) =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                handler.OnRequestStart(req, ctx);
                return handler.OnResponseStartAsync((int)HttpStatusCode.OK, headers, body, ctx).AsTask();
            });

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/buffered"));
            var consumer = new BufferedConsumer(detach);
            var dispatch = interceptor.Wrap(transport.DispatchAsync)(
                request,
                consumer,
                new RequestContext(request),
                CancellationToken.None);
            Assert.IsTrue(dispatch.IsCompleted, "The mock buffered path is expected to complete synchronously.");
            if (dispatch.Exception != null)
                throw dispatch.Exception.GetBaseException();

            return consumer;
        }

        private static async Task<StreamingConsumer> DispatchStreamingAsync(DecompressionInterceptor interceptor, byte[] wire)
        {
            var transport = new CallbackTransport((req, handler, ctx, ct) =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                handler.OnRequestStart(req, ctx);
                return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        headers,
                        new MockResponseBodySource(
                            new[] { new ReadOnlyMemory<byte>(wire) },
                            length: null,
                            trailers: HttpHeaders.Empty,
                            exposeBufferedData: false),
                        ctx)
                    .AsTask();
            });

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/streaming"));
            var consumer = new StreamingConsumer();
            await interceptor.Wrap(transport.DispatchAsync)(request, consumer, new RequestContext(request), CancellationToken.None)
                .ConfigureAwait(false);
            return consumer;
        }

        private static void AssertDecompressionFailure(Exception failure)
        {
            Assert.IsNotNull(failure, "Expected the decompression limit to fail the response.");
            Assert.IsInstanceOf<UHttpException>(failure);
            StringAssert.Contains("decompression", failure.Message.ToLowerInvariant());
        }

        private static byte[] CreatePayload(int length)
        {
            var bytes = new byte[length];
            uint state = 2463534242u;
            for (var i = 0; i < bytes.Length; i++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                bytes[i] = (byte)(state & 0x3F);
            }

            return bytes;
        }

        private static byte[] GzipCompress(byte[] input, CompressionLevel level = CompressionLevel.Fastest)
        {
            using var stream = new MemoryStream();
            using (var gzip = new GZipStream(stream, level, leaveOpen: true))
                gzip.Write(input, 0, input.Length);

            return stream.ToArray();
        }

        private sealed class BufferedConsumer : IHttpHandler
        {
            private readonly bool _detach;

            internal BufferedConsumer(bool detach)
            {
                _detach = detach;
            }

            internal Exception Failure { get; private set; }

            internal DetachedBufferedBody Detached { get; private set; }

            internal byte[] Read { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                if (_detach)
                {
                    // What the response collector does: take the decoded segments over without copying them.
                    Assert.IsTrue(body.TryDetachBufferedBody(out var detached), "Expected a detachable buffered body.");
                    Detached = detached;
                    return;
                }

                try
                {
                    using var output = new MemoryStream();
                    var buffer = new byte[3000];
                    int read;
                    while ((read = await body.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
                        output.Write(buffer, 0, read);

                    Read = output.ToArray();
                }
                finally
                {
                    await body.DisposeAsync().ConfigureAwait(false);
                }
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Failure = error;
            }
        }

        private sealed class StreamingConsumer : IHttpHandler
        {
            internal Exception Failure { get; private set; }

            internal long DecodedLength { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                try
                {
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = await body.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
                        DecodedLength += read;
                }
                catch (Exception ex) when (!(ex is AssertionException))
                {
                    Failure = ex;
                }
                finally
                {
                    await body.DisposeAsync().ConfigureAwait(false);
                }
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Failure = error;
            }
        }

        private sealed class CallbackTransport : IHttpTransport
        {
            private readonly Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> _dispatch;

            internal CallbackTransport(Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> dispatch)
            {
                _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            }

            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                return _dispatch(request, handler, context, cancellationToken);
            }

            public ValueTask<UHttpResponse> SendAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
            }
        }
    }
}
