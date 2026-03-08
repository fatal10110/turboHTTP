## 22.4: Testing Adaptation

### `Runtime/Testing/MockTransport.cs`

Implement `DispatchAsync` instead of `SendAsync`:
```csharp
public Task DispatchAsync(UHttpRequest request, IHttpHandler handler,
                          RequestContext ctx, CancellationToken ct)
{
    CaptureRequest(request);
    handler.OnRequestStart(request, ctx);

    if (!_responseQueue.TryDequeue(out var queued))
    {
        handler.OnResponseError(
            new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                "MockTransport: no queued response")), ctx);
        return Task.CompletedTask;
    }

    if (queued.Delay > TimeSpan.Zero)
        return DispatchWithDelayAsync(request, handler, queued, ctx, ct);

    DriveHandler(handler, queued, ctx);
    return Task.CompletedTask;
}

// M-6: DriveHandler no longer relies on QueuedResponse carrying a request.
// Body: use null check (byte[].IsEmpty doesn't exist on byte[]).
private static void DriveHandler(IHttpHandler handler, MockResponse r, RequestContext ctx)
{
    if (r.Error != null) { handler.OnResponseError(r.Error, ctx); return; }
    handler.OnResponseStart((int)r.StatusCode, r.Headers, ctx);
    if (r.Body != null && r.Body.Length > 0)
        handler.OnResponseData(r.Body, ctx);
    handler.OnResponseEnd(HttpHeaders.Empty, ctx);
}
```

Delay path wraps `DriveHandler` in an `async Task` with `await Task.Delay(queued.Delay, ct)`. `OnRequestStart` still fires synchronously before the delay begins so test transports match the production callback contract.

Public `EnqueueResponse`, `EnqueueError`, `EnqueueJson`, `CapturedRequests` APIs unchanged.

### `Runtime/Testing/RecordReplayTransport.cs`

**Record mode** — `DispatchAsync` wraps `handler` with `RecordingHandler`:
- `RecordingHandler` buffers `OnResponseStart` status/headers and all `OnResponseData` chunks into `SegmentedBuffer`
- `RecordingHandler.OnResponseEnd`: writes artifact to disk; **disposes `SegmentedBuffer` after write** (L-5: disposal in success path); forwards to real `_inner` handler
- `RecordingHandler.OnResponseError`: **disposes `SegmentedBuffer`** (L-5: disposal in error path); forwards to `_inner`

**Replay mode** — `DispatchAsync` looks up artifact by request key; calls `handler.OnRequestStart(request, ctx)` first, then drives the remaining callbacks directly (same ordering contract as `MockTransport`).

**Passthrough mode** — `DispatchAsync` forwards to `_innerTransport.DispatchAsync(request, handler, ctx, ct)`.

`RecordReplayTransport.Redaction.cs` — redaction logic unchanged, moves to `RecordingHandler`.
`RecordReplayTransport.Utilities.cs` — key computation and serialization unchanged.

### Test Files

All test files updated to remove `IHttpMiddleware`, `HttpPipeline`, `HttpPipelineDelegate` references:

| Old test file | New test file |
|---|---|
| `Tests/Runtime/Pipeline/HttpPipelineTests.cs` | `InterceptorPipelineTests.cs` |
| `Tests/Runtime/Pipeline/LoggingMiddlewareTests.cs` | `Tests/Runtime/Pipeline/LoggingInterceptorTests.cs` |
| `Tests/Runtime/Pipeline/DefaultHeadersMiddlewareTests.cs` | `Tests/Runtime/Middleware/DefaultHeadersInterceptorTests.cs` |
| `Tests/Runtime/Middleware/RedirectMiddlewareTests.cs` | `RedirectInterceptorTests.cs` |
| `Tests/Runtime/Middleware/CookieMiddlewareTests.cs` | `CookieInterceptorTests.cs` | *(L-4: was missing)* |
| `Tests/Runtime/Cache/CacheMiddlewareTests.cs` | `Tests/Runtime/Cache/CacheInterceptorTests.cs` |
| `Tests/Runtime/Retry/RetryMiddlewareTests.cs` | `RetryInterceptorTests.cs` |
| `Tests/Runtime/Auth/AuthMiddlewareTests.cs` | `AuthInterceptorTests.cs` |
| `Tests/Runtime/Observability/MetricsMiddlewareTests.cs` | `MetricsInterceptorTests.cs` |
| `Tests/Runtime/Observability/MonitorMiddlewareTests.cs` | `Tests/Runtime/Observability/MonitorInterceptorTests.cs` |
| `Tests/Runtime/Transport/AdaptiveMiddlewareTests.cs` | `Tests/Runtime/Transport/AdaptiveInterceptorTests.cs` |
| *(new)* | `Tests/Runtime/Middleware/DecompressionInterceptorTests.cs` |

`IntegrationTests.cs`, `UHttpClientTests.cs`, `CoreTypesTests.cs`, `StressTests.cs`, `BenchmarkTests.cs` — update `MockTransport` usage if any test directly constructs pipeline; `UHttpClient`-based tests require no changes to test body.

### Validation
- `MockTransport` tests pass: request capture, response queue, delay simulation, error injection
- `MockTransport` and replay mode both honor `OnRequestStart` as the first callback, including queued-error scenarios
- `RecordReplayTransport` tests pass: record mode artifact written, replay mode served, strict key-mismatch policy
- All existing test suites pass with adapted infrastructure
