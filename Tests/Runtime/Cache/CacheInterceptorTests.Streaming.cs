using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Cache
{
    public partial class CacheInterceptorTests
    {
        [Test]
        public void CacheInterceptor_StreamingResponse_CachesAfterNaturalEof()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });
                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                int callCount = 0;

                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    Interlocked.Increment(ref callCount);
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        headers,
                        new MockResponseBodySource(
                            new[]
                            {
                                (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("pay"),
                                Encoding.UTF8.GetBytes("load")
                            },
                            length: 7,
                            trailers: HttpHeaders.Empty,
                            exposeBufferedData: false),
                        ctx).AsTask();
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/stream-cache"));

                using var first = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual("payload", first.GetBodyAsString());

                await WaitUntilAsync(
                    async () => await storage.GetCountAsync().ConfigureAwait(false) == 1,
                    TimeSpan.FromSeconds(1),
                    "Streaming cache store did not complete.");

                using var second = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(1, callCount);
                Assert.AreEqual("HIT", second.Headers.Get("X-Cache"));
                Assert.AreEqual("payload", second.GetBodyAsString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_StreamingResponse_DisposedBeforeEof_IsNotCached()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });
                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");

                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    Interlocked.Increment(ref callCount);
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        headers,
                        new MockResponseBodySource(
                            new[]
                            {
                                (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("first-"),
                                Encoding.UTF8.GetBytes("second")
                            },
                            length: null,
                            trailers: HttpHeaders.Empty,
                            exposeBufferedData: false),
                        ctx).AsTask();
                });

                var pipeline = new InterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/stream-abandon"));
                var context = new RequestContext(request);

                await pipeline.Pipeline(request, new PartialReadAndDisposeHandler(5), context, CancellationToken.None);
                await Task.Delay(50).ConfigureAwait(false);

                Assert.AreEqual(0, await storage.GetCountAsync().ConfigureAwait(false));

                var bufferedPipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                using var second = await bufferedPipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(2, callCount);
                Assert.AreEqual("first-second", second.GetBodyAsString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_KnownLengthAboveLimit_SkipsCachingWithoutBlockingResponse()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy
                {
                    Storage = storage,
                    MaxCacheableResponseBodyBytes = 4
                });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");

                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    Interlocked.Increment(ref callCount);
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        headers,
                        new MockResponseBodySource(
                            new[]
                            {
                                (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("payload")
                            },
                            length: 7,
                            trailers: HttpHeaders.Empty,
                            exposeBufferedData: false),
                        ctx).AsTask();
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/known-limit"));

                using var first = await pipeline.ExecuteAsync(request, new RequestContext(request));
                using var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("payload", first.GetBodyAsString());
                Assert.AreEqual("payload", second.GetBodyAsString());
                Assert.AreEqual(2, callCount);
                Assert.AreEqual(0, await storage.GetCountAsync().ConfigureAwait(false));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_UnknownLengthAboveLimit_DetachesAccumulatorAndDeliversFullResponse()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy
                {
                    Storage = storage,
                    MaxCacheableResponseBodyBytes = 4
                });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");

                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    Interlocked.Increment(ref callCount);
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        headers,
                        new MockResponseBodySource(
                            new[]
                            {
                                (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("pay"),
                                Encoding.UTF8.GetBytes("load")
                            },
                            length: null,
                            trailers: HttpHeaders.Empty,
                            exposeBufferedData: false),
                        ctx).AsTask();
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/unknown-limit"));

                using var first = await pipeline.ExecuteAsync(request, new RequestContext(request));
                using var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("payload", first.GetBodyAsString());
                Assert.AreEqual("payload", second.GetBodyAsString());
                Assert.AreEqual(2, callCount);
                Assert.AreEqual(0, await storage.GetCountAsync().ConfigureAwait(false));
            }).GetAwaiter().GetResult();
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

        private sealed class PartialReadAndDisposeHandler : IHttpHandler
        {
            private readonly int _bytesToRead;

            internal PartialReadAndDisposeHandler(int bytesToRead)
            {
                _bytesToRead = bytesToRead;
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                Assert.NotNull(body);

                var buffer = new byte[_bytesToRead];
                var read = await body.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                Assert.Greater(read, 0);
                await body.DisposeAsync().ConfigureAwait(false);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Assert.Fail("Unexpected error: " + error);
            }
        }
    }
}
