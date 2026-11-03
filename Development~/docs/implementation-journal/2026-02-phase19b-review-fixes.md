# Phase 19b — Full Review & Specialist Agent Fix Pass

**Date:** 2026-02-27
**Status:** Complete — 2-round specialist review + targeted fixes applied
**References:** `Development/docs/phases/phase-19b-api-refactor.md`

---

## Review Scope

Both specialist agents (unity-infrastructure-architect, unity-network-architect) performed a full review of the Phase 19b implementation across all four sub-phases:

- **19b.1**: Interceptor removal / Plugin → Middleware unification ✅ Confirmed complete
- **19b.2**: Pooled request objects / `UHttpClient.CreateRequest` ✅ Confirmed complete (with deferred item)
- **19b.3**: `ReadOnlySequence<byte>` response body ✅ Confirmed complete
- **19b.4**: Legacy compatibility purge ✅ Confirmed complete

---

## Bugs Fixed

### Bug 1 — `ReleaseResponseHold` negative-count path skipped `TryReturnToPool` (pool slot leak)

**File:** `Runtime/Core/UHttpRequest.cs`
**Severity:** Critical

When `ReleaseResponseHold()` decremented `_responseHoldCount` below 0 (double-dispose of response), the code clamped to 0 but returned without calling `TryReturnToPool()`. This left `_isLeased = 1` permanently — the request object could never be re-activated or returned to the pool, leaking that pool slot forever.

**Fix:** Removed early return from the negative-count branch. `TryReturnToPool()` is now always called; `_disposeRequested` is the gate that determines whether the actual pool return proceeds.

---

### Bug 2 — `UHttpResponse.Dispose(bool)` invoked managed pool callback from finalizer thread

**File:** `Runtime/Core/UHttpResponse.cs`
**Severity:** High

The `_onDispose` callback (attached via `AttachRequestRelease`) triggered `ReleaseResponseHold → TryReturnToPool → ObjectPool.Return`, which acquires a managed `lock`. Invoking this from the finalizer thread (`~UHttpResponse()` path) risks:
1. Finalizer blocking on a lock held by a GC-waiting thread (deadlock)
2. Accessing partially-finalized `_leaseOwner` if `UHttpClient` is also being finalized

**Fix:** Wrapped `callback?.Invoke()` with `if (disposing)`. ArrayPool returns (pooled body buffer, segmented body owner) remain on both paths as they are documented thread-safe. If the user forgets to dispose, the pooled `UHttpRequest` slot is retained — detectable via `PoolHealthReporter` diagnostics.

---

### Bug 3 — `DisposeBodyOwner()` used non-atomic plain read/write (ARM64 unsafe)

**File:** `Runtime/Core/UHttpRequest.cs`
**Severity:** Medium

`DisposeBodyOwner()` used:
```csharp
var owner = _bodyOwner;   // plain read
_bodyOwner = null;        // plain write
owner?.Dispose();
```

On ARM64 IL2CPP, the JIT can reorder the write after `Dispose()`. The `Interlocked.Exchange` pattern used in `UHttpResponse` for `_segmentedBodyOwner` is the correct idiom.

**Fix:** Replaced with `Interlocked.Exchange(ref _bodyOwner, null)` for consistency and ARM64 safety.

---

### Bug 4 — `ResetForPool()` reset `_disposed` which is never read for pooled instances

**File:** `Runtime/Core/UHttpRequest.cs`
**Severity:** Medium (design clarity)

`_disposed` is only read by `ThrowIfDisposed()` when `_leaseOwner == null` (standalone, non-pooled requests). For pooled requests, `ThrowIfDisposed()` checks `_isLeased` and `_disposeRequested` instead. The `Interlocked.Exchange(ref _disposed, 0)` in `ResetForPool()` was a no-op that misled future maintainers into thinking `_disposed` participates in the pooled lifecycle.

**Fix:** Removed the reset. Replaced with a comment explaining why `_disposed` is intentionally absent.

---

### Bug 5 — `UHttpClient.SendAsync` accepted pooled requests from a different client

**File:** `Runtime/Core/UHttpClient.cs`
**Severity:** Medium

No validation prevented passing a pooled `UHttpRequest` created by `clientA` to `clientB.SendAsync()`. Since `_leaseOwner` is fixed at construction, the request would silently be returned to the wrong pool on completion.

**Fix:** Added `request.IsOwnedBy(this)` check before `BeginSend()`. Added `IsOwnedBy(UHttpClient)` internal method to `UHttpRequest` using `ReferenceEquals`.

---

## Documentation / Design Decisions Applied

### `UHttpResponse.Request` getter — use-after-dispose hazard

Added `ThrowIfDisposed()` to the `Request` getter and converted from auto-property to backing field `_request`. Added XML doc warning that accessing `Request` after `Dispose()` is undefined for pooled requests.

### `UHttpResponse.Body` — use-after-pool-return contract

Added `<remarks>` documentation to the `Body` property explaining that `ReadOnlySequence<byte>` segments are invalid after `UHttpResponse.Dispose()` — they point to pool-returned memory that may be reused.

### `WithLeasedBody` — deferred Phase 19b.2 item

Added explicit code comment documenting that `Memory.ToArray()` is an intentional copy (not the eventual zero-alloc goal) and that two preconditions must be met before eliminating it:
1. Both HTTP/1.1 serializer and HTTP/2 connection must be updated to accept `ReadOnlyMemory<byte>` instead of `byte[]`
2. The early `DisposeBodyOwner()` call in `Http2Connection` (line 303) must be removed or moved post-send

### `Http2Connection.cs` — DisposeBodyOwner architectural note

Added comment at the `DisposeBodyOwner()` call site in the DATA send path explaining why this is safe only while `Body` is a heap copy, and what must change when `Body` becomes `ReadOnlyMemory<byte>`.

### `UHttpRequest.SendAsync` finally — state machine comment

Added detailed comment explaining how the four state variables (`_sendInProgress`, `_responseHoldCount`, `_disposeRequested`, `_isLeased`) cooperate across the inner/outer finally blocks to ensure exactly one pool return at the correct time.

---

## Files Modified

1. `Runtime/Core/UHttpRequest.cs` — Bugs 1, 3, 4, 5 + `IsOwnedBy` + `WithLeasedBody` deferred doc + `SendAsync` state machine comment
2. `Runtime/Core/UHttpResponse.cs` — Bug 2 + `Request` getter guard + `Body` docs + constructor `_request` backing field
3. `Runtime/Core/UHttpClient.cs` — Bug 5 (`IsOwnedBy` validation in `SendAsync`)
4. `Runtime/Transport/Http2/Http2Connection.cs` — Architectural note on `DisposeBodyOwner` dependency

---

## Phase 19b Sub-Phase Status (Post-Review)

| Sub-Phase | Status |
|---|---|
| 19b.1 — Unify Pipeline (Remove Interceptors) | ✅ Complete |
| 19b.2 — Pooled Request Objects | ✅ Complete |
| 19b.3 — Zero-Allocation Response Bodies | ✅ Complete |
| 19b.4 — Purge Legacy Compatibility | ✅ Complete |

**Phase 19b: COMPLETE** after this fix pass.

---

## Clarification: `WithLeasedBody` ToArray() is NOT a Phase 19b.2 Gap

The specialist review flagged `WithLeasedBody().Memory.ToArray()` as defeating zero-allocation. This is **not** a Phase 19b.2 incompletion — all five Phase 19b.2 requirements (request pool, `CreateRequest`, mutable `With*` methods, pool reset, extension compatibility) are fully satisfied.

The `Memory.ToArray()` copy is a pre-existing limitation of the **transport layer**: both `Http11RequestSerializer` and `Http2Connection` read `request.Body` as `byte[]`. This transport constraint predates Phase 19b. The `_bodyOwner` field is retained only for pool-return lifetime management. Eliminating the copy requires a transport-layer refactor tracked as a separate follow-up item.

---

## Open Follow-Up Items (Transport-layer, not Phase 19b)

1. **`UHttpRequest.Body` as `ReadOnlyMemory<byte>`** — Eliminate `WithLeasedBody().Memory.ToArray()` by updating both HTTP/1.1 and HTTP/2 transport serializers to consume `ReadOnlyMemory<byte>` directly. Remove early `DisposeBodyOwner()` in `Http2Connection` DATA send path as a prerequisite. This is a transport-layer optimization, not a Core API change.
2. **Test: double-dispose of response** — Regression test for Bug 1 (negative `_responseHoldCount` handling).
3. **Test: cross-client pooled request** — Verify `InvalidOperationException` is thrown for Bug 5 fix.
4. **Validate `Interlocked.CompareExchange<Action>` AOT instantiation** — Confirm on physical iOS IL2CPP device that the generic CAS in `AttachRequestRelease` does not require an explicit `link.xml` preserve entry.
