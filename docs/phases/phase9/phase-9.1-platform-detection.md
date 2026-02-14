# Phase 9.1: Platform Detection Utility

**Depends on:** Phase 8
**Assembly:** `TurboHTTP.Core`
**Files:** 1 new

---

## Step 1: Create `PlatformInfo`

**File:** `Runtime/Core/PlatformInfo.cs`

Required behavior:

1. Expose runtime platform identity via Unity runtime APIs.
2. Provide boolean helpers for Editor, desktop, mobile, iOS, and Android.
3. Provide backend helper for IL2CPP detection using compile-time defines.
4. Provide `GetPlatformDescription()` for concise diagnostics.

Implementation constraints:

1. Use `Application.platform` and `Application.unityVersion` only; no reflection.
2. Keep helper properties allocation-free.
3. IL2CPP check must use `ENABLE_IL2CPP` to avoid runtime heuristics.
4. API must be safe in both Editor and Player contexts.

---

## Verification Criteria

1. Platform helpers return expected values in Editor and Player builds.
2. IL2CPP backend flag is `true` in IL2CPP player builds and `false` in Mono.
3. `GetPlatformDescription()` includes platform, backend, and Unity version.
4. No platform checks throw in tests or startup logs.
