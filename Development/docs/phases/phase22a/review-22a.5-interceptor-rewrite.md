# Phase 22a.5 Review: Interceptor and Module Streaming Rewrite

## Review Round 1 (Initial Review)

**Review date:** 2026-03-21
**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Verdict:** BLOCKED — 1 critical, 3 high, 8 medium, 7 low identified

---

## Implementation Completeness

All 7 spec steps are implemented:

| Step | Component | Status |
|------|-----------|--------|
| 1 | `DecompressionBodySource` — incremental GZip/Deflate with ~8KB `BufferedStream`, configurable decompression bomb limit (default 256 MB) | Complete |
| 2 | Retry interceptor — drain/abort via `IResponseBodySource`, replay eligibility based on `Content.Replayability`, no protocol branching | Complete |
| 3 | Redirect interceptor — drain before redispatch, 303->GET body drop, non-replayable body rejection | Complete |
| 4 | `TeeBodySource` for cache — EOF-only commit, abandon discard, silent detach on size limit/write failure, pre-tee Content-Length check | Complete |
| 5 | Bounded observability — `ObservedResponseBodySource` proxy, `TryGetBufferedData` for request preview, `TransportBehaviorFlags.RequestBodyBytesSent` for streaming metrics | Complete |
| 6 | Streaming file downloader — `SendStreamingAsync`, pooled 32KB buffer, `IncrementalHash`, per-chunk progress | Complete |
| 7 | `CapabilityEnforcedInterceptor` + `ObservedHandler` — `RequestMutationSignature` via `Content.GetHashCode()`, `ResponseEventSignature` with body type/state hash, `ObservedBodySource` read/trailer/abort tracking | Complete |

Supporting items verified:
- Decompression: buffered fast-path with CRC32 trailer validation retained, streaming path relies on `GZipStream` internal CRC32
- Retry: `RetryDetectorHandler` discards at response start, linked CTS for drain timeout, `Abort()` fallback
- Redirect: cross-origin header stripping, HTTPS-to-HTTP downgrade protection, loop detection via `_visitedTargets`
- Cache: `_completedNaturally` flag for EOF commit, `MaxCacheableResponseBodyBytes` configurable, trailer loading before store
- Observability: 500-byte default logging preview, monitor bounded capture, metrics `Interlocked.Add` per read
- File download: resume support with Content-Range validation, `FileOptions.Asynchronous`
- Plugin: `ObservedBodySource` tracks bytes/completion/trailers/abort/dispose/detach, streaming read detection

---

## Spec Compliance Checklist

| Criterion | Status |
|-----------|--------|
| No optional module forces whole-body buffering by default | PASS |
| Module behavior correct for both buffered and streaming modes | PASS |
| `CapabilityEnforcedInterceptor` detects request mutation and response observation under new model | PASS |
| Decompression streaming works incrementally | PASS |
| Decompression bomb test: abort at limit, not OOM | PASS |
| Decompression bomb limit is configurable via `DecompressionInterceptor` constructor parameter | PASS |
| `DecompressionBodySource` owns the ~8KB read-ahead buffer, not `ResponseBodyStream` | PASS |
| Cache tee: commit only on natural EOF | PASS |
| Cache tee: discard on abandon | PASS |
| Cache tee: silent detach on write failure or size limit | PASS |
| `TeeBodySource` accumulation bounded by `MaxCacheableResponseBodyBytes` | PASS |
| Retry module uses `IResponseBodySource` abstraction, no protocol branching | PASS |
| Retry: correct for replayable and non-replayable bodies | PASS |
| Redirect: correct for body-required and body-dropped redirects | PASS |
| Request-side logging/monitor does not force buffering | PASS |
| Metrics report actual bytes for streaming uploads | PASS |
| File download: bounded memory, incremental progress, incremental hash | PASS |

---

## BLOCKING Issues

### B-1: `TeeBodySource` uses plain `bool` fields for cross-thread state — ARM64 IL2CPP correctness violation

**File:** `Runtime/Cache/CacheStoringHandler.cs`, lines 149–152
**Found by:** Both agents

`_completedNaturally`, `_aborted`, and `_trailersLoaded` are plain `bool` fields. `Abort()` (line 239) checks and sets `_aborted` as a non-atomic check-then-act. `DisposeAsync` (line 280) reads `_aborted` and `_completedNaturally` without memory barriers. Under IL2CPP on ARM64, writes to `bool` fields are not guaranteed visible to other threads without a memory barrier.

`Abort()` may be called from a different context (timeout, cancellation) than `DisposeAsync`, so `_aborted` must be visible across thread boundaries. This violates the IL2CPP ARM64 correctness requirements established in Phase 19a.3. In contrast, `DecompressionBodySource` correctly uses `int` fields with `Interlocked.Exchange`/`Volatile.Read` for all state tracking.

Additionally, two concurrent calls to `Abort()` could both pass the `if (_aborted) return` guard and call `DetachAccumulator()` and `_inner.Abort()` twice. `DetachAccumulator()` calls `_accumulator?.Dispose()` on a field that could be nulled between the null check and the `Dispose` call — a classic check-then-act race.

**Fix:** Convert `_aborted`, `_completedNaturally`, `_trailersLoaded` to `int` fields. Use `Interlocked.Exchange(ref _aborted, 1) != 0` for idempotent `Abort()`. Use `Volatile.Read`/`Volatile.Write` for all reads and writes. Match the pattern established in `DecompressionBodySource`.

---

### B-2: `ObservedResponseBodySource._aborted` not volatile and `Abort()` not idempotent

**File:** `Runtime/Observability/ObservedResponseBodySource.cs`, lines 38, 113–119
**Found by:** Both agents

Same pattern as B-1. `_aborted` is a plain `bool` with check-then-act in `Abort()`. Two concurrent calls could both pass the guard and invoke `_inner.Abort()` twice. Under the streaming body source contract, `Abort()` is used in error recovery paths and must be safe to call multiple times.

**Fix:** Convert `_aborted` to `int`. Use `Interlocked.Exchange(ref _aborted, 1) != 0` for idempotent abort. Match `DecompressionBodySource.Abort()` pattern.

---

### B-3: `ObservedResponseBodySource._error` assignment is non-atomic

**File:** `Runtime/Observability/ObservedResponseBodySource.cs`, line 88
**Found by:** Network architect

`_error = _error ?? ex` is a read-modify-write on a reference field without synchronization. On ARM64, this could result in torn reads if `ReadAsync` and `Abort()` race.

**Fix:** Use `Interlocked.CompareExchange(ref _error, ex, null)`.

---

### B-4: Missing test for cache tee mid-stream write failure

**File:** `Tests/Runtime/Cache/CacheInterceptorTests.Streaming.cs`
**Found by:** Infrastructure architect

The spec requires: "If cache write fails mid-stream (disk full, serialization error), `TeeBodySource` silently detaches the cache accumulator and continues delivering bytes to the consumer." The `Accumulate` method handles write failures gracefully (line 334–338), but no test exercises this path. Current tests cover: natural EOF cached, early dispose not cached, known-length above limit not cached, unknown-length above limit not cached. Missing: write-to-accumulator fails, consumer continues to receive all bytes.

**Fix:** Add a test using a mock accumulator that throws after N bytes, verifying: (a) consumer receives all bytes from inner source, (b) no cache entry is produced, (c) no exception propagates to consumer.

---

## NON-BLOCKING Issues

### NB-1: `BodySourceStream.Read(Span<byte>)` performs blocking async via `.GetAwaiter().GetResult()`

**File:** `Runtime/Middleware/DecompressionHandler.cs`, lines 489–522
**Found by:** Infrastructure architect

`BodySourceStream.Read(Span<byte>)` and `Read(byte[], int, int)` call `_inner.ReadAsync(...).GetAwaiter().GetResult()`. On Unity's main thread with a `SynchronizationContext`, any continuation posted back to the main thread while blocked in `.GetResult()` will deadlock. This is safe when `DecompressionBodySource.ReadAsync` runs off the main thread (the case via `SendStreamingAsync` today), but the invariant is invisible.

The spec says "confirmed: works on .NET Standard 2.1 and Unity IL2CPP Mono" but does not confirm deadlock freedom on Unity main thread.

**Fix:** Add an explicit code comment documenting that `DecompressionBodySource` must not be consumed from the Unity main thread (or any thread with a `SynchronizationContext` that requires message pumping).

---

### NB-2: `RedirectHandler.OnResponseStartAsync` does not handle `OperationCanceledException` from body drain

**File:** `Runtime/Middleware/RedirectHandler.cs`, lines 148, 181–184
**Found by:** Both agents

When the dispatch cancellation token fires during `DiscardBodyAsync` at line 148, `OperationCanceledException` propagates out of `OnResponseStartAsync`. The outer catch at line 181 only handles `UHttpException`, so the `OperationCanceledException` escapes. This converts a user-cancellation during pre-redirect body drain into an unhandled exception rather than a clean `TrySetCanceled()` on `_completion`.

**Fix:** Add a catch for `OperationCanceledException` in `OnResponseStartAsync` that calls `_completion.TrySetCanceled()`, matching the pattern at lines 173–177 which already handles cancellation from the redispatch call.

---

### NB-3: Two retry tests use legacy `OnResponseStart`/`OnResponseEnd` compat API

**File:** `Tests/Runtime/Retry/RetryInterceptorTests.cs`, lines 581–582, 624–632
**Found by:** Both agents

`RetryAfterHeader_OverridesConfiguredDelay` and `RetryAttempts_ForwardOnRequestStartOnlyOnce` use `handler.OnResponseStart(...)` / `handler.OnResponseEnd(...)` — the legacy shim extensions from `LegacyHttpHandlerCompatExtensions`. These test the buffered-response compat path, not the streaming drain path. There are no tests that verify `RetryDetectorHandler` correctly drains a chunked streaming body source.

**Fix:** Add at least one test using `MockResponseBodySource` in non-buffered mode that verifies `RetryDetectorHandler` correctly drains (or aborts on timeout) a streaming body before retry.

---

### NB-4: `DecompressionHandler._compressionChain` is a non-readonly mutable instance field

**File:** `Runtime/Middleware/DecompressionHandler.cs`, line 21
**Found by:** Both agents

`_compressionChain` is declared as a non-readonly field and reassigned in `OnResponseStartAsync` (line 56) after being initialized in the constructor (line 42). A `DecompressionHandler` is created fresh per dispatch, so reuse is not possible today. But the field's mutability means a second call to `OnResponseStartAsync` would overwrite the first call's chain with no compile-time guard.

**Fix:** Make `_compressionChain` `readonly` and pass the resolved compression chain to the constructor, or assign it once with a guard. This makes the single-assignment invariant explicit and catches bugs at compile time.

---

### NB-5: Missing streaming response bytes test for MetricsInterceptor

**File:** `Tests/Runtime/Observability/MetricsInterceptorTests.cs`
**Found by:** Infrastructure architect

No test verifies that `TotalBytesReceived` correctly accumulates from a non-buffered (streaming) body source via `ObservedResponseBodySource`. The spec requires "count streamed response bytes as they are consumed (incrementally, not at end)."

**Fix:** Add a test using `MockResponseBodySource` in non-buffered mode with a known byte count, verifying `TotalBytesReceived` accumulates correctly after consumer reads complete.

---

### NB-6: `CacheInterceptorTests.Streaming.cs` uses `Task.Run(...).GetAwaiter().GetResult()` instead of `AssertAsync.Run`

**File:** `Tests/Runtime/Cache/CacheInterceptorTests.Streaming.cs`, lines 19, 66, 111, 158
**Found by:** Infrastructure architect

All streaming cache tests use `Task.Run(async () => { ... }).GetAwaiter().GetResult()` instead of the project's standard `AssertAsync.Run` pattern used in `DecompressionInterceptorTests`, `RetryInterceptorTests`, etc. Assertion failures thrown on the thread pool thread may not propagate correctly to NUnit's test runner.

**Fix:** Replace with `AssertAsync.Run(async () => { ... })` to match test suite conventions.

---

### NB-7: `StreamRequestBody` redirect path lacks ownership comment

**File:** `Runtime/Middleware/RedirectInterceptor.cs`, line 220
**Found by:** Infrastructure architect

When `content is StreamRequestBody`, `RedirectContent.Shared(content)` is returned. The `Shared` vs `Owned` distinction controls whether the redirect request disposes the body. Returning `Shared` is correct for seekable streams (which are reset on replay), but the logic is subtle and uncommented.

**Fix:** Add an inline comment explaining that `Shared` is used because the source request retains ownership and the stream is seekable (position-reset on replay).

---

### NB-8: `new byte[64 * 1024]` in buffered decompression path not pooled

**File:** `Runtime/Middleware/DecompressionHandler.cs`, line 158
**Found by:** Network architect

`DecompressBufferedBody` allocates a `new byte[64 * 1024]` scratch buffer each time. This is the non-streaming fast path for already-buffered bodies. Not a hot allocation (once per buffered decompressed response), but inconsistent with project conventions.

**Fix:** Use `ArrayPool<byte>.Shared.Rent(64 * 1024)` with try/finally return.

---

## OBSERVATIONS

### O-1: `DecompressionBodySource.DrainAsync` drains through the decompression stream

**File:** `Runtime/Middleware/DecompressionHandler.cs`, lines 321–339
**Found by:** Network architect

When `DrainAsync` is called, decompression work is performed even when discarding. For HTTP/1.1 connection reuse, draining raw compressed bytes would be more efficient. However, the `BodySourceStream` adapter ties the inner source to the decompression chain, making `_inner.DrainAsync()` unsafe (would leave decompression streams inconsistent). The current approach is the only safe one. Acceptable.

---

### O-2: `_overflowProbe` per-instance allocation

**File:** `Runtime/Middleware/DecompressionHandler.cs`, line 214
**Found by:** Both agents

`private readonly byte[] _overflowProbe = new byte[1]` is allocated per `DecompressionBodySource` instance. Could be `static readonly` since content is never observed.

---

### O-3: `MonitorHandler._totalResponseBytes` and `LoggingHandler._bytesReceived` are non-volatile

**Files:** `Runtime/Observability/MonitorHandler.cs`, line 20; `Runtime/Observability/LoggingHandler.cs`, line 28
**Found by:** Infrastructure architect

Both are plain `long` fields mutated from `ObservedResponseBodySource` callbacks. Per-request single-consumer contract makes this safe in practice, but inconsistent with Phase 6 counter patterns.

---

### O-4: `MonitorHandler.CaptureOnce` saturates `_totalResponseBytes` to `int.MaxValue`

**File:** `Runtime/Observability/MonitorHandler.cs`, line 139
**Found by:** Infrastructure architect

For bodies >2 GB, `originalResponseBodySize` saturates at `int.MaxValue`. Accepted limitation for the int-sized field in `HttpMonitorEvent`.

---

### O-5: `ObservedBodySource.DrainAsync` does not track bytes in `_bytesRead`

**File:** `Runtime/Core/PluginContext.cs`, lines 717–719
**Found by:** Infrastructure architect

`DrainAsync` delegates directly to `_innerBody.DrainAsync(ct)` without going through `ReadAsync`, so bytes drained are not counted in `_bytesRead`. This is intentional (drain is a discard operation), but a read-only plugin that drains a body without wrapping it would not be detected by the `ObservationStateHash` mechanism. No test covers this scenario.

---

### O-6: `DecompressionBodySource.DisposeAsync` — inner abort without dispose on drain failure

**File:** `Runtime/Middleware/DecompressionHandler.cs`, lines 377–398
**Found by:** Infrastructure architect

When drain fails in `DisposeAsync`, `_inner.Abort()` is called but `_inner.DisposeAsync()` is not invoked. Whether `Abort()` alone releases all resources depends on the `IResponseBodySource` implementation contract. Should be documented explicitly.

---

### O-7: Duplicated `DiscardBodyAsync` pattern across retry and redirect

**Files:** `Runtime/Retry/RetryDetectorHandler.cs`, lines 91–151; `Runtime/Middleware/RedirectHandler.cs`, lines 228–288
**Found by:** Network architect

These two methods are nearly identical (linked CTS for timeout, drain with abort fallback, exception handling). Consider extracting a shared `ResponseBodyDrainHelper.DiscardAsync(body, dispatchCt, timeout)` in Core.Internal.

---

### O-8: `CacheStoringHandler.EnsureTrailersLoadedForStoreAsync` calls inner after dispose

**File:** `Runtime/Cache/CacheStoringHandler.cs`, lines 341–357
**Found by:** Infrastructure architect

Called from `DisposeAsync` after checking `_completedNaturally`. Calls `_inner.GetTrailersAsync(CancellationToken.None)` on a source that may already be disposed by the consumer. The catch block handles exceptions gracefully, but behavior depends on the inner source's tolerance for post-dispose calls.

---

## Missing Test Coverage

| # | Gap | Recommendation |
|---|-----|----------------|
| T-1 | Cache tee mid-stream write failure | Add test with mock accumulator that throws, verify consumer continues receiving bytes |
| T-2 | Streaming drain in `RetryDetectorHandler` | Add test with non-buffered `MockResponseBodySource`, verify drain before retry |
| T-3 | Streaming response bytes in `MetricsInterceptor` | Add test verifying incremental `TotalBytesReceived` via `ObservedResponseBodySource` |

---

## Pass Areas (both reviewers agree)

- **Decompression streaming:** Incremental GZipStream reads, ~8KB BufferedStream owned by DecompressionBodySource, CRC32 at EOF via GZipStream internals, configurable bomb limit
- **Retry drain semantics:** `IResponseBodySource` abstraction exclusively, no protocol branching, drain timeout with linked CTS, abort fallback
- **Redirect lifecycle:** Body drain before redispatch, 303->GET body drop, cross-origin header stripping, HTTPS downgrade protection, loop detection
- **Cache tee lifecycle:** EOF-only commit, abandon discard, size-limit silent detach, pre-tee Content-Length check, trailer loading before store
- **Observability bounded capture:** Preview capture bounded at 500 bytes (logging) / configurable (monitor), `TryGetBufferedData` for request preview, `TransportBehaviorFlags.RequestBodyBytesSent` for streaming metrics
- **File downloader:** `SendStreamingAsync` exclusively, pooled 32KB buffer, `IncrementalHash`, per-chunk progress, resume with Content-Range validation
- **Plugin observation:** `ObservedBodySource` tracks reads/trailers/abort/dispose, `RequestMutationSignature` uses `Content.GetHashCode()`, `ResponseEventSignature` includes body type and observation state hash
- **Request body replayability:** `NonReplayable` correctly skips retry even on idempotent methods, `ReplayableViaFactory` correctly eligible
- **Module dependency rules:** All optional modules reference only Core, no cross-module references
- **Platform compatibility:** All APIs within .NET Standard 2.1, WebGL exclusion maintained

---

## Platform Compatibility

| Component | Editor | Standalone | iOS IL2CPP | Android IL2CPP | WebGL |
|-----------|--------|------------|------------|----------------|-------|
| DecompressionBodySource | OK | OK | OK (B-1 fixed) | OK (B-1 fixed) | OK (Middleware assembly) |
| RetryDetectorHandler | OK | OK | OK | OK | OK (Retry assembly) |
| RedirectHandler | OK | OK | OK | OK | OK (Middleware assembly) |
| TeeBodySource | OK | OK | BLOCKED (B-1) | BLOCKED (B-1) | OK (Cache assembly) |
| ObservedResponseBodySource | OK | OK | BLOCKED (B-2, B-3) | BLOCKED (B-2, B-3) | OK (Observability assembly) |
| FileDownloader | OK | OK | OK | OK | OK (Files assembly) |
| CapabilityEnforcedInterceptor | OK | OK | OK | OK | OK (Core assembly) |

---

## Summary Table

| Severity | Count | R1 Status |
|----------|-------|-----------|
| Critical/Blocking | 4 | Open |
| Non-Blocking | 8 | Open |
| Observations | 8 | Informational |
| Missing Tests | 3 | Open |

---

## Required Changes Before 22a.6

1. **B-1:** Convert `TeeBodySource._aborted`, `_completedNaturally`, `_trailersLoaded` to `int` with `Interlocked.Exchange`/`Volatile.Read`. Make `Abort()` idempotent via `Interlocked.Exchange(ref _aborted, 1) != 0`.
2. **B-2:** Convert `ObservedResponseBodySource._aborted` to `int` with `Interlocked.Exchange`. Make `Abort()` idempotent.
3. **B-3:** Replace `_error = _error ?? ex` with `Interlocked.CompareExchange(ref _error, ex, null)` in `ObservedResponseBodySource`.
4. **B-4:** Add cache tee mid-stream write failure test.

---

## Review History

| Round | Date | Verdict | Key Actions |
|-------|------|---------|-------------|
| 1 | 2026-03-21 | BLOCKED | 4 blocking, 8 non-blocking, 8 observations, 3 test gaps |
