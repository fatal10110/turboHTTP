# Phase 14.3: Background Networking on Mobile

**Depends on:** Phase 11
**Assembly:** `TurboHTTP.Mobile`, `TurboHTTP.Unity`, `TurboHTTP.Tests.Runtime`
**Files:** 3 new, 2 modified

---

## Step 1: iOS Background Execution Bridge

**Files:**
- `Runtime/Mobile/iOS/IosBackgroundTaskBridge.cs` (new)
- `Runtime/Unity/UnityExtensions.cs` (modify)

Required behavior:

1. Wrap request execution with `BeginBackgroundTask` / `EndBackgroundTask`.
2. Track expiration callbacks and enforce graceful cancellation before OS hard-kill.
3. Emit timeline diagnostics for background task acquisition/release.

Implementation constraints:

1. Always end iOS background task tokens in success, error, and cancellation paths.
2. Prevent token leaks across retries/redirects.
3. Keep editor/non-iOS behavior as no-op.

---

## Step 2: Android Background Execution Bridge

**Files:**
- `Runtime/Mobile/Android/AndroidBackgroundWorkBridge.cs` (new)
- `Runtime/Mobile/Android/AndroidBackgroundWorkConfig.cs` (new)

Required behavior:

1. Support deferred background operations via `WorkManager` or equivalent bridge.
2. Support long-running transfer path via foreground-service integration hooks.
3. Provide platform capability detection and fallback strategy.

Implementation constraints:

1. Keep Unity runtime API surface independent of Android-specific implementation details.
2. Avoid duplicate enqueues across app resume/retry boundaries.
3. Preserve explicit user cancellation semantics.

---

## Step 3: Add `BackgroundNetworkingMiddleware`

**File:** `Runtime/Core/UHttpClientOptions.cs` (modify)

Required behavior:

1. Add middleware that wraps request execution in platform background guards.
2. Queue failed background requests for resume-time retry where configured.
3. Expose policy knobs for queue size, retry budget, and expiration handling.

---

## Verification Criteria

1. In-flight requests survive expected short background windows on iOS/Android.
2. Expired background windows fail deterministically and trigger fallback policy.
3. Foreground flows remain unchanged when background mode is disabled.
4. Mobile lifecycle tests verify pause/resume edge cases without deadlocks.
