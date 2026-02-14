# Phase 9 Implementation Plan - Overview

Phase 9 is broken into 5 sub-phases executed sequentially with one parallel branch (9.2 and 9.3 after 9.1). Each sub-phase is self-contained with explicit files, verification criteria, and release gates.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [9.1](phase-9.1-platform-detection.md) | Platform Detection Utility | 1 new | Phase 8 |
| [9.2](phase-9.2-platform-configuration.md) | Platform Configuration Rules | 1 new | 9.1 |
| [9.3](phase-9.3-il2cpp-compatibility.md) | IL2CPP Compatibility Validation | 1 new | 9.1 |
| [9.4](phase-9.4-platform-test-suite.md) | Platform Test Suite and Matrix | 1 new | 9.2, 9.3 |
| [9.5](phase-9.5-platform-documentation.md) | Platform Notes and Troubleshooting | 1 new | 9.4 |

## Dependency Graph

```text
Phase 8 (done)
    └── 9.1 Platform Detection Utility
         ├── 9.2 Platform Configuration Rules
         └── 9.3 IL2CPP Compatibility Validation
              └── 9.4 Platform Test Suite and Matrix
                   └── 9.5 Platform Notes and Troubleshooting
```

Sub-phases 9.2 and 9.3 can run in parallel after 9.1. Sub-phase 9.4 gates runtime support claims, and 9.5 publishes validated guidance.

## Existing Foundation (Phases 3C + 6 + 7 + 8)

### Existing Types Used in Phase 9

| Type | Key APIs for Phase 9 |
|------|----------------------|
| `UHttpClient` | request execution across Editor, standalone, iOS, Android |
| `UHttpClientOptions` | timeout and middleware configuration |
| `IHttpTransport` | transport abstraction for platform tests |
| `RecordReplayTransport` | deterministic replay for platform-safe test lanes |
| `RequestContext` | timeline and diagnostics used by platform validation |

### Assembly Structure

| Assembly | References | autoReferenced | Notes |
|----------|-----------|----------------|-------|
| `TurboHTTP.Core` | none | true | platform detection/config and IL2CPP checks |
| `TurboHTTP.Tests.Runtime` | runtime modules | false | platform validation tests |
| `Documentation~` | n/a | n/a | support matrix and troubleshooting guide |

## Cross-Cutting Design Decisions

1. Platform checks must be deterministic and not depend on internet access.
2. IL2CPP validation is a release gate, not optional smoke coverage.
3. External-network platform tests are opt-in and cannot block deterministic CI lanes.
4. Support claims require tested matrix evidence (platform + backend + result).
5. Platform documentation must reflect validated behavior only.

## All Files (5 new)

| # | Action | Path | Assembly |
|---|--------|------|----------|
| 1 | Create | `Runtime/Core/PlatformInfo.cs` | Core |
| 2 | Create | `Runtime/Core/PlatformConfig.cs` | Core |
| 3 | Create | `Runtime/Core/IL2CPPCompatibility.cs` | Core |
| 4 | Create | `Tests/Runtime/Platform/PlatformTests.cs` | Tests |
| 5 | Create | `Documentation~/PlatformNotes.md` | Documentation |

## Post-Implementation

1. Run platform test lanes (Editor/Mono, standalone/IL2CPP, mobile/IL2CPP).
2. Confirm HTTP/2 ALPN behavior and fallback behavior per platform.
3. Update release checklist with matrix evidence from Phase 9.
4. Run specialist reviews before moving to Phase 10.
