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
| 19b.1 | Unify Pipeline (Remove Interceptors) | 2-3 days |
| 19b.2 | Pooled Request Objects (Zero-Allocation Builder) | 1 week |
| 19b.3 | Expose Zero-Allocation Response Bodies natively | 3-4 days |
| 19b.4 | Purge Legacy Compatibility Flows | 1-2 days |

---

## 19b.1: Unify Pipeline (Remove Interceptors)

**Goal:** Eliminate the overlap between the Pipeline and Interceptor architectures by removing the latter.

Summary:
1. Delete `IHttpInterceptor`, `InterceptorRequestResult`, `InterceptorResponseResult`, and related structs.
2. Remove interception invocation loops (`ExecuteWithInterceptorsAsync`) from `UHttpClient`.
3. Remove `UHttpClientOptions.Interceptors` and `InterceptorFailurePolicy`.
4. Update the Plugin infrastructure (`IHttpPlugin`) to contribute `IHttpMiddleware` instances instead of interceptors.

---

## 19b.2: Pooled Request Objects (Zero-Allocation Builder)

**Goal:** Shift from immutable builders to pooled, mutable request objects returned directly from the client.

Summary:
1. Implement a request pool inside `UHttpClient` (or `HttpTransportFactory`).
2. Add `UHttpClient.CreateRequest(HttpMethod, string)` which returns an `IDisposable` request (rented from the pool).
3. Remove `UHttpRequestBuilder` and the `UHttpRequest` immutable copy constructors (`WithHeaders`, `WithBody`, etc.).
4. Make `UHttpRequest` state mutable for the renter but internally safe (cleared automatically upon return to the pool).
5. Update all extensions (JSON, Auth, Multipart) to operate seamlessly on the mutable leased request.

---

## 19b.3: Expose Zero-Allocation Response Bodies

**Goal:** Prevent array allocations on large responses by piping `ReadOnlySequence<byte>` through the public API.

Summary:
1. Refactor `UHttpResponse` to safely expose `ReadOnlySequence<byte>` as the primary body representation.
2. Remove the internal array-flattening logic currently backing `UHttpResponse.Body`.
3. Update JSON serialization extensions to deserialize directly from `ReadOnlySequence<byte>`.
4. Update File download extensions to process and write chunks dynamically from the sequence.

---

## 19b.4: Purge Legacy Compatibility Flows

**Goal:** Remove all code paths and compiler `#if` directives maintained solely for backward compatibility.

Summary:
1. Delete `Http2MaxDecodedHeaderBytes` (currently marked `[Obsolete]`) from `UHttpClientOptions`.
2. Delete the legacy `Register` overload in `HttpTransportFactory` that takes an `int` parameter.
3. Review and remove older target framework `#if` switches (e.g. for `NETSTANDARD2_0`, obsolete .NET Core flags, or BouncyCastle legacy shims).
4. Remove any "legacy" behavior logic (e.g. `UseLegacyResumption`) discovered in TLS or Transport layers unless structurally required for current platforms.

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
