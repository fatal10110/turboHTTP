using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Retry;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Retry
{
    public class RetryInterceptorTests
    {
        [Test]
        public void SuccessOnFirstAttempt_NoRetry()
        {
            AssertAsync.Run(async () =>
            {
                var policy = CreatePolicy(maxRetries: 3);
                var middleware = new RetryInterceptor(policy);
                var transport = new MockTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                using var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(1, transport.RequestCount);
                Assert.IsFalse(context.Timeline.Any(evt => evt.Name == "RetrySucceeded"));
            });
        }

        [Test]
        public void ServerError_RetriesUntilSuccess_AndRecordsRetrySucceeded()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    int currentCall = Interlocked.Increment(ref callCount);
                    var status = currentCall == 1
                        ? HttpStatusCode.InternalServerError
                        : HttpStatusCode.OK;

                    return Task.FromResult(new UHttpResponse(
                        status,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RetryInterceptor(CreatePolicy(maxRetries: 3));
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                using var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, callCount);
                Assert.AreEqual(2, context.GetState<int>("RetryAttempt"));
                Assert.IsTrue(context.Timeline.Any(evt => evt.Name == "RetryScheduled"));
                Assert.IsTrue(context.Timeline.Any(evt => evt.Name == "RetrySucceeded"));
                Assert.IsFalse(context.Timeline.Any(evt => evt.Name == "RetryExhausted"));
            });
        }

        [Test]
        public void SuccessfulTerminalAttempt_DoesNotRecordRetryExhausted()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    int currentCall = Interlocked.Increment(ref callCount);
                    var status = currentCall <= 2
                        ? HttpStatusCode.InternalServerError
                        : HttpStatusCode.OK;

                    return Task.FromResult(new UHttpResponse(
                        status,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RetryInterceptor(CreatePolicy(maxRetries: 2));
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/final-success"));
                var context = new RequestContext(request);

                using var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(3, callCount);
                Assert.IsFalse(context.Timeline.Any(evt => evt.Name == "RetryExhausted"));
                Assert.IsTrue(context.Timeline.Any(evt => evt.Name == "RetrySucceeded"));
            });
        }

        [Test]
        public void RetriesExhausted_ReturnsLastResponse()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.InternalServerError);
                var middleware = new RetryInterceptor(CreatePolicy(maxRetries: 2));
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                using var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.AreEqual(3, transport.RequestCount);
                Assert.IsTrue(context.Timeline.Any(evt => evt.Name == "RetryExhausted"));
                Assert.IsFalse(context.Timeline.Any(evt => evt.Name == "RetrySucceeded"));
            });
        }

        [Test]
        public void RetryableTerminalException_RecordsRetryExhausted()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    throw new UHttpException(
                        new UHttpError(UHttpErrorType.NetworkError, "Connection reset"));
                });

                var middleware = new RetryInterceptor(CreatePolicy(maxRetries: 1));
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/error"));
                var context = new RequestContext(request);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await pipeline.ExecuteAsync(request, context).ConfigureAwait(false);
                });

                Assert.That(ex.HttpError.Type, Is.EqualTo(UHttpErrorType.NetworkError));
                Assert.AreEqual(2, transport.RequestCount);
                Assert.IsTrue(context.Timeline.Any(evt => evt.Name == "RetryExhausted"));
            });
        }

        [Test]
        public void ClientError_NoRetry()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.BadRequest);
                var middleware = new RetryInterceptor(CreatePolicy(maxRetries: 3));
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                using var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.AreEqual(1, transport.RequestCount);
            });
        }

        [Test]
        public void PostRequest_NotRetriedByDefault()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.InternalServerError);
                var middleware = new RetryInterceptor(CreatePolicy(maxRetries: 3));
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.com"));
                var context = new RequestContext(request);

                using var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.AreEqual(1, transport.RequestCount);
            });
        }

        [Test]
        public void PostRequest_RetriedWhenConfigured()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    int currentCall = Interlocked.Increment(ref callCount);
                    var status = currentCall == 1
                        ? HttpStatusCode.InternalServerError
                        : HttpStatusCode.OK;

                    return Task.FromResult(new UHttpResponse(
                        status,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RetryInterceptor(CreatePolicy(
                    maxRetries: 3,
                    onlyRetryIdempotent: false));
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.com"));
                var context = new RequestContext(request);

                using var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, callCount);
            });
        }

        [Test]
        public void RetryableException_Retries()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    int currentCall = Interlocked.Increment(ref callCount);
                    if (currentCall == 1)
                    {
                        throw new UHttpException(
                            new UHttpError(UHttpErrorType.NetworkError, "Connection reset"));
                    }

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RetryInterceptor(CreatePolicy(maxRetries: 3));
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                using var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, callCount);
            });
        }

        [Test]
        public void RetryableException_PostRequest_RetriesWhenConfigured()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    int currentCall = Interlocked.Increment(ref callCount);
                    if (currentCall == 1)
                    {
                        throw new UHttpException(
                            new UHttpError(UHttpErrorType.Timeout, "Timed out"));
                    }

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RetryInterceptor(CreatePolicy(
                    maxRetries: 3,
                    onlyRetryIdempotent: false));
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.com/post-error"));
                var context = new RequestContext(request);

                using var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, callCount);
            });
        }

        [Test]
        public void RetryableErrorAfterResponseStart_IsDeliveredWithoutRetry()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    Interlocked.Increment(ref callCount);
                    handler.OnRequestStart(req, ctx);
                    handler.OnResponseStart((int)HttpStatusCode.OK, new HttpHeaders(), ctx);
                    handler.OnResponseData(Encoding.UTF8.GetBytes("partial"), ctx);
                    handler.OnResponseError(
                        new UHttpException(new UHttpError(UHttpErrorType.NetworkError, "Connection reset")),
                        ctx);
                    return Task.CompletedTask;
                });

                var middleware = new RetryInterceptor(CreatePolicy(maxRetries: 3));
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/partial"));
                var context = new RequestContext(request);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    using var _ = await pipeline.ExecuteAsync(request, context).ConfigureAwait(false);
                });

                Assert.That(ex.HttpError.Type, Is.EqualTo(UHttpErrorType.NetworkError));
                Assert.AreEqual(1, callCount);
            });
        }

        [Test]
        public void CancellationDuringRetryDelay_PropagatesOperationCanceledException()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.InternalServerError);
                var middleware = new RetryInterceptor(new RetryPolicy
                {
                    MaxRetries = 3,
                    InitialDelay = TimeSpan.FromMilliseconds(250),
                    UseJitter = false
                });
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/cancel"));
                var context = new RequestContext(request);

                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromMilliseconds(25));

                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await pipeline.ExecuteAsync(request, context, cts.Token).ConfigureAwait(false);
                });

                Assert.AreEqual(1, transport.RequestCount);
            });
        }

        [Test]
        public void RetryAfterHeader_OverridesConfiguredDelay()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    int currentCall = Interlocked.Increment(ref callCount);
                    handler.OnRequestStart(req, ctx);

                    if (currentCall == 1)
                    {
                        var headers = new HttpHeaders();
                        // HTTP-date Retry-After values are only second-precision, so schedule
                        // at least one full second ahead to keep the observed delay deterministic.
                        var retryAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2);
                        headers.Set("Retry-After", retryAt.ToString("R"));
                        handler.OnResponseStart((int)HttpStatusCode.ServiceUnavailable, headers, ctx);
                        handler.OnResponseEnd(HttpHeaders.Empty, ctx);
                        return Task.CompletedTask;
                    }

                    handler.OnResponseStart((int)HttpStatusCode.OK, new HttpHeaders(), ctx);
                    handler.OnResponseEnd(HttpHeaders.Empty, ctx);
                    return Task.CompletedTask;
                });

                var middleware = new RetryInterceptor(new RetryPolicy
                {
                    MaxRetries = 1,
                    InitialDelay = TimeSpan.FromMilliseconds(1),
                    MaxDelay = TimeSpan.FromMilliseconds(5),
                    UseJitter = false
                });
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/retry-after"));
                var context = new RequestContext(request);

                using var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, callCount);

                var retryScheduled = context.Timeline.Single(evt => evt.Name == "RetryScheduled");
                var delayMs = Convert.ToDouble(retryScheduled.Data["delayMs"]);
                Assert.That(delayMs, Is.GreaterThanOrEqualTo(1000d));
            });
        }

        [Test]
        public void RetryAttempts_ForwardOnRequestStartOnlyOnce()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    int currentCall = Interlocked.Increment(ref callCount);
                    handler.OnRequestStart(req, ctx);
                    handler.OnResponseStart(
                        currentCall == 1
                            ? (int)HttpStatusCode.InternalServerError
                            : (int)HttpStatusCode.OK,
                        new HttpHeaders(),
                        ctx);
                    handler.OnResponseEnd(HttpHeaders.Empty, ctx);
                    return Task.CompletedTask;
                });

                var middleware = new RetryInterceptor(new RetryPolicy
                {
                    MaxRetries = 2,
                    InitialDelay = TimeSpan.Zero,
                    UseJitter = false
                });
                var pipeline = new InterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/start-once"));
                var context = new RequestContext(request);
                var handler = new CountingHandler();

                await pipeline.Pipeline(request, handler, context, CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(2, callCount);
                Assert.AreEqual(1, handler.RequestStartCount);
                Assert.AreEqual(1, handler.ResponseStartCount);
                Assert.AreEqual(1, handler.ResponseEndCount);
            });
        }

        [Test]
        public void StaticPolicies_ReturnIndependentInstances()
        {
            var firstDefault = RetryPolicy.Default;
            firstDefault.MaxRetries = 0;
            firstDefault.UseJitter = false;

            var secondDefault = RetryPolicy.Default;
            Assert.AreEqual(3, secondDefault.MaxRetries);
            Assert.IsTrue(secondDefault.UseJitter);

            var firstNoRetry = RetryPolicy.NoRetry;
            firstNoRetry.MaxRetries = 5;

            var secondNoRetry = RetryPolicy.NoRetry;
            Assert.AreEqual(0, secondNoRetry.MaxRetries);
        }

        [Test]
        public void RetryInterceptor_SnapshotsPolicyAtConstruction()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var policy = CreatePolicy(maxRetries: 1);
                var middleware = new RetryInterceptor(policy);
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    int currentCall = Interlocked.Increment(ref callCount);
                    var status = currentCall == 1
                        ? HttpStatusCode.InternalServerError
                        : HttpStatusCode.OK;

                    return Task.FromResult(new UHttpResponse(
                        status,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                policy.MaxRetries = 0;

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/snapshot"));
                var context = new RequestContext(request);

                using var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, callCount);
            });
        }

        [Test]
        public void RetryPolicy_RejectsInvalidConfigurationValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { BackoffMultiplier = 0d });
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { BackoffMultiplier = -1d });
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { MaxRetries = -1 });
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { InitialDelay = TimeSpan.FromMilliseconds(-1) });
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy { MaxDelay = TimeSpan.Zero });
        }

        [Test]
        public void NoRetryPolicy_PassesThrough()
        {
            AssertAsync.Run(async () =>
            {
                var middleware = new RetryInterceptor(RetryPolicy.NoRetry);
                var transport = new MockTransport(HttpStatusCode.InternalServerError);
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                using var response = await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.AreEqual(1, transport.RequestCount);
            });
        }

        private static RetryPolicy CreatePolicy(
            int maxRetries,
            bool onlyRetryIdempotent = true)
        {
            return new RetryPolicy
            {
                MaxRetries = maxRetries,
                InitialDelay = TimeSpan.FromMilliseconds(1),
                OnlyRetryIdempotent = onlyRetryIdempotent,
                UseJitter = false
            };
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
                context.SetState(TransportBehaviorFlags.SelfDrainsResponseBody, true);
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

        private sealed class CountingHandler : IHttpHandler
        {
            public int RequestStartCount { get; private set; }
            public int ResponseStartCount { get; private set; }
            public int ResponseEndCount { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                RequestStartCount++;
            }

            public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
            {
                ResponseStartCount++;
            }

            public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
            {
            }

            public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
            {
                ResponseEndCount++;
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Assert.Fail($"Unexpected error delivered: {error}");
            }
        }
    }
}
