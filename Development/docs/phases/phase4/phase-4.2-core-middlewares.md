# Phase 4.2: Core Middlewares

**Depends on:** Phase 4.1 (Pipeline Executor)
**Assembly:** `TurboHTTP.Core`
**Files:** 3 new

All three middlewares live in `Runtime/Core/Pipeline/Middlewares/` and use namespace `TurboHTTP.Core`.

---

## Step 1: LoggingMiddleware

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

            LogRequest(request);

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
            var message = $"-> {request.Method} {request.Uri}";

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
                // Decode only first N bytes to avoid allocating a full string for large bodies
                int previewBytes = Math.Min(request.Body.Length, 500);
                var bodyPreview = System.Text.Encoding.UTF8.GetString(request.Body, 0, previewBytes);
                if (request.Body.Length > 500)
                    bodyPreview += "...";
                message += $"\n  Body: {bodyPreview}";
            }

            _log($"[TurboHTTP] {message}");
        }

        private void LogResponse(UHttpRequest request, UHttpResponse response)
        {
            var message = $"<- {request.Method} {request.Uri} -> {(int)response.StatusCode} {response.StatusCode} ({response.ElapsedTime.TotalMilliseconds:F0}ms)";

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
                int previewBytes = Math.Min(response.Body.Length, 500);
                var bodyPreview = System.Text.Encoding.UTF8.GetString(response.Body, 0, previewBytes);
                if (response.Body.Length > 500)
                    bodyPreview += "...";
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
            var message = $"X {request.Method} {request.Uri} -> ERROR ({elapsed.TotalMilliseconds:F0}ms)\n  {exception.Message}";
            _log($"[TurboHTTP][ERROR] {message}");
        }
    }
}
```

### Implementation Notes

1. **`Action<string>` callback:** Unity-agnostic — users can wire to `Debug.Log`, custom logger, or no-op. Default is no-op to prevent NRE.

2. **`DateTime.UtcNow` for timing:** Only used for log output display, not for performance measurement. `RequestContext.Elapsed` (Stopwatch-based) is used for response `ElapsedTime`.

3. **`HttpHeaders` enumeration:** Iterates as `IEnumerable<KeyValuePair<string, string>>` — returns first value per header name. This is sufficient for logging. Multi-value headers (e.g., `Set-Cookie`) will show only the first value. For detailed multi-value logging in future, iterate `Names` + `GetValues()`.

4. **Body preview truncation:** Only the first 500 bytes are decoded to prevent GC pressure from large payloads (e.g., a 10MB response body would otherwise allocate a 10MB string just to truncate it). UTF-8 multi-byte sequences may be split at the boundary — the decoder replaces incomplete sequences with U+FFFD, which is acceptable for logging.

5. **Arrow characters:** Spec uses Unicode arrows (`→`, `←`, `✗`). Changed to ASCII (`->`, `<-`, `X`) for broader terminal/log compatibility.

6. **`finally` block pattern:** Ensures response/error is always logged even if downstream middleware throws. The `response` variable is captured in `try` scope.

---

## Step 2: DefaultHeadersMiddleware

**File:** `Runtime/Core/Pipeline/Middlewares/DefaultHeadersMiddleware.cs`

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Middleware that adds default headers to all requests.
    /// Headers are only added if they don't already exist on the request
    /// (unless overrideExisting is true).
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

### Implementation Notes

1. **Defensive cloning:** `request.Headers.Clone()` + `request.WithHeaders(headers)` creates a new immutable request. Original request is never modified.

2. **`context.UpdateRequest()`:** Tracks the transformation in the request context timeline. Downstream middleware and transport see the modified request.

3. **Default vs override mode:**
   - `overrideExisting = false` (default): Only adds headers that don't exist on the request. Request-level headers always win.
   - `overrideExisting = true`: Overwrites existing headers. Useful for force-setting headers like `User-Agent`.

4. **Relationship to `UHttpRequestBuilder`:** The builder already merges `UHttpClientOptions.DefaultHeaders` in `Build()`. This middleware provides an alternative mechanism for users who want header injection at the middleware level (e.g., different default headers per middleware chain, or headers that depend on runtime state). The two mechanisms are complementary — builder headers are applied first, then middleware headers.

5. **Single-value only:** Uses `Set()` which replaces all values for a header name. Multi-value default headers (e.g., multiple `Accept` values) should use the builder's `WithHeaders()` instead.

---

## Step 3: TimeoutMiddleware

**File:** `Runtime/Core/Pipeline/Middlewares/TimeoutMiddleware.cs`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Middleware that enforces request timeouts.
    /// Uses request.Timeout to determine the timeout duration.
    /// Returns a 408 response on timeout (does not throw).
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
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token))
            {
                try
                {
                    return await next(request, context, linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    // Timeout occurred (not user cancellation)
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

### Implementation Notes

1. **Returns response, does not throw:** Unlike transport-level timeout handling which throws `UHttpException`, this middleware returns a `UHttpResponse` with `408 RequestTimeout` and a `UHttpError`. This design allows `RetryMiddleware` (positioned earlier in the pipeline) to detect timeout responses and retry them.

2. **User cancellation vs timeout:** The `when (timeoutCts.IsCancellationRequested)` filter distinguishes timeout from user cancellation:
   - **Timeout:** `timeoutCts` fires → caught by filter → returns 408 response
   - **User cancellation:** `cancellationToken` fires → `OperationCanceledException` does NOT match filter → propagates up to `UHttpClient.SendAsync` where it's caught by the `catch (OperationCanceledException)` handler

3. **`request.Timeout` source:** Comes from `UHttpRequest.Timeout` property (default 30s). Set via `UHttpRequestBuilder.WithTimeout()` or `UHttpClientOptions.DefaultTimeout`.

4. **`CancellationTokenSource` disposal:** Both `timeoutCts` and `linkedCts` are disposed via `using` statements after the pipeline completes. This is important — `CancellationTokenSource` holds a `Timer` internally and must be disposed to prevent leaks.

5. **Interaction with transport-level timeout:** The transport (e.g., `RawSocketTransport`) receives the linked token. If the transport has its own timeout mechanism, the stricter timeout wins. In practice, the middleware timeout is the primary timeout enforcement point.

6. **Pipeline position:** Two valid configurations (see `overview.md` Decision #2):
   - **Per-attempt timeout (recommended):** Place `RetryMiddleware` BEFORE `TimeoutMiddleware` in the list. Retry wraps Timeout, sees 408 responses, and can retry timed-out attempts.
   - **Overall timeout:** Place `TimeoutMiddleware` BEFORE `RetryMiddleware`. Timeout wraps the entire retry sequence. When it fires, 408 is returned directly — Retry never sees it.

---

## Verification Criteria

- [ ] `LoggingMiddleware` logs request before `next()` and response/error after
- [ ] `LoggingMiddleware` respects `LogLevel.None` (passes through without logging)
- [ ] `DefaultHeadersMiddleware` adds headers that don't exist on request
- [ ] `DefaultHeadersMiddleware` does NOT override existing headers by default
- [ ] `DefaultHeadersMiddleware` overrides when `overrideExisting = true`
- [ ] `TimeoutMiddleware` returns 408 response on timeout (not exception)
- [ ] `TimeoutMiddleware` propagates user cancellation as `OperationCanceledException`
- [ ] All three middlewares call `next()` to continue the pipeline
- [ ] All three middlewares are in `TurboHTTP.Core` namespace
