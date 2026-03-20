# Phase 22a.3 Review: HTTP/2 Streaming Send/Receive

## Review Round 1 (Initial Review)

**Review date:** 2026-03-19
**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Verdict:** BLOCKED — 5 critical, 4 high, 5 medium, 4 low issues identified

---

## Implementation Completeness

All 8 spec steps are implemented:

| Step | Component | Status |
|------|-----------|--------|
| 1 | Producer-fed DATA send path (buffered fast path + streaming path) | Complete |
| 2 | Bounded per-stream receive queue (`SingleReaderChannel<Http2ResponseBodyChunk>`) | Complete |
| 3 | Decoupled flow control (connection WINDOW_UPDATE immediate, per-stream deferred) | Complete |
| 4 | `Http2ResponseBodySource` implementing `IResponseBodySource` | Complete |
| 5 | Post-RST_STREAM DATA frame handling | Complete |
| 6 | Abort / early-dispose protocol | Complete |
| 7 | Stall detection via maintenance loop | Complete |
| 8 | Streaming trailers completion | Complete |

Supporting items verified:
- `link.xml` updated for `SingleReaderChannel<Http2ResponseBodyChunk>` and `Http2ResponseBodySource`
- `Http2ConnectionTestExtensions.SendStreamingRequestAsync` extension method
- 6 streaming response tests covering incremental read, deferred WINDOW_UPDATE, abort/dispose, cancellation, zero-body, stall detection
- Streaming request body tests (known-length stream, unknown-length factory)

---

## CRITICAL Issues

### C-1: `DrainAsync` is a no-op — violates `IResponseBodySource` contract

**File:** `Runtime/Transport/Http2/Http2ResponseBodySource.cs`, lines 113–118
**Found by:** Both agents

```csharp
public ValueTask DrainAsync(CancellationToken ct)
{
    ThrowIfDisposed();
    ct.ThrowIfCancellationRequested();
    return default;
}
```

HTTP/1.1 body source drains fully; this one returns immediately. If middleware calls `DrainAsync` expecting the body to be consumed, the stream stays open with unread DATA in the queue, eventually triggering stall detection RST_STREAM. Silent behavioral regression versus HTTP/1.1.

**Fix:** Implement meaningful drain behavior — either read-and-discard all queued chunks (sending per-stream WINDOW_UPDATE as it goes) or send RST_STREAM(CANCEL) and complete immediately (abort drain). The latter is simpler and correct for HTTP/2 since connection reuse is not per-stream.

---

### C-2: `_bufferedBytes` partial-read accounting mismatch

**File:** `Runtime/Transport/Http2/Http2ResponseBodySource.cs`, lines 249–271 (`ReadCurrentChunkAsync`)
**Found by:** Infrastructure architect

`OnResponseBytesConsumed` is called with `count` (bytes copied to caller's buffer) on each partial read, and `Interlocked.Add(ref _bufferedBytes, -count)` decrements per partial read. But the `ArrayPool` buffer is held until `CompleteCurrentChunkAsync`. This means `_connectionBufferedBytes` drops below actual memory usage prematurely, defeating the `MaxConnectionBufferedBytes` guarantee — the gate may allow connection-level WINDOW_UPDATE when aggregate buffered memory is actually above the limit.

**Fix:** Decrement `_bufferedBytes` and call `OnResponseBytesConsumed` once per chunk in `CompleteCurrentChunkAsync` (when the buffer is actually returned), not per partial read.

---

### C-3: `StreamLevelRecvWindow_SendsStreamWindowUpdate` test contradicts deferred model

**File:** `Tests/Runtime/Transport/Http2/Http2FlowControlTests.cs`, lines 641–722
**Found by:** Infrastructure architect

This pre-existing test sends 3 × 16384-byte DATA frames and expects both connection-level and stream-level WINDOW_UPDATE immediately after DATA arrival. Under the new deferred per-stream WINDOW_UPDATE model, stream-level WINDOW_UPDATE must NOT be sent until the consumer reads those bytes. The test does not drive the consumer before reading the expected frames.

**Fix:** Update the test to drive the consumer (read body bytes) before expecting stream-level WINDOW_UPDATE, or split into separate tests for immediate connection-level and deferred stream-level behavior.

---

### C-4: Abort/enqueue race in `CleanupAsync`

**File:** `Runtime/Transport/Http2/Http2ResponseBodySource.cs`, lines 300–320
**Found by:** Infrastructure architect

`_queue.Complete(disposedError)` is called after `ReleaseUnreadBuffers`. Between `_terminalState` being set to Aborted and `_queue.Complete` being called, a chunk can be written to the queue after `ReleaseUnreadBuffers` has finished draining but before `Complete` prevents further writes. This chunk sits in the queue permanently, holding a pooled buffer that is never returned.

**Fix:** Call `_queue.Complete(disposedError)` before `ReleaseUnreadBuffers`, not after. Then drain. This ensures `TryWrite` fails for any in-flight enqueue, and `ReleaseUnreadBuffers` picks up any chunks that squeezed in before `Complete`.

---

### C-5: `_connectionBufferedBytes` tracks data-only bytes, not flow-controlled bytes

**File:** `Runtime/Transport/Http2/Http2ResponseBodySource.cs`, line 180 (`OnResponseBytesBuffered(length)`)
**Found by:** Network architect

`OnResponseBytesBuffered(length)` uses data length excluding padding, but connection recv window was decremented by `flowControlledLength` (includes padding per RFC 9113 Section 6.1). The `MaxConnectionBufferedBytes` gate is slightly understated — padding bytes are consumed from the connection recv window but never tracked in `_connectionBufferedBytes`.

**Fix:** Track `flowControlledLength` instead of `length` in `_connectionBufferedBytes`, or document as intentional simplification since padding is rare in practice.

---

## HIGH Issues

### H-1: `LastConsumptionTick` not volatile — torn reads on 32-bit IL2CPP ARM

**File:** `Runtime/Transport/Http2/Http2ResponseBodySource.cs`, line 70
**Found by:** Both agents

`long` auto-property written by consumer thread (line 263), read by maintenance loop thread (line 246). On 32-bit platforms (older Android IL2CPP), reading a `long` is not atomic without `Interlocked.Read`, causing torn reads leading to incorrect stall detection.

**Fix:** Change to backing field with `Interlocked.Exchange` on set and `Interlocked.Read` on get.

---

### H-2: `_cleanupTask` race in `BeginCleanup`

**File:** `Runtime/Transport/Http2/Http2ResponseBodySource.cs`, lines 288–298
**Found by:** Both agents

Thread A enters `BeginCleanup`, sets `_disposed = 1`, begins `CleanupAsync`, but hasn't assigned `_cleanupTask`. Thread B enters, sees `_disposed == 1`, reads null `_cleanupTask`, returns `Task.CompletedTask`. Caller thinks dispose is done while cleanup (including `ReleaseBodySourceLifetime`) is still running.

**Fix:** Use a lazy pattern or assign `_cleanupTask` before starting the async method. Alternatively, use `Interlocked.CompareExchange` on `_cleanupTask` to ensure the second caller awaits the same task.

---

### H-3: `MaybeSendConnectionWindowUpdateAsync` increments `_connectionRecvWindow` after frame send

**File:** `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`, lines 258–302
**Found by:** Infrastructure architect

Between `SendWindowUpdateAsync` and `Interlocked.Add(ref _connectionRecvWindow, increment)`, `HandleDataFrameAsync` could read the pre-update `_connectionRecvWindow` and incorrectly trigger `FlowControlError` even though the wire window has been expanded.

**Fix:** Increment `_connectionRecvWindow` before sending the WINDOW_UPDATE frame. If the send fails, the connection is already failing, so the stale counter doesn't matter.

---

### H-4: `SingleReaderChannel` capacity set to 262144 items — misleading and wasteful in pathological cases

**File:** `Runtime/Transport/Http2/Http2ResponseBodySource.cs`, line 55
**Found by:** Both agents

`_queue = new SingleReaderChannel<Http2ResponseBodyChunk>(Math.Max(1, _bufferCapacity))` creates a channel with capacity 262144 items (bytes treated as item count). The byte-level `TryReserveBufferedBytes` is the real gate; the channel item limit is never hit. In pathological cases (many 1-byte DATA frames), 262144 items would require 8192 segment allocations.

**Fix:** Pass a reasonable item count like `_bufferCapacity / 1024` or `int.MaxValue` to make intent clear that byte-level backpressure is the real constraint.

---

## MEDIUM Issues

### M-1: `BufferFull` sends `RST_STREAM(FlowControlError)` — semantically incorrect

**File:** `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs`, lines 186–195
**Found by:** Infrastructure architect

The server didn't violate flow control — the client is resetting because its internal buffer is overwhelmed. Per RFC 9113, `FLOW_CONTROL_ERROR` means the peer exceeded the flow control window. `CANCEL` or `INTERNAL_ERROR` would be more semantically accurate.

---

### M-2: Padding byte leak on abort

**File:** `Runtime/Transport/Http2/Http2ResponseBodySource.cs`, `ReleaseUnreadBuffers`
**Found by:** Network architect

When `ReleaseUnreadBuffers` runs during cleanup, connection recv window permanently loses padding bytes from aborted streams (connection window was decremented by `flowControlledLength` but released bytes only track data length). Theoretical concern — padding is extremely rare in practice.

---

### M-3: `CompleteCurrentChunkAsync` called with `CancellationToken.None` on exhausted-chunk path

**File:** `Runtime/Transport/Http2/Http2ResponseBodySource.cs`, line 256
**Found by:** Infrastructure architect

When `remaining <= 0` (chunk already exhausted from prior partial read), `CompleteCurrentChunkAsync(CancellationToken.None)` is called instead of forwarding the caller's `ct`. The per-stream WINDOW_UPDATE will complete regardless of consumer cancellation. Harmless (RST_STREAM follows shortly), but a code smell.

---

### M-4: `GetTrailersAsync` allocates `TaskCompletionSource<bool>` on every pending call

**File:** `Runtime/Transport/Http2/Http2ResponseBodySource.cs`, lines 134–144
**Found by:** Infrastructure architect

The cancellation bridge uses a new `TaskCompletionSource<bool>` per `GetTrailersAsync` call. For gRPC-style responses where trailers are always present, this runs once and is acceptable. For callers that call defensively multiple times, it allocates repeatedly.

---

### M-5: Connection recv window oscillates 32K–65K; suboptimal for high-throughput

**File:** `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`, lines 258–290
**Found by:** Network architect

Connection-level recv window uses `DefaultInitialWindowSize` (65535) as reference, which is correct per RFC 9113 Section 6.9.1. But the window never grows beyond 65535. A larger initial connection window (via WINDOW_UPDATE stream 0 during `InitializeAsync`) would reduce WINDOW_UPDATE frame overhead for high-throughput scenarios with many concurrent streams.

---

## LOW Issues

### L-1: `_stallTimeoutMs` not volatile

**File:** `Runtime/Transport/Http2/Http2Connection.cs`, line 91
**Found by:** Infrastructure architect

`long` field read in maintenance loop without `Volatile.Read`. On ARM64 IL2CPP, writes (including test reflection) may not be visible. Use `Volatile.Read(ref _stallTimeoutMs)` on the read side.

---

### L-2: Stale `link.xml` entry for `SingleReaderChannel<ReadOnlyMemory<byte>>`

**File:** `Runtime/Transport/link.xml`, line 31
**Found by:** Infrastructure architect

`SingleReaderChannel` is now instantiated with `Http2ResponseBodyChunk`, not `ReadOnlyMemory<byte>`. If no other code uses the old instantiation, this entry increases IL2CPP binary size unnecessarily.

---

### L-3: Stall test uses reflection to mutate `_stallTimeoutMs`

**File:** `Tests/Runtime/Transport/Http2/Http2ConnectionTests.StreamingResponses.cs`, lines 319–324
**Found by:** Infrastructure architect

`BindingFlags.NonPublic` reflection to set `_stallTimeoutMs` is fragile and won't work under IL2CPP if the field is stripped. Consider exposing via `Http2Options.StallTimeoutMs` or a test-only constructor parameter.

---

### L-4: `_recentlyResetStreams` grows unbounded between 5s maintenance cycles

**File:** `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`, lines 336–346
**Found by:** Infrastructure architect

`ConcurrentDictionary<int, long>` holds every reset stream ID until cleanup runs (every 5s with 60s cutoff). In pathological cases (1000 resets in 5s), the dictionary holds 1000 entries. A bounded ring buffer would be more appropriate but the current approach is acceptable for realistic workloads.

---

## Missing Test Coverage

| # | Gap | Spec Criterion |
|---|-----|----------------|
| T-1 | No test for `MaxConnectionBufferedBytes` gate (connection WINDOW_UPDATE suppressed when aggregate exceeds limit) | "Aggregate memory across all streams bounded by MaxConnectionBufferedBytes" |
| T-2 | No concurrent-stream test (slow consumer on stream 1 doesn't block DATA delivery to stream 3) | "One slow consumer does not block DATA delivery for other streams" |
| T-3 | No test for `BufferFull` → `RST_STREAM` path | "Read loop never blocks on per-stream buffer operations" |

---

## Pass Areas (both reviewers agree)

- **Decoupled WINDOW_UPDATE model:** Architecturally sound. Connection-level immediate, per-stream deferred. Correct design for multiplexed streaming.
- **Dual-lifetime ref-counting:** `_lifetimeRefCount` on `Http2Stream` (dispatch + body source) correctly ensures stream returns to pool only when both owners release.
- **CAS-based buffer reservation:** `TryReserveBufferedBytes` spin-loop CAS is correct and non-blocking.
- **Post-RST DATA handling:** Connection window decremented, no delivery, `_recentlyResetStreams` suppresses redundant RST_STREAM. Correct per RFC 9113 Section 5.1.
- **Streaming request body send path:** Known-length and unknown-length sub-paths correct. Lazy ArrayPool buffer rental. Empty END_STREAM frame for unknown-length EOF.
- **Zero-body response pre-completion:** HEADERS+END_STREAM creates pre-completed body source where `ReadAsync` returns 0 immediately.
- **Trailing HEADERS:** State transitions and trailer delivery via `TaskCompletionSource` correct.
- **Stall detection:** Coarse-grained maintenance loop scan (no per-stream Timer), correct per spec.
- **Cancel/Fail routing through body source:** When `ResponseBodySource` exists, `Cancel`/`Fail` correctly fault the body source instead of completing the stream directly.
- **Cancellation registration cleared after streaming response starts:** `TryStartResponse` disposes `CancellationRegistration` for streaming requests, preventing request-level cancellation from aborting owned body.
- **Padding handling:** `flowControlledLength = frame.Length` (includes padding), data extraction strips padding. Correct per RFC 9113 Section 6.1.
- **Protocol correctness (RFC 9113):** Connection WINDOW_UPDATE on half-window threshold, per-stream deferred until consumption, SETTINGS_INITIAL_WINDOW_SIZE adjustments for send windows, DATA before HEADERS → RST_STREAM(PROTOCOL_ERROR), GOAWAY stream fail above lastStreamId.

---

## Platform Compatibility

| Component | Editor | Standalone | iOS IL2CPP | Android IL2CPP | WebGL |
|-----------|--------|------------|------------|----------------|-------|
| Http2ResponseBodySource | OK | OK | Needs H-1 fix | Needs H-1 fix | N/A (excluded) |
| SingleReaderChannel<Http2ResponseBodyChunk> | OK | OK | OK (link.xml covers) | OK (link.xml covers) | N/A |
| ManualResetValueTaskSourceCore | OK | OK | OK (link.xml covers) | OK (link.xml covers) | N/A |
| SendStreamingDataAsync | OK | OK | OK | OK | N/A |
| Stall detection | OK | OK | Needs H-1, L-1 fix | Needs H-1, L-1 fix | N/A |

---

## Summary Table

| Severity | Count | R1 Status | R2 Status |
|----------|-------|-----------|-----------|
| Critical | 5 | Open | All Fixed |
| High | 4 | Open | All Fixed |
| Medium | 5 | Open | All Fixed |
| Low | 4 | Open | All Fixed |
| Missing Tests | 3 | Open | All Added |
| R2 New (Medium) | 3 | — | 2 follow-up, 1 deferred |
| R2 New (Low) | 4 | — | 1 follow-up, 3 deferred |

---

## Required Changes Before 22a.4

~~1. **C-1:** Implement `DrainAsync` (abort-drain via RST_STREAM recommended)~~ — Fixed R2
~~2. **C-2:** Fix `_bufferedBytes` decrement timing — once per chunk completion, not per partial read~~ — Fixed R2
~~3. **C-3:** Update `StreamLevelRecvWindow_SendsStreamWindowUpdate` for deferred model~~ — Fixed R2
~~4. **C-4:** Reorder `_queue.Complete` before `ReleaseUnreadBuffers` in `CleanupAsync`~~ — Fixed R2
~~5. **C-5:** Track `flowControlledLength` in `_connectionBufferedBytes` (or document simplification)~~ — Fixed R2
~~6. **H-1:** Use `Interlocked.Exchange`/`Read` for `LastConsumptionTick` (32-bit ARM safety)~~ — Fixed R2
~~7. **H-2:** Fix `_cleanupTask` race in `BeginCleanup`~~ — Fixed R2
~~8. **H-3:** Increment `_connectionRecvWindow` before sending WINDOW_UPDATE frame~~ — Fixed R2
~~9. **H-4:** Pass reasonable item count to `SingleReaderChannel` constructor~~ — Fixed R2
~~10. **T-1/T-2/T-3:** Add missing tests for `MaxConnectionBufferedBytes`, concurrent streams, and `BufferFull` path~~ — Fixed R2

### Round 2 Follow-ups (before 22a.4)

1. **N-2 (Medium):** Guard `_currentChunk` in `ReleaseUnreadBuffers` against concurrent access from maintenance-loop `Abort()` — potential double ArrayPool return and accounting underflow
2. **NEW-2 (Medium):** Convert recursive `ReadAsync` call in `ReadCurrentChunkAsync` (zero-remaining branch) to iterative `continue` in outer loop — latent infinite-recursion risk
3. **NEW-8 (Low):** Use `Volatile.Read` for `ResponseBodySource` reference in `Http2Stream.IsResponseCompleted` — ARM IL2CPP visibility gap

### Round 2 Deferred (tracked, non-blocking)

- **N-3 (Low):** `SemaphoreSlim` instances (`_connectionRecvWindowUpdateLock`, `_writeLock`, `_windowWaiter`) not disposed in `Http2Connection.Dispose()` — GC handles, bounded lifecycle
- **NEW-1 (Medium):** `_bufferedBytes` (data bytes) vs `_connectionBufferedBytes` (flow-controlled bytes) naming ambiguity — add documenting comment
- **NEW-3 (Medium):** `Cancel` faults body with `OperationCanceledException`, `Fail` with `UHttpException` — asymmetric but semantically correct; document the taxonomy
- **NEW-4 (Low):** `TryReserveBufferedBytes` CAS loop unnecessary since read loop is single-writer — minor efficiency

---

## Spec Compliance Notes

**Mid-connection `SETTINGS_INITIAL_WINDOW_SIZE` and deferred accounting:** The current implementation adjusts send windows correctly. The deferred per-stream receive accounting stores `flowControlledLength` per chunk (reflecting original frame length at receipt time) and sends WINDOW_UPDATE with that value when consumed. A mid-connection decrease could theoretically cause the receiver window to go negative if WINDOW_UPDATE restores bytes at the old rate. This is a deferred concern tracked for future validation.

---

## Review Round 2 (Verification Pass)

**Review date:** 2026-03-20
**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Verdict:** CONDITIONAL PASS — All Round 1 issues fixed. 3 follow-up items before 22a.4, 4 deferred.

### Round 1 Fix Verification

| Issue | Verdict | Notes |
|-------|---------|-------|
| C-1 DrainAsync | PASS | `BeginCleanup(sendRst: ...)` correctly called |
| C-2 `_bufferedBytes` accounting | PASS | Per-chunk decrement, `FlowControlledLength` for window |
| C-3 StreamLevelRecvWindow test | PASS | Deferred model tested with streaming path |
| C-4 Abort/enqueue race ordering | PASS | `Complete()` before `ReleaseUnreadBuffers()` |
| C-5 `_connectionBufferedBytes` tracking | PASS | Uses `flowControlledLength` throughout |
| H-1 `_lastConsumptionTick` ARM safety | PASS | `long` + `Interlocked.Read`/`Exchange` |
| H-2 `_cleanupTask` lock pattern | PASS | Full `lock(_cleanupGate)` guard |
| H-3 `Interlocked.Add` before send | PASS | Window incremented before wire frame |
| H-4 Channel capacity `int.MaxValue` | PASS | Backpressure via `TryReserveBufferedBytes` |
| M-1 BufferFull RST uses Cancel | PASS | `Http2ErrorCode.Cancel` in read loop |
| L-1 `Volatile.Read` for `_stallTimeoutMs` | PASS | Correct on line 331 |
| L-2 Stale link.xml entry | PASS | Correct instantiation hint |
| L-3 Test overrides not via reflection | PASS | `internal` properties used directly |
| L-4 RecentlyResetStreams hard limit | PASS | Soft/hard two-tier trimming correct |
| T-1 MaxConnectionBufferedBytes test | PASS | Correct suppression behavior tested |
| T-2 Concurrent multi-stream test | PASS | Stream isolation validated |
| T-3 BufferFull RST test | PASS | Error code and body fault both asserted |

### New Issues Found in Round 2

| ID | Severity | Source | Description |
|----|----------|--------|-------------|
| N-2 | Medium | Network | `_currentChunk` accessed without synchronization between consumer ReadAsync and maintenance-loop `Abort()` — potential double ArrayPool return and accounting underflow |
| N-3 | Low | Network | `SemaphoreSlim` instances not disposed in `Http2Connection.Dispose()` |
| NEW-1 | Medium | Infra | `_bufferedBytes` (data bytes) vs `_connectionBufferedBytes` (flow-controlled bytes) naming ambiguity — document the distinction |
| NEW-2 | Medium | Infra | `ReadCurrentChunkAsync` recursion via `ReadAsync` on zero-remaining — safe in practice, convert to iterative loop |
| NEW-3 | Medium | Infra | `Cancel` vs `Fail` exception type asymmetry (`OperationCanceledException` vs `UHttpException`) — semantically correct but needs documentation |
| NEW-4 | Low | Infra | `TryReserveBufferedBytes` CAS loop unnecessary (single-writer read loop) |
| NEW-8 | Low | Infra | `ResponseBodySource` reference read missing `Volatile.Read` in `IsResponseCompleted` — ARM IL2CPP gap |

### Platform Compatibility (Updated)

| Component | Editor | Standalone | iOS IL2CPP | Android IL2CPP | WebGL |
|-----------|--------|------------|------------|----------------|-------|
| Http2ResponseBodySource | OK | OK | OK (H-1 fixed) | OK (H-1 fixed) | N/A (excluded) |
| SingleReaderChannel<Http2ResponseBodyChunk> | OK | OK | OK (link.xml) | OK (link.xml) | N/A |
| ManualResetValueTaskSourceCore | OK | OK | OK (link.xml) | OK (link.xml) | N/A |
| SendStreamingDataAsync | OK | OK | OK | OK | N/A |
| Stall detection | OK | OK | OK (H-1, L-1 fixed) | OK (H-1, L-1 fixed) | N/A |
| Http2Stream.IsResponseCompleted | OK | OK | Needs NEW-8 fix | Needs NEW-8 fix | N/A |

---

## Review History

| Round | Date | Verdict | Key Actions |
|-------|------|---------|-------------|
| 1 | 2026-03-19 | NOT APPROVED | 5 critical, 4 high, 5 medium, 4 low issues identified |
| 2 | 2026-03-20 | CONDITIONAL PASS | All 17 R1 issues fixed. 3 follow-ups before 22a.4, 4 deferred |

---

## Review Round 3 (Closure Pass)

**Review date:** 2026-03-20
**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Verdict:** PASS — Round 2 follow-ups fixed, deferred/documentation items closed, no blocking issues remain for 22a.4.

### Round 2 Follow-up Verification

| Issue | Verdict | Notes |
|-------|---------|-------|
| N-2 `_currentChunk` abort/read race | PASS | Current-chunk access is now synchronized and detached safely before release |
| N-3 SemaphoreSlim disposal | PASS | Connection-owned semaphores are now disposed in `Http2Connection.Dispose()` |
| NEW-1 Accounting naming ambiguity | PASS | Distinction between payload-buffer bytes and flow-controlled bytes is now documented inline |
| NEW-2 Recursive zero-remaining read path | PASS | `ReadAsync(...)` stays iterative; no self-recursion remains |
| NEW-3 `Cancel` vs `Fail` exception taxonomy | PASS | Semantics documented inline where the split occurs |
| NEW-4 CAS loop rationale | PASS | Retained intentionally and documented because cleanup can release bytes concurrently |
| NEW-8 `ResponseBodySource` visibility gap | PASS | Volatile-backed field/property now used for `IsResponseCompleted` reads |

### Final Status

- All Round 1 issues: fixed
- All Round 2 follow-ups: fixed
- All previously deferred documentation/cleanup items from Round 2: fixed or documented in-code
- Missing test coverage called out by the review: added

Phase 22a.3 is now review-clean for progression into 22a.4, pending the already-known external validation passes (Unity Test Runner and IL2CPP/mobile/device coverage).
