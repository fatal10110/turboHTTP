# Phase 14.1: Happy Eyeballs (RFC 8305)

**Depends on:** Phase 12
**Assembly:** `TurboHTTP.Transport`, `TurboHTTP.Tests.Runtime`
**Files:** 2 new, 1 modified

---

## Step 1: Implement Dual-Stack Connection Racing

**Files:**
- `Runtime/Transport/Connection/HappyEyeballsConnector.cs` (new)
- `Runtime/Transport/RawSocketTransport.cs` (modify)

Required behavior:

1. Prefer IPv6-first ordering to preserve iOS dual-stack expectations.
2. Start first IPv6 attempt, then stagger IPv4 attempt by `250ms` (configurable).
3. Return the first successful socket and cancel remaining attempts.
4. Aggregate final failure details when all address families fail.
5. Keep DNS result ordering stable inside each family bucket.

Implementation constraints:

1. Preserve existing timeout and cancellation semantics.
2. Ensure losing sockets/tasks are disposed/cancelled without leaks.
3. Avoid unbounded task fan-out for large DNS result sets.
4. Keep behavior deterministic in tests with virtual timers/schedulers.

---

## Step 2: Add Coverage for Broken IPv6 Scenarios

**File:** `Tests/Runtime/Transport/HappyEyeballsTests.cs` (new)

Required behavior:

1. Verify fallback latency is lower than sequential IPv6-then-IPv4 strategy.
2. Verify IPv6 wins when both families are healthy and IPv6 completes first.
3. Verify IPv4 wins when IPv6 path is broken/stalled.
4. Verify cancellation propagates across both race branches.

---

## Verification Criteria

1. Connection setup on broken IPv6 networks no longer waits full socket timeout before IPv4.
2. Fastest-family selection is deterministic and leak-free.
3. Existing connection error typing remains actionable.
4. Happy Eyeballs path is fully covered by deterministic tests.
