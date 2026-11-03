# Phase 19a.5: Internal HTTP Object Pooling

**Depends on:** 19a.0, 19a.1
**Estimated Effort:** 3-4 days

---

## Step 0: Keep `UHttpResponse` Non-Pooled (Safety Decision)

**File:** `Runtime/Core/UHttpResponse.cs` (reviewed, no pooling integration)

Required behavior:

1. Do **not** pool `UHttpResponse` instances.
2. Preserve user-owned response lifetime and disposal semantics.
3. Focus pooling on short-lived internal objects instead.

Rationale:

1. `UHttpResponse` is user-visible and may outlive transport internals.
2. Reuse risks stale-data leaks and aliasing bugs.

---

## Step 1: Pool `ParsedResponse` Instances

**File:** `Runtime/Transport/Http1/Http11ResponseParser.cs` (modified)
**File:** `Runtime/Transport/Http1/ParsedResponsePool.cs` (new)

Required behavior:

1. Pool internal `ParsedResponse` objects.
2. Add reset logic for status, headers, body metadata, and keep-alive flags.
3. Return to pool after `UHttpResponse` construction is complete.

Implementation constraints:

1. Reset must clear all references to avoid retention/leaks.
2. Pool usage remains internal to transport assembly.

---

## Step 2: Pool `Http2Stream` Instances

**File:** `Runtime/Transport/Http2/Http2Stream.cs` (modified)
**File:** `Runtime/Transport/Http2/Http2StreamPool.cs` (new)

Required behavior:

1. Pool stream state objects used per HTTP/2 stream lifecycle.
2. Reset stream IDs, flow-control state, awaitable/completion state, and buffered data references.
3. Use pool capacity defaults aligned to expected concurrency.

Implementation constraints:

1. Safe reset on partial/incomplete stream states.
2. Deterministic return of pooled buffers held by stream state.

---

## Step 3: Pool Header Parsing Scratch Objects

**File:** `Runtime/Transport/Http1/Http11ResponseParser.cs` (modified)
**File:** `Runtime/Performance/HeaderParseScratchPool.cs` (new)

Required behavior:

1. Pool temporary dictionaries/lists used during header parsing/normalization.
2. Keep final `HttpHeaders` instances independent from pooled scratch state.
3. Ensure all scratch containers are cleared before return.

Implementation constraints:

1. No cross-request header data leakage.
2. Preserve case-insensitive HTTP header semantics.

---

## Step 4: Pool `HpackEncoder` Writer State

**File:** `Runtime/Transport/Http2/HpackEncoder.cs` (modified)

Required behavior:

1. Pool reusable encoder state objects (metadata, scratch structures, writer wrappers).
2. Return pooled state in `finally` blocks.

Implementation constraints:

1. Encoding output must remain byte-identical.
2. Pooling must not change dynamic table correctness.

---

## Verification Criteria

1. `UHttpResponse` lifetime semantics are unchanged.
2. Internal pooled objects reset fully and safely.
3. Pool diagnostics show stable active counts under steady load.
4. No security leaks across pooled internal state.
5. Existing HTTP/1.1 and HTTP/2 tests pass.
