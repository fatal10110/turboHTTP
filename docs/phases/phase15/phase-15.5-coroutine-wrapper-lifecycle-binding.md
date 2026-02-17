# Phase 15.5: Coroutine Wrapper Lifecycle Binding

**Depends on:** Phase 15.1, Phase 15.4
**Assembly:** `TurboHTTP.Unity`, `TurboHTTP.Tests.Runtime`
**Files:** 2 new, 1 modified

---

## Step 1: Add Lifecycle-Bound Cancellation Contract

**Files:**
- `Runtime/Unity/LifecycleCancellation.cs` (new)
- `Runtime/Unity/CoroutineWrapper.cs` (modify)

Required behavior:

1. Add optional owner binding (`MonoBehaviour` or `GameObject`) for coroutine wrappers.
2. Auto-cancel bound operations when owner is destroyed/invalidated.
3. Support explicit cancellation token linking with owner lifecycle cancellation.
4. Keep callback dispatch on main thread via dispatcher guarantees.
5. Define owner-destruction check as Unity native-object null semantics (`owner == null` / destroyed handle).
6. Owner inactive state (`activeInHierarchy == false`) does not auto-cancel by default; optional policy flag may enable that behavior.

Implementation constraints:

1. Cancellation linking must be leak-safe (dispose linked token sources deterministically).
2. Owner checks must tolerate Unity null-overload semantics.
3. Lifecycle monitoring must not allocate per-frame polling garbage.
4. Cancellation cause should be distinguishable (owner destroyed vs explicit token vs timeout).
5. Destruction and deactivation policies must be separately testable and documented.

---

## Step 2: Enforce Exactly-Once Terminal Callback Semantics

**Files:**
- `Runtime/Unity/CoroutineWrapper.cs` (modify)
- `Runtime/Unity/LifecycleCancellation.cs` (new)

Required behavior:

1. Success callback fires exactly once for successful completion.
2. Error callback fires exactly once for failure.
3. No callbacks fire after owner teardown or cancellation.
4. Preserve deterministic root exception unwrapping and stack fidelity.
5. Mixed race cases (cancel + failure) resolve predictably with single terminal outcome.

Implementation constraints:

1. Use atomic terminal-state guard to prevent duplicate callbacks.
2. Never swallow non-cancellation exceptions silently.
3. Preserve existing API shape where possible; introduce overloads for lifecycle binding.
4. Ensure callback order is stable and documented.

---

## Step 3: Add Lifecycle-Race Test Coverage

**File:** `Tests/Runtime/Unity/CoroutineWrapperLifecycleTests.cs` (new)

Required behavior:

1. Validate owner destruction before completion suppresses success callback.
2. Validate failure path still triggers exactly one error callback.
3. Validate mixed cancellation/failure race remains deterministic.
4. Validate callbacks always execute on main thread when they execute.
5. Validate inactive-owner behavior follows configured policy (default no auto-cancel).

---

## Verification Criteria

1. Lifecycle-bound wrappers cannot call success/error callbacks after owner teardown.
2. Terminal callback semantics are exactly-once in all tested race conditions.
3. Root exception details remain intact and actionable.
4. Wrapper behavior remains compatible with existing coroutine usage patterns.
