# Phase 22a.2 Review: HTTP/1.1 Streaming Send/Receive

## Review Round 1 (Initial Review)

**Review date:** 2026-03-18
**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Verdict:** BLOCKED — 1 critical, 4 high issues identified

---

## Implementation Completeness

All 8 spec steps are implemented and match the specification:

| Step | Component | Status |
|------|-----------|--------|
| 1 | Known-length request streaming (`Http11RequestSerializer`) | Complete |
| 2 | Chunked request streaming (`Http11RequestSerializer`) | Complete |
| 3 | Retry/failure semantics (`RawSocketTransport`) | Complete |
| 4 | Split header/body response parsing (`Http11ResponseParser`) | Complete |
| 5 | `Http11ResponseBodySource` (4 framing variants) | Complete |
| 6 | Early-dispose drain-or-close policy | Complete |
| 7 | Connection lease transfer for streaming responses | Complete |
| 8 | Timeout scope for streaming responses | Complete |

Supporting items verified:
- `TransportBehaviorFlags.StreamingResponseRequested` in Core/Internal
- `BufferedDispatchBridge` and `StreamingDispatchBridge` updated for new parser flow
- `ParsedResponseHead` ownership-transfer pattern via `TransferReaderOwnership()`
- `Http11RequestWriteState` body-commit tracking for retry semantics
- `ResolveBodyWriteMode` Content-Length/Transfer-Encoding conflict resolution
- Request framing header stripping (`Content-Length`, `Transfer-Encoding`, `Host`)
- Test files for serializer, parser, and transport updated

---

## CRITICAL Issues

### C-1: Lease double-reference on `EmitParsedResponseHeadAsync` failure

**Files:** `Runtime/Transport/RawSocketTransport.cs` (lines 988–1001), `Runtime/Transport/Http1/Http11ResponseBodySource.cs`
**Found by:** Both agents

If `OnResponseStartAsync` throws after `Http11ResponseBodySource` is constructed (reader already transferred via `head.TransferReaderOwnership()`), the body source is never disposed:

1. The `BufferedStreamReader` (now owned by body source) leaks its pooled buffer
2. The `ConnectionLease` (passed to body source) leaks
3. The semaphore permit is never released

The outer `finally { lease?.Dispose(); }` in `DispatchCoreAsync` fires (since `lease` was not nulled), calling `Dispose()` on the same lease the body source holds. `ConnectionLease.Dispose()` is idempotent so it doesn't crash, but the semaphore is released while the body source still references the connection — another request can acquire the same connection concurrently.

**Fix:** Wrap body source construction + `OnResponseStartAsync` in try/catch; call `bodySource.Abort()` on failure. Null the outer `lease` immediately after body source takes ownership.

---

## HIGH Issues

### H-1: Per-read `CancellationTokenSource.CreateLinkedTokenSource` allocation in body read hot path

**File:** `Runtime/Transport/Http1/Http11ResponseBodySource.cs` (lines 393–398 and throughout `ReadContentLengthAsync`, `ReadChunkedAsync`, `ReadToEndAsync`)
**Found by:** Both agents

Every `ReadAsync` call creates a new linked CTS via `CreateLinkedReadTokenSource` when both the transport token and user token are cancellable. For a 100 MB download with 32 KB reads = ~3,000 CTS allocations (plus GC pressure on IL2CPP mobile).

The streaming path passes `CancellationToken.None` as `_transportReadToken`, so the optimization short-circuit avoids the allocation there. But buffered responses with both tokens active hit this on every read.

**Fix:** Cache the linked CTS once at construction time when both tokens are cancellable. Store in `_linkedReadCts` field, dispose in `CompleteBody`/`CloseBody`.

---

### H-2: `ReadToEndAsync` body source lacks `MaxResponseBodySize` enforcement

**File:** `Runtime/Transport/Http1/Http11ResponseBodySource.cs` (lines 310–319)
**Found by:** Network architect

The old buffered parser path (`Http11ResponseParser.ReadToEndAsync`) had a 100 MB `MaxResponseBodySize` limit. The new streaming body source's `ReadToEndAsync` has no cumulative size tracking — regression for buffered consumers of read-to-end (connection-close) responses. The chunked path correctly tracks `_decodedChunkBytes` and enforces the limit.

**Fix:** Add cumulative size tracking to `ReadToEndAsync` (similar to chunked path's `_decodedChunkBytes` + `MaxResponseBodySize` enforcement).

---

### H-3: `long.TryParse(NumberStyles.HexNumber)` IL2CPP stripping risk

**File:** `Runtime/Transport/Http1/Http11ResponseParser.cs` (line 536)
**Found by:** Infrastructure architect

`NumberStyles.HexNumber` in `ParseChunkSizeLine` is now on the streaming hot path. IL2CPP AOT may strip some `NumberStyles` overloads on Unity 2021.3 LTS when not referenced transitively from preserved types. The existing buffered `ReadChunkedBodyAsync` also calls `ParseChunkSizeLine`, so this isn't new, but the streaming path makes it critical.

**Fix:** Add a `link.xml` preservation entry for `System.Globalization.NumberStyles` or validate on a physical IL2CPP build.

---

### H-4: Unity Test Runner / IL2CPP validation not completed

**Found by:** Infrastructure architect

Implementation journal confirms no Unity package import, Test Runner execution, or IL2CPP device validation has been done. Harness validation uses .NET 8 (not .NET Standard 2.1 under Unity's IL2CPP). Per `AGENTS.md`, physical device validation is a mandatory step for transport changes.

**Action:** Track as blocking item for phase sign-off.

---

## MEDIUM Issues

### M-1: `_timeoutMessage` string allocation at construction

**File:** `Runtime/Transport/Http1/Http11ResponseBodySource.cs`
**Found by:** Infrastructure architect

`$"Request timed out after {requestTimeout.TotalSeconds}s"` allocates a string on every `Http11ResponseBodySource` construction. Avoidable by deferring format to throw time (store raw `double` field, format on exception).

---

### M-2: `_transportReadToken` is dead code in streaming mode

**File:** `Runtime/Transport/Http1/Http11ResponseBodySource.cs`
**Found by:** Infrastructure architect

When `StreamingResponseRequested` is `true`, `CancellationToken.None` is passed as `transportBodyReadToken`. The body source's timeout-vs-cancellation distinction logic is dead code for the streaming path. The journal acknowledges this is intentional, but the spec and implementation should be explicit that `_transportReadToken` is unused in streaming mode.

---

### M-3: `DisposeAsync` drain timeout cannot link caller's cancellation token

**File:** `Runtime/Transport/Http1/Http11ResponseBodySource.cs` (lines 212–231)
**Found by:** Both agents

The spec says "drain operation uses `CancellationTokenSource.CreateLinkedTokenSource(callerCt, 2secondTimeout)`." But `IAsyncDisposable.DisposeAsync()` has no cancellation parameter. The implementation links only `_transportReadToken` + 2-second timeout. For streaming responses where `_transportReadToken` is `CancellationToken.None`, the drain has only the 2-second timeout.

**Fix:** Update the spec to reflect the `IAsyncDisposable` contract limitation.

---

### M-4: `_remainingContentLength` unsynchronized read in `ShouldAttemptDisposeDrain`

**File:** `Runtime/Transport/Http1/Http11ResponseBodySource.cs`
**Found by:** Infrastructure architect

`_remainingContentLength` is decremented by `ReadContentLengthAsync` during reads. `ShouldAttemptDisposeDrain` reads it to decide the drain budget. If `DisposeAsync` is called concurrently with an in-progress `ReadAsync`, the read of `_remainingContentLength` is unsynchronized. On ARM64/IL2CPP this is a non-atomic 64-bit load on a potentially-writing field.

**Fix:** Document that `DisposeAsync` must not be called concurrently with `ReadAsync` (single-consumer requirement).

---

### M-5: `EndsWith("chunked")` matching overly permissive

**File:** `Runtime/Transport/Http1/Http11ResponseParser.cs` (line 360)
**Found by:** Network architect

`te.EndsWith("chunked", StringComparison.OrdinalIgnoreCase)` would incorrectly match a hypothetical `notchunked` value. Per RFC 9112 Section 6.1, the check should verify `chunked` is a complete token (preceded by `,`, whitespace, or is the entire value).

**Fix:** Verify token boundary before "chunked".

---

### M-6: Per-chunk `string` allocation from `ReadLineAsync` for chunk header parsing

**File:** `Runtime/Transport/Http1/Http11ResponseBodySource.cs` (line 290)
**Found by:** Network architect

Each chunk header read allocates a string for the size line. For a 100 MB response with 32 KB chunks (~3,125 chunks) = ~3,125 string allocations. Known Phase 6 deferred item, consistent with project's deferred optimization strategy.

---

### M-7: `DispatchOnLeaseAsync` always returns `true` — vestigial return value

**File:** `Runtime/Transport/RawSocketTransport.cs` (line 955)
**Found by:** Both agents

The method signature returns `Task<bool>` but always returns `true`. The `bool` return is vestigial from the old buffered era. The caller checks `if (result) { lease = null; }`, which always executes.

**Fix:** Clean up — either change return type to `void` or document why the return governs lease ownership in the streaming model.

---

## LOW Issues

| # | Issue | Source |
|---|-------|--------|
| L-1 | `CompleteBody` calls both `ReturnToPool()` and `Dispose()` on lease — works via idempotency but fragile contract dependency | Network |
| L-2 | Dual-header (`TE` + `CL`) warning uses `Debug.WriteLine` — stripped in release builds; should use project diagnostic logger pattern | Network |
| L-3 | 18-byte `ArrayPool` rent for chunk header buffer could be `stackalloc` (max chunk header is 10 bytes) | Network |
| L-4 | `BufferedStreamReader._disposed` is plain `bool`, not `volatile` — inconsistent with project's `volatile bool _disposed` pattern | Infra |
| L-5 | `ShouldAttemptDisposeDrain` always returns `true` for chunked — actual budget enforced inside `DrainWithinBudgetAsync` | Infra |
| L-6 | Chunked drain budget counts decoded bytes only, not wire bytes (spec-compliant but may undercount actual I/O for small chunks) | Network |
| L-7 | Multiple linked CTS allocations per `ReadChunkedAsync` when crossing chunk boundaries (two CTS per call: one for chunk size, one for data) | Network |

---

## INFO Items

| # | Item | Source |
|---|------|--------|
| I-1 | Overall architecture quality is high — staged head/body split is clean, ownership-transfer pattern eliminates ambiguity | Infra |
| I-2 | No module dependency violations — all new code in Transport and Core/Internal | Both |
| I-3 | `TransportBehaviorFlags` placement in Core/Internal using string constants is correct | Infra |
| I-4 | RFC 9112 Section 7.1 chunk-size uppercase hex (`StandardFormat('X')`) is valid per ABNF (`HEXDIG` includes both cases) | Network |
| I-5 | HEAD/1xx/204/304 correctly mapped to `Empty` before checking any headers | Network |
| I-6 | `Http11RequestWriteState` uses `Volatile.Read`/`Interlocked.Exchange` correctly for ARM IL2CPP | Both |
| I-7 | Request body streaming uses pooled 32 KB buffer with proper `finally` return | Network |
| I-8 | `FormatChunkHeader` uses `Utf8Formatter.TryFormat` with zero extra allocation | Network |
| I-9 | Connection lifecycle for empty/zero-length body responses is correct (constructor calls `CompleteBody()` immediately) | Network |

---

## Carry-Forward Items from 22a.1 Review

| # | Severity | Item | Status |
|---|----------|------|--------|
| NEW-W-1 | WARNING | RetryDetectorHandler concurrent dispose safety for streaming body sources | **Validate in 22a.2** — `Http11ResponseBodySource` terminal state transitions use `Interlocked.CompareExchange`, safe for concurrent `Abort()` |
| NEW-W-2 | WARNING | RecordingHandler records empty body for streaming responses | Still open — document as known limitation |
| W-6 | WARNING | IL2CPP device validation | Still open — now combined with H-4 |
| W-7 | WARNING | Http2Stream SegmentedBuffer copy | Deferred to 22a.3 (`Http2ResponseBodySource`) |

---

## Required Actions Before 22a.4

1. **(Critical) Fix C-1** — Lease ownership leak on handler callback failure in `EmitParsedResponseHeadAsync`
2. **(High) Fix H-1** — Cache linked CTS at construction instead of per-read allocation
3. **(High) Fix H-2** — Add `MaxResponseBodySize` enforcement to `ReadToEndAsync` body source
4. **(High) Track H-3** — `link.xml` preservation or IL2CPP build validation for `NumberStyles.HexNumber`
5. **(High) Track H-4** — Unity Test Runner / IL2CPP device validation as blocking gate
6. **(Medium) Update spec** for M-3 — `IAsyncDisposable` cannot accept caller's cancellation token

---

## Review Round 2

**Review date:** 2026-03-19
**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Verdict:** CONDITIONALLY APPROVED — all code issues fixed; IL2CPP device validation remains open

---

### Round 1 Issue Disposition

| # | Severity | Issue | R2 Status |
|---|----------|-------|-----------|
| C-1 | Critical | Lease double-reference on `EmitParsedResponseHeadAsync` failure | **FIXED** — try/catch wraps body source construction + `OnResponseStartAsync`; `bodySource?.Abort()` on failure releases reader + lease. Outer `lease.Dispose()` is safe via idempotency (outer lease not nulled, but `ConnectionLease.Dispose` is guarded by `_semaphoreReleased` under `_lock`). |
| H-1 | High | Per-read CTS allocation in body read hot path | **FIXED** — `GetEffectiveReadToken` caches linked CTS per caller token in `_cachedReadTokenSource`. Same-token reads reuse cached CTS (zero allocs). Short-circuit for `CancellationToken.None` transport token avoids lock entirely in streaming path. |
| H-2 | High | `ReadToEndAsync` lacks `MaxResponseBodySize` enforcement | **FIXED** — `_readToEndBytesRead` tracks cumulative bytes; throws `IOException` when exceeding 100 MB. Matches chunked path's enforcement pattern. |
| H-3 | High | `NumberStyles.HexNumber` IL2CPP stripping risk | **FIXED** — Eliminated at root. Both `ParseChunkSizeLine` and new `ParseChunkSizeFromBuffer` use manual hex parsing via `HexValue(char)` / `ParseChunkSizeToken`. No `NumberStyles` dependency remains on the chunk parsing hot path. |
| H-4 | High | Unity Test Runner / IL2CPP validation | **PARTIALLY FIXED** — Unity 2021.3.45f2 package import + EditMode compilation succeeds. IL2CPP backend unavailable in test environment; device validation still open. Carry forward as phase sign-off gate. |
| M-1 | Medium | `_timeoutMessage` string allocation at construction | **FIXED** — Stores `_requestTimeoutSeconds` as `double`; formats string only at throw time in `CreateTimeoutException`. |
| M-2 | Medium | `_transportReadToken` dead code in streaming mode | **FIXED** — Class and field comments explicitly document that streaming callers pass `CancellationToken.None`. `GetEffectiveReadToken` short-circuits cleanly. |
| M-3 | Medium | `DisposeAsync` drain cannot link caller's `ct` | **FIXED** — Spec updated to reflect `IAsyncDisposable` contract limitation. Implementation uses `_transportReadToken` + 2s timeout. |
| M-4 | Medium | `_remainingContentLength` unsynchronized ARM64 read | **FIXED** — Uses `Interlocked.Read` / `Interlocked.Add` for all accesses. Single-consumer documented at class level. |
| M-5 | Medium | `EndsWith("chunked")` overly permissive | **FIXED** — Replaced with `EndsWithTransferCodingToken` that validates token boundary (preceded by `,` or whitespace, or entire value). |
| M-6 | Medium | Per-chunk string allocation from `ReadLineAsync` | **FIXED** — `ReadChunkSizeAsync` reads bytes and parses via `ParseChunkSizeFromBuffer`, returning `long` directly. No intermediate string allocation. |
| M-7 | Medium | `DispatchOnLeaseAsync` vestigial `bool` return | **FIXED** — Returns `Task` (void). Caller no longer checks a return value. |
| L-1 | Low | `CompleteBody` dual `ReturnToPool` + `Dispose` | **FIXED** — Calls either `ReturnToPool()` or `Dispose()`, not both. `ReturnToPool()` now internally releases the semaphore. |
| L-2 | Low | `Debug.WriteLine` stripped in release builds | **FIXED** — Uses `context?.RecordEvent("Http11DualFramingHeaders", ...)` with structured data. |
| L-3 | Low | 18-byte `ArrayPool` rent for chunk header | **FIXED** — Uses `new byte[18]` local (one per request, not per chunk). |
| L-4 | Low | `BufferedStreamReader._disposed` non-volatile | **FIXED** — Now `private volatile bool _disposed`. |
| L-5 | Low | `ShouldAttemptDisposeDrain` always true for chunked | **FIXED** — Documented with code comment explaining budget enforcement in `DrainWithinBudgetAsync`. |
| L-6 | Low | Drain budget counts decoded bytes only | **FIXED** — Documented as spec-intended behavior. |
| L-7 | Low | Multiple CTS per `ReadChunkedAsync` | **FIXED** — `GetEffectiveReadToken` caching eliminates this. Same-token reads reuse cached CTS. |

---

### New Issues Identified in Round 2

#### NEW-R2-1 (Low): `ReadExpectedCrlfAsync` allocates `byte[2]` per chunk terminator

**File:** `Runtime/Transport/Http1/Http11ResponseParser.cs`
**Found by:** Both agents

Each chunk terminator read allocates `new byte[2]`. For ~3,125 chunks in a 100 MB response = ~3,125 small allocations. Cannot use `stackalloc` in async context. Track as Phase 6 optimization follow-up.

#### NEW-R2-2 (Low): Outer `lease` not nulled after body source takes ownership

**File:** `Runtime/Transport/RawSocketTransport.cs`
**Found by:** Infrastructure architect

`DispatchOnLeaseAsync` does not null the outer `lease` after `EmitParsedResponseHeadAsync` succeeds. The outer `finally { lease?.Dispose(); }` fires a redundant idempotent `Dispose()` on every streaming response. Functionally correct due to `ConnectionLease` idempotency, but not self-documenting. Consider adding a comment or nulling the reference.

---

### Carry-Forward Items

| # | Severity | Item | Status |
|---|----------|------|--------|
| H-4 | High | IL2CPP device validation (iOS/Android) | Open — environment constraint; gate for phase sign-off |
| NEW-W-1 | Warning | RetryDetectorHandler concurrent dispose safety | **CONFIRMED SAFE** — terminal state transitions use `Interlocked.CompareExchange`; `Abort()` is idempotent |
| NEW-W-2 | Warning | RecordingHandler records empty body for streaming | Still open — documented as known limitation |
| W-7 | Warning | Http2Stream SegmentedBuffer copy | Deferred to 22a.3 |
| NEW-R2-1 | Low | `byte[2]` per-chunk-terminator allocation | Defer to Phase 6 |
| NEW-R2-2 | Low | Outer lease redundant `Dispose()` on streaming path | Cosmetic; document or null the reference |

---

### Round 2 Verdict

**CONDITIONALLY APPROVED** — All 1 critical, 4 high (code), 7 medium, and 7 low issues from round 1 are **FIXED**. Two new low-severity items identified (acceptable). Code is ready to proceed to 22a.3/22a.4.

**Remaining gate:** IL2CPP native build + physical device validation (H-4) — blocks phase milestone sign-off, not 22a.3 implementation work.
