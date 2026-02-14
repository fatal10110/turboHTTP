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

Implementation constraints:

1. Subscribe/unsubscribe to capture events in `OnEnable`/`OnDisable`.
2. Throttle repaint/update frequency to remain responsive under heavy traffic.
3. Guard null collections in UI rendering paths.
4. Keep text rendering safe for large payloads (truncate or fold where needed).

---

## Verification Criteria

1. Monitor window updates when new requests are captured.
2. Selecting a row updates all details tabs correctly.
3. Filters narrow results deterministically.
4. Exported JSON matches selected event data.
5. UI remains usable with large histories near configured cap.
