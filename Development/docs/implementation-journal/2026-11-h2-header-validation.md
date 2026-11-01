# 2026-11 — Shared request field validator for HTTP/1.1 and HTTP/2 (#26, #49)

## What
- New `Runtime/Transport/Internal/HttpFieldValidator.cs`, the one outgoing request field check, used by:
  - the HTTP/1.1 serializer: the synthesized Host, user headers and request trailers (`Http11RequestSerializer.ValidateHeader` now delegates to it);
  - the HTTP/2 request header list (`Http2Connection.DispatchAsync` → `ValidateRequestFields`);
  - HTTP/2 request trailers (`CollectRequestTrailers` → `ValidateRequestField`). The duplicate `ValidateTrailerHeader` was removed.
- Rules:
  - Name: a non-empty RFC 9110 5.1 token, so `:`-prefixed (pseudo-header) names, CR/LF/NUL/SP and non-ASCII are rejected. A `:` name gets its own message (RFC 9113 8.3).
  - Value: RFC 9110 5.5 field-value, i.e. VCHAR / obs-text / SP / HTAB. NUL, CR, LF, the other C0 controls and DEL are rejected. Characters above U+00FF are rejected too: HPACK used to replace them through the Latin-1 encoder instead of failing. Leading or trailing SP/HTAB is rejected.
  - Messages carry the validated name and a character code, never the value. Name errors no longer echo the raw name.

## Decisions
- **Uppercase names on HTTP/2 are accepted and lowercased**, not rejected. The `HttpHeaders` API is case-insensitive and callers set `Authorization` etc. with capitals. RFC 9113 8.2.1 constrains the field *as sent*, and the sent name is always lowercased (`ToLowerAsciiHeaderName`).
- **Surrounding whitespace is rejected on both protocols**, not trimmed. RFC 9110 5.5 field-value grammar excludes it, and RFC 9110 2.2 says senders MUST NOT generate non-matching elements. RFC 9113 8.2.1 makes it malformed on HTTP/2. Silently trimming would change caller data. Behavior change: e.g. `WithBearerToken("")` (`"Bearer "`) now fails at send.
- **HTTP/2 validates before any stream slot or stream id is taken**: at the top of `DispatchAsync`, before the `try`. A rejected request writes no frame, burns no stream id and rents no pooled stream, so nothing needs releasing. The extra pass enumerates `request.Headers` once more: one boxed key enumerator per request.
- **Error type**: HTTP/2 header and trailer violations are `UHttpException(InvalidRequest)` with the `ArgumentException` as inner exception (previously trailers became `NetworkError`, which is retryable). HTTP/1.1 is unchanged (`ArgumentException` from the serializer, wrapped as `Unknown` by `RawSocketTransport`) → follow-up #243.
- Validation is strict for every user header, including those HTTP/2 drops (connection-specific) or HTTP/1.1 replaces (Content-Length/Transfer-Encoding are skipped in H1 before validation, as before).
- `:authority`/`:path` come from `Uri` (escaped) and are not re-validated; the H1 synthesized Host still is.
- Core's `UHttpRequest.ValidateTrailerFieldName` (declared trailer names) keeps its own token check; Core cannot reference Transport, and the transports validate the actual trailer fields again.

## Files
- `Runtime/Transport/Internal/HttpFieldValidator.cs` (+ .meta)
- `Runtime/Transport/Http1/Http11RequestSerializer.cs`
- `Runtime/Transport/Http2/Http2Connection.cs`, `Http2Connection.Send.cs`
- `Tests/Runtime/Transport/HttpFieldValidatorTests.cs` (+ .meta)
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.RequestHeaderValidation.cs` (+ .meta)

## Deferred
- #243 (v1.1): HTTP/1.1 field-validation failures should map to `InvalidRequest`.

## Verification
- `./Tools~/ci/check.sh`: OK (invariants, format, shadow build with analyzers).
- Out-of-Unity NUnit run: a scratch net8.0 project compiled the runtime test sources against the shadow-built DLLs.
  - The new tests plus `Http11SerializerTests` and all `Http2ConnectionTests` partials: 254/254 pass.
  - With the three runtime files reverted to origin/main, 15 of the 18 new protocol-level test cases fail. The 3 passing are the accept-path cases.
  - Wider Transport/Core/Middleware/Auth namespaces: 1004 pass. 171 fail, only with `FileNotFoundException: UnityEngine.CoreModule` (environment, not this change).
- Not run: Unity EditMode/PlayMode (lane rule; host editor periodic run), IL2CPP, devices.
