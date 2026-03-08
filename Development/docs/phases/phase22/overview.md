# Phase 22: Interceptor Architecture Redesign

**Milestone:** M4 (v2.0)
**Dependencies:** Phase 19a–19c (performance & API groundwork)
**Estimated Complexity:** Very High
**Critical:** Yes — replaces the core pipeline abstraction end-to-end. Clean break; no backward compatibility.

> **Review status:** All approved findings from
> `Development/docs/phases/phase22/review.md` have been incorporated into this overview and the
> linked sub-phase documents.

## Context

The current ASP.NET Core-style middleware pipeline (`IHttpMiddleware` / `HttpPipeline`) has two structural limitations that block important features:

1. **Fully-buffered body model** — `IHttpTransport.SendAsync` returns a complete `UHttpResponse` before any middleware sees the response. Transparent decompression requires a second full-buffer pass, and upload/download progress callbacks are meaningless in this model.
2. **No short-circuit or re-dispatch capability** — Middlewares that skip the transport (cache hit) or repeat it (retry, redirect) must work around the delegate chain rather than composing with it.

The solution is the **undici-style interceptor model** (https://github.com/nodejs/undici/tree/main/lib/interceptor): the transport becomes a push-based event driver calling `IHttpHandler` callbacks instead of returning a fully-formed `UHttpResponse`. Interceptors wrap the dispatch function and/or the handler, enabling short-circuit (cache) and re-dispatch (retry, redirect) immediately. Phase 22 establishes the handler boundary needed for future end-to-end streaming, but concrete behavior in this phase remains mixed: HTTP/2 can surface data incrementally, while HTTP/1.1 parsing and decompression are still buffered.

---

## Architecture Overview

### Two New Primitives

**`IHttpHandler`** — synchronous response lifecycle callbacks (the response consumer):

```csharp
public interface IHttpHandler
{
    /// <summary>Fires before any network I/O. Always the first callback.</summary>
    void OnRequestStart(UHttpRequest request, RequestContext context);

    void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context);

    /// <summary>
    /// The span is valid only for the duration of this call.
    /// Callers must copy data they wish to retain beyond this invocation.
    /// </summary>
    void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context);

    void OnResponseEnd(HttpHeaders trailers, RequestContext context);

    /// <summary>
    /// May be called at any point after <c>OnRequestStart</c>, including after
    /// <c>OnResponseStart</c> and <c>OnResponseData</c> (partial response error mid-transfer).
    /// Implementations must handle all callback orderings.
    /// After this fires, no further callbacks will be delivered.
    /// </summary>
    void OnResponseError(UHttpException error, RequestContext context);
}
```

Callbacks are **synchronous** — no async in `IHttpHandler`. Async work (retry delay, cache lookup) lives at the `DispatchFunc` level.

**`DispatchFunc`** — the unit of interception, replacing `HttpPipelineDelegate`:

```csharp
public delegate Task DispatchFunc(
    UHttpRequest request,
    IHttpHandler handler,
    RequestContext context,
    CancellationToken ct);
```

**`IHttpInterceptor`** — replaces `IHttpMiddleware`; returns a new `DispatchFunc` wrapping the next:

```csharp
public interface IHttpInterceptor
{
    DispatchFunc Wrap(DispatchFunc next);
}
```

### Error Delivery Contract (C-7)

A single, unambiguous contract governs error delivery across all transports:

| Error type | Delivery mechanism | Task completion |
|------------|-------------------|-----------------|
| Transport / network / HTTP error | `handler.OnResponseError(mapped, ctx)` — called before Task returns | Task completes **normally** (not faulted) |
| Cancellation | Task throws `OperationCanceledException` — **no** `OnResponseError` callback | Task faults/cancels |

**After `OnResponseError` is called, no further handler callbacks will fire, and the dispatch Task MUST complete normally (not throw).**

A `MapException(Exception) → UHttpException` helper in `RawSocketTransport` ensures all exception types are mapped consistently before calling `OnResponseError`. `RequestFailed` / `RequestCancelled` context events are recorded inside the transport error path before calling `OnResponseError`.

### Three Composition Patterns

| Pattern | Used by | Mechanism |
|---------|---------|-----------|
| **Dispatch-wrapping only** | Auth, DefaultHeaders, Concurrency, BackgroundNetworking | Mutate request or acquire/release resources before/after `next(request, handler, ctx, ct)` |
| **Handler-wrapping** | Logging, Metrics, Monitor, Decompression, Adaptive | Call `next(request, new WrappedHandler(inner, ...), ctx, ct)` |
| **Both** | Retry, Redirect, Cache, Cookie | Check at dispatch level (cache hit, policy), optionally short-circuit or re-dispatch, wrap handler to intercept response stream |

### Terminal Handler

`ResponseCollectorHandler` (Core, `internal`) bridges the push model back to the pull-based public API:

- **Constructor**: `ResponseCollectorHandler(UHttpRequest request, RequestContext context)`
- `OnRequestStart` → updates `_request` reference (for redirect effective-request tracking)
- `OnResponseData` → writes chunks into `SegmentedBuffer`
- `OnResponseEnd` → constructs `UHttpResponse(statusCode, headers, _body.AsSequence(), _body, _context.Elapsed, _request)`, sets TCS
- `OnResponseError` → disposes `_body`, faults TCS

`UHttpClient.SendAsync` **changes return type to `Task<UHttpResponse>`** (Phase 22 is a clean break — this eliminates the `ValueTask`→`Task` allocation mismatch and the zero-alloc fast path is no longer meaningful here since `collector.ResponseTask` always allocates). Internally:

```csharp
var collector = new ResponseCollectorHandler(request, context);
var pipelineTask = _pipeline(request, collector, context, ct);
_ = pipelineTask.ContinueWith(t =>
{
    if (t.IsFaulted) collector.Fail(t.Exception.GetBaseException());
    else if (t.IsCanceled) collector.Cancel();
    else collector.EnsureCompleted(); // safety net: fault TCS if pipeline completed normally without a response callback
}, TaskContinuationOptions.ExecuteSynchronously);
var response = await collector.ResponseTask.ConfigureAwait(false);
// H-10: Wire pooled-request retain/release
if (request.IsPooled)
{
    request.RetainForResponse();
    response.AttachRequestRelease(request.ReleaseResponseHold);
}
return response;
```

`collector.EnsureCompleted()`: if `ResponseTask` is already completed, no-op; otherwise faults TCS with `InvalidOperationException("Pipeline completed without delivering a response")`. Prevents `SendAsync` hanging on handler-wrapping bugs or transport defects.

This safety net remains valid because redirect handling keeps the outer dispatch Task pending until the terminal hop completes. A normal multi-hop redirect chain must not trip `EnsureCompleted()`.

### Updated Transport Interface

```csharp
public interface IHttpTransport : IDisposable
{
    // SendAsync removed. Transport drives handler callbacks as data arrives.
    Task DispatchAsync(UHttpRequest request, IHttpHandler handler,
                       RequestContext context, CancellationToken ct);
}
```

### Performance Impact

| Metric | Current pipeline | Interceptor model |
|--------|-----------------|-------------------|
| Async state machines per request (data path) | N (one per middleware) | 0 — handler callbacks are synchronous |
| Per-request middleware allocations | 0 (stateless singletons) | N handler wrappers — pooling deferred (see M-1 note) |
| Peak memory for large responses | Full body buffered before any middleware | Composition improves immediately, but memory wins are partial in Phase 22: HTTP/2 can surface chunks incrementally, HTTP/1.1 still buffers the full parsed body, and decompression still buffers compressed + decompressed bodies |
| Virtual dispatch per chunk (data path) | 0 (body inside transport) | N per chunk — negligible vs I/O latency |

> **M-1 (Handler Wrapper Pooling):** The performance table originally claimed "N handler wrappers (poolable → 0)". Pooling is not designed in Phase 22 — no pool location, reset semantics, or capacity is specified for any handler. The accepted baseline: Logging + Metrics + Decompression = 3 handler wrapper allocations per request; `RetryDetectorHandler` allocates per-attempt. Pooling for hot-path handlers (`RetryDetectorHandler`, `LoggingHandler`, `MetricsHandler`) is deferred to a follow-up phase.

Phase 22's primary gain is composition correctness and a single error-delivery contract. Full HTTP/1.1 streaming and decompress-per-chunk memory reductions are explicitly deferred follow-up work.

---

## Sub-Phase Index

| Sub-Phase | Name | Effort |
|-----------|------|--------|
| [22.1](phase-22.1-core-interfaces.md) | Core Interfaces & Pipeline | 2–3 days |
| [22.2](phase-22.2-transport-adaptation.md) | Transport Adaptation | 3–4 days |
| [22.3](phase-22.3-interceptor-rewrites.md) | Interceptor Rewrites (all modules) | 4–5 days |
| [22.4](phase-22.4-testing-adaptation.md) | Testing Adaptation | 2–3 days |

---

## Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| Handler callbacks synchronous | Eliminates async state machine per callback on data path; async work lives at `DispatchFunc` level |
| `SendAsync` return type changed to `Task<UHttpResponse>` | Phase 22 is a clean break. `collector.ResponseTask` is always `Task<UHttpResponse>`; wrapping in `ValueTask` provides no benefit and the allocation mismatch causes per-call overhead. Simpler to change the return type than introduce `ManualResetValueTaskSourceCore` in the collector. |
| HTTP/1.1 parser unchanged in Phase 22 | Parser still accumulates into `SegmentedBuffer`; body enumerated post-parse. True streaming parser is a follow-up phase |
| `DecompressionHandler` buffers compressed body into `SegmentedBuffer` | `SegmentedBuffer` avoids LOH pressure. Peak memory = compressed + decompressed. Full-buffer approach documented as known limitation; streaming decompression via persistent `GZipStream` + ring buffer deferred |
| Request mutation is clone-on-write | `UHttpRequest.WithHeader()`, `WithHeaders()`, `WithMetadata()`, and `SetTimeoutInternal()` mutate the instance in place. Interceptors that apply transient headers or timeouts must clone first, call `ctx.UpdateRequest(clone)`, and pass the clone downstream so retries/redirects do not inherit accidental state |
| Request body ownership stays with `UHttpRequest` | Transports must not call `request.DisposeBodyOwner()`. Request body memory is released only when the request is disposed or returned to the pool after the outermost dispatch chain completes |
| Brotli excluded | `BrotliStream` not reliably available on Unity 2021.3 LTS Mono; platform probe required — deferred |
| `RetryDetectorHandler` discards retryable bodies, last attempt uses real handler | Exhaustion delivery guaranteed — terminal failure flows to real handler naturally. Zero allocation on retry path for discarded bodies |
| `RedirectInterceptor` uses an explicit completion bridge | `RedirectHandler` still starts follow-up dispatch from `OnResponseEnd`, but the wrapping interceptor awaits `RedirectHandler.Completion`. That Task is completed only by the terminal hop or by a bridged cancellation/fault from a spawned redirect dispatch, so the outer pipeline Task never completes early |
| HTTP/2 `Http2Stream` uses `ManualResetValueTaskSourceCore` | Replaces `TaskCompletionSource` (per-request allocation) and `PoolableValueTaskSource` from 19a.5. Embedded struct: zero allocation, in-place reset in `PrepareForPool()`. Preserves the zero-alloc achievement of Phase 19a.5. |
| HTTP/2 trailing headers separated into `Http2Stream._trailers` | Trailing headers must not be merged into response headers; separate field + `AppendTrailers()` preserves semantic distinction |
| Error delivery: single contract per error type | Transport errors → `OnResponseError` + Task completes normally. Cancellation → Task throws. Eliminates double-counting in Logging/Metrics handlers and defines clear contract for all handler implementations |
| `CacheStoringHandler` ownership transfer (not snapshot) | Zero-copy: buffer ownership transfers to store task; handler sets `_responseBody = null`. Store task takes full dispose responsibility. Error path disposes if still owned. |
| Stale-while-revalidate uses cloned request/context | Background revalidation races with `RequestContext.Clear()` in `SendAsync`'s `finally`. Clone request; fresh `RequestContext`; `CancellationToken.None` avoids cancelled-token rejection |
| `MockTransport.DriveHandler` synchronous | Deterministic test execution; delay path wraps in `async Task` only when needed |
| Plugin `RegisterMiddleware` → `RegisterInterceptor` | `PluginContext` API updated; no compatibility shim. `PluginCapabilities` also gains `AllowRedispatch` so capability enforcement can distinguish observation from repeated downstream dispatch |
| Handler wrapper pooling deferred | No pool design specified for Phase 22. Accepted baseline: 3 wrapper allocations per request (Logging + Metrics + Decompression). Pooling deferred to follow-up phase. |

---

## Architectural Notes

**No backpressure mechanism:** `IHttpHandler.OnResponseData` is synchronous and void. Under future streaming HTTP/2 parsing, a slow consumer would block the read loop thread, stalling all streams. This is not a Phase 22 defect (streaming parsing is deferred) but is documented as a known constraint.

**TLS/Security impact: zero.** Transport adaptation is purely at the `IHttpTransport` interface boundary. TLS negotiation, ALPN, BouncyCastle fallback are all below this layer.

**Platform compatibility: confirmed.** `ReadOnlySpan<byte>` works on IL2CPP. Lambda closures in `Wrap` methods generate AOT-compatible code. Same patterns as existing `HttpPipeline.BuildPipeline()`. `ManualResetValueTaskSourceCore<T>` is available in .NET Standard 2.1.

---

## Files Changed Summary

### TurboHTTP.Core
- **Removed:** `IHttpMiddleware.cs`, `Pipeline/HttpPipeline.cs`
- **New:** `IHttpHandler.cs`, `IHttpInterceptor.cs`, `DispatchFunc.cs`, `Pipeline/InterceptorPipeline.cs`, `Pipeline/ResponseCollectorHandler.cs`, `BackgroundNetworkingInterceptor.cs`
- **Modified:** `IHttpTransport.cs`, `UHttpClient.cs` (SendAsync return type → Task; redirect completion bridge expects outer dispatch to stay pending), `UHttpClientOptions.cs` (Interceptors, Clone() doc), `UHttpRequest.cs` (add `Clone()`, reuse `SetTimeoutInternal()` on cloned requests), `RequestContext.cs` (add `CreateForBackground(UHttpRequest request)`), `HttpHeaders.cs` (add Empty), `IHttpPlugin.cs` (`AllowRedispatch` capability), `PluginContext.cs`, `AdaptiveMiddleware.cs` → `AdaptiveInterceptor.cs` + `AdaptiveHandler.cs`, `BackgroundNetworkingPolicy.cs` (policy-only after interceptor extraction)

### TurboHTTP.Transport
- **Modified (significant):** `Http2/Http2Stream.cs` (ManualResetValueTaskSourceCore, _trailers, NullHandler)
- **Modified:** `RawSocketTransport.cs` (DispatchAsync, MapException, OnRequestStart placement, error routing, remove transport-owned body disposal), `Http2/Http2Connection.cs` (DispatchAsync, no request-body disposal), `Http2/Http2StreamPool.cs`
- **Modified (behavioral):** `Http2/Http2Connection.ReadLoop.cs` (DecodeAndSetHeaders result handling, AppendTrailers)
- **Modified (minor):** `Http1/Http11ResponseParser.cs` (EnumerateBodySegments using AsSequence)

### Module Assemblies
- **Auth:** `AuthMiddleware.cs` → `AuthInterceptor.cs` (clone-on-write before auth header injection)
- **Middleware:** `DefaultHeadersMiddleware.cs` → `DefaultHeadersInterceptor.cs` (clone-on-write only when defaults are injected); `RedirectMiddleware.cs` → `RedirectInterceptor.cs` + `RedirectHandler.cs` (explicit completion bridge, `UpdateRequest`, timeout budget); `CookieMiddleware.cs` → `CookieInterceptor.cs` + `CookieHandler.cs` (clone-on-write before `Cookie` header injection); **new** `DecompressionInterceptor.cs` + `DecompressionHandler.cs` (SegmentedBuffer, clone-on-write before `Accept-Encoding` injection)
- **Cache:** `CacheMiddleware*.cs` → `CacheInterceptor*.cs` + `CacheStoringHandler.cs` (ownership transfer, ServeCachedEntry callbacks, cloned background revalidation context)
- **Observability:** `LoggingMiddleware.cs` → `LoggingInterceptor.cs` + `LoggingHandler.cs`; `MetricsMiddleware.cs` → `MetricsInterceptor.cs` + `MetricsHandler.cs`; `MonitorMiddleware.cs` → `MonitorInterceptor.cs` + `MonitorHandler.cs` (IHttpInterceptor type check)
- **Retry:** `RetryMiddleware.cs` → `RetryInterceptor.cs` + `RetryDetectorHandler.cs` (exhaustion via real handler, single error path)
- **RateLimit:** `ConcurrencyMiddleware.cs` → `ConcurrencyInterceptor.cs`

### TurboHTTP.Testing
- **Modified:** `MockTransport.cs` (DispatchAsync, `OnRequestStart`-first callback ordering, DriveHandler fixes), `RecordReplayTransport.cs` (RecordingHandler with SegmentedBuffer disposal)

### Tests
- **Renamed + updated:** All `*MiddlewareTests.cs` → `*InterceptorTests.cs`; `HttpPipelineTests.cs` → `InterceptorPipelineTests.cs`; explicit rename table now includes `CacheMiddlewareTests.cs`, `MonitorMiddlewareTests.cs`, `AdaptiveMiddlewareTests.cs`, and the moved `DefaultHeadersInterceptorTests.cs`
- **New:** `DecompressionInterceptorTests.cs`

---

## Validation Criteria

Each sub-phase must pass **both specialist agent reviews** (unity-infrastructure-architect + unity-network-architect) before the next sub-phase begins.

**22.1 complete when:**
- Core assembly compiles with zero `IHttpMiddleware`/`HttpPipeline` references
- `new UHttpClient()` constructs without exception
- Zero-interceptor `InterceptorPipeline` dispatches directly to transport
- New API surface compiles: `UHttpRequest.Clone()`, `RequestContext.CreateForBackground(UHttpRequest)`, `PluginCapabilities.AllowRedispatch`
- `CapabilityEnforcedInterceptor` design reviewed and approved

**22.2 complete when:**
- `IntegrationTests` deterministic suite passes (HTTP/1.1 + HTTP/2)
- `Http2ConnectionTests` + `Http2FlowControlTests` pass
- `StressTests` pass (1000-request, concurrency enforcement, multi-host, pool leak)
- Error delivery contract validated: transport error → `OnResponseError` + normal Task; cancellation → exception
- Retry/redirect re-dispatch with pooled request bodies passes without `ObjectDisposedException` or reused-buffer corruption

**22.3 complete when:**
- All interceptor unit tests pass
- `DecompressionInterceptorTests` pass (gzip, deflate, passthrough, header stripping, SegmentedBuffer disposal on error)
- `RetryInterceptorTests` pass (5xx, transport error, idempotency, exhaustion delivers via real handler)
- `RedirectInterceptorTests` pass (all status codes, cross-origin, downgrade guard, loop, outer dispatch remains pending until terminal hop, redirect cancellation propagates as `OperationCanceledException`)
- `CacheInterceptorTests` pass (fresh, stale, stale-while-revalidate with cloned context, Vary)

**22.4 complete when:**
- All `MockTransport` tests pass (capture, queue, delay, error)
- `MockTransport` preserves `OnRequestStart`-first ordering even for queued error cases
- All `RecordReplayTransport` tests pass (record with disposal, replay, strict mismatch)
- Complete test suite passes (`dotnet test` clean run)

**Phase 22 complete when:**
- Both specialist agent reviews pass on the full implementation
- `BenchmarkTests` quality gates pass
- Implementation journal written at `Development/docs/implementation-journal/2026-03-phase22-interceptor-redesign.md`
- `CLAUDE.md` Development Status section updated
