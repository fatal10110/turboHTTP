# Phase 12.4: Editor Settings and Auto-Enable

**Depends on:** Phase 12.3
**Assembly:** `TurboHTTP.Editor`
**Files:** 1 new

---

## Step 1: Add Editor Settings Integration

**File:** `Editor/Settings/TurboHttpSettings.cs`

Required behavior:

1. Add Unity Preferences entry for TurboHTTP monitor behavior.
2. Persist monitor-enabled preference via `EditorPrefs`.
3. Provide quick action to open the HTTP Monitor window.
4. Auto-enable monitor wiring in play mode when setting is enabled.
5. Persist monitor payload settings (`MaxCaptureSize`) and optional default confidential-header masking behavior.

Implementation constraints:

1. Editor-only code must stay out of runtime assemblies.
2. Auto-enable logic must be idempotent across domain reloads.
3. Disable path must cleanly avoid extra listeners/subscriptions.
4. Settings defaults should keep monitor enabled for editor debugging.
5. Settings defaults should prioritize debugging fidelity: `MaxCaptureSize` default 5 MB and header masking default off.
6. `EditorPrefs` keys must use a unique TurboHTTP prefix (for example `TurboHTTP_*`) to avoid collisions with other packages.

---

## Verification Criteria

1. Preferences toggle persists across Unity restarts.
2. Enabling/disabling setting affects monitor wiring in next play session.
3. "Open HTTP Monitor" action opens or focuses the monitor window.
4. Disabling monitor leaves runtime request behavior unchanged.
5. `MaxCaptureSize` and default masking preferences persist and are applied on domain reload/play mode entry.
