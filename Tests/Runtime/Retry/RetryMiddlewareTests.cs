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
        public async Task SuccessOnFirstAttempt_NoRetry()
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
        }

        [Test]
        public async Task ServerError_RetriesUntilSuccess()
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
        }

        [Test]
        public async Task RetriesExhausted_ReturnsLastResponse()
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
        }

        [Test]
        public async Task ClientError_NoRetry()
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
        }

        [Test]
        public async Task PostRequest_NotRetriedByDefault()
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
        }

        [Test]
        public async Task PostRequest_RetriedWhenConfigured()
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
        }

        [Test]
        public async Task RecordsRetryEventsInContext()
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
        }

        [Test]
        public async Task RetryableException_Retries()
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
        }

        [Test]
        public async Task NoRetryPolicy_PassesThrough()
        {
            var middleware = new RetryMiddleware(RetryPolicy.NoRetry);
            var transport = new MockTransport(HttpStatusCode.InternalServerError);
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            var response = await pipeline.ExecuteAsync(request, context);

            Assert.AreEqual(1, transport.RequestCount); // No retry
        }
    }
}
