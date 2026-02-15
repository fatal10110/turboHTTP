using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Observability;
using TurboHTTP.Retry;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Integration
{
    public class PipelineIntegrationTests
    {
        [Test]
        public void FullPipeline_AllMiddlewaresExecute()        {
            Task.Run(async () =>
            {
                var logs = new List<string>();
                var metricsMiddleware = new MetricsMiddleware();

                var defaultHeaders = new HttpHeaders();
                defaultHeaders.Set("X-Client", "TurboHTTP");

                var options = new UHttpClientOptions
                {
                    Transport = new MockTransport(),
                    Middlewares = new List<IHttpMiddleware>
                    {
                        new LoggingMiddleware(msg => logs.Add(msg)),
                        metricsMiddleware,
                        new DefaultHeadersMiddleware(defaultHeaders),
                        new AuthMiddleware(new StaticTokenProvider("test-token"))
                    }
                };

                using var client = new UHttpClient(options);
                var response = await client.Get("https://api.example.com/data").SendAsync();

                // Verify response
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                // Verify logging captured request + response
                Assert.AreEqual(2, logs.Count);

                // Verify metrics
                Assert.AreEqual(1, metricsMiddleware.Metrics.TotalRequests);
                Assert.AreEqual(1, metricsMiddleware.Metrics.SuccessfulRequests);

                // Verify default headers were applied (check via mock transport)
                var transport = (MockTransport)options.Transport;
                Assert.AreEqual("TurboHTTP", transport.LastRequest.Headers.Get("X-Client"));

                // Verify auth header was applied
                Assert.AreEqual("Bearer test-token",
                    transport.LastRequest.Headers.Get("Authorization"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void NoMiddlewares_WorksLikeDirectTransport()        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                var options = new UHttpClientOptions { Transport = transport };

                using var client = new UHttpClient(options);
                var response = await client.Get("https://test.com/api").SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(1, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RetryOnTimeoutException_RetriesWhenTransportTimesOut()        {
            Task.Run(async () =>
            {
                int callCount = 0;
                // Simulate transport throwing UHttpException(Timeout) on first attempt,
                // same as RawSocketTransport does when request.Timeout fires.
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    if (callCount <= 1)
                    {
                        throw new UHttpException(new UHttpError(
                            UHttpErrorType.Timeout,
                            "Request timed out after 0.05s"));
                    }
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>(), ctx.Elapsed, req));
                });

                var retryPolicy = new RetryPolicy
                {
                    MaxRetries = 2,
                    InitialDelay = TimeSpan.FromMilliseconds(1)
                };

                var pipeline = new HttpPipeline(
                    new IHttpMiddleware[]
                    {
                        new RetryMiddleware(retryPolicy)
                    },
                    transport);

                var request = new UHttpRequest(
                    HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, callCount); // First attempt timed out, second succeeded
            }).GetAwaiter().GetResult();
        }
    }
}
