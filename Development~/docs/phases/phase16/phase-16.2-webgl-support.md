# Phase 16.2: WebGL Support (Browser Fetch Transport)

**Depends on:** Phase 3 (Transport)
**Assembly:** `TurboHTTP.WebGL`, `TurboHTTP.Tests.Runtime`
**Files:** 4 new, 1 modified

---

## Step 1: Implement JavaScript Fetch Plugin

**File:** `Plugins/WebGL/TurboHTTPFetch.jslib` (new)

Required behavior:

1. Implement JavaScript plugin wrapping the browser `fetch()` API.
2. Support all HTTP methods used by TurboHTTP (GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS).
3. Accept request parameters from C# marshaling: URL, method, headers (serialized), body (byte array), timeout.
4. Return response data to C# callbacks: status code, headers (serialized), body (byte array).
5. Support request cancellation via `AbortController`.
6. Handle CORS preflight transparently (browser handles this, but error surfaces must be clear).
7. Support streaming response bodies for large downloads (via `ReadableStream` where available).
8. Marshal errors back to C# with actionable error codes: network error, CORS error, timeout, abort.
9. Manage concurrent request lifecycle with unique request IDs for callback correlation.

Implementation constraints:

1. Use `mergeInto(LibraryManager.library, { ... })` pattern required by Unity .jslib plugins.
2. String marshaling must use `UTF8ToString` / `lengthBytesUTF8` / `stringToUTF8` for C#↔JS interop.
3. Byte array marshaling must use `HEAPU8` for zero-copy access where possible.
4. Memory allocated via `_malloc` must be freed via `_free` after C# has copied the data.
5. Request ID generation must be collision-free under concurrent requests.
6. Plugin must not leak `AbortController` or `fetch` promises on cancellation.
7. `fetch()` credentials mode should default to `same-origin` (standard browser behavior).

---

## Step 2: Implement WebGL Browser Transport

**Files:**
- `Runtime/WebGL/WebGLBrowserTransport.cs` (new)
- `Runtime/WebGL/WebGLInterop.cs` (new)

Required behavior:

1. Implement `IHttpTransport` contract (`SendAsync` + `IDisposable`).
2. Define `[DllImport("__Internal")]` extern methods matching .jslib plugin exports.
3. Bridge `UHttpRequest` to JavaScript fetch parameters:
   - Serialize headers to format consumable by .jslib.
   - Marshal request body bytes to JS heap.
   - Map `UHttpRequest.Timeout` to `AbortController` timeout.
4. Bridge JavaScript response back to `UHttpResponse`:
   - Parse status code and headers from serialized callback data.
   - Copy response body bytes from JS heap to managed `byte[]`.
   - Populate `UHttpResponse` with request reference and elapsed time.
5. Support cancellation: `CancellationToken` triggers `AbortController.abort()` via interop call.
6. Map JavaScript error types to `UHttpException` with appropriate `UHttpErrorType`:
   - `TypeError` (network/CORS) → `UHttpErrorType.Network`.
   - `AbortError` → `UHttpErrorType.Timeout` or `UHttpErrorType.Cancelled`.
7. Use `TaskCompletionSource<UHttpResponse>` with `RunContinuationsAsynchronously` for async completion from JS callbacks.
8. Track in-flight requests and cancel/fail all pending on `Dispose()`.

Implementation constraints:

1. All Unity API calls and `TaskCompletionSource` completions must happen on main thread (WebGL is single-threaded).
2. `#if UNITY_WEBGL && !UNITY_EDITOR` guards must wrap all `[DllImport]` declarations.
3. Provide editor-time stub that throws `PlatformNotSupportedException` with clear message.
4. Header serialization format must be simple and allocation-efficient (e.g., `key:value\n` pairs).
5. Response body must be fully copied to managed memory before freeing JS heap allocation.
6. Do not assume `ReadableStream` availability — fallback to `response.arrayBuffer()` for full-body fetch.

---

## Step 3: Add Transport Registration and Platform Wiring

**File:** `Runtime/WebGL/TurboHTTP.WebGL.asmdef` (new)

Required behavior:

1. Configure assembly definition:
   - References: `TurboHTTP.Core`.
   - `autoReferenced: false`.
   - `includePlatforms: ["WebGL"]` (only compiled for WebGL builds).
   - `defineConstraints: []` (no test constraints).
2. Register `WebGLBrowserTransport` via `[ModuleInitializer]` or `[RuntimeInitializeOnLoadMethod]` with `HttpTransportFactory`.
3. Platform detection: register WebGL transport only when `Application.platform == RuntimePlatform.WebGLPlayer`.
4. Ensure `RawSocketTransport` is not registered on WebGL (it already has `excludePlatforms: ["WebGL"]`).
5. Log diagnostic message on transport registration confirming WebGL fetch backend is active.

Implementation constraints:

1. Follow `RawSocketTransport.EnsureRegistered()` pattern for IL2CPP/WebGL timing safety.
2. Module initializer must be idempotent — multiple calls must not corrupt factory state.
3. Keep registration code minimal to avoid WebGL startup overhead.
4. Transport factory must resolve `WebGLBrowserTransport` as default on WebGL, `RawSocketTransport` on all other platforms.

---

## Step 4: Add WebGL Transport Tests

**File:** `Tests/Runtime/WebGL/WebGLTransportTests.cs` (new)

Required behavior:

1. Validate transport registration on WebGL platform via factory resolution.
2. Validate request serialization: headers, body, method, URL are correctly marshaled.
3. Validate response deserialization: status code, headers, body are correctly reconstructed.
4. Validate cancellation triggers abort and completes `TaskCompletionSource` with appropriate error.
5. Validate timeout handling maps to abort with `UHttpErrorType.Timeout`.
6. Validate disposal cancels all in-flight requests.
7. Validate error mapping: network error, CORS error, abort error → correct `UHttpErrorType`.
8. Validate concurrent request correlation (multiple in-flight requests complete independently).
9. Add editor-mode test confirming `PlatformNotSupportedException` is thrown when not on WebGL.

Note: Full browser-based integration tests require WebGL build deployment. Unit tests use mock interop layer for deterministic coverage.

---

## Verification Criteria

1. `WebGLBrowserTransport` implements full `IHttpTransport` contract and integrates with `UHttpClient` pipeline.
2. Browser fetch correctly handles GET, POST, PUT, DELETE with headers and body.
3. CORS errors surface as actionable `UHttpException` with `Network` error type.
4. Cancellation and timeout reliably abort in-flight browser fetch requests.
5. No memory leaks from JS heap allocations (all `_malloc` balanced with `_free`).
6. Transport auto-registers on WebGL platform without manual bootstrap code.
7. Existing `RawSocketTransport` tests continue to pass on non-WebGL platforms.
8. Pipeline middlewares (retry, auth, logging, rate limiting) compose correctly with WebGL transport.
