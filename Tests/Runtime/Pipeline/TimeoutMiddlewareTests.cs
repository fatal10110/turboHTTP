using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Pipeline
{
    public class TimeoutMiddlewareTests
    {
        [Test]
        public async Task FastRequest_ReturnsNormally()
        {
            var middleware = new TimeoutMiddleware();
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(
                HttpMethod.GET, new Uri("https://test.com"),
                timeout: TimeSpan.FromSeconds(5));
            var context = new RequestContext(request);

            var response = await pipeline.ExecuteAsync(request, context);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [Test]
        public async Task SlowRequest_ReturnsTimeoutResponse()
        {
            var middleware = new TimeoutMiddleware();
            // Simulate a slow transport
            var transport = new MockTransport(async (req, ctx, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new UHttpResponse(
                    HttpStatusCode.OK, new HttpHeaders(), null, ctx.Elapsed, req);
            });
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(
                HttpMethod.GET, new Uri("https://test.com"),
                timeout: TimeSpan.FromMilliseconds(50));
            var context = new RequestContext(request);

            var response = await pipeline.ExecuteAsync(request, context);

            Assert.AreEqual(HttpStatusCode.RequestTimeout, response.StatusCode);
            Assert.IsNotNull(response.Error);
            Assert.AreEqual(UHttpErrorType.Timeout, response.Error.Type);
        }

        [Test]
        public void UserCancellation_ThrowsOperationCancelled()
        {
            var middleware = new TimeoutMiddleware();
            var transport = new MockTransport(async (req, ctx, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new UHttpResponse(
                    HttpStatusCode.OK, new HttpHeaders(), null, ctx.Elapsed, req);
            });
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(
                HttpMethod.GET, new Uri("https://test.com"),
                timeout: TimeSpan.FromSeconds(30));
            var context = new RequestContext(request);

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Cancel immediately

            Assert.ThrowsAsync<OperationCanceledException>(
                () => pipeline.ExecuteAsync(request, context, cts.Token));
        }
    }
}
