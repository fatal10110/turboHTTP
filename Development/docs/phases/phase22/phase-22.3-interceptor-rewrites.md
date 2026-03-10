## 22.3: Interceptor Rewrites

All existing middleware classes are replaced. File naming: `*Middleware.cs` → `*Interceptor.cs`; handler companions named `*Handler.cs` in the same directory.

### Request clone-on-write rule

`UHttpRequest.WithHeader()`, `WithHeaders()`, `WithMetadata()`, and `SetTimeoutInternal()` all mutate the request instance in place. Any interceptor that applies derived or transient request changes must:

1. Call `req.Clone()`
2. Mutate the clone
3. Call `ctx.UpdateRequest(clone)`
4. Pass the clone downstream

Interceptors that make no request changes pass the original request through untouched. Phase 22 does **not** rely on fluent immutable request helpers.

### Simple dispatch-wrapping interceptors

**`AuthInterceptor`** (`Runtime/Auth/AuthInterceptor.cs`):
```csharp
public DispatchFunc Wrap(DispatchFunc next) => async (req, handler, ctx, ct) =>
{
    var token = await _provider.GetTokenAsync(ct).ConfigureAwait(false);
    var requestForNext = req;

    if (!string.IsNullOrEmpty(token))
    {
        requestForNext = req.Clone();
        requestForNext.WithHeader("Authorization", $"Bearer {token}");
        ctx.UpdateRequest(requestForNext);
    }

    await next(requestForNext, handler, ctx, ct).ConfigureAwait(false);
};
```
Remove `AuthMiddleware.cs`. `AuthBuilderExtensions` unchanged.

**`DefaultHeadersInterceptor`** (`Runtime/Middleware/`): stateless. If at least one default header must be injected (respecting `overrideExisting`), clone the request once, apply the missing headers to the clone, `ctx.UpdateRequest(clone)`, then call `next(clone, ...)`. If no changes are needed, forward the original request unchanged.

**`ConcurrencyInterceptor`** (`Runtime/RateLimit/`):
```csharp
public DispatchFunc Wrap(DispatchFunc next) => async (req, handler, ctx, ct) =>
{
    var host = req.Uri.Authority;
    ctx.RecordEvent("ConcurrencyAcquire");
    await _limiter.AcquireAsync(host, ct).ConfigureAwait(false);
    try
    {
        ctx.RecordEvent("ConcurrencyAcquired");
        await next(req, handler, ctx, ct).ConfigureAwait(false);
        // M-4: Dispatch Task completion guarantees all handler callbacks have been
        // delivered (the TCS in ResponseCollectorHandler is the true gate). The semaphore
        // is released only after the dispatch Task — and therefore all callbacks — complete.
    }
    finally
    {
        _limiter.Release(host);
        ctx.RecordEvent("ConcurrencyReleased");
    }
};
```
Implements `IDisposable`. `ConcurrencyLimiter` unchanged.

> **Redirect chain note:** The concurrency permit (semaphore) remains held for the entire redirect chain because `RedirectInterceptor` keeps the dispatch Task pending via the completion bridge until the terminal hop completes. This is correct behavior — the logical request is still in flight and should count against the concurrency limit. A 5-hop redirect chain holds one permit for the full duration, not five sequential permits.

**`BackgroundNetworkingInterceptor`** (`Runtime/Core/`): acquire background execution scope, `await next(...)`, release in `finally`. Retain `TryDequeueReplayable(out UHttpRequest request)` public API for Unity integration (M-11) — same method signature as current `BackgroundNetworkingMiddleware`.

`TryDequeueReplayable(out UHttpRequest request)` moves to the new `BackgroundNetworkingInterceptor` because it operates on interceptor-owned queue state. `BackgroundNetworkingPolicy.cs` remains policy/config only after the extraction.

### Handler-wrapping interceptors

**`AdaptiveInterceptor`** + **`AdaptiveHandler`** (`Runtime/Core/`):

`AdaptiveInterceptor` is **handler-wrapping** (not dispatch-wrapping only) to observe response bytes (H-1):
```csharp
public DispatchFunc Wrap(DispatchFunc next) => async (req, handler, ctx, ct) =>
{
    var adjustedTimeout = _detector.GetAdjustedTimeout(req.Timeout);
    var requestForNext = req;

    if (adjustedTimeout != req.Timeout)
    {
        requestForNext = req.Clone();
        requestForNext.SetTimeoutInternal(adjustedTimeout);
        ctx.UpdateRequest(requestForNext);
    }

    await next(requestForNext, new AdaptiveHandler(handler, _detector), ctx, ct).ConfigureAwait(false);
};
```

`AdaptiveHandler`:
- Field: `long _bytesReceived` — use `long` to avoid overflow on large responses (>2GB file downloads)
- `OnResponseData`: `_bytesReceived += chunk.Length`; forward
- `OnResponseEnd`: `_detector.AddSample(bytesTransferred: _bytesReceived)`; forward

**`LoggingInterceptor`** + **`LoggingHandler`** (`Runtime/Observability/`):
- `Wrap`: log request, return `next(req, new LoggingHandler(handler, _log, req, _options, ctx.Elapsed), ctx, ct)`
- `LoggingHandler`:
  - `OnResponseStart` → log status line + optional headers (with redaction)
  - `OnResponseData` → track byte count; forward
  - `OnResponseEnd` → log elapsed, total bytes received; forward
  - `OnResponseError` → log error; forward

**`MetricsInterceptor`** + **`MetricsHandler`** (`Runtime/Observability/`):
- `Wrap`: `Interlocked.Increment(ref _metrics.TotalRequests)`, add bytes-sent, return `next(req, new MetricsHandler(handler, _metrics, req), ctx, ct)`
- `MetricsHandler`:
  - `OnResponseStart` → record status code
  - `OnResponseData` → `Interlocked.Add(ref _metrics.TotalBytesReceived, chunk.Length)`; forward
  - `OnResponseEnd` → update success count, rolling average via `Interlocked`; forward
  - `OnResponseError` → `Interlocked.Increment(ref _metrics.FailedRequests)`; forward

**`MonitorInterceptor`** + **`MonitorHandler`** (`Runtime/Observability/`, Editor-only): same pattern; auto-wired by `UHttpClient` via lazy type-resolution. The `#if UNITY_EDITOR` type-check uses `typeof(IHttpInterceptor).IsAssignableFrom(monitorType)` (L-3 — was `IHttpMiddleware`).

**`DecompressionInterceptor`** + **`DecompressionHandler`** (`Runtime/Middleware/`, **NEW**):
- `Wrap`: if `AutomaticDecompression` is enabled and `Accept-Encoding` is absent, clone the request, inject `Accept-Encoding: gzip, deflate` on the clone, call `ctx.UpdateRequest(clone)`, then return `next(clone, new DecompressionHandler(handler), ctx, ct)`. If the header is already set, forward the original request unchanged.
- `DecompressionHandler`:
  - `OnResponseStart`: detect `Content-Encoding` header; if `gzip` or `deflate`, strip `Content-Encoding` and `Content-Length` from headers forwarded to `_inner.OnResponseStart`; else pass through unchanged
  - `OnResponseData`: if compressing, append into `SegmentedBuffer _compressedBuffer` (H-2: **not** `MemoryStream` — `SegmentedBuffer` avoids LOH pressure via linked 16KB pooled segments); else forward directly
  - `OnResponseEnd`: if compressed:
    1. Obtain `ReadOnlySequence<byte>` via `_compressedBuffer.AsSequence()`
    2. Wrap with `ReadOnlySequenceStream` adapter (see below); wrap with `GZipStream` / `DeflateStream`
    3. Rent a 64KB buffer from `ArrayPool<byte>.Shared`; read decompressed data in a loop; call `_inner.OnResponseData(span)` per read. **The 64KB buffer must be returned in a `finally` block** covering the decompression loop to prevent pool leak on decompression errors (e.g., corrupt gzip data).
    4. Dispose decompression streams and `_compressedBuffer`
    5. Call `_inner.OnResponseEnd(trailers, ctx)`
    - Note: `GZipStream` validates the checksum when the last compressed block is read, not at stream creation — streaming decompress-per-chunk IS feasible but requires a persistent `GZipStream` wrapping a ring buffer. Deferred to a follow-up phase. Phase 22 buffers the compressed body into `SegmentedBuffer` then decompresses in `OnResponseEnd`. Peak memory = compressed_size + decompressed_size.

#### `ReadOnlySequenceStream` adapter (`Runtime/Core/Internal/ReadOnlySequenceStream.cs`)

`ReadOnlySequenceStream` is a read-only `Stream` adapter over `ReadOnlySequence<byte>`. Required because `GZipStream`/`DeflateStream` accept `Stream`, and copying the `SegmentedBuffer` contents into a `MemoryStream` would defeat LOH avoidance for compressed bodies >85KB. Lives in `Core/Internal` so both `DecompressionHandler` and future consumers can use it.

```csharp
internal sealed class ReadOnlySequenceStream : Stream
{
    private ReadOnlySequence<byte> _sequence;
    private SequencePosition _position;

    internal ReadOnlySequenceStream(ReadOnlySequence<byte> sequence)
    {
        _sequence = sequence;
        _position = sequence.Start;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _sequence.Length;
    public override long Position
    {
        get => _sequence.Slice(_sequence.Start, _position).Length;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _sequence.Slice(_position);
        if (remaining.IsEmpty) return 0;

        var toCopy = (int)Math.Min(count, remaining.Length);
        remaining.Slice(0, toCopy).CopyTo(new Span<byte>(buffer, offset, toCopy));
        _position = _sequence.GetPosition(toCopy, _position);
        return toCopy;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
```
  - `OnResponseError`: dispose `_compressedBuffer`; forward — **both** success and error paths must dispose (L-5 pattern, same as `RecordingHandler`)
  - **Brotli deferred** — `BrotliStream` not reliably available on Unity 2021.3 LTS Mono; requires platform probe (see Phase 23 / Phase 24.1 cross-reference below)

> **L-7 Decompression overlap:** Decompression is planned in three places: Phase 23 (transport-level), Phase 24.1 (reframed as `DecompressionMiddleware`), and Phase 22 (`DecompressionInterceptor`). **Phase 22's `DecompressionInterceptor` supersedes Phase 24.1** — Phase 24.1 `DecompressionMiddleware` implementation is dropped; Phase 22 is the canonical implementation. Phase 23 transport-level decompression (if implemented) targets a different layer; cross-reference in Phase 23 docs accordingly.

### Re-dispatching interceptors

**`RetryInterceptor`** + **`RetryDetectorHandler`** (`Runtime/Retry/`):

Per the **Error Delivery Contract**: transport errors arrive via `handler.OnResponseError`, not as `UHttpException` thrown from `await next(...)`. The catch block handles only `OperationCanceledException` (propagated). `RetryDetectorHandler.OnResponseError` signals retryable transport errors. The dual-path (exception catch + `OnResponseError`) is collapsed into a single path (C-2).

`Wrap` lambda:
```csharp
public DispatchFunc Wrap(DispatchFunc next) => async (request, handler, ctx, ct) =>
{
    if (!ShouldAttemptRetry(request))
    {
        await next(request, handler, ctx, ct).ConfigureAwait(false);
        return;
    }

    int attempt = 0;
    TimeSpan delay = _policy.InitialDelay;

    while (true)
    {
        attempt++;
        ctx.SetState("RetryAttempt", attempt);
        bool isLastAttempt = attempt > _policy.MaxRetries;

        if (isLastAttempt)
        {
            // Terminal attempt uses the real handler. Transport failures still flow through
            // OnResponseError, while HTTP 5xx remains a normal response delivered to the real
            // handler/collector. Exhaustion is only recorded when the final attempt still ends
            // in a retryable failure response/error; successful terminal recovery records
            // RetrySucceeded instead.
            var terminalObserver = new RetryTerminalObserverHandler(handler);
            await next(request, terminalObserver, ctx, ct).ConfigureAwait(false);
            if (attempt > 1)
            {
                if (terminalObserver.WasRetryableFailure)
                    ctx.RecordEvent("RetryExhausted", new Dictionary<string, object> { { "attempts", attempt } });
                else if (terminalObserver.WasCommitted && !terminalObserver.DeliveredError)
                    ctx.RecordEvent("RetrySucceeded", new Dictionary<string, object> { { "attempts", attempt } });
            }
            return;
        }

        var detector = new RetryDetectorHandler(handler);
        await next(request, detector, ctx, ct).ConfigureAwait(false);
        // Transport errors: arrived via detector.OnResponseError
        // 5xx responses: arrived via detector.OnResponseStart

        if (detector.WasRetryable)
        {
            ctx.RecordEvent("RetryScheduled");
            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay = NextDelay(delay);
            continue;
        }

        // Committed (2xx/3xx/4xx): detector forwarded to real handler
        if (attempt > 1)
            ctx.RecordEvent("RetrySucceeded", new Dictionary<string, object> { { "attempts", attempt } });
        return;
    }
};
```

`RetryDetectorHandler`:
- `bool _committed`, `bool WasRetryable` (unified flag replacing `WasRetryableStatus`)
- `OnRequestStart`: **always forward to `_inner`** regardless of retry state — `_inner` (e.g., `LoggingHandler`, `MetricsHandler`) may rely on `OnRequestStart` having fired before `OnResponseError` arrives. Per the `IHttpHandler` contract, `OnResponseError` "may be called at any point after `OnRequestStart`", so `OnRequestStart` must have been delivered.
- `OnResponseStart`: if 5xx → `WasRetryable = true` (do NOT forward); else → `_committed = true`, forward
- `OnResponseData`: if `_committed` forward; else discard (zero allocation on retry path)
- `OnResponseEnd`: if `_committed` forward; else discard
- `OnResponseError`: if error is retryable before commitment → `WasRetryable = true` (do NOT forward); if the response path was already committed, forward to `_inner` and stop retrying because the logical response already became visible downstream
- **No `OnResponseError` → exception exception-path**: error delivery via callbacks only, consistent with Error Delivery Contract

**`RedirectInterceptor`** + **`RedirectHandler`** (`Runtime/Middleware/`):

`Wrap` lambda: resolve `followRedirects` from metadata; if disabled → `await next(request, handler, ctx, ct)`; else:
```csharp
var redirectHandler = new RedirectHandler(handler, next, request, _policy, ctx, ct);
await next(request, redirectHandler, ctx, ct).ConfigureAwait(false);
await redirectHandler.Completion.ConfigureAwait(false); // H-12: outer dispatch stays pending until terminal hop
```

`RedirectHandler`:
- Fields: `_inner`, `_dispatch: DispatchFunc`, `_currentRequest`, `_redirectCount`, `bool _willRedirect`, `bool _committed`, `TaskCompletionSource<object> _completion`
- `Completion`: completed only when the terminal hop ends/errors or when a spawned redirect dispatch faults/cancels before delivering terminal callbacks
- `OnResponseStart`: call `TryGetRedirectTarget(statusCode, headers, out target)`; if redirect → `_willRedirect = true` (do NOT forward); else → `_committed = true`, forward
- `OnResponseData`: if `_committed` forward; else discard
- `OnResponseEnd`:
  - if `_committed` → forward; `_completion.TrySetResult(null)`; return
  - if `_willRedirect`:
    - `_willRedirect = false`; validate redirect count (`_redirectCount >= _policy.MaxRedirects` → `_inner.OnResponseError(TooManyRedirects, ctx); _completion.TrySetResult(null); return`)
    - `newRequest = BuildRedirectRequest(...)` — same RFC-compliant logic as current `RedirectMiddleware`
    - Apply total redirect timeout budget to `newRequest` (M-10 — same as `RedirectMiddleware.ApplyTotalRedirectTimeoutBudget`)
    - `ctx.UpdateRequest(newRequest)` (M-5 — track redirect hops in context)
    - `_currentRequest = newRequest`; `_redirectCount++`; `ctx.RecordEvent("Redirect")`
    - Start the next hop immediately, but bridge its task into `Completion` so the outer interceptor remains pending:
      ```csharp
      var redirectTask = _dispatch(newRequest, this, ctx, _ct);
      _ = redirectTask.ContinueWith(t =>
      {
          try
          {
              if (t.IsFaulted)
                  _completion.TrySetException(t.Exception.GetBaseException()); // observes Exception
              else if (t.IsCanceled)
                  _completion.TrySetCanceled();
          }
          catch (Exception bridgeError)
          {
              _completion.TrySetException(bridgeError);
          }
      }, TaskContinuationOptions.ExecuteSynchronously |
         TaskContinuationOptions.NotOnRanToCompletion);
      ```
      Successful redirect tasks do not complete `Completion`; only the terminal handler callbacks do. This keeps the outer dispatch Task alive across the whole redirect chain.
- `OnResponseError`: forward to `_inner`, then `_completion.TrySetResult(null)` because callback-based errors are terminal but the dispatch Task itself completes normally

This resolves both H-12 and M-13: redirect chains no longer race `collector.EnsureCompleted()`, and every spawned redirect Task is observed through the completion bridge.

**`CacheInterceptor`** + **`CacheStoringHandler`** (`Runtime/Cache/`):

`Wrap` lambda (async):
```csharp
public DispatchFunc Wrap(DispatchFunc next) => async (request, handler, ctx, ct) =>
{
    if (!IsCacheable(request))
    {
        await next(request, handler, ctx, ct).ConfigureAwait(false);
        return;
    }

    var lookupResult = await LookupCacheAsync(request, ct).ConfigureAwait(false);

    if (lookupResult.Entry != null && lookupResult.Entry.IsFresh(DateTimeOffset.UtcNow))
    {
        ServeCachedEntry(handler, lookupResult.Entry, request, ctx);
        return;
    }

    if (lookupResult.Entry != null && lookupResult.Entry.IsStaleWhileRevalidate(DateTimeOffset.UtcNow))
    {
        ServeCachedEntry(handler, lookupResult.Entry, request, ctx);
        // C-5: Clone request and create fresh context for background revalidation.
        // Original request/context will be torn down when SendAsync's finally runs.
        var revalRequest = request.Clone();
        var revalCtx = RequestContext.CreateForBackground(revalRequest);
        _ = RevalidateAsync(revalRequest, lookupResult, next, revalCtx, CancellationToken.None);
        return;
    }

    // Cache miss or conditional — dispatch and store
    await next(request, new CacheStoringHandler(handler, this, lookupResult, request), ctx, ct)
        .ConfigureAwait(false);
};
```

`ServeCachedEntry` must call all four callbacks in order (M-7):
```csharp
private static void ServeCachedEntry(IHttpHandler handler, CacheEntry entry,
                                     UHttpRequest request, RequestContext ctx)
{
    handler.OnRequestStart(request, ctx);                       // required for Logging/Metrics
    handler.OnResponseStart(entry.StatusCode, entry.Headers, ctx);
    foreach (var segment in entry.Body.AsSequence())
        handler.OnResponseData(segment.Span, ctx);
    handler.OnResponseEnd(HttpHeaders.Empty, ctx);
}
```

`CacheStoringHandler`:
- `OnResponseStart`: record status + headers; forward to `_inner`
- `OnResponseData`: copy chunk into `SegmentedBuffer _responseBody`; forward to `_inner`
- `OnResponseEnd`: **H-7 — specify ownership transfer explicitly**: transfer `_responseBody` ownership to the store task (zero-copy, handler does NOT dispose); the store task disposes after write completes or on failure. Cache writes remain **strongly ordered** (not eventual) — fire-and-forget but TCS not gated on store completion. Storage failures are logged but not propagated to caller.
  ```csharp
  var bodyToStore = _responseBody;
  _responseBody = null; // transfer ownership — handler no longer owns this buffer
  _ = StoreAsync(bodyToStore, ...); // store task takes dispose responsibility
  _inner.OnResponseEnd(trailers, ctx);
  ```
- `OnResponseError`: dispose `_responseBody` (if still owned — null-check guards double-dispose); forward to `_inner`

Partial files `CacheMiddleware.Parsing.cs`, `CacheMiddleware.UriNormalization.cs`, `CacheMiddleware.Variants.cs` renamed to `CacheInterceptor.*.cs`; logic unchanged.

**`CookieInterceptor`** + **`CookieHandler`** (`Runtime/Middleware/`):
- Interceptor `Wrap`: if the jar yields a `Cookie` header, clone the request, inject the header on the clone, call `ctx.UpdateRequest(clone)`, then call `next(clone, new CookieHandler(handler, _jar, clone.Uri), ctx, ct)`. If no cookie header is needed, forward the original request unchanged.
- `CookieHandler.OnResponseStart`: extract `Set-Cookie` header values; call `_jar.SetCookies(uri, setCookieValues)`; forward

### Renamed/Removed

| Old file | New file | Note |
|---|---|---|
| `Runtime/Core/IHttpMiddleware.cs` | deleted | |
| `Runtime/Core/Pipeline/HttpPipeline.cs` | deleted | |
| `Runtime/Auth/AuthMiddleware.cs` | `AuthInterceptor.cs` | |
| `Runtime/Middleware/DefaultHeadersMiddleware.cs` | `DefaultHeadersInterceptor.cs` | |
| `Runtime/Middleware/RedirectMiddleware.cs` | `RedirectInterceptor.cs` + `RedirectHandler.cs` | |
| `Runtime/Middleware/CookieMiddleware.cs` | `CookieInterceptor.cs` + `CookieHandler.cs` | |
| `Runtime/Cache/CacheMiddleware*.cs` | `CacheInterceptor*.cs` + `CacheStoringHandler.cs` | |
| `Runtime/Observability/LoggingMiddleware.cs` | `LoggingInterceptor.cs` + `LoggingHandler.cs` | |
| `Runtime/Observability/MetricsMiddleware.cs` | `MetricsInterceptor.cs` + `MetricsHandler.cs` | |
| `Runtime/Observability/MonitorMiddleware.cs` | `MonitorInterceptor.cs` + `MonitorHandler.cs` | |
| `Runtime/Retry/RetryMiddleware.cs` | `RetryInterceptor.cs` + `RetryDetectorHandler.cs` | |
| `Runtime/RateLimit/ConcurrencyMiddleware.cs` | `ConcurrencyInterceptor.cs` | |
| `Runtime/Core/AdaptiveMiddleware.cs` | `AdaptiveInterceptor.cs` + `AdaptiveHandler.cs` | |
| `Runtime/Core/BackgroundNetworkingPolicy.cs` | `BackgroundNetworkingPolicy.cs` + `BackgroundNetworkingInterceptor.cs` | Policy remains in-place; queue/replay API (`TryDequeueReplayable`) moves to the interceptor |
| *(new)* | `Runtime/Middleware/DecompressionInterceptor.cs` + `DecompressionHandler.cs` | |
| *(new)* | `Runtime/Core/Internal/ReadOnlySequenceStream.cs` | Read-only `Stream` adapter over `ReadOnlySequence<byte>` for decompression |

### Validation
- All interceptor unit tests pass
- `DecompressionInterceptorTests`: gzip response decompressed, `Content-Encoding` stripped, `Content-Length` stripped, passthrough on uncompressed, `SegmentedBuffer` disposed on error
- `RetryInterceptorTests`: 5xx retry with backoff, transport error retry, idempotency guard, exhaustion delivers terminal failure through real handler
- `RedirectInterceptorTests`: 301/302/303/307/308, cross-origin auth strip, HTTPS→HTTP downgrade guard, loop detection, outer dispatch remains pending until terminal hop, cancellation propagates without `OnResponseError`
- `CacheInterceptorTests`: fresh hit (no network), stale-while-revalidate (cloned context), Vary header matching, conditional revalidation
