# Core Extraction — 2026-02-14

## What

Extracted non-core concerns from `TurboHTTP.Core` to enforce proper module boundaries. Core now contains only request/response types, pipeline infrastructure, transport abstraction, and the client API — with zero external assembly references.

## Changes

### 1. LoggingMiddleware → TurboHTTP.Observability

- Moved `Runtime/Core/Pipeline/Middlewares/LoggingMiddleware.cs` → `Runtime/Observability/LoggingMiddleware.cs`
- Changed namespace from `TurboHTTP.Core` → `TurboHTTP.Observability`
- Updated `Tests/Runtime/Pipeline/LoggingMiddlewareTests.cs` with `using TurboHTTP.Observability`

### 2. DefaultHeadersMiddleware → new TurboHTTP.Middleware

- Created `Runtime/Middleware/TurboHTTP.Middleware.asmdef` (references Core, `autoReferenced: false`)
- Moved `Runtime/Core/Pipeline/Middlewares/DefaultHeadersMiddleware.cs` → `Runtime/Middleware/DefaultHeadersMiddleware.cs`
- Changed namespace from `TurboHTTP.Core` → `TurboHTTP.Middleware`
- Added `TurboHTTP.Middleware` to `TurboHTTP.Complete.asmdef` and `TurboHTTP.Tests.Runtime.asmdef`
- Updated `Tests/Runtime/Pipeline/DefaultHeadersMiddlewareTests.cs` and `Tests/Runtime/Integration/PipelineIntegrationTests.cs`

### 3. TimeoutMiddleware — deleted

- Deleted `Runtime/Core/Pipeline/Middlewares/TimeoutMiddleware.cs`
- Deleted `Tests/Runtime/Pipeline/TimeoutMiddlewareTests.cs`
- Removed empty `Runtime/Core/Pipeline/Middlewares/` directory
- Updated `PipelineIntegrationTests.cs`: rewrote `RetryWrapsTimeout` test to use transport-level timeout exceptions instead of TimeoutMiddleware's 408 response path; deleted `UserCancellation_PropagatesEvenWhenTimeoutFires` test

**Rationale:** `RawSocketTransport` already enforces `request.Timeout` via `CancellationTokenSource.CancelAfter()` and throws `UHttpException(UHttpErrorType.Timeout)`. This is the same exception path as DNS/connect/network failures. `RetryMiddleware` catches it via the exception handler (`ex.HttpError.IsRetryable()` returns true for `Timeout`). The middleware's 408-response path was redundant — a timeout is an infrastructure failure, not an HTTP 408 from a server.

### 4. JSON logic → TurboHTTP.JSON

- Moved `Runtime/Core/JsonExtensions.cs` → `Runtime/JSON/JsonExtensions.cs` (namespace `TurboHTTP.JSON`)
- Created `Runtime/JSON/JsonRequestBuilderExtensions.cs` — extension methods `WithJsonBody<T>` on `UHttpRequestBuilder`
- Stripped `WithJsonBody<T>` overloads and `#if TURBOHTTP_USE_SYSTEM_TEXT_JSON` block from `Runtime/Core/UHttpRequestBuilder.cs`; kept `WithJsonBody(string)` (no serialization dependency)
- Removed `using TurboHTTP.JSON` and `#if` directives from `UHttpRequestBuilder.cs`
- Updated `TurboHTTP.Core.asmdef`: removed `TurboHTTP.JSON` reference (now zero references)
- Updated `TurboHTTP.JSON.asmdef`: added `TurboHTTP.Core` reference (dependency inverted)
- Updated test files: `JsonExtensionsTests.cs`, `UHttpRequestBuilderJsonTests.cs`, `TestHttpClient.cs`

## Decisions

- **Core has zero assembly references** — the dependency direction is now Core ← JSON (not Core → JSON)
- **ContentTypes.cs stays in Core** — it's pure string constants (`"application/json"`) with no JSON implementation dependency
- **All WithJsonBody overloads in JSON** — even `WithJsonBody(string)` (no serializer) moved to `TurboHTTP.JSON` because the builder in Core should not have JSON-specific convenience methods; Core stays content-format agnostic
- **New TurboHTTP.Middleware assembly** — chosen over putting DefaultHeadersMiddleware in an existing module; provides a home for future general-purpose middlewares (Redirect, Compression, etc.)
- **LoggingMiddleware in Observability** — natural fit alongside MetricsMiddleware; both are observability concerns

## Files Created

- `Runtime/Observability/LoggingMiddleware.cs`
- `Runtime/Middleware/TurboHTTP.Middleware.asmdef`
- `Runtime/Middleware/DefaultHeadersMiddleware.cs`
- `Runtime/JSON/JsonExtensions.cs`
- `Runtime/JSON/JsonRequestBuilderExtensions.cs`
- `Runtime/Auth/AuthBuilderExtensions.cs`

## Files Modified

- `Runtime/Core/UHttpRequestBuilder.cs`
- `Runtime/Core/TurboHTTP.Core.asmdef`
- `Runtime/JSON/TurboHTTP.JSON.asmdef`
- `Runtime/TurboHTTP.Complete.asmdef`
- `Tests/Runtime/TurboHTTP.Tests.Runtime.asmdef`
- `Tests/Runtime/Pipeline/LoggingMiddlewareTests.cs`
- `Tests/Runtime/Pipeline/DefaultHeadersMiddlewareTests.cs`
- `Tests/Runtime/Integration/PipelineIntegrationTests.cs`
- `Tests/Runtime/Core/JsonExtensionsTests.cs`
- `Tests/Runtime/Core/UHttpRequestBuilderJsonTests.cs`
- `Tests/Runtime/Core/UHttpClientTests.cs`
- `Tests/Runtime/TestHttpClient.cs`
- `Runtime/Files/MultipartFormDataBuilder.cs`
- `CLAUDE.md`

### 5. Builder cleanup — remove convenience aliases

- Removed `Accept(mediaType)`, `ContentType(mediaType)`, `WithBearerToken(token)` from `Runtime/Core/UHttpRequestBuilder.cs`
- Created `Runtime/Auth/AuthBuilderExtensions.cs` — `WithBearerToken` as extension method on `UHttpRequestBuilder`
- Updated all callers:
  - `Runtime/JSON/JsonRequestBuilderExtensions.cs` — `.ContentType(ContentTypes.Json)` → `.WithHeader("Content-Type", ContentTypes.Json)`
  - `Runtime/JSON/JsonExtensions.cs` — `.Accept(ContentTypes.Json)` → `.WithHeader("Accept", ContentTypes.Json)` (done in step 4)
  - `Runtime/Files/MultipartFormDataBuilder.cs` — `.ContentType(GetContentType())` → `.WithHeader("Content-Type", GetContentType())`
  - `Tests/Runtime/Core/UHttpClientTests.cs` — added `using TurboHTTP.Auth;`
  - `Tests/Runtime/TestHttpClient.cs` — added `using TurboHTTP.Auth;`

**Rationale:** `Accept()` and `ContentType()` were pure aliases for `WithHeader("Accept", ...)` / `WithHeader("Content-Type", ...)` — no value over the raw API. `WithBearerToken` is auth-specific and belongs in the Auth module. Builder in Core should only expose raw HTTP primitives.

## Files Deleted

- `Runtime/Core/Pipeline/Middlewares/LoggingMiddleware.cs`
- `Runtime/Core/Pipeline/Middlewares/DefaultHeadersMiddleware.cs`
- `Runtime/Core/Pipeline/Middlewares/TimeoutMiddleware.cs`
- `Runtime/Core/JsonExtensions.cs`
- `Tests/Runtime/Pipeline/TimeoutMiddlewareTests.cs`
