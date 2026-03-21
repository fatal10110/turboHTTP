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
        public void NonBufferedCompressedResponse_IsDecompressed_WhenBufferedResponseIsRequested()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("streaming-buffered-path");
                var compressed = Compress(body, useGzip: true);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                headers.Set("Content-Length", compressed.Length.ToString());

                UHttpRequest dispatchedRequest = null;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    dispatchedRequest = req;
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                            (int)HttpStatusCode.OK,
                            headers,
                            new MockResponseBodySource(Chunk(compressed, 5), compressed.Length),
                            ctx)
                        .AsTask();
                });

                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/non-buffered-buffered"));
                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("gzip, deflate", dispatchedRequest.Headers.Get("Accept-Encoding"));
                Assert.AreEqual("streaming-buffered-path", response.GetBodyAsString());
                Assert.IsFalse(response.Headers.Contains("Content-Encoding"));
                Assert.IsFalse(response.Headers.Contains("Content-Length"));
            });
        }

        [Test]
        public void NonBufferedCompressedResponse_IsDecompressed_WhenStreamingResponseIsRequested()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("streaming-response-path");
                var compressed = Compress(body, useGzip: true);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                headers.Set("Content-Length", compressed.Length.ToString());

                var trailers = new HttpHeaders();
                trailers.Set("X-Trailer", "ok");

                UHttpRequest dispatchedRequest = null;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    dispatchedRequest = req;
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                            (int)HttpStatusCode.OK,
                            headers,
                            new MockResponseBodySource(Chunk(compressed, 3), compressed.Length, trailers),
                            ctx)
                        .AsTask();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    Interceptors = new List<IHttpInterceptor> { new DecompressionInterceptor() }
                });

                await using var response = await client
                    .Get("https://example.test/non-buffered-streaming")
                    .SendStreamingAsync()
                    .ConfigureAwait(false);

                var decompressed = await ReadAllAsync(response.Body).ConfigureAwait(false);
                var returnedTrailers = await response.GetTrailersAsync().ConfigureAwait(false);

                Assert.AreEqual("gzip, deflate", dispatchedRequest.Headers.Get("Accept-Encoding"));
                Assert.AreEqual("streaming-response-path", Encoding.UTF8.GetString(decompressed));
                Assert.IsFalse(response.Headers.Contains("Content-Encoding"));
                Assert.IsFalse(response.Headers.Contains("Content-Length"));
                Assert.AreEqual("ok", returnedTrailers.Get("X-Trailer"));
            });
        }

        [Test]
        public void OversizedDecompressedStreamingSource_ReportsResponseError()
        {
            var body = Encoding.UTF8.GetBytes(new string('x', 128));
            var compressed = Compress(body, useGzip: true);
            var headers = new HttpHeaders();
            headers.Set("Content-Encoding", "gzip");

            var transport = new CallbackTransport((req, handler, ctx, ct) =>
            {
                handler.OnRequestStart(req, ctx);
                return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        headers,
                        new MockResponseBodySource(Chunk(compressed, 4), compressed.Length),
                        ctx)
                    .AsTask();
            });

            var pipeline = new TestInterceptorPipeline(
                new[] { new DecompressionInterceptor(maxDecompressedBodySizeBytes: 32) },
                transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/oversized-streaming"));
            var context = new RequestContext(request);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
            {
                using var _ = await pipeline.ExecuteAsync(request, context);
            });

            Assert.That(ex.HttpError.InnerException?.Message, Does.Contain("maximum size"));
        }

        [Test]
        public void DownstreamResponseReadFailure_IsConvertedIntoOnResponseError()
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
                    return handler.OnResponseStartAsync(
                            (int)HttpStatusCode.OK,
                            headers,
                            new MockResponseBodySource(Chunk(compressed, 4), compressed.Length),
                            ctx)
                        .AsTask();
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

        private static IEnumerable<ReadOnlyMemory<byte>> Chunk(byte[] bytes, int chunkSize)
        {
            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));
            if (chunkSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunkSize));

            for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            {
                var count = Math.Min(chunkSize, bytes.Length - offset);
                yield return new ReadOnlyMemory<byte>(bytes, offset, count);
            }
        }

        private static async Task<byte[]> ReadAllAsync(Stream stream)
        {
            using var output = new MemoryStream();
            var buffer = new byte[7];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read == 0)
                    break;

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
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
                return _dispatch(
                    request,
                    new SafeHandler(handler, context),
                    context,
                    cancellationToken);
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

            private sealed class SafeHandler : IHttpHandler
            {
                private readonly IHttpHandler _inner;
                private readonly RequestContext _context;
                private int _terminated;

                internal SafeHandler(IHttpHandler inner, RequestContext context)
                {
                    _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                    _context = context ?? throw new ArgumentNullException(nameof(context));
                }

                public void OnRequestStart(UHttpRequest request, RequestContext context)
                {
                    if (Volatile.Read(ref _terminated) != 0)
                        return;

                    try
                    {
                        _inner.OnRequestStart(request, context ?? _context);
                    }
                    catch (Exception ex)
                    {
                        ReportFailure(ex);
                    }
                }

                public ValueTask OnResponseStartAsync(
                    int statusCode,
                    HttpHeaders headers,
                    IResponseBodySource body,
                    RequestContext context)
                {
                    if (body == null)
                        throw new ArgumentNullException(nameof(body));
                    if (Volatile.Read(ref _terminated) != 0)
                    {
                        TryAbort(body);
                        return default;
                    }

                    try
                    {
                        var pending = _inner.OnResponseStartAsync(statusCode, headers, body, context ?? _context);
                        if (pending.IsCompletedSuccessfully)
                        {
                            pending.GetAwaiter().GetResult();
                            Interlocked.Exchange(ref _terminated, 1);
                            return default;
                        }

                        return AwaitResponseStartAsync(pending, body);
                    }
                    catch (Exception ex)
                    {
                        TryAbort(body);
                        ReportFailure(ex);
                        return default;
                    }
                }

                public void OnResponseError(UHttpException error, RequestContext context)
                {
                    if (Interlocked.Exchange(ref _terminated, 1) != 0)
                        return;

                    _inner.OnResponseError(
                        error ?? new UHttpException(
                            new UHttpError(
                                UHttpErrorType.Unknown,
                                "IHttpHandler.OnResponseError received a null error.")),
                        context ?? _context);
                }

                private async ValueTask AwaitResponseStartAsync(ValueTask pending, IResponseBodySource body)
                {
                    try
                    {
                        await pending.ConfigureAwait(false);
                        Interlocked.Exchange(ref _terminated, 1);
                    }
                    catch (Exception ex)
                    {
                        TryAbort(body);
                        ReportFailure(ex);
                    }
                }

                private void ReportFailure(Exception ex)
                {
                    if (Interlocked.Exchange(ref _terminated, 1) != 0)
                        return;

                    _inner.OnResponseError(
                        ex as UHttpException
                        ?? new UHttpException(
                            new UHttpError(
                                UHttpErrorType.Unknown,
                                ex?.Message ?? "Handler callback failed.",
                                ex)),
                        _context);
                }

                private static void TryAbort(IResponseBodySource body)
                {
                    try
                    {
                        body?.Abort();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private sealed class ThrowOnDataHandler : IHttpHandler
        {
            internal UHttpException LastError { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                if (body != null)
                {
                    var buffer = new byte[8];
                    _ = await body.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                }

                throw new InvalidOperationException("inner handler failed");
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                LastError = error;
            }
        }
    }
}
