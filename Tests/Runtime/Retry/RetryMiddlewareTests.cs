using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Retry;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Retry
{
    public class RetryMiddlewareTests
    {
        [Test]
        public void SuccessOnFirstAttempt_NoRetry()        {
            Task.Run(async () =>
            {
                var policy = new RetryPolicy { MaxRetries = 3 };
                var middleware = new RetryMiddleware(policy);
                var transport = new MockTransport();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(1, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ServerError_RetriesUntilSuccess()        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    var status = callCount <= 2
                        ? HttpStatusCode.InternalServerError
                        : HttpStatusCode.OK;
                    return Task.FromResult(new UHttpResponse(
                        status, new HttpHeaders(), null, ctx.Elapsed, req));
                });

                var policy = new RetryPolicy
                {
                    MaxRetries = 3,
                    InitialDelay = TimeSpan.FromMilliseconds(1) // Fast for tests
                };
                var middleware = new RetryMiddleware(policy);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(3, callCount); // 2 failures + 1 success
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RetriesExhausted_ReturnsLastResponse()        {
            Task.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.InternalServerError);
                var policy = new RetryPolicy
                {
                    MaxRetries = 2,
                    InitialDelay = TimeSpan.FromMilliseconds(1)
                };
                var middleware = new RetryMiddleware(policy);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.AreEqual(3, transport.RequestCount); // 1 original + 2 retries
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ClientError_NoRetry()        {
            Task.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.BadRequest);
                var policy = new RetryPolicy
                {
                    MaxRetries = 3,
                    InitialDelay = TimeSpan.FromMilliseconds(1)
                };
                var middleware = new RetryMiddleware(policy);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.AreEqual(1, transport.RequestCount); // No retry
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PostRequest_NotRetriedByDefault()        {
            Task.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.InternalServerError);
                var policy = new RetryPolicy
                {
                    MaxRetries = 3,
                    InitialDelay = TimeSpan.FromMilliseconds(1)
                };
                var middleware = new RetryMiddleware(policy);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.com"));
                var context = new RequestContext(request);

                var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.AreEqual(1, transport.RequestCount); // POST not retried
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PostRequest_RetriedWhenConfigured()        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    var status = callCount <= 1
                        ? HttpStatusCode.InternalServerError
                        : HttpStatusCode.OK;
                    return Task.FromResult(new UHttpResponse(
                        status, new HttpHeaders(), null, ctx.Elapsed, req));
                });

                var policy = new RetryPolicy
                {
                    MaxRetries = 3,
                    InitialDelay = TimeSpan.FromMilliseconds(1),
                    OnlyRetryIdempotent = false // Allow POST retry
                };
                var middleware = new RetryMiddleware(policy);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.com"));
                var context = new RequestContext(request);

                var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, callCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RecordsRetryEventsInContext()        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    var status = callCount <= 1
                        ? HttpStatusCode.InternalServerError
                        : HttpStatusCode.OK;
                    return Task.FromResult(new UHttpResponse(
                        status, new HttpHeaders(), null, ctx.Elapsed, req));
                });

                var policy = new RetryPolicy
                {
                    MaxRetries = 3,
                    InitialDelay = TimeSpan.FromMilliseconds(1)
                };
                var middleware = new RetryMiddleware(policy);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(2, context.GetState<int>("RetryAttempt"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RetryableException_Retries()        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    if (callCount <= 1)
                        throw new UHttpException(
                            new UHttpError(UHttpErrorType.NetworkError, "Connection reset"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, new HttpHeaders(), null, ctx.Elapsed, req));
                });

                var policy = new RetryPolicy
                {
                    MaxRetries = 3,
                    InitialDelay = TimeSpan.FromMilliseconds(1)
                };
                var middleware = new RetryMiddleware(policy);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                var response = await pipeline.ExecuteAsync(request, context);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, callCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void NoRetryPolicy_PassesThrough()        {
            Task.Run(async () =>
            {
                var middleware = new RetryMiddleware(RetryPolicy.NoRetry);
                var transport = new MockTransport(HttpStatusCode.InternalServerError);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(1, transport.RequestCount); // No retry
            }).GetAwaiter().GetResult();
        }
    }
}
