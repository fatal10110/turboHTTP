# 2026-03 Phase 22a Streaming Plan

## What Was Added

Added a new standalone technical phase plan for end-to-end request/response streaming:

- `Development/docs/phases/phase-22a-end-to-end-streaming.md`

Also corrected the previous placement mistake: the plan no longer lives under the `phase22/` subphase tree and is no longer linked from the Phase 22 overview as if it were part of 22.x.

## Purpose

Phase 22 established the interceptor/handler boundary, but the codebase still has buffered-body constraints in the request model, HTTP/1.1 parser, decompression path, file download path, and buffered public API. The new plan defines Phase 22a as a separate clean-break phase that makes upload and download streaming first-class without sacrificing the small buffered fast path.

## Decisions Captured In The Plan

1. **No backward compatibility:** the plan is intentionally a clean break. It replaces the ambiguous single buffered `SendAsync(...)` public path with explicit buffered and streaming entry points.
2. **Dual-path performance model:** the plan does not force all traffic through a streaming-only path. Small buffered request/response handling remains a first-class optimization target.
3. **Pull-based body transfer:** the plan replaces per-chunk push callbacks as the primary body contract with request/response body-source abstractions so backpressure is explicit and HTTP/2 flow control can track bytes actually consumed.
4. **Replayability is explicit:** retries and redirects depend on request-body replayability, not on hidden buffering.
5. **Bounded memory on large payloads:** the plan sets explicit initial targets for send buffers, receive buffers, and HTTP/2 per-stream buffering so large uploads/downloads stop scaling managed memory with payload size.

## Why This Shape Was Chosen

The existing Phase 22 design is the right dispatch/interceptor direction, but true streaming still needs a different body-transfer contract. The current synchronous `IHttpHandler.OnResponseData(...)` callback is efficient for buffered replay and metrics, but it is not a sufficient long-term primitive for high-performance streaming because it does not model backpressure naturally and it leaves HTTP/1.1 and HTTP/2 with different ownership/drain assumptions.

The new plan keeps the Phase 22 dispatch/interceptor architecture, but moves body transfer into explicit request/response body sources and treats the buffered public API as an adapter above the same substrate.

## Files Modified

| File | Change |
|------|--------|
| `Development/docs/phases/phase-22a-end-to-end-streaming.md` | New standalone detailed streaming plan |
| `Development/docs/phases/phase22/overview.md` | Removed the incorrect link that implied 22a was part of Phase 22 |

## Validation

Documentation-only change:

- no runtime code changed
- no tests run

## 2026-03-15 Review Pass

Full dual-agent review (unity-infrastructure-architect + unity-network-architect) identified 12 Critical, 22 Important, and 10 Suggestion findings. All findings were addressed in the plan document. Key changes:

### Critical Fixes Applied

1. `IResponseBodySource` changed from `internal` to **`public`** in Core (parallels `IHttpTransport`)
2. `PluginContext.cs` and `DispatchBridge.cs` added to file impact list (were missing entirely)
3. HTTP/2 bounded queue design specified: `SingleReaderChannel<T>` using `ManualResetValueTaskSourceCore<int>` (SPSC, non-blocking enqueue)
4. Abort/dispose race protocol specified: atomic aborted flag → discard → RST_STREAM → release
5. **Decoupled WINDOW_UPDATE model:** connection-level on receipt, per-stream on consumption (prevents cross-stream starvation)
6. Post-RST_STREAM DATA frame handling specified (connection-level window accounting)
7. Read loop never blocks on per-stream buffer — overflow triggers `RST_STREAM(FLOW_CONTROL_ERROR)`
8. HEAD/1xx/204/304 explicitly enumerated as no-body responses producing `EmptyResponseBodySource`
9. `IAsyncDisposable` IL2CPP validation checkpoint added to 22a.1 completion criteria
10. GZIP trailer validation: rely on `GZipStream` internal CRC32 for streaming; document early-dispose skips validation
11. `ConnectionLease.TransferOwnership()` specified for streaming HTTP/1.1 path; leak detection via finalizer
12. `DecompressionBodySource` `Stream` adapter gets ~8KB read-ahead buffer for `GZipStream` inner reads

### Important Fixes Applied

- `RequestBodyReadSession` lifecycle fully specified (Core/Internal, disposal rules, replayability semantics)
- `FileRequestBody` moved to `TurboHTTP.Files` (Core stays file-I/O-free)
- `CapabilityEnforcedInterceptor` + `ObservedHandler` redesign added to 22a.5 deliverables
- HTTP/1.1 drain-or-close: three-condition gate (deterministic framing + remaining <= 64KB + no Connection: close) + 2s timeout
- `BufferedStreamReader` transfer to body source specified
- Chunked encoder EOF semantics specified (ReadAsync returning 0 = terminal)
- HTTP/2 zero-body responses (HEADERS+END_STREAM) produce pre-completed body source
- "No bytes committed" defined as "no request body bytes committed" (headers may have been sent)
- `IResponseBodySource.ReadAsync` contract fully specified (EOF, partial reads, cancellation, single-reader, UHttpException)
- `Fault(Exception)` method added to `IResponseBodySource` for error propagation
- HTTP/1.1 trailers: `GetTrailersAsync` returns empty; full parsing deferred
- Per-stream buffer size must be >= advertised initial window
- Timeout scope: request timeout for headers only; body reads governed by consumer's CancellationToken
- Loopback `HttpListener` benchmarks (not MockTransport) for latency regression measurement
- Sub-phase 22a.5 (buffered fast path) reordered before 22a.4 (interceptor rewrite)
- `MockResponseBodySource` added to 22a.1 deliverables for self-testing
- `StreamingOptions` type named for runtime-configurable thresholds

### Non-Goals Added

- `Expect: 100-continue` handling (deferred to post-22a)
- Streaming through proxy connections (deferred to post-22a)
- HTTP/1.1 trailer parsing (deferred)
- HTTP/1.1 request trailers (out of scope)

## Deferred Until Implementation

1. The HTTP/2 queue uses `SingleReaderChannel<T>` — exact ring buffer vs segmented queue internal representation is chosen during 22a.3 implementation.
2. Small-body thresholds are initial targets only and will need measurement on mobile and IL2CPP.
3. Request replayability behavior for seekable streams will need tight implementation rules to avoid fragile caller-managed reset assumptions.
4. `IAsyncDisposable` fallback strategy (IDisposable-only + explicit DisposeAsync method) activated only if IL2CPP validation fails in 22a.1.

## 2026-03-16 Review Follow-up

Applied a second review-fix pass to close the documented findings and several related repo-specific gaps:

1. **Clone semantics specified explicitly** — Phase 22a docs now distinguish sequential replayability from detached cloneability. `UHttpRequest.Clone()` is defined as a detached clone API; `StreamRequestBody` is not cloneable; same-dispatch header/timeout mutation uses a shared-content copy helper instead.
2. **`SendAsync` migration blast radius expanded** — added an explicit compile-surface migration sweep for `TurboHTTP.UniTask`, JSON helpers, OAuth, Unity helpers, and coroutine wrappers so the repo does not land in a half-migrated state.
3. **Buffered response fast path made ownership-safe** — replaced the response-side `TryGetBufferedData(out ReadOnlyMemory<byte>)` proposal with a detach/transfer contract (`TryDetachBufferedBody(out DetachedBufferedBody)`) that can safely carry either single-segment or segmented pooled storage into `UHttpResponse`.
4. **HTTP/2 zero-body trailer wording corrected** — `HEADERS+END_STREAM` now resolves `GetTrailersAsync` to empty immediately.
5. **Other gaps researched and folded into the plan**:
   - `FileRequestBuilderExtensions` now extends `UHttpRequest`, not a nonexistent `UHttpRequestBuilder`
   - request-side observability now has an explicit plan: buffered previews use `TryGetBufferedData(...)`; streaming request bodies do not force buffering
   - metrics for unknown-length streaming uploads now rely on transport-populated byte counters instead of `request.Body.Length`
   - documentation/update checklist now references `AGENTS.md` alongside `CLAUDE.md`

### Files Modified

- `Development/docs/phases/phase22a/phase-22a.1-core-body-model.md`
- `Development/docs/phases/phase22a/phase-22a.3-http2-streaming.md`
- `Development/docs/phases/phase22a/phase-22a.4-buffered-fast-path.md`
- `Development/docs/phases/phase22a/phase-22a.5-interceptor-rewrite.md`
- `Development/docs/phases/phase22a/phase-22a.6-validation.md`
- `Development/docs/phases/phase-22a-end-to-end-streaming.md`

### Validation

Documentation-only change:

- no runtime code changed
- no tests run
