# Phase 7 Implementation Plan — Overview

Phase 7 is broken into 6 sub-phases executed sequentially. Each sub-phase is self-contained with its own files, verification criteria, and review checkpoints.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [7.1](phase-7.1-mock-transport.md) | Mock Transport Extensions | 1 modified | Phase 6 |
| [7.2](phase-7.2-record-replay-transport.md) | Record/Replay Transport | 1 new | 7.1 |
| [7.3](phase-7.3-test-helpers.md) | Test Helpers | 1 new | 7.1 |
| [7.4](phase-7.4-core-type-tests.md) | Core Type Unit Tests | 1 new | 7.1, 7.3 |
| [7.5](phase-7.5-integration-tests.md) | Integration Test Suite | 1 new | 7.1, 7.2, 7.3 |
| [7.6](phase-7.6-benchmarks-and-coverage.md) | Benchmarks, Coverage, and Quality Gates | 1 new | 7.4, 7.5 |

## Dependency Graph

```text
Phase 6 (done)
    └── 7.1 Mock Transport
         ├── 7.2 Record/Replay Transport
         └── 7.3 Test Helpers
              ├── 7.4 Core Type Unit Tests
              └── 7.5 Integration Test Suite
                   └── 7.6 Benchmarks, Coverage, and Quality Gates
```

Sub-phases 7.2 and 7.3 can run in parallel after 7.1. Sub-phases 7.4 and 7.5 can run in parallel once 7.3 is done.

## Existing Foundation (Phases 4 + 6)

### Existing Types Used in Phase 7

| Type | Key APIs for Phase 7 |
|------|----------------------|
| `IHttpTransport` | testing transport abstraction |
| `UHttpClient` | request execution in unit/integration paths |
| `UHttpRequest` / `UHttpResponse` | fixture and assertion surface |
| `UHttpError` / `UHttpException` | error-path validation |
| Performance middleware/types | validation under load and benchmark paths |

### Assembly Structure

| Assembly | References | autoReferenced | Notes |
|----------|-----------|----------------|-------|
| `TurboHTTP.Testing` | Core | false | Mock and record/replay transport |
| `TurboHTTP.Tests.Runtime` | All runtime modules | false | Unit, integration, benchmark tests |

## Cross-Cutting Design Decisions

1. **Deterministic first:** unit tests should default to mock transport.
2. **Record/replay is explicit:** recordings are opt-in, schema-versioned, and stored at known paths.
3. **AOT-safe serializer paths only:** testing serialization must work under IL2CPP builds when included.
4. **Redaction by default:** sensitive data must never be persisted unredacted.
5. **Separation of integration categories:** real-internet tests remain optional for CI stability.
6. **Benchmarks are guardrails, not vanity metrics:** focus on regressions and thresholds.

## All Files (5 new, 1 modified)

| # | Action | Path | Assembly |
|---|--------|------|----------|
| 1 | Modify | `Runtime/Testing/MockTransport.cs` | Testing |
| 2 | Create | `Runtime/Testing/RecordReplayTransport.cs` | Testing |
| 3 | Create | `Tests/Runtime/TestHelpers.cs` | Tests |
| 4 | Create | `Tests/Runtime/Core/CoreTypesTests.cs` | Tests |
| 5 | Create | `Tests/Runtime/Integration/IntegrationTests.cs` | Tests |
| 6 | Create | `Tests/Runtime/Performance/BenchmarkTests.cs` | Tests |

## Post-Implementation

1. Run both specialist agent reviews (unity-infrastructure-architect, unity-network-architect).
2. Run all runtime tests with integration category split (deterministic vs external).
3. Create or update implementation journal entry for Phase 7.
4. Update `CLAUDE.md` development status for Phase 7 completion.
