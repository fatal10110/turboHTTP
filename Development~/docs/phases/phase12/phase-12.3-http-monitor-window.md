# Phase 12.3: HTTP Monitor Editor Window

**Depends on:** Phase 12.2
**Assembly:** `TurboHTTP.Editor`
**Files:** 1 new

---

## Step 1: Implement `HttpMonitorWindow`

**File:** `Editor/Monitor/HttpMonitorWindow.cs`

Required behavior:

1. Render request list with method, URL, status, and elapsed time.
2. Render details tabs for request, response, timeline, and raw payloads.
3. Support filter controls (URL, method; optional status) and clear action.
4. Support export of selected event to JSON.
5. Support optional "Mask Confidential Headers" toggle (default off).

Implementation constraints:

1. Subscribe/unsubscribe to capture events in `OnEnable`/`OnDisable`.
2. Cache history data in-window and refresh only when capture events indicate data changed.
3. Marshal capture event UI work onto main thread (`EditorApplication.delayCall` or equivalent) before invoking Unity UI APIs.
4. Throttle repaint/update frequency to remain responsive under heavy traffic.
5. Guard null collections in UI rendering paths.
6. Keep body rendering safe for binary/large payloads using event helper methods (placeholder/preview text where applicable).
7. Header masking must be applied only when the toggle is enabled.

---

## Verification Criteria

1. Monitor window updates when new requests are captured.
2. Selecting a row updates all details tabs correctly.
3. Filters narrow results deterministically.
4. Exported JSON matches selected event data.
5. UI remains usable with large histories near configured cap.
6. Request list rendering does not allocate new history snapshots each `OnGUI` cycle.
7. Toggle-based header masking works and defaults to unmasked output.
