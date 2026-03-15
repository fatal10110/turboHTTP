using System;
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
    public class DecompressionInterceptorTests
    {
        [Test]
        public void GzipResponse_IsDecompressed_AndEncodingHeadersRemoved()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("compressed-payload");
                var compressed = Compress(body, useGzip: true);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                headers.Set("Content-Length", compressed.Length.ToString());

                var transport = new MockTransport(HttpStatusCode.OK, headers, compressed);
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/data"));
                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("gzip, deflate", transport.LastRequest.Headers.Get("Accept-Encoding"));
                Assert.AreEqual("compressed-payload", response.GetBodyAsString());
                Assert.IsFalse(response.Headers.Contains("Content-Encoding"));
                Assert.IsFalse(response.Headers.Contains("Content-Length"));
            });
        }

        [Test]
        public void ExistingAcceptEncoding_IsPreserved()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("already-configured");
                var compressed = Compress(body, useGzip: true);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");

                var transport = new MockTransport(HttpStatusCode.OK, headers, compressed);
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

                var requestHeaders = new HttpHeaders();
                requestHeaders.Set("Accept-Encoding", "gzip");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/data"), requestHeaders);

                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("gzip", transport.LastRequest.Headers.Get("Accept-Encoding"));
                Assert.AreEqual("already-configured", response.GetBodyAsString());
            });
        }

        [Test]
        public void UncompressedResponse_PassesThroughUnchanged()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("plain-payload");
                var headers = new HttpHeaders();
                headers.Set("Content-Type", "text/plain");

                var transport = new MockTransport(HttpStatusCode.OK, headers, body);
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/plain"));
                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("plain-payload", response.GetBodyAsString());
                Assert.AreEqual("text/plain", response.Headers.Get("Content-Type"));
            });
        }

        [Test]
        public void InjectedAcceptEncoding_DoesNotLeakIntoRequestContextAfterSuccess()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("context-restore");
                var compressed = Compress(body, useGzip: true);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");

                var transport = new MockTransport(HttpStatusCode.OK, headers, compressed);
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/context-success"));
                var context = new RequestContext(request);
                using var _ = await pipeline.ExecuteAsync(request, context);

                Assert.AreSame(request, context.Request);
                Assert.IsFalse(request.Headers.Contains("Accept-Encoding"));
                Assert.AreEqual("gzip, deflate", transport.LastRequest.Headers.Get("Accept-Encoding"));
            });
        }

        [Test]
        public void CorruptGzip_ReportsResponseError()
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Encoding", "gzip");

            var transport = new MockTransport(HttpStatusCode.OK, headers, Encoding.UTF8.GetBytes("not-gzip"));
            var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/corrupt"));
            var context = new RequestContext(request);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
            {
                using var _ = await pipeline.ExecuteAsync(request, context);
            });

            Assert.That(ex.HttpError.Message, Does.Contain("decompression"));
        }

        [Test]
        public void TruncatedGzip_ReportsResponseError()
        {
            var body = Encoding.UTF8.GetBytes("truncated-payload");
            var compressed = Compress(body, useGzip: true);
            var truncated = new byte[compressed.Length - 2];
            Array.Copy(compressed, truncated, truncated.Length);

            var headers = new HttpHeaders();
            headers.Set("Content-Encoding", "gzip");

            var transport = new MockTransport(HttpStatusCode.OK, headers, truncated);
            var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/truncated"));
            var context = new RequestContext(request);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
            {
                using var _ = await pipeline.ExecuteAsync(request, context);
            });

            Assert.That(ex.HttpError.Message, Does.Contain("decompression"));
        }

        [Test]
        public void OversizedDecompressedResponse_ReportsResponseError()
        {
            var body = Encoding.UTF8.GetBytes(new string('x', 128));
            var compressed = Compress(body, useGzip: true);
            var headers = new HttpHeaders();
            headers.Set("Content-Encoding", "gzip");

            var transport = new MockTransport(HttpStatusCode.OK, headers, compressed);
            var pipeline = new TestInterceptorPipeline(
                new[] { new DecompressionInterceptor(maxDecompressedBodySizeBytes: 32) },
                transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/oversized"));
            var context = new RequestContext(request);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
            {
                using var _ = await pipeline.ExecuteAsync(request, context);
            });

            Assert.That(ex.HttpError.InnerException?.Message, Does.Contain("maximum size"));
        }

        [Test]
        public void OversizedCompressedResponse_ReportsResponseError()
        {
            var body = new byte[] { 1, 7, 13, 21, 34, 55, 89, 144 };
            var compressed = Compress(body, useGzip: true);
            var headers = new HttpHeaders();
            headers.Set("Content-Encoding", "gzip");

            var transport = new MockTransport(HttpStatusCode.OK, headers, compressed);
            var pipeline = new TestInterceptorPipeline(
                new[] { new DecompressionInterceptor(maxDecompressedBodySizeBytes: 24) },
                transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/oversized-compressed"));
            var context = new RequestContext(request);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
            {
                using var _ = await pipeline.ExecuteAsync(request, context);
            });

            Assert.That(ex.HttpError.InnerException?.Message, Does.Contain("Compressed response body exceeded"));
        }

        [Test]
        public void XGzipAlias_IsDecompressed()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("alias-payload");
                var compressed = Compress(body, useGzip: true);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "x-gzip");

                var transport = new MockTransport(HttpStatusCode.OK, headers, compressed);
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/x-gzip"));
                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("alias-payload", response.GetBodyAsString());
                Assert.IsFalse(response.Headers.Contains("Content-Encoding"));
            });
        }

        [Test]
        public void MultiValueContentEncoding_IsDecompressedInReverseOrder()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("double-encoded");
                var gzipCompressed = Compress(body, useGzip: true);
                var doubleCompressed = Compress(gzipCompressed, useGzip: false);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip, deflate");

                var transport = new MockTransport(HttpStatusCode.OK, headers, doubleCompressed);
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/multi"));
                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("double-encoded", response.GetBodyAsString());
                Assert.IsFalse(response.Headers.Contains("Content-Encoding"));
            });
        }

        [Test]
        public void DownstreamOnResponseDataFailure_IsConvertedIntoOnResponseError()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("payload");
                var compressed = Compress(body, useGzip: true);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");

                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    handler.OnRequestStart(req, ctx);
                    handler.OnResponseStart((int)HttpStatusCode.OK, headers, ctx);

                    try
                    {
                        handler.OnResponseData(compressed, ctx);
                        handler.OnResponseEnd(HttpHeaders.Empty, ctx);
                    }
                    catch
                    {
                        // Simulate a transport that shields handler callback failures.
                    }

                    return Task.CompletedTask;
                });

                var pipeline = new InterceptorPipeline(
                    new IHttpInterceptor[] { new DecompressionInterceptor() },
                    transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/callback-error"));
                var context = new RequestContext(request);
                var handler = new ThrowOnDataHandler();

                await pipeline.Pipeline(request, handler, context, CancellationToken.None);

                Assert.IsNotNull(handler.LastError);
                Assert.That(handler.LastError.Message, Does.Contain("inner handler failed"));
            });
        }

        [Test]
        public void AsyncDispatchFault_RestoresOriginalRequestContext()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new CallbackTransport(async (req, handler, ctx, ct) =>
                {
                    handler.OnRequestStart(req, ctx);
                    await Task.Yield();
                    throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError, "async failure"));
                });

                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/context-fault"));
                var context = new RequestContext(request);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                    await pipeline.ExecuteAsync(request, context));

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.AreSame(request, context.Request);
                Assert.IsFalse(request.Headers.Contains("Accept-Encoding"));
            });
        }

        private static byte[] Compress(byte[] input, bool useGzip)
        {
            using var stream = new MemoryStream();
            using (Stream compressionStream = useGzip
                       ? new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true)
                       : new DeflateStream(stream, CompressionLevel.Fastest, leaveOpen: true))
            {
                compressionStream.Write(input, 0, input.Length);
            }

            return stream.ToArray();
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

        private sealed class ThrowOnDataHandler : IHttpHandler
        {
            internal UHttpException LastError { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
            {
            }

            public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
            {
                throw new InvalidOperationException("inner handler failed");
            }

            public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
            {
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                LastError = error;
            }
        }
    }
}
