# Phase 2: Core Type System

**Status:** COMPLETE

## What was implemented

All 8 core types and 3 test files.

## Files created

- `Runtime/Core/HttpMethod.cs` — Enum (GET/POST/PUT/DELETE/PATCH/HEAD/OPTIONS) + zero-alloc extensions (IsIdempotent, HasBody, ToUpperString with pre-allocated string array)
- `Runtime/Core/HttpHeaders.cs` — Case-insensitive multi-value header collection (Dictionary<string, List<string>>). Methods: Set, Add, Get, GetValues, Contains, Remove, Clone. RFC 9110/6265 compliant.
- `Runtime/Core/UHttpRequest.cs` — Immutable request with defensive header cloning. Builder pattern: WithHeaders, WithBody, WithTimeout, WithMetadata. Internal constructor with ownsHeaders optimization.
- `Runtime/Core/UHttpResponse.cs` — Response with StatusCode, Headers, Body, ElapsedTime, Request, Error. IsSuccessStatusCode, GetBodyAsString (UTF-8), EnsureSuccessStatusCode.
- `Runtime/Core/UHttpError.cs` — Error taxonomy enum (NetworkError, Timeout, HttpError, CertificateError, Cancelled, InvalidRequest, Unknown). UHttpError class with IsRetryable() logic. UHttpException wraps UHttpError.
- `Runtime/Core/RequestContext.cs` — Thread-safe execution context with lock-based synchronization. Timeline events, state dictionary, stopwatch. Defensive snapshots on read.
- `Runtime/Core/IHttpTransport.cs` — Transport interface extending IDisposable. SendAsync method.
- `Runtime/Core/HttpTransportFactory.cs` — Thread-safe factory using Lazy<T> with ExecutionAndPublication mode. Register, SetForTesting, Reset methods.
- `Tests/Runtime/Core/HttpMethodTests.cs` — 4 tests for IsIdempotent/HasBody
- `Tests/Runtime/Core/HttpHeadersTests.cs` — 3 tests for case-insensitivity, Set/Clone
- `Tests/Runtime/Core/UHttpErrorTests.cs` — 5 tests for IsRetryable logic

## Decisions

- Dictionary<string, List<string>> for headers (RFC multi-value support, Set-Cookie per RFC 6265).
- Defensive header cloning in UHttpRequest constructor.
- Shared byte[] body ownership (documented, copy deferred to Phase 10 ReadOnlyMemory migration).
- Lock-based synchronization in RequestContext for HTTP/2 async continuations.
- HttpTransportFactory uses Lazy<T> with LazyThreadSafetyMode.ExecutionAndPublication.
- IHttpTransport extends IDisposable for connection pool cleanup.
