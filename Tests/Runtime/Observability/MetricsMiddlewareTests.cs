using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Observability
{
    public class MetricsMiddlewareTests
    {
        [Test]
        public void TracksSuccessfulRequest()        {
            Task.Run(async () =>
            {
                var middleware = new MetricsMiddleware();
                var transport = new MockTransport();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

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
                var middleware = new MetricsMiddleware();
                var transport = new MockTransport(HttpStatusCode.InternalServerError);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

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
                var middleware = new MetricsMiddleware();
                var transport = new MockTransport();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

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
                var middleware = new MetricsMiddleware();
                var transport = new MockTransport();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

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
                var middleware = new MetricsMiddleware();
                var transport = new MockTransport();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

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
                var middleware = new MetricsMiddleware();
                var transport = new MockTransport(
                    HttpStatusCode.OK, body: responseBody);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

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
                var middleware = new MetricsMiddleware();
                var transport = new MockTransport();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

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
            var middleware = new MetricsMiddleware();
            var transport = new MockTransport((req, ctx, ct) =>
            {
                throw new UHttpException(
                    new UHttpError(UHttpErrorType.NetworkError, "Connection refused"));
            });
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            AssertAsync.ThrowsAsync<UHttpException>(
                async () => await pipeline.ExecuteAsync(request, context));

            Assert.AreEqual(1, middleware.Metrics.TotalRequests);
            Assert.AreEqual(1, middleware.Metrics.FailedRequests);
        }
    }
}
