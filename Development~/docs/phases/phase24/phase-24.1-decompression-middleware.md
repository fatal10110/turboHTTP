# Phase 24.1: Decompression Middleware (gzip / brotli)

**Depends on:** Phase 4, Phase 5
**Estimated Effort:** 1 week

---

## Step 0: Define Middleware Contract and Capability Model

Required behavior:

1. Define `DecompressionMiddleware : IHttpMiddleware` as a transparent response-processing middleware.
2. Define configuration surface in client options (`EnableDecompression`, advertised encodings, warning/log hooks).
3. Define runtime capability detection for Brotli availability.
4. Define exact behavior per encoding: `gzip`, `br`, `identity`, unknown values.

Implementation constraints:

1. Capability checks must be runtime based (`Type.GetType(...)` or equivalent probe), not only compile-time guards.
2. Unknown or unavailable encodings must fail gracefully via pass-through + warning.
3. Middleware must not change behavior when no `Content-Encoding` header is present.

---

## Step 1: Implement Outbound `Accept-Encoding` Injection

Required behavior:

1. Add `Accept-Encoding: gzip, br` by default when decompression is enabled.
2. Avoid duplicate header insertion when the request already sets `Accept-Encoding`.
3. Respect user configuration for disabling `br` on unsupported targets.

Implementation constraints:

1. Header mutation must happen before `next(request)` is invoked.
2. Preserve any explicit user-provided header precedence rules.
3. Injection must be cheap on hot paths (avoid unnecessary allocations).

---

## Step 2: Implement Inbound Response Decompression Path

Required behavior:

1. Inspect response `Content-Encoding` and branch by supported codecs.
2. For `gzip`, decode with `GZipStream`; for `br`, decode with `BrotliStream` when available.
3. Stream decompression into pooled/chunked buffers (no unbounded double-buffering).
4. Replace response body with decompressed content and remove/normalize `Content-Encoding` post-decode.

Implementation constraints:

1. Do not eagerly allocate large contiguous arrays when stream/chunk processing is possible.
2. Preserve cancellation and timeout semantics while decoding.
3. Identity/no-op payloads must pass through without extra work.

---

## Step 3: Wire Default Registration and Platform Fallback

Required behavior:

1. Register middleware in default `UHttpClientOptions` pipeline when enabled.
2. Allow opt-out via `EnableDecompression = false`.
3. Add IL2CPP preservation entries (`link.xml`) for compression stream types used at runtime.
4. Emit deterministic warnings for unsupported `br` on .NET Standard 2.0 targets.

Implementation constraints:

1. Default behavior must remain safe across Editor, Standalone, iOS, Android, and WebGL.
2. Fallback logging must be concise and non-spammy under repeated requests.
3. Registration order must remain compatible with existing middleware ordering assumptions.

---

## Step 4: Validate Middleware Interactions

Required behavior:

1. Validate decompression with cached and non-cached responses.
2. Validate behavior with retry and auth middleware chains.
3. Validate unknown encoding behavior (pass-through with warning, no crash).

Implementation constraints:

1. Middleware must remain transparent from caller perspective (`GetAsync` / `SendAsync` API unchanged).
2. Error mapping must remain consistent with existing `UHttpError` taxonomy.

---

## Verification Criteria

1. `Accept-Encoding` injection works and is configurable.
2. `gzip` and `br` payloads are decoded correctly where supported.
3. Brotli-unavailable platforms continue with deterministic pass-through behavior.
4. Unknown `Content-Encoding` values do not break response handling.
5. Decompression middleware can be disabled globally.
6. `link.xml` preservation prevents IL2CPP stripping regressions.
7. Integration tests cover round-trip, fallback, unknown encoding, and disabled mode.
