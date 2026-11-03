# Phase 9.2: Platform Configuration Rules

**Depends on:** Phase 9.1
**Assembly:** `TurboHTTP.Core`
**Files:** 1 new

---

## Step 1: Create `PlatformConfig`

**File:** `Runtime/Core/PlatformConfig.cs`

Required behavior:

1. Provide recommended default timeout per platform class (mobile vs desktop).
2. Provide recommended max concurrency per platform class.
3. Expose capability helpers for TLS and certificate-validation customization.
4. Provide one call (`LogPlatformInfo`) to log platform defaults at startup.

Implementation constraints:

1. Return deterministic values without network probing.
2. Use `TimeSpan` and integer values with explicit defaults.
3. Keep platform assumptions documented in XML comments.
4. Do not mutate global state when logging.

---

## Verification Criteria

1. Timeout and concurrency recommendations are positive and platform-appropriate.
2. Capability helpers return stable values across repeated calls.
3. Startup logs contain platform description and recommended defaults.
4. Platform configuration can be consumed by `UHttpClientOptions` without custom adapters.
