# Phase 12.1: Monitor Event Data Model

**Depends on:** Phase 11
**Assembly:** `TurboHTTP.Observability`
**Files:** 1 new

---

## Step 1: Create `HttpMonitorEvent`

**File:** `Runtime/Observability/HttpMonitorEvent.cs`

Required behavior:

1. Capture request metadata (method, URL, headers, body snapshot).
2. Capture response metadata (status, headers, body snapshot, errors).
3. Capture timing metadata (timestamp, elapsed, timeline events).
4. Include body snapshot metadata (`OriginalRequestBodySize`, `OriginalResponseBodySize`) so truncation is explicit in UI/export.
5. Provide helpers for safe request/response body text rendering.

Implementation constraints:

1. Use bounded body snapshots (truncate by configurable size in capture layer).
2. Preserve monitor event immutability after capture.
3. Keep helper decoding resilient to invalid byte sequences.
4. Distinguish transport failures from HTTP status errors.

---

## Verification Criteria

1. Event model holds all fields required by monitor UI tabs.
2. Body helpers return empty string for null/empty bodies.
3. Timeline data is preserved in capture order.
4. Serialized event output is stable for export and replay tooling.
5. Truncated bodies are clearly marked and original byte sizes are available to UI/export.
