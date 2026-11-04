# Phase 11.4: Unity Helper Extensions

**Depends on:** Phase 11.2, Phase 11.3
**Assembly:** `TurboHTTP.Unity`
**Files:** 1 new

---

## Step 1: Add Unity Convenience Extensions

**File:** `Runtime/Unity/UnityExtensions.cs`

Required behavior:

1. Add helpers to download content into `persistentDataPath` and `temporaryCachePath`.
2. Add `CreateUnityClient(...)` helper with Unity-friendly defaults.
3. Keep helper APIs composable with existing `UHttpClientOptions` configuration.

Implementation constraints:

1. Validate and normalize file paths before writing content.
2. Create destination directories when missing.
3. Ensure file writes honor cancellation tokens where supported.
4. Use `Path.Combine` for all path joins and `Path.GetFullPath` for canonicalization before validation checks.
5. Do not hardcode platform-specific separators.

---

## Verification Criteria

1. Helper downloads produce files in expected Unity data paths.
2. `CreateUnityClient` applies default headers and still allows overrides.
3. Path validation prevents invalid traversal inputs.
4. Helpers behave consistently in Editor and Player builds.
5. Path composition uses `Path.Combine`-based joins and works across Windows/macOS/Linux targets.
