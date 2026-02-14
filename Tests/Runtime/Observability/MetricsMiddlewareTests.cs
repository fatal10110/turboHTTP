using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Observability
{
    public class MetricsMiddlewareTests
    {
        [Test]
        public async Task TracksSuccessfulRequest()
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
        }

        [Test]
        public async Task TracksFailedRequest()
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
        }

        [Test]
        public async Task TracksByHost()
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
        }

        [Test]
        public async Task TracksByStatusCode()
        {
            var middleware = new MetricsMiddleware();
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.AreEqual(1, middleware.Metrics.RequestsByStatusCode[200]);
        }

        [Test]
        public async Task TracksBytesSent()
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
        }

        [Test]
        public async Task TracksBytesReceived()
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
        }

        [Test]
        public async Task Reset_ClearsAllMetrics()
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

            Assert.ThrowsAsync<UHttpException>(
                () => pipeline.ExecuteAsync(request, context));

            Assert.AreEqual(1, middleware.Metrics.TotalRequests);
            Assert.AreEqual(1, middleware.Metrics.FailedRequests);
        }
    }
}
