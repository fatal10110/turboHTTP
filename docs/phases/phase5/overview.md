# Phase 5 Implementation Plan — Overview

Phase 5 is broken into 6 sub-phases executed sequentially (with 5.3-5.5 parallelizable after 5.1-5.2). Each sub-phase is self-contained with its own files, verification criteria, and review checkpoints.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [5.1](phase-5.1-response-encoding.md) | UHttpResponse Content Helpers | 1 modified | Phase 4 |
| [5.2](phase-5.2-content-types.md) | Content Type Constants | 1 new | 5.1 |
| [5.3](phase-5.3-json-extensions.md) | JSON Extensions | 1 new | 5.1, 5.2, Phase 3D |
| [5.4](phase-5.4-file-downloader.md) | File Downloader | 1 new | 5.1, 5.2 |
| [5.5](phase-5.5-multipart-builder.md) | Multipart Form Data Builder | 1 new | 5.2 |
| [5.6](phase-5.6-tests.md) | Tests and Integration | 4 new | 5.1-5.5 |

## Dependency Graph

```text
Phase 4 (done)
    └── 5.1 UHttpResponse Content Helpers
         └── 5.2 Content Type Constants
              ├── 5.3 JSON Extensions
              ├── 5.4 File Downloader
              └── 5.5 Multipart Form Data Builder
                   └── 5.6 Tests and Integration
```

Sub-phases 5.3, 5.4, and 5.5 can be implemented in parallel once 5.1 and 5.2 are complete. Sub-phase 5.6 verifies all content handlers.

## Existing Foundation (Phases 3D + 4)

### Core Types (all in `Runtime/Core/`, namespace `TurboHTTP.Core`)

| Type | Key APIs for Phase 5 |
|------|----------------------|
| `UHttpResponse` | `Body`, `Headers`, `GetBodyAsString()`, `EnsureSuccessStatusCode()` |
| `UHttpRequestBuilder` | `WithBody()`, `WithJsonBody()`, `ContentType()`, `Accept()` |
| `UHttpClient` | `Get()`, `Post()`, `Put()`, `Patch()`, `Delete()` |
| `HttpHeaders` | `Set()`, `Get()`, `Contains()` |
| `UHttpException` | thrown by `EnsureSuccessStatusCode()` |

### JSON Foundation (`Runtime/JSON/`, namespace `TurboHTTP.JSON`)

| Type | Key APIs for Phase 5 |
|------|----------------------|
| `IJsonSerializer` | `Serialize()`, `Deserialize<T>()` |
| `JsonSerializer` facade | static `Serialize()`, `Deserialize<T>()`, `Current` |
| `LiteJsonSerializer` | default serializer implementation |

### Assembly Structure

| Assembly | References | autoReferenced | Notes |
|----------|-----------|----------------|-------|
| `TurboHTTP.Core` | none | true | Response helpers, `ContentTypes`, `JsonExtensions` |
| `TurboHTTP.Files` | Core | false | File downloader and multipart builder |
| `TurboHTTP.Tests.Runtime` | All runtime modules | false | Phase 5 test coverage |

## Cross-Cutting Design Decisions

1. **Serializer abstraction first:** JSON extensions must use `TurboHTTP.JSON.JsonSerializer` (and optional `IJsonSerializer` overloads), not direct `System.Text.Json` calls.
2. **Encoding fallback behavior:** `GetContentEncoding()` returns UTF-8 when charset is missing or invalid to preserve compatibility with common API responses.
3. **Client lifecycle ownership:** `FileDownloader` receives a required `UHttpClient` instance. No hidden default client allocation.
4. **Typed checksum failure:** Checksum validation throws `ChecksumMismatchException` with algorithm/expected/actual fields for structured handling.
5. **Resume semantics:** Resume uses `Range` headers and expects `206 PartialContent`; `200 OK` fallback restarts from scratch; `416` retries from scratch.
6. **Multipart safety:** Multipart builder validates boundary characters and rejects CR/LF in field names and filenames to prevent header injection.
7. **Current transport limitation:** Download progress is reported after response buffering completes. Incremental streaming progress is deferred to a future transport phase.
8. **Core-first constants:** `ContentTypes` lives in `TurboHTTP.Core` so all modules can consume MIME constants without extra references.

## All Files (8 new, 1 modified)

| # | Action | Path | Assembly |
|---|--------|------|----------|
| 1 | Modify | `Runtime/Core/UHttpResponse.cs` | Core |
| 2 | Create | `Runtime/Core/ContentTypes.cs` | Core |
| 3 | Create | `Runtime/Core/JsonExtensions.cs` | Core |
| 4 | Create | `Runtime/Files/FileDownloader.cs` | Files |
| 5 | Create | `Runtime/Files/MultipartFormDataBuilder.cs` | Files |
| 6 | Create | `Tests/Runtime/Core/ContentTypesTests.cs` | Tests |
| 7 | Create | `Tests/Runtime/Core/JsonExtensionsTests.cs` | Tests |
| 8 | Create | `Tests/Runtime/Files/FileDownloaderTests.cs` | Tests |
| 9 | Create | `Tests/Runtime/Files/MultipartFormDataBuilderTests.cs` | Tests |

## Post-Implementation

1. Run both specialist agent reviews (unity-infrastructure-architect, unity-network-architect).
2. Run full runtime test suite and confirm Phase 5 tests are stable.
3. Create or update `docs/implementation-journal/2026-02-phase5-content-handlers.md`.
4. Update `CLAUDE.md` Development Status section with Phase 5 completion details.
