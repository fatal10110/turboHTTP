# Phase 4.5: Tests & Integration

**Depends on:** Phases 4.2, 4.3, 4.4
**Assembly:** `TurboHTTP.Tests.Runtime`
**Files:** 8 new

All tests use NUnit with async/await patterns. Test assembly already references all modules.

**Create directories:**
- `Tests/Runtime/Pipeline/`
- `Tests/Runtime/Auth/` (if not exists)
- `Tests/Runtime/Observability/` (if not exists)

---

## Step 1: HttpPipelineTests

**File:** `Tests/Runtime/Pipeline/HttpPipelineTests.cs`
**Namespace:** `TurboHTTP.Tests.Pipeline`

```csharp
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
        public async Task Pipeline_ExecutesMiddlewareInOrder()
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
        }

        [Test]
        public async Task Pipeline_EmptyMiddleware_CallsTransportDirectly()
        {
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(
                Array.Empty<IHttpMiddleware>(), transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            var response = await pipeline.ExecuteAsync(request, context);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(1, transport.RequestCount);
        }

        [Test]
        public async Task Pipeline_MiddlewareCanShortCircuit()
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

            Assert.ThrowsAsync<InvalidOperationException>(
                () => pipeline.ExecuteAsync(request, context));
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

            Assert.ThrowsAsync<ArgumentNullException>(
                () => pipeline.ExecuteAsync(null, context));
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

            public async Task<UHttpResponse> InvokeAsync(
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
            public Task<UHttpResponse> InvokeAsync(
                UHttpRequest request, RequestContext context,
                HttpPipelineDelegate next, CancellationToken ct)
            {
                // Return without calling next() — short-circuits the pipeline
                var response = new UHttpResponse(
                    HttpStatusCode.Forbidden, new HttpHeaders(), null,
                    context.Elapsed, request);
                return Task.FromResult(response);
            }
        }

        private class ThrowingMiddleware : IHttpMiddleware
        {
            public Task<UHttpResponse> InvokeAsync(
                UHttpRequest request, RequestContext context,
                HttpPipelineDelegate next, CancellationToken ct)
            {
                throw new InvalidOperationException("Test exception");
            }
        }
    }
}
```

---

## Step 2: LoggingMiddlewareTests

**File:** `Tests/Runtime/Pipeline/LoggingMiddlewareTests.cs`
**Namespace:** `TurboHTTP.Tests.Pipeline`

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Pipeline
{
    public class LoggingMiddlewareTests
    {
        [Test]
        public async Task LogsRequestAndResponse()
        {
            var logs = new List<string>();
            var middleware = new LoggingMiddleware(msg => logs.Add(msg));
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/api"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.AreEqual(2, logs.Count);
            Assert.That(logs[0], Does.Contain("GET"));
            Assert.That(logs[0], Does.Contain("test.com"));
            Assert.That(logs[1], Does.Contain("200"));
        }

        [Test]
        public async Task LogLevelNone_NoLogs()
        {
            var logs = new List<string>();
            var middleware = new LoggingMiddleware(
                msg => logs.Add(msg),
                LoggingMiddleware.LogLevel.None);
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.IsEmpty(logs);
        }

        [Test]
        public async Task NonSuccessStatus_LogsWarn()
        {
            var logs = new List<string>();
            var middleware = new LoggingMiddleware(msg => logs.Add(msg));
            var transport = new MockTransport(HttpStatusCode.NotFound);
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.That(logs[1], Does.Contain("[WARN]"));
        }

        [Test]
        public void Exception_LogsError()
        {
            var logs = new List<string>();
            var middleware = new LoggingMiddleware(msg => logs.Add(msg));
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

            Assert.AreEqual(2, logs.Count); // Request log + error log
            Assert.That(logs[1], Does.Contain("[ERROR]"));
        }
    }
}
```

---

## Step 3: DefaultHeadersMiddlewareTests

**File:** `Tests/Runtime/Pipeline/DefaultHeadersMiddlewareTests.cs`
**Namespace:** `TurboHTTP.Tests.Pipeline`

```csharp
using System;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Pipeline
{
    public class DefaultHeadersMiddlewareTests
    {
        [Test]
        public async Task AddsDefaultHeaders()
        {
            var defaults = new HttpHeaders();
            defaults.Set("X-Custom", "DefaultValue");
            defaults.Set("Accept", "application/json");

            var middleware = new DefaultHeadersMiddleware(defaults);
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.AreEqual("DefaultValue", transport.LastRequest.Headers.Get("X-Custom"));
            Assert.AreEqual("application/json", transport.LastRequest.Headers.Get("Accept"));
        }

        [Test]
        public async Task DoesNotOverrideExistingHeaders()
        {
            var defaults = new HttpHeaders();
            defaults.Set("Accept", "application/json");

            var middleware = new DefaultHeadersMiddleware(defaults);
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var headers = new HttpHeaders();
            headers.Set("Accept", "text/html");
            var request = new UHttpRequest(
                HttpMethod.GET, new Uri("https://test.com"), headers);
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            // Request header takes precedence
            Assert.AreEqual("text/html", transport.LastRequest.Headers.Get("Accept"));
        }

        [Test]
        public async Task OverridesWhenConfigured()
        {
            var defaults = new HttpHeaders();
            defaults.Set("Accept", "application/json");

            var middleware = new DefaultHeadersMiddleware(defaults, overrideExisting: true);
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var headers = new HttpHeaders();
            headers.Set("Accept", "text/html");
            var request = new UHttpRequest(
                HttpMethod.GET, new Uri("https://test.com"), headers);
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.AreEqual("application/json", transport.LastRequest.Headers.Get("Accept"));
        }

        [Test]
        public async Task DoesNotModifyOriginalRequest()
        {
            var defaults = new HttpHeaders();
            defaults.Set("X-Added", "value");

            var middleware = new DefaultHeadersMiddleware(defaults);
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            // Original request should NOT have the added header
            Assert.IsNull(request.Headers.Get("X-Added"));
            // Transport should have received it
            Assert.AreEqual("value", transport.LastRequest.Headers.Get("X-Added"));
        }
    }
}
```

---

## Step 4: TimeoutMiddlewareTests

**File:** `Tests/Runtime/Pipeline/TimeoutMiddlewareTests.cs`
**Namespace:** `TurboHTTP.Tests.Pipeline`

```csharp
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
```

---

## Step 5: RetryMiddlewareTests

**File:** `Tests/Runtime/Retry/RetryMiddlewareTests.cs`
**Namespace:** `TurboHTTP.Tests.Retry`

```csharp
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
        public void RetryableException_Retries()
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

            Assert.DoesNotThrowAsync(async () =>
            {
                var response = await pipeline.ExecuteAsync(request, context);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            });

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
```

---

## Step 6: AuthMiddlewareTests

**File:** `Tests/Runtime/Auth/AuthMiddlewareTests.cs`
**Namespace:** `TurboHTTP.Tests.Auth`

```csharp
using System;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Auth
{
    public class AuthMiddlewareTests
    {
        [Test]
        public async Task AddsAuthorizationHeader()
        {
            var provider = new StaticTokenProvider("test-token-123");
            var middleware = new AuthMiddleware(provider);
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.AreEqual(
                "Bearer test-token-123",
                transport.LastRequest.Headers.Get("Authorization"));
        }

        [Test]
        public async Task CustomScheme()
        {
            var provider = new StaticTokenProvider("api-key-456");
            var middleware = new AuthMiddleware(provider, scheme: "ApiKey");
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.AreEqual(
                "ApiKey api-key-456",
                transport.LastRequest.Headers.Get("Authorization"));
        }

        [Test]
        public async Task EmptyToken_SkipsHeader()
        {
            var provider = new StaticTokenProvider("");
            var middleware = new AuthMiddleware(provider);
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.IsNull(transport.LastRequest.Headers.Get("Authorization"));
        }

        [Test]
        public async Task NullToken_SkipsHeader()
        {
            var provider = new StaticTokenProvider(null);
            var middleware = new AuthMiddleware(provider);
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.IsNull(transport.LastRequest.Headers.Get("Authorization"));
        }

        [Test]
        public void NullTokenProvider_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => new AuthMiddleware(null));
        }
    }
}
```

---

## Step 7: MetricsMiddlewareTests

**File:** `Tests/Runtime/Observability/MetricsMiddlewareTests.cs`
**Namespace:** `TurboHTTP.Tests.Observability`

```csharp
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

            await pipeline.ExecuteAsync(
                new UHttpRequest(HttpMethod.GET, new Uri("https://api.example.com/a")),
                new RequestContext(
                    new UHttpRequest(HttpMethod.GET, new Uri("https://api.example.com/a"))));

            await pipeline.ExecuteAsync(
                new UHttpRequest(HttpMethod.GET, new Uri("https://other.com/b")),
                new RequestContext(
                    new UHttpRequest(HttpMethod.GET, new Uri("https://other.com/b"))));

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
```

---

## Step 8: PipelineIntegrationTests

**File:** `Tests/Runtime/Integration/PipelineIntegrationTests.cs`
**Namespace:** `TurboHTTP.Tests.Integration`

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Integration
{
    public class PipelineIntegrationTests
    {
        [Test]
        public async Task FullPipeline_AllMiddlewaresExecute()
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
        }

        [Test]
        public async Task NoMiddlewares_WorksLikeDirectTransport()
        {
            var transport = new MockTransport();
            var options = new UHttpClientOptions { Transport = transport };

            using var client = new UHttpClient(options);
            var response = await client.Get("https://test.com/api").SendAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(1, transport.RequestCount);
        }
    }
}
```

---

## Verification Criteria

- [ ] All pipeline tests pass (ordering, short-circuit, exceptions, null args)
- [ ] All logging tests pass (request/response/error logging, log levels)
- [ ] All default headers tests pass (add, no-override, override, immutability)
- [ ] All timeout tests pass (fast request, timeout response, user cancellation)
- [ ] All retry tests pass (success, server error, 4xx, POST, exhaustion, exceptions, context)
- [ ] All auth tests pass (bearer, custom scheme, empty token, null token)
- [ ] All metrics tests pass (success, failure, by-host, by-status, bytes, reset, exception)
- [ ] Integration test passes with full middleware pipeline through `UHttpClient`
- [ ] No-middleware integration test passes (backwards compatibility)
