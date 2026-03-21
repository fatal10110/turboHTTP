# Phase 22a.4 Review: Buffered Fast Path and Performance Tuning

## Review Round 1 (Initial Review)

**Review date:** 2026-03-20
**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Verdict:** BLOCKED — 3 blocking, 7 non-blocking, 6 observations identified

---

## Implementation Completeness

All 5 spec steps are implemented:

| Step | Component | Status |
|------|-----------|--------|
| 1 | `StreamingOptions` runtime-configurable thresholds, wired into `UHttpClientOptions` and transport | Complete |
| 2 | Direct buffered request-body send path (HTTP/1.1 coalesced write, HTTP/2 confirmed) | Complete |
| 3 | Direct buffered response collector path (`DetachedBufferedBody`, `TryDetachBufferedBody`, zero-copy collector) | Complete |
| 4 | Handler/body-source wrapper pooling assessment (deferred — allocation cost negligible) | Complete |
| 5 | Allocation and latency audit (no tuning changes needed) | Complete |

Supporting items verified:
- `StreamingOptions.Clone()` and `IsDefault()` for snapshot semantics
- `UHttpClientOptions.Clone()` deep-copies `StreamingOptions`
- `HttpTransportFactory` additive overloads for `StreamingOptions`
- `RawSocketTransport` snapshots streaming options and plumbs to HTTP/1.1 and HTTP/2 subpaths
- 6 new config-plumbing tests, 2 serializer threshold tests, 4 detach/collector tests, 2 HTTP/2 body-source detach tests
- Loopback benchmark measurements (GET 1KB JSON: 14 bytes/req, POST 1KB JSON: 18 bytes/req, POST 1KB form: 4 bytes/req)

---

## BLOCKING Issues

### B-1: `DetachedBufferedBody` is `public struct` with mutating `DetachOwner()` — defensive copy risk

**File:** `Runtime/Core/DetachedBufferedBody.cs`, lines 41–46
**File:** `Runtime/Core/Pipeline/ResponseCollectorHandler.cs`, lines 154–155
**Found by:** Infrastructure architect

`DetachedBufferedBody` is a `public struct`. Calling `DetachOwner()` on it works correctly within `ResponseCollectorHandler` because `detachedBody` is a local variable filled via `out`. However, any caller who stores it in a `readonly` context — a `readonly` field, a `readonly` struct field, or through an interface — will get a defensive copy, and `DetachOwner()` will null out `_owner` on the copy, leaving the original with a live owner reference that is never disposed. Similarly, `DisposeOwnedResources()` calls `DetachOwner()?.Dispose()` and would silently no-op on a defensive copy.

The design as used internally is technically safe, but making the struct `public` with mutating methods is a semantic trap with no language-level guard against defensive copies.

**Fix:** Make `DetachedBufferedBody` `internal`. Its ownership semantics are only meaningful within the framework internals. `UHttpResponse` already exposes `Body` as `ReadOnlySequence<byte>`, so the struct does not need to be part of the public API surface. External `IResponseBodySource` implementations that need to create the struct can still use it if the constructors remain accessible via `internal`.

---

### B-2: `BufferedResponseBodySource.TryDetachBufferedBody` silently discards non-empty trailers

**File:** `Runtime/Core/Internal/BufferedResponseBodySource.cs`, lines 34–50
**File:** `Runtime/Core/Pipeline/ResponseCollectorHandler.cs`, lines 145–146
**Found by:** Infrastructure architect

When `TryDetachBufferedBody` succeeds on a `BufferedResponseBodySource`, the source sets `_disposed = 1` atomically (line 48), clears `_trailers`, and returns `true`. The collector then skips `GetTrailersAsync` (line 145–146):

```csharp
if (!detached)
    _ = await body.GetTrailersAsync(_cancellationToken).ConfigureAwait(false);
```

The `BufferedResponseBodySource` constructor accepts a `trailers` parameter. If constructed with non-empty trailers and the detach path is taken, those trailers are silently discarded. The `UHttpResponse` has no way to receive them.

**Fix:** `TryDetachBufferedBody` on `BufferedResponseBodySource` should return `false` when `_trailers` is non-empty (i.e., when there is actual trailer content beyond `HttpHeaders.Empty`), forcing the fallback path that retrieves trailers. Lower-risk than extending `DetachedBufferedBody` to carry trailers.

---

### B-3: `Http11ResponseBodySource.TryDetachBufferedBody` — concurrent read guard and 32-bit alignment concern

**File:** `Runtime/Transport/Http1/Http11ResponseBodySource.cs`, lines 96–107
**Found by:** Infrastructure architect

```csharp
if (Volatile.Read(ref _terminalState) == 1 &&
    (_bodyKind == Http11ResponseBodyKind.Empty ||
     (_bodyKind == Http11ResponseBodyKind.ContentLength &&
      Interlocked.Read(ref _remainingContentLength) == 0)))
```

Two issues:

1. `_remainingContentLength` is a `long` (64-bit). `Interlocked.Read` on a `long` on 32-bit IL2CPP (Android ARMv7) requires the field to be 8-byte aligned. Field alignment is controlled by IL2CPP runtime layout, not the developer — potentially non-atomic on 32-bit.

2. No guard against concurrent `ReadAsync`. Between checking `_terminalState` and reading `_remainingContentLength`, a concurrent `ReadAsync` could be in progress. The class-level comment says "single-consumer" but `TryDetachBufferedBody` is called from the collector, which could race with middleware observing the body.

**Fix:** Add a `_hasReadData` flag (set to 1 once any `ReadAsync` or `DrainAsync` is called) and check it with `Volatile.Read` at the top of `TryDetachBufferedBody`. This is the same pattern used correctly in `Http2ResponseBodySource`. This also makes the `Interlocked.Read` on `_remainingContentLength` unnecessary — if no reads have occurred and the body is terminal-empty, the remaining length check is redundant.

---

## NON-BLOCKING Issues

### NB-1: `StreamingOptions.IsDefault()` duplicates default values — fragile, not DRY

**File:** `Runtime/Core/StreamingOptions.cs`, lines 10–16 (field defaults) and 76–82 (`IsDefault()` body)
**Found by:** Infrastructure architect

The default values appear twice: once in field initializers and once in `IsDefault()`. If a developer changes one field's default, `IsDefault()` silently breaks, causing `UHttpClient` to either unnecessarily allocate a dedicated transport or fail to allocate one when it should.

**Fix:** Use `private const` or `static readonly` default fields, and reference those constants from both the initializer and `IsDefault()`.

---

### NB-2: `Http11RequestSerializer` retains fallback constants that shadow `StreamingOptions` values

**File:** `Runtime/Transport/Http1/Http11RequestSerializer.cs`, lines 31–32
**Found by:** Infrastructure architect

```csharp
private const int DefaultSmallBufferedRequestThresholdBytes = 32 * 1024;
private const int DefaultStreamingSendBufferBytes = 32 * 1024;
```

These constants duplicate the defaults in `StreamingOptions` and are a third location where these values are defined. The null-coalescing guard exists for tests that call `SerializeAsync` directly without options.

**Fix:** Replace the null-coalescing guard with `streamingOptions ?? new StreamingOptions()` at method entry, or make the parameter non-nullable and fix the test call sites.

---

### NB-3: `MockResponseBodySource.DisposeAsync` increments counter even after detach

**File:** `Runtime/Testing/MockResponseBodySource.cs`, lines 87–101, 162–167
**Found by:** Infrastructure architect

After `TryDetachBufferedBody` returns `true`, it sets `_disposed = 1`. If a caller then invokes `DisposeAsync`, the counter is still incremented before the disposed check, producing a spurious `DisposeAsyncCount` of 1 that could cause confusing test failures.

**Fix:** In `MockResponseBodySource.DisposeAsync`, check if already disposed before incrementing the counter, matching the `BufferedResponseBodySource.DisposeCore` idempotency pattern.

---

### NB-4: HTTP/2 detach path omits `WINDOW_UPDATE` for session-level flow control

**File:** `Runtime/Transport/Http2/Http2ResponseBodySource.cs`, lines 143–147
**Found by:** Infrastructure architect

On the detach path, only `OnResponseBytesConsumed` is called — there is no `OnStreamChunkConsumedAsync` call. The connection-level flow control window is updated locally via `_connectionBufferedBytes` decrement, but the peer does not receive a `WINDOW_UPDATE` frame until the next unrelated consumption event triggers `MaybeSendConnectionWindowUpdateAsync`.

For completed response bodies (the precondition for detach), the stream is half-closed(remote), so stream-level `WINDOW_UPDATE` is not needed. The connection-level `WINDOW_UPDATE` is deferred — correct per RFC 9113, but the server may not be able to send data on future streams until the session window recovers.

**Fix:** After detach succeeds, fire-and-forget a connection-level `WINDOW_UPDATE` for `releasedFlowControlledBytes`. Use the existing `MaybeSendConnectionWindowUpdateAsync` mechanism or add a dedicated synchronous connection-level window update trigger.

**Network architect assessment:** Semantically correct since `OnResponseBytesConsumed` does decrement `_connectionBufferedBytes` and the threshold check in `MaybeSendConnectionWindowUpdateAsync` will eventually fire. The delay is bounded by the next consumption event. Acceptable for current workloads.

---

### NB-5: Double-dispose safety in `CollectAsync` depends on undocumented `DetachOwner()` idempotency

**File:** `Runtime/Core/Pipeline/ResponseCollectorHandler.cs`, lines 200–206
**Found by:** Infrastructure architect

The exception handler disposes both `detachedOwner` and calls `detachedBody.DisposeOwnedResources()`. This is safe because `DetachOwner()` nulls `_owner` before returning, making the second call a no-op. But the correctness depends on this undocumented idempotency relationship.

**Fix:** Add a code comment explaining that `detachedOwner` and `detachedBody.DisposeOwnedResources()` are safe to call together because `DetachOwner()` already transferred (nulled) `_owner` from the struct.

---

### NB-6: HTTP/1.1 empty-body detach relies on implicit constructor behavior

**File:** `Runtime/Transport/Http1/Http11ResponseBodySource.cs`, lines 92–107
**Found by:** Infrastructure architect

When `TryDetachBufferedBody` returns `true` for empty bodies, `CollectAsync` skips `DisposeAsync`. For `Http11ResponseBodySource`, disposal is what returns the connection lease to the pool. This is safe because the constructor calls `CompleteBody()` for empty bodies, which already returns the lease. But this dependency is invisible from `CollectAsync`.

**Fix:** Add a comment in `TryDetachBufferedBody` documenting that returning `true` implies the connection lease has already been returned via `CompleteBody()` in the constructor.

---

### NB-7: 3-argument `Http2Connection` constructor in test may reference a pre-existing overload

**File:** `Tests/Runtime/Transport/Http2/Http2ConnectionTests.cs`, line 260
**Found by:** Infrastructure architect

```csharp
var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);
```

This calls a constructor without `StreamingOptions`. If this overload exists (pre-dating 22a.4) and defaults to `new StreamingOptions()`, it should be documented. If it was removed, the test needs updating.

**Fix:** Confirm the overload exists and document, or update the test to pass `new StreamingOptions()` explicitly.

---

## OBSERVATIONS

### O-1: `ValidatePositive` always reports `paramName = "value"` in exception

**File:** `Runtime/Core/StreamingOptions.cs`, lines 21, 27, 33, 39, 45, 51, 57
**Found by:** Infrastructure architect

`nameof(value)` resolves to `"value"` — not the property name. `ArgumentOutOfRangeException.ParamName` will always read `"value"` instead of e.g. `"SmallBufferedRequestThresholdBytes"`. Cosmetic issue.

---

### O-2: `UHttpClientOptions.Clone()` correctly deep-copies `StreamingOptions`

**File:** `Runtime/Core/UHttpClientOptions.cs`, line 156
**Found by:** Both agents

```csharp
Streaming = Streaming?.Clone() ?? new StreamingOptions()
```

Confirmed correct. Test at `UHttpClientTests.Builders.cs` validates independence.

---

### O-3: `DefaultHttp2PerStreamReceiveBufferBytes` may be silently overridden by window/frame settings

**File:** `Runtime/Transport/Http2/Http2Connection.cs`, lines 135–139
**Found by:** Infrastructure architect

`Math.Max(streamingOptions.DefaultHttp2PerStreamReceiveBufferBytes, Math.Max(_localSettings.InitialWindowSize, _localSettings.MaxFrameSize))` means a user setting 64KB will silently get 256KB if `InitialWindowSize` is larger. Consider adding a note to `StreamingOptions` XML docs.

---

### O-4: Protocol correctness confirmed — HTTP/1.1 coalesced write

**Found by:** Network architect

`TryWriteSmallBufferedRequestAsync` correctly: writes `Content-Length` header before body, appends body after `\r\n\r\n`, uses `KnownLength` mode preventing accidental chunked encoding, falls through for above-threshold bodies.

---

### O-5: Protocol correctness confirmed — HTTP/2 detach and flow control

**Found by:** Network architect

`TryDetachBufferedBody` correctly: only succeeds when `TerminalStateCompleted` (END_STREAM received), refuses after partial reads, transitions to `TerminalStateDetached` atomically, drains queue into detached chunks, decrements `_bufferedBytes` and calls `OnResponseBytesConsumed`, releases stream lifetime. Correct per RFC 9113 Section 6.9.

---

### O-6: Zero-allocation analysis confirms targets met

**Found by:** Network architect

- **Small buffered request:** Single `PooledHeaderWriter.WriteToAsync` call, body appended as `ReadOnlySpan<byte>` to pooled buffer.
- **Detached response (single chunk):** One `ArrayPoolMemoryOwner<byte>` wrapper (~24 bytes) + one stack-allocated `DetachedBufferedBody` struct.
- **Detached response (multi-chunk):** `DetachedBufferedChunkOwner` + linked `DetachedChunkSequenceSegment` nodes — proportional to chunk count, not data size.

---

## Missing Test Coverage

| # | Gap | Recommendation |
|---|-----|----------------|
| T-1 | `DetachedBufferedSequence` test omits body content assertion — only checks `IsSingleSegment` and owner disposal | Add content verification for `"payload"` bytes |

---

## Pass Areas (both reviewers agree)

- **StreamingOptions snapshot model:** Correct. Clone-on-construct, immutable after snapshot.
- **Transport factory additive API:** Backward-compatible. Existing overloads untouched.
- **HTTP/1.1 threshold-based fast path:** Coalesced write for small bodies, separate writes for large bodies, no session allocation for either.
- **HTTP/2 buffered request path:** Unchanged and correct — direct memory-to-DATA-frame, no session wrapper.
- **HTTP/2 response detach:** Terminal-only, pre-read-only, atomic state transition, correct stream lifecycle release.
- **Exception safety in `CollectAsync`:** Thorough ownership tracking with `detached`, `detachedOwner`, `detachedOwnershipTransferred` flags.
- **Wrapper pooling deferral:** Evidence-based (loopback benchmark), correct decision for 22a.4.
- **Platform compatibility:** All APIs within .NET Standard 2.1. WebGL exclusion maintained.
- **TLS paths:** Untouched by this phase.

---

## Platform Compatibility

| Component | Editor | Standalone | iOS IL2CPP | Android IL2CPP | WebGL |
|-----------|--------|------------|------------|----------------|-------|
| StreamingOptions | OK | OK | OK | OK | OK (Core assembly) |
| DetachedBufferedBody | OK | OK | OK | OK | OK (Core assembly) |
| BufferedResponseCollectorHandler | OK | OK | OK | OK | OK (Core assembly) |
| Http11RequestSerializer fast path | OK | OK | OK | OK | N/A (Transport excluded) |
| Http11ResponseBodySource detach | OK | OK | OK (B-3 fixed) | OK (B-3 fixed) | N/A |
| Http2ResponseBodySource detach | OK | OK | OK | OK | N/A |

---

## Summary Table

| Severity | Count | R1 Status | R2 Status | R3 Status |
|----------|-------|-----------|-----------|-----------|
| Blocking | 3 | Open | All Fixed | All Fixed |
| Non-Blocking | 7 | Open | Open (deferred) | All Fixed |
| Observations | 6 | Informational | Informational | Informational |
| Missing Tests | 1 | Open | Open (deferred) | All Fixed |
| R2 New (Non-blocking) | 2 | — | Open (deferred) | All Fixed |

---

## ~~Required Changes Before 22a.5~~

~~1. **B-1:** Make `DetachedBufferedBody` `internal` (or at minimum make `DetachOwner()`/`DisposeOwnedResources()` `internal`)~~
~~2. **B-2:** `BufferedResponseBodySource.TryDetachBufferedBody` must return `false` when trailers are non-empty~~
~~3. **B-3:** Add `_hasReadData` guard to `Http11ResponseBodySource.TryDetachBufferedBody` (matching `Http2ResponseBodySource` pattern)~~

All three resolved in R2 — see Round 2 below.

---

## Review Round 2 (Verification Pass)

**Review date:** 2026-03-20
**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Verdict:** PASS — All Round 1 blocking issues fixed. No new blocking issues.

### Round 1 Fix Verification

| Issue | Verdict | Notes |
|-------|---------|-------|
| B-1 `DetachedBufferedBody` defensive copy | PASS | Converted to `public readonly struct` with `OwnershipState` sealed inner class. `Interlocked.Exchange(ref _owner, null)` guarantees exactly-once semantics across copies. IL2CPP-safe on 32-bit ARM. |
| B-2 Trailer discard on detach | PASS | `_trailers.Count != 0` guard added at line 38. `HttpHeaders.Empty.Count == 0` confirmed. Non-empty trailers force fallback to `GetTrailersAsync` path. |
| B-3 Concurrent read guard + 32-bit alignment | PASS | `_hasReadData` flag (set via `Interlocked.Exchange` in `ReadAsync`/`DrainAsync`) checked via `Volatile.Read` at top of `TryDetachBufferedBody`. Uses immutable `_length.GetValueOrDefault()` instead of `Interlocked.Read(ref _remainingContentLength)` — no 32-bit alignment concern. Consistent with HTTP/2 pattern. |

### Protocol Correctness

None of the fixes alter wire behavior. Changes are in body-source ownership/detach decision layer only. HTTP/1.1 and HTTP/2 framing, flow control, and serialization are untouched.

### New Issues Found in Round 2

| ID | Severity | Source | Description |
|----|----------|--------|-------------|
| NEW-1 | Non-blocking | Infra | `Http11ResponseBodySource.DrainAsync` sets `_hasReadData = 1` before the terminal-state early-exit check (line 206). An already-completed empty body gets `_hasReadData` set unnecessarily. Harmless under current call graph (drain is only called on non-detach path). |
| NEW-2 | Non-blocking | Infra | `BufferedDispatchBridgeTests.DetachedBodyProbeSource.TryDetachBufferedBody` always returns `true` — no test for the false-return fallback-to-drain path. Pre-existing gap, not introduced by R1 fixes. |

### Platform Compatibility (Updated)

| Component | Editor | Standalone | iOS IL2CPP | Android IL2CPP | WebGL |
|-----------|--------|------------|------------|----------------|-------|
| DetachedBufferedBody (readonly struct + OwnershipState) | OK | OK | OK | OK | OK |
| BufferedResponseBodySource trailer guard | OK | OK | OK | OK | OK |
| Http11ResponseBodySource `_hasReadData` guard | OK | OK | OK | OK | N/A |
| Http2ResponseBodySource detach | OK | OK | OK | OK | N/A |

---

## Review History

| Round | Date | Verdict | Key Actions |
|-------|------|---------|-------------|
| 1 | 2026-03-20 | BLOCKED | 3 blocking, 7 non-blocking, 6 observations |
| 2 | 2026-03-20 | PASS | All 3 blocking issues fixed. 2 new non-blocking items. No blocking issues remain. |
| 3 | 2026-03-20 | PASS | Remaining Round 2 non-blocking items fixed. No open issues remain. |

---

## Review Round 3 (Closure Pass)

**Review date:** 2026-03-20
**Reviewers:** implementation follow-up against Round 2 findings
**Verdict:** PASS — All remaining open issues fixed.

### Round 2 Follow-up Verification

| Issue | Verdict | Notes |
|-------|---------|-------|
| NEW-1 `DrainAsync` marks already-completed empty body as read | PASS | `_hasReadData` now flips only after the terminal-state completed early-exit. Completed empty-body `DrainAsync` remains a no-op and no longer suppresses the valid detach case. |
| NEW-2 Missing collector fallback-to-drain test | PASS | Added `CollectResponseAsync_DetachDeclined_FallsBackToDrainAndDispose`, which verifies the collector drains the body, retrieves trailers, and disposes the source when detach returns `false`. |

### Final Status

- No blocking issues remain.
- No non-blocking issues remain.
- Observations O-2/O-4/O-5/O-6 remain informational only.
