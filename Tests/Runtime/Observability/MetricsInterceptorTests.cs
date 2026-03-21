using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Middleware;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Observability
{
    public class MetricsInterceptorTests
    {
        [Test]
        public void TracksSuccessfulRequest()        {
            Task.Run(async () =>
            {
                var middleware = new MetricsInterceptor();
                var transport = new MockTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(1, middleware.Metrics.TotalRequests);
                Assert.AreEqual(1, middleware.Metrics.SuccessfulRequests);
                Assert.AreEqual(0, middleware.Metrics.FailedRequests);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void TracksFailedRequest()        {
            Task.Run(async () =>
            {
                var middleware = new MetricsInterceptor();
                var transport = new MockTransport(HttpStatusCode.InternalServerError);
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(1, middleware.Metrics.TotalRequests);
                Assert.AreEqual(0, middleware.Metrics.SuccessfulRequests);
                Assert.AreEqual(1, middleware.Metrics.FailedRequests);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void TracksByHost()        {
            Task.Run(async () =>
            {
                var middleware = new MetricsInterceptor();
                var transport = new MockTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request1 = new UHttpRequest(HttpMethod.GET, new Uri("https://api.example.com/a"));
                await pipeline.ExecuteAsync(request1, new RequestContext(request1));

                var request2 = new UHttpRequest(HttpMethod.GET, new Uri("https://other.com/b"));
                await pipeline.ExecuteAsync(request2, new RequestContext(request2));

                Assert.AreEqual(1, middleware.Metrics.RequestsByHost["api.example.com"]);
                Assert.AreEqual(1, middleware.Metrics.RequestsByHost["other.com"]);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void TracksByStatusCode()        {
            Task.Run(async () =>
            {
                var middleware = new MetricsInterceptor();
                var transport = new MockTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(1, middleware.Metrics.RequestsByStatusCode[200]);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void TracksBytesSent()        {
            Task.Run(async () =>
            {
                var middleware = new MetricsInterceptor();
                var transport = new MockTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var body = Encoding.UTF8.GetBytes("hello world");
                var request = new UHttpRequest(
                    HttpMethod.POST, new Uri("https://test.com"), body: body);
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(body.Length, middleware.Metrics.TotalBytesSent);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void TracksBytesReceived()        {
            Task.Run(async () =>
            {
                var responseBody = Encoding.UTF8.GetBytes("{\"status\": \"ok\"}");
                var middleware = new MetricsInterceptor();
                var transport = new MockTransport(
                    HttpStatusCode.OK, body: responseBody);
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(responseBody.Length, middleware.Metrics.TotalBytesReceived);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Reset_ClearsAllMetrics()        {
            Task.Run(async () =>
            {
                var middleware = new MetricsInterceptor();
                var transport = new MockTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(1, middleware.Metrics.TotalRequests);

                middleware.Reset();

                Assert.AreEqual(0, middleware.Metrics.TotalRequests);
                Assert.AreEqual(0, middleware.Metrics.SuccessfulRequests);
                Assert.AreEqual(0, middleware.Metrics.FailedRequests);
                Assert.IsEmpty(middleware.Metrics.RequestsByHost);
                Assert.IsEmpty(middleware.Metrics.RequestsByStatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Exception_TracksAsFailure()
        {
            var middleware = new MetricsInterceptor();
            var transport = new MockTransport((req, ctx, ct) =>
            {
                throw new UHttpException(
                    new UHttpError(UHttpErrorType.NetworkError, "Connection refused"));
            });
            var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            AssertAsync.ThrowsAsync<UHttpException, UHttpResponse>(
                () => pipeline.ExecuteAsync(request, context));

            Assert.AreEqual(1, middleware.Metrics.TotalRequests);
            Assert.AreEqual(1, middleware.Metrics.FailedRequests);
        }

        [Test]
        public void RedirectStatus_IsNotCountedAsFailure()
        {
            Task.Run(async () =>
            {
                var middleware = new MetricsInterceptor();
                var transport = new MockTransport(HttpStatusCode.Found);
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/redirect"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(1, middleware.Metrics.TotalRequests);
                Assert.AreEqual(1, middleware.Metrics.SuccessfulRequests);
                Assert.AreEqual(0, middleware.Metrics.FailedRequests);
                Assert.AreEqual(1, middleware.Metrics.RequestsByStatusCode[302]);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void StreamingResponse_CountsConsumedBytes_AndFinalizesOnDispose()
        {
            Task.Run(async () =>
            {
                var middleware = new MetricsInterceptor();
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        new HttpHeaders(),
                        new MockResponseBodySource(
                            new[]
                            {
                                (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("data"),
                                Encoding.UTF8.GetBytes("more")
                            },
                            length: 8,
                            trailers: HttpHeaders.Empty,
                            exposeBufferedData: false),
                        ctx).AsTask();
                });

                var pipeline = new InterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/stream"));
                var context = new RequestContext(request);

                var response = await StreamingDispatchBridge.CollectResponseAsync(
                    pipeline.Pipeline,
                    request,
                    context,
                    CancellationToken.None);

                try
                {
                    Assert.AreEqual(1, middleware.Metrics.TotalRequests);
                    Assert.AreEqual(0, middleware.Metrics.SuccessfulRequests);
                    Assert.AreEqual(0, middleware.Metrics.FailedRequests);

                    var buffer = new byte[4];
                    Assert.AreEqual(4, await response.Body.ReadAsync(buffer, CancellationToken.None));
                    Assert.AreEqual(4, middleware.Metrics.TotalBytesReceived);
                    Assert.AreEqual(0, middleware.Metrics.SuccessfulRequests);
                }
                finally
                {
                    await response.DisposeAsync();
                }

                Assert.AreEqual(1, middleware.Metrics.SuccessfulRequests);
                Assert.AreEqual(0, middleware.Metrics.FailedRequests);
                Assert.AreEqual(4, middleware.Metrics.TotalBytesReceived);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void StreamingResponse_FullConsumption_CountsAllReceivedBytes()
        {
            AssertAsync.Run(async () =>
            {
                var middleware = new MetricsInterceptor();
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        new HttpHeaders(),
                        new MockResponseBodySource(
                            new[]
                            {
                                (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("data"),
                                Encoding.UTF8.GetBytes("more")
                            },
                            length: 8,
                            trailers: HttpHeaders.Empty,
                            exposeBufferedData: false),
                        ctx).AsTask();
                });

                var pipeline = new InterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/stream-full"));
                var context = new RequestContext(request);

                var response = await StreamingDispatchBridge.CollectResponseAsync(
                    pipeline.Pipeline,
                    request,
                    context,
                    CancellationToken.None);

                try
                {
                    var buffer = new byte[8];
                    Assert.AreEqual(4, await response.Body.ReadAsync(buffer.AsMemory(0, 4), CancellationToken.None));
                    Assert.AreEqual(4, await response.Body.ReadAsync(buffer.AsMemory(4, 4), CancellationToken.None));
                    Assert.AreEqual(8, middleware.Metrics.TotalBytesReceived);
                }
                finally
                {
                    await response.DisposeAsync();
                }

                Assert.AreEqual(1, middleware.Metrics.SuccessfulRequests);
                Assert.AreEqual(0, middleware.Metrics.FailedRequests);
                Assert.AreEqual(8, middleware.Metrics.TotalBytesReceived);
            });
        }

        [Test]
        public void StreamingRequestBody_UsesTransportReportedBytesSent()
        {
            Task.Run(async () =>
            {
                var middleware = new MetricsInterceptor();
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    ctx.SetState(TransportBehaviorFlags.RequestBodyBytesSent, 7L);
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        new HttpHeaders(),
                        new MockResponseBodySource(ReadOnlyMemory<byte>.Empty, 0, HttpHeaders.Empty),
                        ctx).AsTask();
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.com/upload"))
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(
                            new MemoryStream(Encoding.UTF8.GetBytes("payload"), writable: false)));

                await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(7, middleware.Metrics.TotalBytesSent);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void StreamingResponseReadFailure_IsTrackedAsFailure()
        {
            Task.Run(async () =>
            {
                var middleware = new MetricsInterceptor();
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        new HttpHeaders(),
                        new FaultingBodySource(
                            Encoding.UTF8.GetBytes("abc"),
                            new IOException("stream broke")),
                        ctx).AsTask();
                });

                var pipeline = new InterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/fault"));
                var context = new RequestContext(request);

                var response = await StreamingDispatchBridge.CollectResponseAsync(
                    pipeline.Pipeline,
                    request,
                    context,
                    CancellationToken.None);

                try
                {
                    var buffer = new byte[3];
                    Assert.AreEqual(3, await response.Body.ReadAsync(buffer, CancellationToken.None));
                    AssertAsync.ThrowsAsync<IOException, int>(() =>
                        response.Body.ReadAsync(new byte[1], 0, 1, CancellationToken.None));
                }
                finally
                {
                    await response.DisposeAsync();
                }

                Assert.AreEqual(0, middleware.Metrics.SuccessfulRequests);
                Assert.AreEqual(1, middleware.Metrics.FailedRequests);
                Assert.AreEqual(3, middleware.Metrics.TotalBytesReceived);
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

        private sealed class FaultingBodySource : IResponseBodySource
        {
            private readonly ReadOnlyMemory<byte> _firstChunk;
            private readonly Exception _failure;
            private int _readCount;

            internal FaultingBodySource(ReadOnlyMemory<byte> firstChunk, Exception failure)
            {
                _firstChunk = firstChunk;
                _failure = failure ?? throw new ArgumentNullException(nameof(failure));
            }

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
                ct.ThrowIfCancellationRequested();

                if (Interlocked.Increment(ref _readCount) == 1)
                {
                    _firstChunk.CopyTo(destination);
                    return new ValueTask<int>(_firstChunk.Length);
                }

                throw _failure;
            }

            public async ValueTask DrainAsync(CancellationToken ct)
            {
                byte[] buffer = new byte[16];
                while (await ReadAsync(buffer, ct).ConfigureAwait(false) != 0)
                {
                }
            }

            public void Abort()
            {
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return new ValueTask<HttpHeaders>(HttpHeaders.Empty);
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }
    }
}
