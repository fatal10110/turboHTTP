# Phase 10.5: Rate Limit Policy Model

**Depends on:** Phase 9
**Assembly:** `TurboHTTP.RateLimit`
**Files:** 1 new

---

## Step 1: Create `RateLimitPolicy`

**File:** `Runtime/RateLimit/RateLimitConfig.cs`

Required behavior:

1. Define defaults for max requests and refill window.
2. Support per-host mode and global mode.
3. Support host-specific overrides.
4. Support over-limit behavior mode (wait vs fail-fast) for middleware use.

Implementation constraints:

1. Validate policy values (`MaxRequests > 0`, positive window).
2. Keep override lookup case-insensitive for host keys.
3. Keep policy object immutable after startup where feasible.
4. Document behavior for unknown hosts.

---

## Verification Criteria

1. Invalid policy values fail fast with actionable exceptions.
2. Override lookup returns host-specific policy when configured.
3. Global mode bypasses per-host lookup correctly.
4. Policy can be serialized in diagnostics and test fixtures.
