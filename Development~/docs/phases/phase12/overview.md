# Phase 12 Implementation Plan - Overview

Phase 12 is broken into 4 sub-phases. Runtime event capture comes first, then the Editor monitor UI and settings integration.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [12.1](phase-12.1-monitor-event-model.md) | Monitor Event Data Model | 1 new | Phase 11 |
| [12.2](phase-12.2-monitor-middleware.md) | Monitor Collector Middleware | 1 new | 12.1 |
| [12.3](phase-12.3-http-monitor-window.md) | HTTP Monitor Editor Window | 1 new | 12.2 |
| [12.4](phase-12.4-editor-settings.md) | Editor Settings and Auto-Enable | 1 new | 12.3 |

## Dependency Graph

```text
Phase 11 (done)
    └── 12.1 Monitor Event Data Model
         └── 12.2 Monitor Collector Middleware
              └── 12.3 HTTP Monitor Editor Window
                   └── 12.4 Editor Settings and Auto-Enable
```

## Existing Foundation (Phases 4 + 8 + 11)

### Existing Types Used in Phase 12

| Type | Key APIs for Phase 12 |
|------|----------------------|
| `IHttpMiddleware` | request/response capture point |
| `RequestContext` | timeline and elapsed metrics |
| `UHttpRequest` / `UHttpResponse` | monitor payload snapshots |
| `HttpHeaders` | request and response header representation |
| Unity Editor APIs | monitor window, settings provider, editor preferences |

### Assembly Structure

| Assembly | References | autoReferenced | Notes |
|----------|-----------|----------------|-------|
| `TurboHTTP.Observability` | Core | false | monitor event model and capture middleware |
| `TurboHTTP.Editor` | Core, Observability | false | monitor window and settings integration |

## Cross-Cutting Design Decisions

1. Monitor capture must never break or block request processing.
2. Captured history must be bounded and configurable.
3. Payload capture policy should favor debugging fidelity: text payloads are captured up to configurable `MaxCaptureSize` (default 5 MB), while binary payloads are stored as preview/metadata to avoid OOM.
4. Header masking is optional and disabled by default so auth/debug workflows remain visible unless explicitly masked.
5. Editor UI updates should be efficient and resilient under heavy request volume, avoiding per-`OnGUI` history snapshot allocations.
6. Capture notifications may arrive off the main thread, and window refresh logic must marshal Unity API calls back to the main thread.
7. Runtime capture and Editor visualization must stay loosely coupled.

## All Files (4 new)

| # | Action | Path | Assembly |
|---|--------|------|----------|
| 1 | Create | `Runtime/Observability/HttpMonitorEvent.cs` | Observability |
| 2 | Create | `Runtime/Observability/MonitorMiddleware.cs` | Observability |
| 3 | Create | `Editor/Monitor/HttpMonitorWindow.cs` | Editor |
| 4 | Create | `Editor/Settings/TurboHttpSettings.cs` | Editor |

## Post-Implementation

1. Validate monitor behavior under sustained request traffic.
2. Validate editor tooling only compiles in Unity Editor assemblies.
3. Run manual UX checks (filter, export, clear, selection, timeline).
4. Validate large payload behavior (large JSON preserved, binary assets preview-only, no Editor OOM spikes).
5. Validate header masking toggle behavior (default off, opt-in mask on common confidential headers).
6. Gate release on no runtime regressions when monitor is disabled.
