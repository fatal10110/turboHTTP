# Phase 12 (Editor Tooling / HTTP Monitor) Review — 2026-02-20

Comprehensive review performed by both specialist agents (`unity-infrastructure-architect` and `unity-network-architect`) on the Phase 12 Editor Tooling implementation.

## Review Scope

**Files reviewed:**
- `Runtime/Observability/HttpMonitorEvent.cs` — Immutable event data model
- `Runtime/Observability/MonitorMiddleware.cs` — Ring-buffer capture middleware
- `Editor/Monitor/HttpMonitorWindow.cs` — Main editor window
- `Editor/Monitor/HttpMonitorWindow.Panels.cs` — UI rendering panels
- `Editor/Monitor/HttpMonitorWindow.Export.cs` — JSON export
- `Editor/Monitor/HttpMonitorWindow.Replay.cs` — Request replay
- `Editor/Settings/TurboHttpSettings.cs` — Preferences panel
- `Tests/Runtime/Observability/MonitorMiddlewareTests.cs` — Tests

## Issues Found and Fixed

### Critical / High Severity

| ID | Source | Severity | Description | Fix |
|---|---|---|---|---|
| C-2 | Infra | Critical | Double-copy of body bytes — `MonitorMiddleware.CreateBodySnapshot` already makes a defensive copy, then `HttpMonitorEvent.CloneBody` copies again (2x memory for large bodies) | Removed redundant `.ToArray()` in `CloneBody`, now passes through directly |
| H-1 | Net | High | Multi-value headers silently flattened — `CopyHeaders` used `IEnumerable` enumerator which only yields first value per header name. `Set-Cookie`, `Via`, etc. lost data | Rewrote `CopyHeaders` to use `headers.Names` + `headers.GetValues()` with RFC 6265-compliant `;` separator for `Set-Cookie` and `,` for others |
| C-6 | Infra | High | `HeaderValueTransform` property not thread-safe — auto-property read/write not guaranteed atomic on all platforms | Replaced with `Volatile.Read`/`Volatile.Write` backed field. Same treatment for `DiagnosticLogger` |
| H-2 | Net | High | Export writes confidential headers in cleartext when UI masking toggle is off — credential leakage risk in shared/committed files | `ToExportHeaders` now always masks confidential headers regardless of UI toggle |
| H-4 | Net | High | No warning when replaying non-idempotent methods (POST/PATCH/DELETE) — accidental side effects | Added non-idempotent method warning as first check in `BuildReplayWarning` |
| H-2 | Infra | High | `ReplayEvent` fire-and-forget `_ = ReplayEventAsync(evt)` swallows unobserved exceptions | Changed to `async void` with try/catch wrapper for proper exception logging |

### Medium Severity

| ID | Source | Description | Fix |
|---|---|---|---|
| M-9 | Net | ConfidentialHeaders missing `WWW-Authenticate` (inconsistent with `LoggingMiddleware.DefaultSensitiveHeaders`) | Added `WWW-Authenticate` to the set |
| M-1 | Net | Raw tab showed custom format instead of HTTP wire format approximation | Rewrote to show `METHOD /path HTTP/1.1`, `Host:` header, bare headers, status line as `HTTP/1.1 CODE TEXT` |
| M-2 | Net | Misleading `[Serializable]` on `HttpMonitorEvent` and `HttpMonitorTimelineEvent` (contain non-serializable `ReadOnlyMemory<byte>` and `IReadOnlyDictionary` fields) | Removed attribute, added doc comment directing to Export path |

### Test Coverage Gaps Addressed

| Test | Description |
|---|---|
| `HeaderValueTransformRedactsCaputuredHeaderValues` | Verifies transform hook redacts `Authorization` while passing through other headers |
| `CapturesMultiValueHeaders` | Verifies `Set-Cookie` multi-value uses `; ` separator and other headers use `, ` separator |

### Deferred / Documented (Low priority)

- **L-1**: `JsonUtility` for export — acceptable for Editor-only code, model types designed around its limitations
- **L-5**: Binary detection false-positive for UTF-16/UTF-32 — mitigated by content-type check running first
- **M-4**: Body capture GC pressure (5MB allocations) — Editor-only overhead, acceptable when monitoring is disabled in production
- **M-7/M-8**: OnGUI per-frame allocations for body text and StringBuilder — Editor UI code, low impact

## Architecture Assessment

**Overall Grade: A-** (after fixes)

Strengths:
- Clean module separation (Observability runtime + Editor UI)
- Proper immutable event model with defensive copying
- Thread-safe ring buffer with bounded capacity
- Throttled UI repaints preventing Editor stalls
- Comprehensive error isolation in capture and event publishing
- Well-designed export model working around `JsonUtility` limitations

All critical and high severity issues have been resolved. The implementation is ready for shipping.
