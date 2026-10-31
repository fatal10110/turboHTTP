using System;
using System.Buffers;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Retry;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Retry
{
    /// <summary>
    /// Retry-After is bounded by <see cref="RetryPolicy.MaxDelay"/> (issue #54).
    /// </summary>
    public class RetryAfterCapTests
    {
        // Safety net only: on a regression the interceptor would wait for the server-provided day,
        // so the test cancels instead of hanging the runner. The passing path never waits on it.
        private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

        [Test]
        public void ComputeDelay_RetryAfterAboveMaxDelay_IsClampedToMaxDelay()
        {
            var policy = new RetryPolicy
            {
                InitialDelay = TimeSpan.FromMilliseconds(10),
                MaxDelay = TimeSpan.FromSeconds(2),
                UseJitter = false
            };

            var delay = policy.ComputeDelay(1, TimeSpan.FromSeconds(86400));

            Assert.AreEqual(TimeSpan.FromSeconds(2), delay);
        }

        [Test]
        public void ComputeDelay_RetryAfterAboveMaxDelay_WithJitter_NeverExceedsMaxDelay()
        {
            var policy = new RetryPolicy
            {
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(3),
                UseJitter = true
            };

            for (int attempt = 1; attempt <= 8; attempt++)
            {
                var delay = policy.ComputeDelay(attempt, TimeSpan.FromSeconds(86400));
                Assert.That(delay, Is.LessThanOrEqualTo(policy.MaxDelay), "attempt " + attempt);
            }
        }

        [Test]
        public void ComputeDelay_RetryAfterWithinMaxDelay_RaisesDelayToRetryAfter()
        {
            var policy = new RetryPolicy
            {
                InitialDelay = TimeSpan.FromMilliseconds(10),
                MaxDelay = TimeSpan.FromSeconds(30),
                UseJitter = true
            };

            var delay = policy.ComputeDelay(1, TimeSpan.FromSeconds(5));

            Assert.AreEqual(TimeSpan.FromSeconds(5), delay);
        }

        [Test]
        public void RetryAfter86400_ReturnsServerResponseImmediately_WithoutRetrying()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    int call = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    if (call == 1)
                    {
                        headers.Set("Retry-After", "86400");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.ServiceUnavailable,
                            headers,
                            new byte[] { 0x42 },
                            ctx.Elapsed,
                            req));
                    }

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, headers, Array.Empty<byte>(), ctx.Elapsed, req));
                });

                var interceptor = new RetryInterceptor(new RetryPolicy
                {
                    MaxRetries = 3,
                    InitialDelay = TimeSpan.FromMilliseconds(1),
                    MaxDelay = TimeSpan.FromMilliseconds(50),
                    UseJitter = false
                });
                var pipeline = new TestInterceptorPipeline(new[] { interceptor }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/retry-after-day"));
                var context = new RequestContext(request);

                using var guard = new CancellationTokenSource(HangGuard);
                using var response = await pipeline.ExecuteAsync(request, context, guard.Token);

                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.AreEqual("86400", response.Headers.Get("Retry-After"));
                Assert.AreEqual(new byte[] { 0x42 }, response.Body.ToArray());
                Assert.AreEqual(1, callCount);
                Assert.IsFalse(context.Timeline.Any(evt => evt.Name == "RetryScheduled"));
                Assert.IsFalse(context.Timeline.Any(evt => evt.Name == "RetrySucceeded"));

                var capped = context.Timeline.Single(evt => evt.Name == "RetryAfterExceedsMaxDelay");
                Assert.AreEqual(86400000d, Convert.ToDouble(capped.Data["retryAfterMs"]));
                Assert.AreEqual(50d, Convert.ToDouble(capped.Data["maxDelayMs"]));
            });
        }

        [Test]
        public void RetryAfterAboveCap_OnLaterAttempt_ReturnsThatResponse()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    int call = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    if (call == 2)
                        headers.Set("Retry-After", "86400");

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.ServiceUnavailable, headers, Array.Empty<byte>(), ctx.Elapsed, req));
                });

                var interceptor = new RetryInterceptor(new RetryPolicy
                {
                    MaxRetries = 3,
                    InitialDelay = TimeSpan.FromMilliseconds(1),
                    MaxDelay = TimeSpan.FromMilliseconds(5),
                    UseJitter = false
                });
                var pipeline = new TestInterceptorPipeline(new[] { interceptor }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/retry-after-later"));
                var context = new RequestContext(request);

                using var guard = new CancellationTokenSource(HangGuard);
                using var response = await pipeline.ExecuteAsync(request, context, guard.Token);

                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.AreEqual("86400", response.Headers.Get("Retry-After"));
                Assert.AreEqual(2, callCount);
                Assert.AreEqual(1, context.Timeline.Count(evt => evt.Name == "RetryScheduled"));
                Assert.IsFalse(context.Timeline.Any(evt => evt.Name == "RetrySucceeded"));
            });
        }

        [Test]
        public void RetryAfterZero_IsWithinCap_AndRetries()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    int call = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    if (call == 1)
                    {
                        headers.Set("Retry-After", "0");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.ServiceUnavailable, headers, Array.Empty<byte>(), ctx.Elapsed, req));
                    }

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, headers, Array.Empty<byte>(), ctx.Elapsed, req));
                });

                var interceptor = new RetryInterceptor(new RetryPolicy
                {
                    MaxRetries = 1,
                    InitialDelay = TimeSpan.FromMilliseconds(1),
                    MaxDelay = TimeSpan.FromMilliseconds(5),
                    UseJitter = false
                });
                var pipeline = new TestInterceptorPipeline(new[] { interceptor }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/retry-after-zero"));
                var context = new RequestContext(request);

                using var guard = new CancellationTokenSource(HangGuard);
                using var response = await pipeline.ExecuteAsync(request, context, guard.Token);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, callCount);
                Assert.IsFalse(context.Timeline.Any(evt => evt.Name == "RetryAfterExceedsMaxDelay"));
            });
        }
    }
}
