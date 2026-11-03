# Phase 12 Plan Review

## Summary
The Phase 12 plan is well-structured and covers the key components for the Editor Monitoring tool. The breakdown into sub-phases (Model, Middleware, Window, Settings) is logical.

However, there are a few implementation details in the provided code snippets (in `phase-12-editor-tools.md`) that do not fully align with the requirements stated in the sub-phase documents, particularly regarding performance and memory safety.

## Key Findings

### 1. Memory Safety (Large Payloads)
- **Observation**: The current plan allows full capture of all bodies.
- **Context**: As an Editor tool, visibility is key. Developers often need to inspect full JSON/XML responses. However, capturing massive binary assets (Textures, AssetBundles) can still crash the Editor (OOM).
- **Recommendation**:
    - **Do NOT** truncate text-based payloads by default (or set a very high limit, e.g., 5MB).
    - **DO** detect binary/large content types (images, videos, asset bundles) and default to capturing a "preview" or metadata only, with a "Download/Save" option if possible, or just a placeholder to avoid filling memory with 50MB texture arrays.
    - Add a user setting: `MaxCaptureSize` (default: 5MB?).

### 2. Editor Performance (GC Allocations)
- **Issue**: `MonitorMiddleware.History` property returns `_history.ToList()` inside a lock.
- **Observation**: `HttpMonitorWindow.OnGUI` calls this property implicitly via `DrawToolbar` and `DrawRequestList`. `OnGUI` can run multiple times per frame (e.g., on mouse movement).
- **Impact**: Creating a new `List<HttpMonitorEvent>` (which implies an array allocation) every `OnGUI` call will generate significant garbage, causing Editor stuttering.
- **Recommendation**:
    - Change `MonitorMiddleware.History` to return `IReadOnlyList<HttpMonitorEvent>` or expose a thread-safe way to access it without allocation (e.g., `GetHistorySnapshot(List<HttpMonitorEvent> buffer)`).
    - Alternatively, make `HttpMonitorWindow` cache the list and only update it when `OnRequestCaptured` event fires, rather than fetching it in `OnGUI`.

### 3. Redaction Support
- **Context**: In development usage, seeing Auth tokens is often necessary for debugging.
- **Recommendation**:
    - Do **not** redact by default.
    - Optionally add a "Mask Confidential Headers" toggle in the Monitor window toolbar (default: off).
    - If enabled, mask common headers like `Authorization` or `X-Auth-Token`.

### 4. Binary Data Handling
- **Issue**: `HttpMonitorEvent.GetRequestBodyAsString()` assumes UTF8.
- **Observation**: If the request body is binary (e.g., an image or protobuf), `GetString` might return garbage or be slow.
- **Recommendation**: Check `Content-Type` header (if available) or check for null bytes before attempting to convert to string. If binary, return `"<Binary Data: X bytes>"` or a hex preview.

### 5. Thread Safety in UI
- **Issue**: `MonitorMiddleware` fires `OnRequestCaptured` on the network thread.
- **Observation**: `HttpMonitorWindow` subscribes and calls `Repaint()`.
- **Impact**: While `Repaint()` is generally thread-safe in modern Unity, it's good practice to ensure UI updates are scheduled on the main thread if they involve more than just setting a dirty flag (e.g., if you later add logic to read data).
- **Recommendation**: Consider using `EditorApplication.delayCall` or `MainThreadDispatcher` (from Phase 11) to marshal the event to the main thread before notifying the window, or ensure `HttpMonitorWindow` only sets a "needs update" flag that `OnGUI` checks.

## Action Items
1.  **Update `MonitorMiddleware.cs` snippet**:
    - Add configurable body size limit (default high, e.g., 5MB).
    - Fix `History` access pattern to be allocation-free or cached.
2.  **Update `HttpMonitorEvent.cs` snippet**:
    - Add `OriginalRequestSize` / `OriginalResponseSize` properties.
    - Improve `GetBodyAsString` to handle binary data gracefully and robustly.
3.  **Refine `HttpMonitorWindow.cs`**:
    - Cache the history list and update only on events.
    - Handle binary body display in "Raw" or "Body" tabs.
    - (Optional) Add toggle for header masking.

