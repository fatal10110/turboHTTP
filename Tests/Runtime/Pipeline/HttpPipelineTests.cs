using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Pipeline
{
    public class HttpPipelineTests
    {
        [Test]
        public void Pipeline_ExecutesMiddlewareInOrder()        {
            Task.Run(async () =>
            {
                var executionOrder = new List<string>();

                var middleware1 = new OrderTrackingMiddleware("M1", executionOrder);
                var middleware2 = new OrderTrackingMiddleware("M2", executionOrder);
                var middleware3 = new OrderTrackingMiddleware("M3", executionOrder);

                var transport = new MockTransport();
                var pipeline = new HttpPipeline(
                    new[] { middleware1, middleware2, middleware3 },
                    transport
                );

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(
                    new[] { "M1-Before", "M2-Before", "M3-Before",
                            "M3-After", "M2-After", "M1-After" },
                    executionOrder.ToArray());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Pipeline_EmptyMiddleware_CallsTransportDirectly()        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                var pipeline = new HttpPipeline(
                    Array.Empty<IHttpMiddleware>(), transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(1, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Pipeline_MiddlewareCanShortCircuit()        {
            Task.Run(async () =>
            {
                var shortCircuit = new ShortCircuitMiddleware();
                var shouldNotRun = new OrderTrackingMiddleware("Never", new List<string>());

                var transport = new MockTransport();
                var pipeline = new HttpPipeline(
                    new IHttpMiddleware[] { shortCircuit, shouldNotRun },
                    transport
                );

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.AreEqual(0, transport.RequestCount); // Transport never called
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Pipeline_PropagatesExceptions()
        {
            var throwingMiddleware = new ThrowingMiddleware();
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(
                new[] { throwingMiddleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            AssertAsync.ThrowsAsync<InvalidOperationException>(
                async () => await pipeline.ExecuteAsync(request, context));
        }

        [Test]
        public void Pipeline_NullTransport_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => new HttpPipeline(Array.Empty<IHttpMiddleware>(), null));
        }

        [Test]
        public void Pipeline_NullRequest_Throws()
        {
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(
                Array.Empty<IHttpMiddleware>(), transport);
            var context = new RequestContext(
                new UHttpRequest(HttpMethod.GET, new Uri("https://test.com")));

            AssertAsync.ThrowsAsync<ArgumentNullException>(
                async () => await pipeline.ExecuteAsync(null, context));
        }

        // --- Helper middleware classes ---

        private class OrderTrackingMiddleware : IHttpMiddleware
        {
            private readonly string _name;
            private readonly List<string> _order;

            public OrderTrackingMiddleware(string name, List<string> order)
            {
                _name = name;
                _order = order;
            }

            public async ValueTask<UHttpResponse> InvokeAsync(
                UHttpRequest request, RequestContext context,
                HttpPipelineDelegate next, CancellationToken ct)
            {
                _order.Add($"{_name}-Before");
                var response = await next(request, context, ct);
                _order.Add($"{_name}-After");
                return response;
            }
        }

        private class ShortCircuitMiddleware : IHttpMiddleware
        {
            public ValueTask<UHttpResponse> InvokeAsync(
                UHttpRequest request, RequestContext context,
                HttpPipelineDelegate next, CancellationToken ct)
            {
                // Return without calling next() — short-circuits the pipeline
                var response = new UHttpResponse(
                    HttpStatusCode.Forbidden, new HttpHeaders(), Array.Empty<byte>(),
                    context.Elapsed, request);
                return new ValueTask<UHttpResponse>(response);
            }
        }

        private class ThrowingMiddleware : IHttpMiddleware
        {
            public ValueTask<UHttpResponse> InvokeAsync(
                UHttpRequest request, RequestContext context,
                HttpPipelineDelegate next, CancellationToken ct)
            {
                throw new InvalidOperationException("Test exception");
            }
        }
    }
}
