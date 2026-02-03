# Phase 4.3: Module Middlewares

**Depends on:** Phase 4.1 (Pipeline Executor)
**Assemblies:** `TurboHTTP.Retry`, `TurboHTTP.Auth`, `TurboHTTP.Observability`
**Files:** 7 new

Each middleware lives in its own optional assembly, referencing only `TurboHTTP.Core`. Users include only the modules they need.

---

## Step 1: RetryPolicy

**File:** `Runtime/Retry/RetryPolicy.cs`
**Namespace:** `TurboHTTP.Retry`

```csharp
using System;

namespace TurboHTTP.Retry
{
    /// <summary>
    /// Configuration for retry behavior.
    /// </summary>
    public class RetryPolicy
    {
        /// <summary>
        /// Maximum number of retry attempts after the initial request.
        /// Default: 3 (total of 4 attempts including the original).
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Initial delay before the first retry. Subsequent delays are
        /// multiplied by BackoffMultiplier.
        /// </summary>
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Multiplier applied to delay after each retry attempt.
        /// Default: 2.0 (exponential backoff: 1s, 2s, 4s, ...).
        /// </summary>
        public double BackoffMultiplier { get; set; } = 2.0;

        /// <summary>
        /// When true, only retry idempotent methods (GET, HEAD, PUT, DELETE, OPTIONS).
        /// POST and PATCH are not retried to prevent duplicate side effects.
        /// </summary>
        public bool OnlyRetryIdempotent { get; set; } = true;

        /// <summary>
        /// Default retry policy: 3 retries, 1s initial delay, 2x backoff, idempotent only.
        /// </summary>
        public static RetryPolicy Default => new RetryPolicy();

        /// <summary>
        /// No retry policy: disables all retries.
        /// </summary>
        public static RetryPolicy NoRetry => new RetryPolicy { MaxRetries = 0 };
    }
}
```

---

## Step 2: RetryMiddleware

**File:** `Runtime/Retry/RetryMiddleware.cs`
**Namespace:** `TurboHTTP.Retry`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Retry
{
    /// <summary>
    /// Middleware that automatically retries failed requests with exponential backoff.
    /// Retries on 5xx server errors and retryable transport errors.
    /// Only retries idempotent methods by default.
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

                    // Success or non-retryable status
                    if (response.IsSuccessStatusCode || !IsRetryableResponse(response))
                    {
                        if (attempt > 1)
                        {
                            context.RecordEvent("RetrySucceeded",
                                new System.Collections.Generic.Dictionary<string, object>
                                {
                                    { "attempts", attempt }
                                });
                        }
                        return response;
                    }

                    // Retryable error — check if retries exhausted
                    if (attempt > _policy.MaxRetries)
                    {
                        context.RecordEvent("RetryExhausted");
                        return response; // Return last failed response
                    }

                    // Wait before retry
                    _log($"[RetryMiddleware] Attempt {attempt} failed with {(int)response.StatusCode}, retrying in {delay.TotalSeconds:F1}s...");
                    await Task.Delay(delay, cancellationToken);
                    delay = TimeSpan.FromMilliseconds(
                        delay.TotalMilliseconds * _policy.BackoffMultiplier);
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
                    delay = TimeSpan.FromMilliseconds(
                        delay.TotalMilliseconds * _policy.BackoffMultiplier);
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

### Implementation Notes

1. **Exponential backoff:** Delays are `1s → 2s → 4s → ...` with default settings. No jitter (could be added in a future enhancement to prevent thundering herd).

2. **Attempt counting:** `attempt` starts at 1 (first attempt). `MaxRetries = 3` means up to 4 total attempts (1 original + 3 retries). The check `attempt > _policy.MaxRetries` fires after the 4th attempt.

3. **Two retry paths:**
   - **Response path:** 5xx responses or responses with retryable `UHttpError` (e.g., timeout response from `TimeoutMiddleware`). Returns last failed response when exhausted.
   - **Exception path:** `UHttpException` with `IsRetryable() == true` (network errors, timeouts thrown by transport). Re-throws last exception when exhausted.

4. **`Task.Delay` cancellation:** If the caller cancels during a backoff delay, `Task.Delay` throws `OperationCanceledException`. This propagates up through `UHttpClient.SendAsync`'s `catch (OperationCanceledException)` handler.

5. **Context tracking:** Each attempt sets `RetryAttempt` state and records a timeline event. On success after retry, records `RetrySucceeded` with attempt count. On exhaustion, records `RetryExhausted`. This enables metrics and debugging.

6. **Idempotency guard:** `HttpMethod.IsIdempotent()` returns true for GET, HEAD, PUT, DELETE, OPTIONS. POST and PATCH are NOT retried by default — prevents duplicate side effects (e.g., double-charging a payment). Users can set `OnlyRetryIdempotent = false` to override.

7. **No retry budget / circuit breaker:** As noted in the original spec's review section, this implementation lacks global retry budgets and circuit breaker patterns. These prevent retry storms under widespread failures. Deferred to Phase 6 (Advanced Middleware).

---

## Step 3: IAuthTokenProvider

**File:** `Runtime/Auth/IAuthTokenProvider.cs`
**Namespace:** `TurboHTTP.Auth`

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Provides authentication tokens for HTTP requests.
    /// Implementations may return static tokens, refresh OAuth tokens, etc.
    /// </summary>
    public interface IAuthTokenProvider
    {
        /// <summary>
        /// Get the current authentication token.
        /// Returns null or empty string to skip authentication for this request.
        /// </summary>
        Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
    }
}
```

---

## Step 4: StaticTokenProvider

**File:** `Runtime/Auth/StaticTokenProvider.cs`
**Namespace:** `TurboHTTP.Auth`

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Token provider that returns a fixed token.
    /// Suitable for API keys and tokens that don't expire during the client's lifetime.
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
}
```

---

## Step 5: AuthMiddleware

**File:** `Runtime/Auth/AuthMiddleware.cs`
**Namespace:** `TurboHTTP.Auth`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Middleware that adds authentication headers to requests.
    /// Supports Bearer tokens (default), Basic auth, and custom schemes.
    /// </summary>
    public class AuthMiddleware : IHttpMiddleware
    {
        private readonly IAuthTokenProvider _tokenProvider;
        private readonly string _scheme;

        /// <param name="tokenProvider">Provider that supplies auth tokens</param>
        /// <param name="scheme">Auth scheme (e.g., "Bearer", "Basic"). Default: "Bearer"</param>
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
            var token = await _tokenProvider.GetTokenAsync(cancellationToken);

            if (!string.IsNullOrEmpty(token))
            {
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

### Implementation Notes

1. **Async token retrieval:** `IAuthTokenProvider.GetTokenAsync` is async to support future OAuth token refresh workflows (network call to refresh endpoint).

2. **Skip on empty token:** When `GetTokenAsync` returns null or empty, the request is sent without an Authorization header. This allows conditional auth (e.g., public endpoints mixed with authenticated endpoints).

3. **Header override:** Uses `headers.Set()` which replaces any existing Authorization header. If the request already has an Authorization header (e.g., set via builder's `WithBearerToken()`), the middleware will override it. Users who want request-level auth to take precedence should not use `AuthMiddleware` — use the builder instead.

4. **Request immutability:** `request.Headers.Clone()` + `request.WithHeaders()` creates a new request. Original is preserved.

---

## Step 6: HttpMetrics

**File:** `Runtime/Observability/HttpMetrics.cs`
**Namespace:** `TurboHTTP.Observability`

```csharp
using System.Collections.Concurrent;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// HTTP metrics collected by MetricsMiddleware.
    /// All fields are updated atomically via Interlocked operations.
    /// Read access is eventually consistent during concurrent writes.
    /// </summary>
    public class HttpMetrics
    {
        // Use public fields (not properties) for Interlocked compatibility.
        // Interlocked requires ref to field, which doesn't work with property backing fields.
        public long TotalRequests;
        public long SuccessfulRequests;
        public long FailedRequests;
        public double AverageResponseTimeMs;
        public long TotalBytesReceived;
        public long TotalBytesSent;

        /// <summary>
        /// Request count per host (e.g., "api.example.com" -> 42).
        /// </summary>
        public ConcurrentDictionary<string, long> RequestsByHost { get; } =
            new ConcurrentDictionary<string, long>();

        /// <summary>
        /// Request count per HTTP status code (e.g., 200 -> 100, 404 -> 5).
        /// </summary>
        public ConcurrentDictionary<int, long> RequestsByStatusCode { get; } =
            new ConcurrentDictionary<int, long>();
    }
}
```

### Implementation Notes

1. **Public fields vs properties:** `Interlocked.Increment(ref _metrics.TotalRequests)` requires a `ref` to a field. Auto-property backing fields cannot be passed by `ref`. Using public fields is intentional.

2. **`AverageResponseTimeMs` consistency:** Calculated as running average. During concurrent writes, reads may see an intermediate value. This is acceptable for metrics — exact point-in-time accuracy is not required.

---

## Step 7: MetricsMiddleware

**File:** `Runtime/Observability/MetricsMiddleware.cs`
**Namespace:** `TurboHTTP.Observability`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Middleware that collects HTTP request/response metrics.
    /// Thread-safe for concurrent use. Metrics are exposed via the Metrics property.
    /// </summary>
    public class MetricsMiddleware : IHttpMiddleware
    {
        private readonly HttpMetrics _metrics = new HttpMetrics();
        private long _totalResponseTimeMs;

        /// <summary>
        /// Access collected metrics. Read access is eventually consistent.
        /// </summary>
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
                _metrics.RequestsByStatusCode.AddOrUpdate(
                    statusCode, 1, (_, count) => count + 1);

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
                // Eventually consistent average — acceptable for metrics
                _metrics.AverageResponseTimeMs =
                    (double)Interlocked.Read(ref _totalResponseTimeMs) /
                    Interlocked.Read(ref _metrics.TotalRequests);
            }
        }

        /// <summary>
        /// Reset all metrics to zero. NOT thread-safe — call only when no requests are in flight.
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _metrics.TotalRequests, 0);
            Interlocked.Exchange(ref _metrics.SuccessfulRequests, 0);
            Interlocked.Exchange(ref _metrics.FailedRequests, 0);
            Interlocked.Exchange(ref _totalResponseTimeMs, 0);
            _metrics.AverageResponseTimeMs = 0;
            Interlocked.Exchange(ref _metrics.TotalBytesReceived, 0);
            Interlocked.Exchange(ref _metrics.TotalBytesSent, 0);
            _metrics.RequestsByHost.Clear();
            _metrics.RequestsByStatusCode.Clear();
        }
    }
}
```

### Implementation Notes

1. **Thread safety:** All counters use `Interlocked` operations. Dictionaries use `ConcurrentDictionary.AddOrUpdate`. Safe for concurrent middleware invocations from multiple `UHttpClient` instances sharing the same `MetricsMiddleware`.

2. **Average response time:** Uses a running total and divides by request count. The `Interlocked.Read` calls in the `finally` block ensure atomic reads of both values, but the division itself is not atomic — a concurrent read during a write may see a slightly stale average. This is acceptable for metrics.

3. **`Reset()` is NOT thread-safe:** Documented — should only be called when no requests are in flight. Calling during active requests may produce inconsistent metrics. A fully thread-safe reset would require a lock around the entire `InvokeAsync` method, which would serialize all requests.

4. **`_totalResponseTimeMs` is internal:** Not exposed on `HttpMetrics` — only the computed `AverageResponseTimeMs` is visible. This prevents external code from corrupting the running total.

5. **Body size tracking:** Counts raw `byte[]` lengths, not compressed or transfer-encoded sizes. This reflects the application-level data volume, not wire-level.

---

## Verification Criteria

- [ ] `RetryMiddleware` retries on 5xx responses
- [ ] `RetryMiddleware` retries on `UHttpException` with `IsRetryable() == true`
- [ ] `RetryMiddleware` does NOT retry 4xx responses
- [ ] `RetryMiddleware` does NOT retry POST by default
- [ ] `RetryMiddleware` implements exponential backoff (1s, 2s, 4s)
- [ ] `RetryMiddleware` returns last failed response when retries exhausted
- [ ] `RetryMiddleware` records attempt events in `RequestContext`
- [ ] `AuthMiddleware` adds `Authorization: Bearer <token>` header
- [ ] `AuthMiddleware` skips header when token is null/empty
- [ ] `MetricsMiddleware` increments all counters correctly
- [ ] `MetricsMiddleware` tracks per-host and per-status-code counts
- [ ] All module middlewares compile in their respective assemblies
- [ ] No circular dependencies (each module references only Core)
