using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests;

namespace TurboHTTP.Tests.Pipeline
{
    [TestFixture]
    public class InterceptorPipelineTests
    {
        [Test]
        public void Pipeline_ExecutesInterceptorsInOrder()
        {
            Task.Run(async () =>
            {
                var executionOrder = new List<string>();
                var transport = new RecordingTransport();
                var pipeline = new InterceptorPipeline(
                    new IHttpInterceptor[]
                    {
                        new OrderTrackingInterceptor("I1", executionOrder),
                        new OrderTrackingInterceptor("I2", executionOrder),
                        new OrderTrackingInterceptor("I3", executionOrder)
                    },
                    transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);
                using var response = await TransportDispatchHelper.CollectResponseAsync(
                    pipeline.Pipeline,
                    request,
                    context,
                    CancellationToken.None);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                CollectionAssert.AreEqual(
                    new[] { "I1-Before", "I2-Before", "I3-Before", "I3-After", "I2-After", "I1-After" },
                    executionOrder);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Pipeline_EmptyInterceptor_CallsTransportDirectly()
        {
            Task.Run(async () =>
            {
                var transport = new RecordingTransport();
                var pipeline = new InterceptorPipeline(Array.Empty<IHttpInterceptor>(), transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);
                using var response = await TransportDispatchHelper.CollectResponseAsync(
                    pipeline.Pipeline,
                    request,
                    context,
                    CancellationToken.None);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(1, transport.DispatchCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Pipeline_InterceptorsCanShortCircuit()
        {
            Task.Run(async () =>
            {
                var transport = new RecordingTransport();
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[]
                    {
                        new ShortCircuitInterceptor(),
                        new OrderTrackingInterceptor("Never", new List<string>())
                    },
                    transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/short-circuit"));
                var context = new RequestContext(request);
                using var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.AreEqual(0, transport.DispatchCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Pipeline_NullInterceptorList_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new InterceptorPipeline(null, new RecordingTransport()));
        }

        [Test]
        public void Pipeline_NullTransport_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new InterceptorPipeline(Array.Empty<IHttpInterceptor>(), null));
        }

        [Test]
        public void Pipeline_NullInterceptorElement_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                new InterceptorPipeline(
                    new IHttpInterceptor[] { null },
                    new RecordingTransport()));
        }

        [Test]
        public void Pipeline_SynchronousCancellation_PropagatesOperationCanceledException()
        {
            Task.Run(async () =>
            {
                var pipeline = new InterceptorPipeline(
                    new IHttpInterceptor[] { new SynchronousCancellationInterceptor() },
                    new RecordingTransport());

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);
                using var cts = new CancellationTokenSource();
                cts.Cancel();

                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                    await TransportDispatchHelper.CollectResponseAsync(
                        pipeline.Pipeline,
                        request,
                        context,
                        cts.Token));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CollectResponseAsync_SynchronousDerivedCancellation_PropagatesOriginalException()
        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/queued"));
                var context = new RequestContext(request);
                using var cts = new CancellationTokenSource();
                var expected = new BackgroundRequestQueuedException("dedupe-key", "scope-1", cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<BackgroundRequestQueuedException>(async () =>
                {
                    await TransportDispatchHelper.CollectResponseAsync(
                        (req, handler, ctx, cancellationToken) => throw expected,
                        request,
                        context,
                        CancellationToken.None);
                });

                Assert.AreEqual("dedupe-key", ex.ReplayDedupeKey);
                Assert.AreEqual("scope-1", ex.ScopeId);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void EmptyHeaders_AreFrozen()
        {
            Assert.Throws<InvalidOperationException>(() => HttpHeaders.Empty.Set("X-Leak", "yes"));
        }

        [Test]
        public void Pipeline_ResponseEnd_UsesFrozenEmptyTrailers()
        {
            Task.Run(async () =>
            {
                var pipeline = new InterceptorPipeline(
                    new IHttpInterceptor[] { new TrailerMutationInterceptor() },
                    new RecordingTransport());

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/trailers"));
                var context = new RequestContext(request);
                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                    await TransportDispatchHelper.CollectResponseAsync(
                        pipeline.Pipeline,
                        request,
                        context,
                        CancellationToken.None));

                Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
                Assert.IsInstanceOf<InvalidOperationException>(ex.HttpError.InnerException);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Pipeline_PropagatesSynchronousExceptions()
        {
            var transport = new RecordingTransport();
            var pipeline = new TestInterceptorPipeline(
                new[] { new ThrowingInterceptor() },
                transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/failure"));
            var context = new RequestContext(request);

            AssertAsync.ThrowsAsync<InvalidOperationException, UHttpResponse>(
                () => pipeline.ExecuteAsync(request, context));
        }

        [Test]
        public void Pipeline_NullRequest_Throws()
        {
            var pipeline = new TestInterceptorPipeline(Array.Empty<IHttpInterceptor>(), new RecordingTransport());
            var context = new RequestContext(new UHttpRequest(HttpMethod.GET, new Uri("https://test.com")));

            AssertAsync.ThrowsAsync<ArgumentNullException, UHttpResponse>(
                () => pipeline.ExecuteAsync(null, context));
        }

        private sealed class RecordingTransport : IHttpTransport
        {
            public int DispatchCount { get; private set; }

            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                DispatchCount++;
                var response = new UHttpResponse(
                    HttpStatusCode.OK,
                    new HttpHeaders(),
                    Array.Empty<byte>(),
                    context.Elapsed,
                    request);

                try
                {
                    handler.OnRequestStart(request, context);
                    handler.OnResponseStart((int)response.StatusCode, response.Headers, context);
                    handler.OnResponseEnd(HttpHeaders.Empty, context);
                    return Task.CompletedTask;
                }
                finally
                {
                    response.Dispose();
                }
            }

            public void Dispose()
            {
            }
        }

        private sealed class OrderTrackingInterceptor : IHttpInterceptor
        {
            private readonly string _name;
            private readonly List<string> _order;

            public OrderTrackingInterceptor(string name, List<string> order)
            {
                _name = name;
                _order = order;
            }

            public DispatchFunc Wrap(DispatchFunc next)
            {
                return async (request, handler, context, cancellationToken) =>
                {
                    _order.Add(_name + "-Before");
                    await next(request, handler, context, cancellationToken).ConfigureAwait(false);
                    _order.Add(_name + "-After");
                };
            }
        }

        private sealed class SynchronousCancellationInterceptor : IHttpInterceptor
        {
            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return next(request, handler, context, cancellationToken);
                };
            }
        }

        private sealed class TrailerMutationInterceptor : IHttpInterceptor
        {
            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                    next(request, new TrailerMutationHandler(handler), context, cancellationToken);
            }

            private sealed class TrailerMutationHandler : IHttpHandler
            {
                private readonly IHttpHandler _inner;

                public TrailerMutationHandler(IHttpHandler inner)
                {
                    _inner = inner;
                }

                public void OnRequestStart(UHttpRequest request, RequestContext context)
                {
                    _inner.OnRequestStart(request, context);
                }

                public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
                {
                    _inner.OnResponseStart(statusCode, headers, context);
                }

                public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
                {
                    _inner.OnResponseData(chunk, context);
                }

                public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
                {
                    trailers.Set("X-Leak", "yes");
                    _inner.OnResponseEnd(trailers, context);
                }

                public void OnResponseError(UHttpException error, RequestContext context)
                {
                    _inner.OnResponseError(error, context);
                }
            }
        }

        private sealed class ShortCircuitInterceptor : IHttpInterceptor
        {
            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                {
                    handler.OnRequestStart(request, context);
                    handler.OnResponseStart((int)HttpStatusCode.Forbidden, new HttpHeaders(), context);
                    handler.OnResponseEnd(HttpHeaders.Empty, context);
                    return Task.CompletedTask;
                };
            }
        }

        private sealed class ThrowingInterceptor : IHttpInterceptor
        {
            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                    throw new InvalidOperationException("Test exception");
            }
        }

    }
}
