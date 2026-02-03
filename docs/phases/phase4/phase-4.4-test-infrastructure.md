# Phase 4.4: Test Infrastructure (MockTransport)

**Depends on:** Phase 4.1 (Pipeline Executor)
**Assembly:** `TurboHTTP.Testing`
**Files:** 1 new

---

## Step 1: MockTransport

**File:** `Runtime/Testing/MockTransport.cs`
**Namespace:** `TurboHTTP.Testing`

```csharp
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Testing
{
    /// <summary>
    /// Mock transport for unit testing middleware and client behavior.
    /// Returns configurable responses without making real network calls.
    /// Thread-safe for concurrent use.
    /// </summary>
    public class MockTransport : IHttpTransport
    {
        private readonly Func<UHttpRequest, RequestContext, CancellationToken, Task<UHttpResponse>> _handler;
        private int _requestCount;
        private volatile UHttpRequest _lastRequest;

        /// <summary>
        /// Number of times SendAsync has been called.
        /// </summary>
        public int RequestCount => Interlocked.CompareExchange(ref _requestCount, 0, 0);

        /// <summary>
        /// The most recent request received by this transport.
        /// </summary>
        public UHttpRequest LastRequest => _lastRequest;

        /// <summary>
        /// Create a MockTransport that returns the specified status code with
        /// optional headers, body, and error.
        /// </summary>
        public MockTransport(
            HttpStatusCode statusCode = HttpStatusCode.OK,
            HttpHeaders headers = null,
            byte[] body = null,
            UHttpError error = null)
        {
            var responseHeaders = headers ?? new HttpHeaders();
            _handler = (req, ctx, ct) =>
            {
                var response = new UHttpResponse(
                    statusCode, responseHeaders, body, ctx.Elapsed, req, error);
                return Task.FromResult(response);
            };
        }

        /// <summary>
        /// Create a MockTransport with a custom handler for advanced scenarios
        /// (e.g., fail first N requests, return different responses per request).
        /// </summary>
        public MockTransport(
            Func<UHttpRequest, RequestContext, CancellationToken, Task<UHttpResponse>> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// Create a MockTransport with a simplified custom handler (sync, no context).
        /// </summary>
        public MockTransport(Func<UHttpRequest, UHttpResponse> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handler = (req, ctx, ct) => Task.FromResult(handler(req));
        }

        public Task<UHttpResponse> SendAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            _lastRequest = request;

            cancellationToken.ThrowIfCancellationRequested();

            return _handler(request, context, cancellationToken);
        }

        public void Dispose()
        {
            // No resources to dispose
        }
    }
}
```

### Implementation Notes

1. **Three constructor overloads:**
   - **Simple (default):** Fixed response with configurable status, headers, body, error. Covers 90% of test cases.
   - **Full handler:** `Func<UHttpRequest, RequestContext, CancellationToken, Task<UHttpResponse>>` for complex scenarios like retry testing (fail first N, then succeed).
   - **Simplified handler:** `Func<UHttpRequest, UHttpResponse>` for quick one-liners without context/token boilerplate.

2. **Thread safety:** `_requestCount` uses `Interlocked`. `_lastRequest` is `volatile` — eventual consistency is acceptable (tests typically await completion before asserting).

3. **Cancellation support:** `cancellationToken.ThrowIfCancellationRequested()` is checked before invoking the handler. This allows timeout tests to work correctly — if the token is cancelled before the transport is invoked, it throws `OperationCanceledException`.

4. **`ctx.Elapsed` for response timing:** Uses the context's elapsed time rather than a fixed value. This means response `ElapsedTime` reflects actual test execution time (typically <1ms), which is realistic for assertions.

5. **No request history list:** Only stores `LastRequest` to minimize memory. For tests needing full request history, use the handler overload with a captured list:
   ```csharp
   var requests = new List<UHttpRequest>();
   var transport = new MockTransport((req, ctx, ct) => {
       requests.Add(req);
       return Task.FromResult(new UHttpResponse(...));
   });
   ```

### Usage Examples

**Simple 200 OK:**
```csharp
var transport = new MockTransport();
```

**Custom status with body:**
```csharp
var transport = new MockTransport(
    HttpStatusCode.NotFound,
    body: Encoding.UTF8.GetBytes("{\"error\": \"not found\"}")
);
```

**Fail first 2 attempts, then succeed (for retry tests):**
```csharp
int callCount = 0;
var transport = new MockTransport((req, ctx, ct) =>
{
    callCount++;
    if (callCount <= 2)
    {
        return Task.FromResult(new UHttpResponse(
            HttpStatusCode.InternalServerError,
            new HttpHeaders(), null, ctx.Elapsed, req));
    }
    return Task.FromResult(new UHttpResponse(
        HttpStatusCode.OK,
        new HttpHeaders(), null, ctx.Elapsed, req));
});
```

**Throw exception (for error handling tests):**
```csharp
var transport = new MockTransport((req, ctx, ct) =>
{
    throw new UHttpException(
        new UHttpError(UHttpErrorType.NetworkError, "Connection refused"));
});
```

---

## Verification Criteria

- [ ] `MockTransport` implements `IHttpTransport` interface
- [ ] Default constructor returns 200 OK with empty body
- [ ] Custom status/headers/body constructor works correctly
- [ ] Handler overload supports dynamic response logic
- [ ] `RequestCount` increments on each `SendAsync` call
- [ ] `LastRequest` stores the most recent request
- [ ] `ThrowIfCancellationRequested` is called before handler
- [ ] `MockTransport` is in `TurboHTTP.Testing` namespace and assembly
