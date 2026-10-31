using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Middleware
{
    /// <summary>
    /// #57: responses without content (HEAD, 1xx/204/304, empty body) keep Content-Encoding and are not decoded.
    /// #150: gzip trailer (CRC32 + ISIZE) and premature EOF are validated on the streaming path, per gzip layer.
    /// </summary>
    public class DecompressionBodilessAndTruncationTests
    {
        private static readonly byte[] s_payload = CreatePayload(64 * 1024);

        [Test]
        public void HeadResponse_WithContentEncodingGzip_IsForwardedWithHeadersIntact()
        {
            AssertAsync.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                headers.Set("Content-Length", "1234");
                headers.Set("ETag", "\"v1\"");

                var transport = new MockTransport(HttpStatusCode.OK, headers, Array.Empty<byte>());
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);
                var request = new UHttpRequest(HttpMethod.HEAD, new Uri("https://example.test/head"));

                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("gzip", response.Headers.Get("Content-Encoding"));
                Assert.AreEqual("1234", response.Headers.Get("Content-Length"));
                Assert.AreEqual("\"v1\"", response.Headers.Get("ETag"));
                Assert.IsTrue(response.Body.IsEmpty);
            });
        }

        [Test]
        public void HeadResponse_NonBufferedSource_IsNotDecoded()
        {
            AssertAsync.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                headers.Set("Content-Length", "1234");

                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                            (int)HttpStatusCode.OK,
                            headers,
                            new MockResponseBodySource(
                                Array.Empty<ReadOnlyMemory<byte>>(),
                                length: null,
                                trailers: HttpHeaders.Empty,
                                exposeBufferedData: false),
                            ctx)
                        .AsTask();
                });

                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);
                var request = new UHttpRequest(HttpMethod.HEAD, new Uri("https://example.test/head-streaming"));

                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("gzip", response.Headers.Get("Content-Encoding"));
                Assert.AreEqual("1234", response.Headers.Get("Content-Length"));
                Assert.IsTrue(response.Body.IsEmpty);
            });
        }

        [TestCase(204)]
        [TestCase(304)]
        public void NoContentStatus_WithContentEncodingGzip_IsForwardedWithHeadersIntact(int statusCode)
        {
            AssertAsync.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                headers.Set("ETag", "\"v1\"");

                var transport = new MockTransport((HttpStatusCode)statusCode, headers, Array.Empty<byte>());
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/no-content"));

                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(statusCode, (int)response.StatusCode);
                Assert.AreEqual("gzip", response.Headers.Get("Content-Encoding"));
                Assert.AreEqual("\"v1\"", response.Headers.Get("ETag"));
                Assert.IsTrue(response.Body.IsEmpty);
            });
        }

        [Test]
        public void EmptyBody_WithContentEncodingGzip_IsForwardedWithHeadersIntact()
        {
            AssertAsync.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                headers.Set("Content-Length", "0");

                var transport = new MockTransport(HttpStatusCode.OK, headers, Array.Empty<byte>());
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/empty"));

                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("gzip", response.Headers.Get("Content-Encoding"));
                Assert.IsTrue(response.Body.IsEmpty);
            });
        }

        [Test]
        public void StreamingGzip_TruncatedInsideDeflateData_FailsBodyRead()
        {
            var compressed = GzipCompress(s_payload);
            AssertStreamingReadFails("gzip", Cut(compressed, compressed.Length / 2));
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(7)]
        public void StreamingGzip_TruncatedInsideTrailer_FailsBodyRead(int bytesCut)
        {
            var compressed = GzipCompress(s_payload);
            AssertStreamingReadFails("gzip", Cut(compressed, compressed.Length - bytesCut));
        }

        [Test]
        public void StreamingGzip_WithWrongTrailerCrc_FailsBodyRead()
        {
            var compressed = GzipCompress(s_payload);
            compressed[compressed.Length - 8] ^= 0xFF;
            AssertStreamingReadFails("gzip", compressed);
        }

        [Test]
        public void StreamingGzip_TruncatedBody_FailsTrailerDrain()
        {
            AssertAsync.Run(async () =>
            {
                var compressed = GzipCompress(s_payload);
                var consumer = await DispatchStreamingAsync(
                        "gzip",
                        Cut(compressed, compressed.Length - 3),
                        trailersOnly: true)
                    .ConfigureAwait(false);

                AssertDecompressionFailure(consumer.Failure);
            });
        }

        [Test]
        public void StreamingGzip_Complete_ReadsAllBytes()
        {
            AssertAsync.Run(async () =>
            {
                var consumer = await DispatchStreamingAsync("gzip", GzipCompress(s_payload)).ConfigureAwait(false);

                Assert.IsNull(consumer.Failure);
                CollectionAssert.AreEqual(s_payload, consumer.Decoded);
            });
        }

        // Multi-coding (RFC 9110 section 8.4): "Content-Encoding: gzip, deflate" means gzip was applied first,
        // so the wire bytes are deflate(gzip(content)). Every gzip layer validates its own trailer; a raw
        // deflate layer has no trailer and relies on the runtime inflater.
        [Test]
        public void StreamingMultiCoding_OuterGzipTruncated_FailsBodyRead()
        {
            var wire = GzipCompress(DeflateCompress(s_payload));
            AssertStreamingReadFails("deflate, gzip", Cut(wire, wire.Length - 5));
        }

        [Test]
        public void StreamingMultiCoding_InnerGzipTruncated_FailsBodyRead()
        {
            var gzip = GzipCompress(s_payload);
            var wire = DeflateCompress(Cut(gzip, gzip.Length / 2));
            AssertStreamingReadFails("gzip, deflate", wire);
        }

        [Test]
        public void StreamingMultiCoding_Complete_ReadsAllBytes()
        {
            AssertAsync.Run(async () =>
            {
                var wire = DeflateCompress(GzipCompress(s_payload));
                var consumer = await DispatchStreamingAsync("gzip, deflate", wire).ConfigureAwait(false);

                Assert.IsNull(consumer.Failure);
                CollectionAssert.AreEqual(s_payload, consumer.Decoded);
            });
        }

        [Test]
        public void BufferedMultiCoding_InnerGzipTruncated_ReportsResponseError()
        {
            AssertAsync.Run(async () =>
            {
                var gzip = GzipCompress(s_payload);
                var wire = DeflateCompress(Cut(gzip, gzip.Length - 4));
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip, deflate");

                var transport = new MockTransport(HttpStatusCode.OK, headers, wire);
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/buffered-multi"));

                Exception failure = null;
                try
                {
                    using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                AssertDecompressionFailure(failure);
            });
        }

        private static void AssertStreamingReadFails(string contentEncoding, byte[] wire)
        {
            AssertAsync.Run(async () =>
            {
                var consumer = await DispatchStreamingAsync(contentEncoding, wire).ConfigureAwait(false);
                AssertDecompressionFailure(consumer.Failure);
            });
        }

        private static void AssertDecompressionFailure(Exception failure)
        {
            Assert.IsNotNull(failure, "A truncated or corrupt compressed body must not read as a clean end of stream.");

            for (var current = failure; current != null; current = current.InnerException)
            {
                if (current is UHttpException httpException)
                {
                    StringAssert.Contains("decompression", httpException.Message.ToLowerInvariant());
                    return;
                }
            }

            Assert.Fail("Expected a UHttpException decompression failure, got: " + failure);
        }

        /// <summary>
        /// Dispatches through <see cref="DecompressionInterceptor"/> with a non-buffered transport body (the
        /// streaming DecompressionBodySource path) and consumes the decoded body like a streaming caller.
        /// </summary>
        private static async Task<StreamingConsumer> DispatchStreamingAsync(
            string contentEncoding,
            byte[] wire,
            bool trailersOnly = false)
        {
            var transport = new CallbackTransport((req, handler, ctx, ct) =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", contentEncoding);
                handler.OnRequestStart(req, ctx);
                return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        headers,
                        new MockResponseBodySource(
                            Chunk(wire, 1024),
                            length: wire.Length,
                            trailers: HttpHeaders.Empty,
                            exposeBufferedData: false),
                        ctx)
                    .AsTask();
            });

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/streaming"));
            var consumer = new StreamingConsumer(trailersOnly);
            await new DecompressionInterceptor()
                .Wrap(transport.DispatchAsync)(request, consumer, new RequestContext(request), CancellationToken.None)
                .ConfigureAwait(false);
            return consumer;
        }

        private static byte[] CreatePayload(int length)
        {
            // Deterministic, poorly compressible bytes so the deflate data spans many blocks.
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

        private static byte[] GzipCompress(byte[] input)
        {
            using var stream = new MemoryStream();
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(input, 0, input.Length);

            return stream.ToArray();
        }

        private static byte[] DeflateCompress(byte[] input)
        {
            using var stream = new MemoryStream();
            using (var deflate = new DeflateStream(stream, CompressionLevel.Fastest, leaveOpen: true))
                deflate.Write(input, 0, input.Length);

            return stream.ToArray();
        }

        private static byte[] Cut(byte[] bytes, int length)
        {
            var cut = new byte[length];
            Buffer.BlockCopy(bytes, 0, cut, 0, length);
            return cut;
        }

        private static IEnumerable<ReadOnlyMemory<byte>> Chunk(byte[] bytes, int chunkSize)
        {
            for (var offset = 0; offset < bytes.Length; offset += chunkSize)
                yield return new ReadOnlyMemory<byte>(bytes, offset, Math.Min(chunkSize, bytes.Length - offset));
        }

        private sealed class StreamingConsumer : IHttpHandler
        {
            private readonly bool _trailersOnly;

            internal StreamingConsumer(bool trailersOnly)
            {
                _trailersOnly = trailersOnly;
            }

            internal Exception Failure { get; private set; }

            internal byte[] Decoded { get; private set; }

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
                    Assert.IsFalse(body.TryGetBufferedData(out _), "Expected the streaming decompression path.");
                    if (_trailersOnly)
                    {
                        await body.GetTrailersAsync(CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    using var output = new MemoryStream();
                    var buffer = new byte[4096];
                    while (true)
                    {
                        var read = await body.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                        if (read == 0)
                            break;

                        output.Write(buffer, 0, read);
                    }

                    Decoded = output.ToArray();
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
