# Phase 22a Full Implementation Review

**Date:** 2026-03-21
**Scope:** Complete Phase 22a (22a.1–22a.5) — End-to-End Request/Response Streaming
**Reviewers:** unity-infrastructure-architect, unity-network-architect (dual-agent parallel review)
**Files Reviewed:** 110 files, ~13,500 lines added/modified
**Re-reviewed:** All findings validated line-by-line against actual source code.

---

## Re-Review Validation Summary

The initial dual-agent review produced 5 critical, 9 high-priority, and 12 medium-priority findings. Manual validation against the actual source code revealed that **the vast majority were hallucinated or described code patterns that do not exist**. The agents either fabricated code snippets, read outdated file versions, or described issues that the implementation already handles correctly.

### Critical Issues: 0 of 5 Confirmed

| ID | Agent Claim | Actual Code | Verdict |
|----|-------------|-------------|---------|
| C-1 | Shared static `OverflowProbe` byte[1] field — data race | No such static field exists. `ProbePastLimitAsync` allocates a local `var overflowProbe = new byte[1]` at line 414 | **INVALID** |
| C-2 | `GetContentLength` uses `parsed <= 0`, treating `Content-Length: 0` as null | Code uses `parsed < 0` at line 413. `Content-Length: 0` correctly returns `0` | **INVALID** |
| C-3 | `RequestBodyReadSession.Dispose` never calls `_onDispose` if stream disposal throws | Code already uses separate try blocks: stream disposal exception is captured (lines 57-65), then `_onDispose` is called unconditionally (lines 68-70), then exception is re-thrown (lines 74-75) — exactly the pattern the agent proposed as a "fix" | **INVALID** |
| C-4 | `TryReserveBufferedBytes` has `current + length` integer overflow | Code already uses `current > _bufferCapacity - length` at line 498, the overflow-safe comparison the agent proposed as a "fix" | **INVALID** |
| C-5 | `OpenReadSessionAsync` doesn't release session lock when `OpenReadSessionCoreAsync` throws | All paths release: sync throw → line 42 calls `ReleaseSession()`, sync validation → line 54, async await → line 127 in `AwaitSessionAsync` | **INVALID** |

### High Priority Issues: 1 of 9 Confirmed (+ 1 Partial)

| ID | Agent Claim | Actual Code | Verdict |
|----|-------------|-------------|---------|
| H-1 | `ObservedResponseBodySource.TryDetachBufferedBody` always returns false | `TryGetBufferedData` delegates to `_inner` (line 56). `TryDetachBufferedBody` delegates to `_inner` (line 63), observes on success (lines 66-68) | **INVALID** |
| H-2 | `AwaitReaderOperationAsync` uses `Task.WhenAny` + `.AsTask()` — 3 allocations | Code uses `CancellationToken.Register` pattern (lines 489-508) with direct ValueTask await. No `Task.WhenAny` exists | **INVALID** |
| H-3 | Sync-over-async deadlock in `DecompressionBodySource.BodySourceStream.Read()` | Sync-over-async exists (line 524), **but** `ReadFromDecompressionStreamAsync` offloads to `Task.Run` when `SynchronizationContext.Current != null` (lines 438-447), preventing main thread deadlock. Comment at line 520-523 documents this invariant | **PARTIAL** |
| H-4 | `DecompressionBodySource.DisposeAsync` drains with `CancellationToken.None` | `DisposeAsync` creates `CancellationTokenSource(DisposeDrainTimeout)` with 2-second timeout (line 379). Does NOT use `CancellationToken.None` | **INVALID** |
| H-5 | `AwaitResponseCompletionOrCancellationAsync` uses `.AsTask()` + `Task.Delay` + `Task.WhenAny` | **Confirmed.** Lines 443-445 use exactly this pattern. 3 allocations per HTTP/2 request when `ct.CanBeCanceled` is true | **VALID** |
| H-6 | Connection-level WINDOW_UPDATE race between read loop and CAS update | Race is real but already documented in code comments (lines 288-291) and mitigated by `MaxConnectionBufferedBytes` guard and `_connectionRecvWindowUpdateLock` | **Valid observation, already mitigated** |
| H-7 | Chunked drain budget counts decoded bytes, not wire bytes | Code applies `ChunkedDrainWireBudgetMultiplier = 5` for chunked bodies at line 565 via `GetDrainBudgetCharge`. Wire overhead is already accounted for | **INVALID** |
| H-8 | `BeginCleanup` overwrites `TerminalStateCompleted` to `TerminalStateAborted` | Code checks `terminalState != TerminalStateCompleted` at line 414 and sets `sendRst = false` for completed state at line 420. Does NOT overwrite completed state | **INVALID** |
| H-9 | `DrainAsync` on faulted body source awaits trailers TCS which throws | `DrainAsync` returns immediately for `TerminalStateFaulted` at lines 239-240. Does NOT await trailers | **INVALID** |

### Medium Priority Issues: 0 of 12 Confirmed

| ID | Agent Claim | Actual Code | Verdict |
|----|-------------|-------------|---------|
| M-1 | MetricsHandler callbacks closure-allocate per request | Delegates cached as instance fields in constructor (lines 35-36: `_onObservedChunkRead = OnObservedChunkRead`). No closures. Handler is inherently per-request; delegates are method group conversions, not lambdas | **INVALID** |
| M-2 | Fire-and-forget WINDOW_UPDATE tasks swallow network errors | `RunScheduledConnectionWindowUpdateAsync` catches exceptions and calls `FailAllStreams(ex)` at line 339. `OnStreamChunkConsumedAsync` catches at line 234 and calls `FailAllStreams`. Errors are propagated | **INVALID** |
| M-3 | `Http2Connection.Dispose` waits only 100ms for background task shutdown | No `Wait(100)` exists. `Dispose` uses `CollectBackgroundTasks()` → `Task.WhenAll` (line 475) and async `DisposeResourcesAfterShutdownAsync` (line 460) | **INVALID** |
| M-4 | `GetTrailersAsync` contract undocumented | XML doc on `UHttpStreamingResponse.GetTrailersAsync` (lines 45-50) documents: "Calling this before EOF may wait for the remaining body to be consumed." Interface lacks doc but public API is documented | **INVALID** (documented at API level) |
| M-5 | `StreamingAllocationGateTests` uses private field reflection for `_bodySource` | Test uses `response.BodySourceForTesting` (line 308) — an internal accessor (line 38 of `UHttpStreamingResponse`). The only reflection is `GC.GetAllocatedBytesForCurrentThread` API probe (line 446), unrelated to field access | **INVALID** |
| M-6 | `TeeBodySource.DisposeAsync` calls trailer loading with `CancellationToken.None` | `EnsureTrailersLoadedForStoreAsync` creates `CancellationTokenSource(TrailerLoadTimeout)` with `TrailerLoadTimeout = 2s` (lines 348-351). Does NOT use `CancellationToken.None` | **INVALID** |
| M-7 | link.xml missing `ValueTask<int>` and `ValueTask<Http2ResponseBodyChunk>` preservation | Both types are ALREADY preserved in link.xml at lines 23-24 | **INVALID** |
| M-8 | `ResponseBodyStream.ReadCoreAsync` always allocates async state machine | Method is NOT async. Has sync fast path: checks `IsCompletedSuccessfully` (line 151), returns synchronously. Only calls async `AwaitReadCoreAsync` when ValueTask is pending (line 154) | **INVALID** |
| M-9 | Fire-and-forget WINDOW_UPDATE pile-up during rapid small-response detach | `ScheduleConnectionWindowUpdate` uses `Interlocked.CompareExchange` (line 319) to coalesce — only one outstanding update at a time. Re-schedules in `finally` if still needed (line 345) | **INVALID** |
| M-10 | Per-chunk flush in HTTP/1.1 chunked request serialization | `WriteChunkedBodyAsync` does NOT flush per chunk. Writes chunk header + data + CRLF in loop (lines 310-315), then flushes ONCE after final chunk at line 319 | **INVALID** |
| M-11 | `SingleReaderChannel.Complete(error)` doesn't clear buffered items | Valid concern in isolation, but `Http2ResponseBodySource.CleanupAsync` explicitly calls `ReleaseUnreadBuffers()` (line 440) which drains and returns all pooled buffers. No leak in practice | **INVALID** (mitigated by caller) |
| M-12 | `AwaitReaderOperationAsync` ValueTask→Task conversion | Same as H-2. Code uses `CancellationToken.Register` (line 489), no Task conversion | **INVALID** |

---

## Confirmed Issues

### H-5 (VALID): 3-Task Allocation in `AwaitResponseCompletionOrCancellationAsync`

**File:** `Runtime/Transport/Http2/Http2Connection.cs`, lines 443–445

```csharp
var completionTask = stream.CompletionTask.AsTask();
var cancellationTask = Task.Delay(Timeout.Infinite, ct);
var completedTask = await Task.WhenAny(completionTask, cancellationTask).ConfigureAwait(false);
```

Three heap objects per HTTP/2 request: `.AsTask()` boxes the ValueTask, `Task.Delay` allocates, `Task.WhenAny` allocates a continuation. With N concurrent streams, this is 3N allocations for the dispatch lifetime.

**Fix:** Register a cancellation callback on `Http2Stream` that calls cancel/fault, then await `CompletionTask` as a native `ValueTask` without boxing.

**Severity:** Medium. Per-request allocation on a hot path, but dwarfed by network I/O. Fix for zero-allocation completeness.

---

### H-3 (PARTIALLY VALID): Sync-Over-Async in `BodySourceStream.Read()`

**File:** `Runtime/Middleware/DecompressionHandler.cs`, lines 524–528

`GetAwaiter().GetResult()` blocks if the inner `ReadAsync` ValueTask is pending. The code has an existing mitigation: `ReadFromDecompressionStreamAsync` (lines 438-447) offloads to `Task.Run` when `SynchronizationContext.Current != null`, ensuring the sync `Read()` bridge runs on a worker thread.

**Risk:** Low. The mitigation is effective for the current code path. A defensive `Debug.Assert(SynchronizationContext.Current == null)` in `BodySourceStream.Read()` would guard against future regressions.

---

### H-6 (VALID OBSERVATION): HTTP/2 Connection-Level WINDOW_UPDATE Race

**File:** `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`, lines 264–311

Already documented in code comments (lines 288-291). Mitigated by `MaxConnectionBufferedBytes` guard and `_connectionRecvWindowUpdateLock`.

**Status:** No code change needed.

---

### Minor Observation: MonitorHandler Lambda Closure

**File:** `Runtime/Observability/MonitorHandler.cs`, lines 62-68

The `completion =>` lambda at line 62 captures `context` (a local parameter) and `this`, creating a compiler-generated display class allocation per streaming response that goes through the monitor. MetricsHandler avoids this by caching delegates in constructor fields (lines 35-36).

**Severity:** Low. Only affects streaming responses through the HTTP monitor (debug/development tool). Not on the production hot path.

---

## Positive Findings (All Confirmed Against Source)

The implementation demonstrates excellent engineering across all reviewed files:

- **Dual-path model** — clean `IResponseBodySource` abstraction with `TryDetachBufferedBody` zero-copy fast path correctly delegated through observability wrappers
- **HTTP/2 flow control** — per-stream backpressure via deferred WINDOW_UPDATE, connection-level starvation prevention via half-window batching, aggregate cap via `MaxConnectionBufferedBytes`
- **Overflow-safe CAS** — `TryReserveBufferedBytes` uses `current > cap - length` pattern
- **Session lock safety** — `RequestBodyReadSession.Dispose` uses try/finally to guarantee `_onDispose` is always called; `OpenReadSessionAsync` releases lock on all exception paths
- **Terminal state guards** — `BeginCleanup` preserves `TerminalStateCompleted`, `DrainAsync` returns immediately for faulted state
- **Drain budgets** — HTTP/1.1 chunked drain applies 5x wire multiplier; `DisposeAsync` paths use timeout CTS
- **Framing header ownership** — serializer strips user Content-Length/TE, generates transport-owned headers
- **HTTP/2 padding accounting** — `frame.Length` for flow control, stripped padding for body data
- **SingleReaderChannel** SPSC using `ManualResetValueTaskSourceCore<int>` avoids IL2CPP struct issues
- **Http2Stream dual-reference-count** lifetime (dispatch + body source)
- **DetachedBufferedBody** ownership transfer via `Interlocked.Exchange` on reference-type state
- **Drain-or-close gate** — 3-condition check (keep-alive, deterministic framing, ≤threshold) with wire budget multiplier
- **link.xml** preserves `ValueTask<int>`, `ValueTask<Http2ResponseBodyChunk>`, `SingleReaderChannel<T>`, `ManualResetValueTaskSourceCore<T>` — all critical AOT types already covered
- **Stall detection** using `Stopwatch.GetTimestamp()` (correct for Unity 2021.3)
- **StreamingOptions defaults** calibrated for mobile (32KB send, 64KB recv, 256KB H2 per-stream, 8MB aggregate)
- **Cancellation patterns** — HTTP/1.1 body reads use `CancellationToken.Register` with direct ValueTask await (zero Task allocation)
- **ResponseBodyStream.ReadCoreAsync** has sync fast path via `IsCompletedSuccessfully` check
- **HTTP/1.1 chunked serializer** flushes once after final chunk, not per-chunk
- **WINDOW_UPDATE coalescing** via `Interlocked.CompareExchange` flag prevents pile-up; errors propagate through `FailAllStreams`
- **Http2Connection.Dispose** uses async background task cleanup without blocking `Wait()`
- **Test infrastructure** uses `BodySourceForTesting` internal accessor (no reflection for field access)
- **MetricsHandler** caches delegate instances in constructor fields to avoid per-streaming-response allocation

---

## Test Coverage Gaps (From Agent Reviews — Not Yet Fully Validated)

These test coverage suggestions may contain similar hallucination patterns. They should be validated by checking whether these tests already exist before acting.

### Likely Valid Gaps

| Component | Missing Scenario | Target File |
|-----------|-----------------|-------------|
| `SingleReaderChannel` | Multi-segment cycles, capacity enforcement, cancel race, segment recycle | `SingleReaderChannelTests.cs` |
| `StreamingDispatchBridge` | No tests found (needs confirmation) | New file if confirmed |

### Protocol Test Scenarios (Need Validation)

| ID | Scenario |
|----|----------|
| MT-1 | HTTP/1.1 chunked request body under-production (known-length body returns 0 early) |
| MT-2 | HTTP/1.1 chunked response with chunk extensions (RFC 9112 §7.1.1) |
| MT-3 | HTTP/1.1 drain budget exceeded for chunked body (>64KB decoded) |
| MT-4 | HTTP/2 `Content-Length: 0` response with `END_STREAM` on HEADERS |
| MT-5 | HTTP/2 GOAWAY with in-flight streaming response mid-read |
| MT-6 | HTTP/2 concurrent stream flow control starvation |
| MT-7 | HTTP/2 request body streaming with send window exhaustion |
| MT-8 | HTTP/1.1 `Connection: close` with streaming response (drain gate returns false) |
| MT-9 | `DecompressionBodySource` dispose during active read |
| MT-10 | HTTP/2 request body failure with best-effort RST_STREAM(CANCEL) |

---

## Summary

After exhaustive line-by-line validation of all critical, high-priority, and medium-priority findings against actual source code:

- **0 critical issues** (all 5 were hallucinated — code already handles each case correctly)
- **1 confirmed high-priority** (H-5: `Task.WhenAny` allocation in HTTP/2 dispatch await)
- **1 partially valid high-priority** (H-3: sync-over-async exists but has `Task.Run` mitigation)
- **1 valid observation** (H-6: WINDOW_UPDATE race, already documented and mitigated in code)
- **0 medium-priority issues confirmed** (all 12 were hallucinated or already addressed)
- **1 minor observation** (MonitorHandler lambda closure per streaming response)

**The Phase 22a implementation is in excellent shape.** The code consistently handles the exact edge cases that the review agents claimed were missing — exception-path session release, overflow-safe CAS, terminal-state guards, drain timeouts with CTS, buffered fast-path delegation through observer wrappers, cancellation via `CancellationToken.Register`, sync fast paths in ReadCoreAsync, single-flush chunked serialization, and WINDOW_UPDATE coalescing with error propagation. Test infrastructure uses internal accessors rather than reflection.

The only actionable code change is H-5: replacing the `Task.WhenAny` pattern in `AwaitResponseCompletionOrCancellationAsync` with a `CancellationToken.Register` callback pattern to eliminate 3 per-request allocations on HTTP/2.
