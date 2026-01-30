# Phase 4: Pipeline Infrastructure

**Milestone:** M1 (v0.1 "usable")
**Dependencies:** Phase 3 (Client API)
**Estimated Complexity:** High
**Critical:** Yes - Core architecture pattern

## Overview

Implement the middleware pipeline architecture that allows request/response interception and transformation. Create essential middlewares: Logging, Timeout, DefaultHeaders, Retry, Auth, and Metrics. This phase establishes the pattern that all advanced features will follow.

## Goals

1. Create `IHttpMiddleware` interface
2. Implement `HttpPipeline` executor
3. Integrate pipeline into `UHttpClient`
4. Create `LoggingMiddleware` for request/response logging
5. Create `TimeoutMiddleware` for enforcing timeouts
6. Create `DefaultHeadersMiddleware` for adding headers
7. Create `RetryMiddleware` (basic retry logic in TurboHTTP.Retry module)
8. Create `AuthMiddleware` (basic auth in TurboHTTP.Auth module)
9. Create `MetricsMiddleware` (basic metrics in TurboHTTP.Observability module)
10. Create `DecompressionMiddleware` (Gzip/Deflate support)
11. Create `CookieMiddleware` (Cookie jar support)

## Tasks

### Task 4.1: Middleware Interface

**File:** `Runtime/Core/IHttpMiddleware.cs`

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Delegate representing the next middleware in the pipeline.
    /// </summary>
    public delegate Task<UHttpResponse> HttpPipelineDelegate(
        UHttpRequest request,
        RequestContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Middleware that can intercept and transform HTTP requests/responses.
    /// Middleware is executed in order: Request → [Middleware Chain] → Transport → [Middleware Chain] → Response
    /// </summary>
    public interface IHttpMiddleware
    {
        /// <summary>
        /// Process the request and/or response.
        /// Must call next() to continue the pipeline, or return early to short-circuit.
        /// </summary>
        /// <param name="request">The HTTP request</param>
        /// <param name="context">Request execution context</param>
        /// <param name="next">Delegate to invoke the next middleware</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The HTTP response</returns>
        Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken);
    }
}
```

**Notes:**

- Middleware pattern similar to ASP.NET Core
- Each middleware must call `next()` to continue pipeline
- Middleware can transform request before calling `next()`
- Middleware can transform response after `next()` returns
- Middleware can short-circuit by returning without calling `next()`

### Task 4.2: Pipeline Executor

**File:** `Runtime/Core/HttpPipeline.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Executes a chain of middleware followed by the transport layer.
    /// </summary>
    public class HttpPipeline
    {
        private readonly IReadOnlyList<IHttpMiddleware> _middlewares;
        private readonly IHttpTransport _transport;
        private readonly HttpPipelineDelegate _pipeline;

        public HttpPipeline(IEnumerable<IHttpMiddleware> middlewares, IHttpTransport transport)
        {
            _middlewares = middlewares?.ToList() ?? new List<IHttpMiddleware>();
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _pipeline = BuildPipeline();
        }

        /// <summary>
        /// Execute the pipeline for a given request.
        /// </summary>
        public Task<UHttpResponse> ExecuteAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            // Execute the pipeline
            return _pipeline(request, context, cancellationToken);
        }

        private HttpPipelineDelegate BuildPipeline()
        {
            // Start with the transport as the final step
            HttpPipelineDelegate pipeline = (req, ctx, ct) =>
                _transport.SendAsync(req, ctx, ct);

            // Wrap each middleware in reverse order
            // This ensures they execute in the correct order:
            // Request: M1 → M2 → M3 → Transport
            // Response: Transport → M3 → M2 → M1
            for (int i = _middlewares.Count - 1; i >= 0; i--)
            {
                var middleware = _middlewares[i];
                var next = pipeline;

                pipeline = (req, ctx, ct) =>
                    middleware.InvokeAsync(req, ctx, next, ct);
            }

            return pipeline;
        }
    }
}
```

**Notes:**

- Pipeline delegate chain is built once per `HttpPipeline` instance and reused across requests
- Middleware executes in order for requests, reverse order for responses
- Transport is the final step in the pipeline

### Task 4.3: Integrate Pipeline into UHttpClient

**File:** `Runtime/Core/UHttpClient.cs` (update `SendAsync` method)

**Note:** Construct `_pipeline` once in the `UHttpClient` constructor (or lazily on first use) after options are finalized:
`_pipeline = new HttpPipeline(Options.Middlewares, _transport);`. Treat `Options.Middlewares` as immutable after the client is created.

```csharp
/// <summary>
/// Send a pre-built request.
/// This is the core execution method.
/// </summary>
public async Task<UHttpResponse> SendAsync(
    UHttpRequest request,
    CancellationToken cancellationToken = default)
{
    if (request == null)
        throw new ArgumentNullException(nameof(request));

    var context = new RequestContext(request);
    context.RecordEvent("RequestStart");

    try
    {
        // Execute a cached pipeline (construct once per client, not per request)
        var response = await _pipeline.ExecuteAsync(request, context, cancellationToken);

        context.RecordEvent("RequestComplete");
        context.Stop();

        return response;
    }
    catch (UHttpException)
    {
        context.RecordEvent("RequestFailed");
        context.Stop();
        throw; // Re-throw UHttpException as-is
    }
    catch (Exception ex)
    {
        context.RecordEvent("RequestFailed", new System.Collections.Generic.Dictionary<string, object>
        {
            { "error", ex.Message }
        });
        context.Stop();

        // Convert exception to UHttpError
        var error = new UHttpError(
            UHttpErrorType.Unknown,
            ex.Message,
            ex
        );

        throw new UHttpException(error);
    }
}
```

### Task 4.4: Logging Middleware

**File:** `Runtime/Core/Pipeline/Middlewares/LoggingMiddleware.cs`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Middleware that logs HTTP requests and responses.
    /// </summary>
    public class LoggingMiddleware : IHttpMiddleware
    {
        private readonly Action<string> _log;
        private readonly LogLevel _logLevel;
        private readonly bool _logHeaders;
        private readonly bool _logBody;

        public enum LogLevel
        {
            None,
            Minimal,   // Only log URL and status
            Standard,  // Log URL, status, elapsed time
            Detailed   // Log everything including headers and body
        }

        public LoggingMiddleware(
            Action<string> log = null,
            LogLevel logLevel = LogLevel.Standard,
            bool logHeaders = false,
            bool logBody = false)
        {
            _log = log ?? (_ => { });
            _logLevel = logLevel;
            _logHeaders = logHeaders;
            _logBody = logBody;
        }

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            if (_logLevel == LogLevel.None)
            {
                return await next(request, context, cancellationToken);
            }

            // Log request
            LogRequest(request);

            // Execute next middleware
            var startTime = DateTime.UtcNow;
            UHttpResponse response = null;
            Exception exception = null;

            try
            {
                response = await next(request, context, cancellationToken);
                return response;
            }
            catch (Exception ex)
            {
                exception = ex;
                throw;
            }
            finally
            {
                var elapsed = DateTime.UtcNow - startTime;

                // Log response or error
                if (exception != null)
                {
                    LogError(request, exception, elapsed);
                }
                else if (response != null)
                {
                    LogResponse(request, response);
                }
            }
        }

        private void LogRequest(UHttpRequest request)
        {
            var message = $"→ {request.Method} {request.Uri}";

            if (_logLevel >= LogLevel.Detailed && _logHeaders && request.Headers.Count > 0)
            {
                message += "\n  Headers:";
                foreach (var header in request.Headers)
                {
                    message += $"\n    {header.Key}: {header.Value}";
                }
            }

            if (_logLevel >= LogLevel.Detailed && _logBody && request.Body != null && request.Body.Length > 0)
            {
                var bodyPreview = System.Text.Encoding.UTF8.GetString(request.Body);
                if (bodyPreview.Length > 500)
                    bodyPreview = bodyPreview.Substring(0, 500) + "...";
                message += $"\n  Body: {bodyPreview}";
            }

            _log($"[TurboHTTP] {message}");
        }

        private void LogResponse(UHttpRequest request, UHttpResponse response)
        {
            var message = $"← {request.Method} {request.Uri} → {(int)response.StatusCode} {response.StatusCode} ({response.ElapsedTime.TotalMilliseconds:F0}ms)";

            if (_logLevel >= LogLevel.Detailed && _logHeaders && response.Headers.Count > 0)
            {
                message += "\n  Headers:";
                foreach (var header in response.Headers)
                {
                    message += $"\n    {header.Key}: {header.Value}";
                }
            }

            if (_logLevel >= LogLevel.Detailed && _logBody && response.Body != null && response.Body.Length > 0)
            {
                var bodyPreview = System.Text.Encoding.UTF8.GetString(response.Body);
                if (bodyPreview.Length > 500)
                    bodyPreview = bodyPreview.Substring(0, 500) + "...";
                message += $"\n  Body: {bodyPreview}";
            }

            if (response.IsSuccessStatusCode)
            {
                _log($"[TurboHTTP] {message}");
            }
            else
            {
                _log($"[TurboHTTP][WARN] {message}");
            }
        }

        private void LogError(UHttpRequest request, Exception exception, TimeSpan elapsed)
        {
            var message = $"✗ {request.Method} {request.Uri} → ERROR ({elapsed.TotalMilliseconds:F0}ms)\n  {exception.Message}";
            _log($"[TurboHTTP][ERROR] {message}");
        }
    }
}
```

### Task 4.5: Default Headers Middleware

**File:** `Runtime/Core/Pipeline/Middlewares/DefaultHeadersMiddleware.cs`

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Middleware that adds default headers to all requests.
    /// Useful for User-Agent, Accept-Encoding, etc.
    /// </summary>
    public class DefaultHeadersMiddleware : IHttpMiddleware
    {
        private readonly HttpHeaders _defaultHeaders;
        private readonly bool _overrideExisting;

        public DefaultHeadersMiddleware(HttpHeaders defaultHeaders, bool overrideExisting = false)
        {
            _defaultHeaders = defaultHeaders ?? new HttpHeaders();
            _overrideExisting = overrideExisting;
        }

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            // Clone headers to avoid modifying original request
            var headers = request.Headers.Clone();

            // Add default headers
            foreach (var header in _defaultHeaders)
            {
                if (_overrideExisting || !headers.Contains(header.Key))
                {
                    headers.Set(header.Key, header.Value);
                }
            }

            // Create new request with updated headers
            var modifiedRequest = request.WithHeaders(headers);
            context.UpdateRequest(modifiedRequest);

            // Continue pipeline
            return await next(modifiedRequest, context, cancellationToken);
        }
    }
}
```

### Task 4.6: Timeout Middleware

**File:** `Runtime/Core/Pipeline/Middlewares/TimeoutMiddleware.cs`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Middleware that enforces request timeouts.
    /// </summary>
    public class TimeoutMiddleware : IHttpMiddleware
    {
        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            using (var timeoutCts = new CancellationTokenSource(request.Timeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
            {
                try
                {
                    return await next(request, context, linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    // Timeout occurred
                    var error = new UHttpError(
                        UHttpErrorType.Timeout,
                        $"Request timeout after {request.Timeout.TotalSeconds}s"
                    );

                    return new UHttpResponse(
                        System.Net.HttpStatusCode.RequestTimeout,
                        new HttpHeaders(),
                        null,
                        context.Elapsed,
                        request,
                        error
                    );
                }
            }
        }
    }
}
```

### Task 4.7: Retry Middleware (Basic)

**File:** `Runtime/Retry/RetryMiddleware.cs`

```csharp
	using System;
	using System.Threading;
	using System.Threading.Tasks;
	using TurboHTTP.Core;

	namespace TurboHTTP.Retry
	{
    /// <summary>
    /// Configuration for retry behavior.
    /// </summary>
    public class RetryPolicy
    {
        public int MaxRetries { get; set; } = 3;
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);
        public double BackoffMultiplier { get; set; } = 2.0;
        public bool OnlyRetryIdempotent { get; set; } = true;

        public static RetryPolicy Default => new RetryPolicy();
        public static RetryPolicy NoRetry => new RetryPolicy { MaxRetries = 0 };
    }

    /// <summary>
    /// Middleware that automatically retries failed requests.
    /// </summary>
	public class RetryMiddleware : IHttpMiddleware
	{
	    private readonly Action<string> _log;
	    private readonly RetryPolicy _policy;

	    public RetryMiddleware(RetryPolicy policy = null, Action<string> log = null)
	    {
	        _policy = policy ?? RetryPolicy.Default;
	        _log = log ?? (_ => { });
	    }

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            // Check if this request should be retried
            if (!ShouldRetry(request))
            {
                return await next(request, context, cancellationToken);
            }

            int attempt = 0;
            TimeSpan delay = _policy.InitialDelay;

            while (true)
            {
                attempt++;
                context.SetState("RetryAttempt", attempt);
                context.RecordEvent($"RetryAttempt{attempt}");

                try
                {
                    var response = await next(request, context, cancellationToken);

                    // Success or non-retryable error
                    if (response.IsSuccessStatusCode || !IsRetryableResponse(response))
                    {
                        if (attempt > 1)
                        {
                            context.RecordEvent("RetrySucceeded", new System.Collections.Generic.Dictionary<string, object>
                            {
                                { "attempts", attempt }
                            });
                        }
                        return response;
                    }

                    // Retryable error
                    if (attempt > _policy.MaxRetries)
                    {
                        context.RecordEvent("RetryExhausted");
                        return response; // Return last response
                    }

	                    // Wait before retry
	                    _log($"[RetryMiddleware] Attempt {attempt} failed, retrying in {delay.TotalSeconds:F1}s...");
	                    await Task.Delay(delay, cancellationToken);
	                    delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * _policy.BackoffMultiplier);
	                }
	                catch (UHttpException ex) when (ex.HttpError.IsRetryable())
                {
                    if (attempt > _policy.MaxRetries)
                    {
                        context.RecordEvent("RetryExhausted");
                        throw;
                    }

	                    _log($"[RetryMiddleware] Attempt {attempt} failed with {ex.HttpError.Type}, retrying in {delay.TotalSeconds:F1}s...");
	                    await Task.Delay(delay, cancellationToken);
	                    delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * _policy.BackoffMultiplier);
	                }
            }
        }

        private bool ShouldRetry(UHttpRequest request)
        {
            if (_policy.MaxRetries == 0)
                return false;

            if (_policy.OnlyRetryIdempotent && !request.Method.IsIdempotent())
                return false;

            return true;
        }

        private bool IsRetryableResponse(UHttpResponse response)
        {
            if (response.Error != null)
                return response.Error.IsRetryable();

            // Retry on 5xx server errors
            int statusCode = (int)response.StatusCode;
            return statusCode >= 500 && statusCode < 600;
        }
    }
}
```

**Notes:**

- Exponential backoff with configurable multiplier
- Only retries idempotent methods by default (GET, PUT, DELETE, HEAD, OPTIONS)
- Respects `UHttpError.IsRetryable()` logic
- Records retry attempts in timeline

### Task 4.8: Auth Middleware (Basic)

**File:** `Runtime/Auth/AuthMiddleware.cs`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Token provider interface for authentication.
    /// </summary>
    public interface IAuthTokenProvider
    {
        Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Simple static token provider.
    /// </summary>
    public class StaticTokenProvider : IAuthTokenProvider
    {
        private readonly string _token;

        public StaticTokenProvider(string token)
        {
            _token = token;
        }

        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_token);
        }
    }

    /// <summary>
    /// Middleware that adds authentication headers to requests.
    /// </summary>
    public class AuthMiddleware : IHttpMiddleware
    {
        private readonly IAuthTokenProvider _tokenProvider;
        private readonly string _scheme;

        public AuthMiddleware(IAuthTokenProvider tokenProvider, string scheme = "Bearer")
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _scheme = scheme;
        }

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            // Get token
            var token = await _tokenProvider.GetTokenAsync(cancellationToken);

            if (!string.IsNullOrEmpty(token))
            {
                // Add Authorization header
                var headers = request.Headers.Clone();
                headers.Set("Authorization", $"{_scheme} {token}");

                var modifiedRequest = request.WithHeaders(headers);
                context.UpdateRequest(modifiedRequest);

                return await next(modifiedRequest, context, cancellationToken);
            }

            return await next(request, context, cancellationToken);
        }
    }
}
```

### Task 4.9: Metrics Middleware (Basic)

**File:** `Runtime/Observability/MetricsMiddleware.cs`

```csharp
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// HTTP metrics collected by MetricsMiddleware.
    /// </summary>
    public class HttpMetrics
    {
        public long TotalRequests { get; set; }
        public long SuccessfulRequests { get; set; }
        public long FailedRequests { get; set; }
        public double AverageResponseTimeMs { get; set; }
        public long TotalBytesReceived { get; set; }
        public long TotalBytesSent { get; set; }

        public ConcurrentDictionary<string, long> RequestsByHost { get; } = new ConcurrentDictionary<string, long>();
        public ConcurrentDictionary<int, long> RequestsByStatusCode { get; } = new ConcurrentDictionary<int, long>();
    }

    /// <summary>
    /// Middleware that collects HTTP metrics.
    /// </summary>
    public class MetricsMiddleware : IHttpMiddleware
    {
        private readonly HttpMetrics _metrics = new HttpMetrics();
        private long _totalResponseTimeMs;

        public HttpMetrics Metrics => _metrics;

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _metrics.TotalRequests);

            var host = request.Uri.Host;
            _metrics.RequestsByHost.AddOrUpdate(host, 1, (_, count) => count + 1);

            if (request.Body != null)
            {
                Interlocked.Add(ref _metrics.TotalBytesSent, request.Body.Length);
            }

            UHttpResponse response = null;
            try
            {
                response = await next(request, context, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref _metrics.SuccessfulRequests);
                }
                else
                {
                    Interlocked.Increment(ref _metrics.FailedRequests);
                }

                var statusCode = (int)response.StatusCode;
                _metrics.RequestsByStatusCode.AddOrUpdate(statusCode, 1, (_, count) => count + 1);

                if (response.Body != null)
                {
                    Interlocked.Add(ref _metrics.TotalBytesReceived, response.Body.Length);
                }

                return response;
            }
            catch
            {
                Interlocked.Increment(ref _metrics.FailedRequests);
                throw;
            }
            finally
            {
                var elapsedMs = (long)context.Elapsed.TotalMilliseconds;
                Interlocked.Add(ref _totalResponseTimeMs, elapsedMs);
                _metrics.AverageResponseTimeMs = (double)_totalResponseTimeMs / _metrics.TotalRequests;
            }
        }

        public void Reset()
        {
            _metrics.TotalRequests = 0;
            _metrics.SuccessfulRequests = 0;
            _metrics.FailedRequests = 0;
            _totalResponseTimeMs = 0;
            _metrics.AverageResponseTimeMs = 0;
            _metrics.TotalBytesReceived = 0;
            _metrics.TotalBytesSent = 0;
            _metrics.RequestsByHost.Clear();
            _metrics.RequestsByStatusCode.Clear();
        }
    }
}
```

## Validation Criteria

### Success Criteria

- [ ] `IHttpMiddleware` interface defined
- [ ] `HttpPipeline` executes middleware in correct order
- [ ] `LoggingMiddleware` logs requests and responses
- [ ] `DefaultHeadersMiddleware` adds headers without overwriting
- [ ] `TimeoutMiddleware` cancels requests after timeout
- [ ] `RetryMiddleware` retries failed requests with backoff
- [ ] `AuthMiddleware` adds Authorization header
- [ ] `MetricsMiddleware` collects accurate metrics
- [ ] Pipeline integrated into `UHttpClient`
- [ ] Multiple middlewares can be chained

### Unit Tests

Create test file: `Tests/Runtime/Pipeline/HttpPipelineTests.cs`

```csharp
using NUnit.Framework;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Pipeline
{
    public class HttpPipelineTests
    {
        [Test]
        public async Task Pipeline_ExecutesMiddlewareInOrder()
        {
            var executionOrder = new System.Collections.Generic.List<string>();

            var middleware1 = new TestMiddleware("M1", executionOrder);
            var middleware2 = new TestMiddleware("M2", executionOrder);
            var middleware3 = new TestMiddleware("M3", executionOrder);

            var transport = new MockTransport();
            var pipeline = new HttpPipeline(
                new[] { middleware1, middleware2, middleware3 },
                transport
            );

            var request = new UHttpRequest(HttpMethod.GET, new System.Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.AreEqual(new[] { "M1-Before", "M2-Before", "M3-Before", "M3-After", "M2-After", "M1-After" },
                executionOrder.ToArray());
        }

        private class TestMiddleware : IHttpMiddleware
        {
            private readonly string _name;
            private readonly System.Collections.Generic.List<string> _executionOrder;

            public TestMiddleware(string name, System.Collections.Generic.List<string> executionOrder)
            {
                _name = name;
                _executionOrder = executionOrder;
            }

            public async Task<UHttpResponse> InvokeAsync(
                UHttpRequest request,
                RequestContext context,
                HttpPipelineDelegate next,
                System.Threading.CancellationToken cancellationToken)
            {
                _executionOrder.Add($"{_name}-Before");
                var response = await next(request, context, cancellationToken);
                _executionOrder.Add($"{_name}-After");
                return response;
            }
        }
    }
}
```

## Next Steps

Once Phase 4 is complete and validated:

1. Move to [Phase 5: Content Handlers](phase-05-content-handlers.md)
2. Implement JSON serialization helpers
3. Create file download handler with resume support

## Notes

- Middleware pattern is foundation for all advanced features
- Order matters: Retry should be early, Logging should be first/last
- Each middleware is self-contained and testable
- Phase 6 will add more advanced middleware (Cache, RateLimit)
- M1 milestone is reached after this phase

## Review Notes

> **TODO: No Retry Budget / Circuit Breaker** - The current `RetryMiddleware` implements exponential backoff and idempotency awareness, but lacks:
>
> - **Retry budget**: A global or per-host limit on total retries across all requests to prevent retry storms under widespread failures
> - **Circuit breaker pattern**: Automatically "open" the circuit after N consecutive failures to a host, failing fast for a cooldown period before attempting again
>
> Without these, a degraded backend could cause all clients to simultaneously retry, amplifying load and delaying recovery. Consider adding:
>
> - `CircuitBreakerMiddleware` with configurable failure thresholds and half-open states
> - A `retryBudget` option in `RetryPolicy` (e.g., "max 20% of requests can be retries")
