# Phase 19b: API Modernization & Refactoring

**Milestone:** M4 (v1.2)
**Dependencies:** Phase 19 (Async Runtime Refactor), Phase 19a (Extreme Performance)
**Estimated Complexity:** High
**Estimated Effort:** 2-3 weeks
**Critical:** No — API simplification and cleanup track.

## Overview

Phase 19b focuses on cleaning up the `TurboHTTP` API surface, removing legacy compatibility constraints, eliminating duplication between Middlewares and Interceptors, and adopting zero-allocation builder patterns (pooled requests).

This document serves as the top-level phase plan for the public API refactoring.

## Greenfield Assumptions

1. This is a new library track with no user backward-compatibility burden. Complete refactoring of public APIs (including breaking changes) is accepted and expected.
2. Zero-allocation runtime paths are the primary architecture, not optional compatibility features. The API must naturally guide users toward zero-allocation usage by default.
3. Legacy backward-compatibility flags, definitions, and obsolete properties must be fully purged from the codebase.

## High-Level Goals

| Area | Problem | Target Outcome |
|---|---|---|
| API Duplication | Middleware (`IHttpMiddleware`) and Interceptors (`IHttpInterceptor`) duplicate pipeline interception logic, creating mental and runtime overhead. | Unify the pipeline. Remove `IHttpInterceptor` completely in favor of using Middlewares for all request/response interception. |
| Per-Request Allocations | `UHttpRequestBuilder` and `UHttpRequest` immutable `.With*` methods create per-request object allocations and defensive header clones. | Replace with a pooled request factory (`UHttpClient.CreateRequest`) returning mutable, leased `UHttpRequest` objects. |
| Response Body Flattening | `UHttpResponse.Body` lazily flattens `ReadOnlySequence<byte>` to arrays, causing Large Object Heap (LOH) pressure. | Remove lazy array flattening. Expose `ReadOnlySequence<byte>` natively, updating content handlers to consume it directly. |
| Legacy Technical Debt | Transport factory contains legacy backward-compatibility overloads and `UHttpClientOptions` has `[Obsolete]` properties. | Delete all obsolete properties, remove legacy factory overloads, and cleanse `#if` define wraps for obsolete target frameworks. |

## Sub-Phase Index

| Sub-Phase | Name | Estimated Effort |
|---|---|---|
| [19b.1](phase-19b.1-unify-pipeline.md) | Unify Pipeline (Remove Interceptors) | 2-3 days |
| [19b.2](phase-19b.2-pooled-request-objects.md) | Pooled Request Objects (Zero-Allocation Builder) | 1 week |
| [19b.3](phase-19b.3-zero-allocation-response-bodies.md) | Expose Zero-Allocation Response Bodies natively | 3-4 days |
| [19b.4](phase-19b.4-purge-legacy.md) | Purge Legacy Compatibility Flows | 1-2 days |

---

## Verification

### Compile & Build
- Ensure the project builds successfully after deleting `IHttpInterceptor` and `UHttpRequestBuilder`.
- Verify all unit tests compile with the new `UHttpClient.CreateRequest()` syntax.

### Code Quality & Allocations
- Create unit tests with `MemoryProfiler` (or manual alloc tracking) ensuring that sending a basic GET request allocates exactly 0 bytes on the hot path (relying wholly on pooled objects).
- Confirm via profiler that large responses are not allocating contiguous byte arrays on the Large Object Heap.

### Plugin Compatibility
- Verify existing plugins (telemetry, observability, monitor) successfully migrate to the `IHttpMiddleware` pattern without architectural roadblocks.
