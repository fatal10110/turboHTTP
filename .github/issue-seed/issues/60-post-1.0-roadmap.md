---
key: ARCH-01
title: [Architecture] Split UniTask adapter so HTTP-only users do not pull in WebSocket
milestone: v1.1: Post-release hardening
labels: type:architecture, area:core, area:websocket, priority:P2, source:review-2026-10
---
## Summary
`TurboHTTP.UniTask.asmdef` references `TurboHTTP.WebSocket`, so every UniTask user compiles and ships the WebSocket module even if they only use HTTP.

## Suggested fix
Split into `TurboHTTP.UniTask` (Core only) and `TurboHTTP.UniTask.WebSocket` (Core + WebSocket), both gated by the existing `versionDefines` (`com.cysharp.unitask >= 2.0.0` → `TURBOHTTP_UNITASK`). Document the allowed cross-module references (AGENTS.md list) in CLAUDE.md ([[DOC-01]]).

## Acceptance criteria
- [ ] Project with UniTask + Core only compiles without WebSocket.
- [ ] WebSocket UniTask extensions still available when both are present.

## Context
Source: 2026-10 full review (architecture).

===ISSUE===
---
key: PH-16.3
title: [Phase 16.3] Security & privacy hardening: RedactionPolicy, cache partitioning, TLS pinning hooks
milestone: v1.1: Post-release hardening
labels: type:phase, type:security, area:observability, area:cache, area:tls, priority:P2, source:roadmap
---
## Summary
Phase 16.3 plans a centralized `RedactionPolicy`, cache partitioning and TLS pinning hooks. The 2026-10 review found redaction gaps in Logging ([[MOD-16]]), Editor Monitor ([[UNI-06]]) and RecordReplay ([[UNI-07]]); those point fixes ship in v1.0-beta, this phase generalizes them.

## Scope (from `Development/docs/phases/phase16/phase-16.3-security-privacy-hardening.md`)
- [ ] Step 1: immutable `RedactionPolicy` (case-insensitive header sets, URL query parameter redaction, optional body redaction delegate with fail-closed behavior), consumed by Logging, Monitor, RecordReplay, exceptions/timeline
- [ ] Step 2: cache partitioning policy (per-user/auth partition key; complements the Auth/Cache ordering fix [[MOD-06]])
- [ ] Step 3: TLS pinning hooks (SPKI pin sets) for both SslStream and BouncyCastle providers — must not bypass chain validation ([[TLS-01]])
- [ ] Step 4: security & privacy tests

## Acceptance criteria
- [ ] One policy object drives every redaction site; no module keeps its own list.
- [ ] Pinning tests pass on both providers.

## Context
Source: roadmap Phase 16.3 (paths in the plan predate the interceptor rename: `LoggingMiddleware` → `LoggingInterceptor`).

===ISSUE===
---
key: PH-16.1
title: [Phase 16.1] Rate limiting: token bucket policies and 429/Retry-After handling
milestone: v1.2: Features & performance
labels: type:phase, type:enhancement, area:ratelimit, priority:P2, source:roadmap
---
## Summary
`TurboHTTP.RateLimit` currently implements only concurrency limiting (`ConcurrencyLimiter`, `ConcurrencyInterceptor`, `RequestQueue`). Phase 16.1 adds real rate limiting.

## Scope (from `Development/docs/phases/phase16/phase-16.1-rate-limiting.md`)
- [ ] `TokenBucket` — Stopwatch-based monotonic integer-tick refill, lock-free `Interlocked` acquisition (32-bit IL2CPP safe), `TryAcquire` + `WaitAsync` with cancellation, allocation-free fast path
- [ ] `RateLimitPolicy` configuration (global, per-host, per-route)
- [ ] `RateLimitInterceptor` integrating the bucket into the pipeline
- [ ] Honor `429` + `Retry-After` (bounded, see [[MOD-05]]) to adapt bucket state
- [ ] Tests incl. clock skew, zero capacity, cancellation leak checks

## Acceptance criteria
- [ ] Phase 16.1 verification criteria met; docs updated.

## Context
Source: roadmap Phase 16.1.

===ISSUE===
---
key: PH-16.5
title: [Phase 16.5] Parallel request helpers (Batch, Race, AllOrNone)
milestone: v1.2: Features & performance
labels: type:phase, type:enhancement, area:core, priority:P3, source:roadmap
---
## Summary
Combinators for common parallel patterns, flowing through the full `UHttpClient` pipeline.

## Scope (from `Development/docs/phases/phase16/phase-16.5-parallel-request-helpers.md`)
- [ ] `Batch` (all results, optional local `maxConcurrency` via `SemaphoreSlim`)
- [ ] `Race` (first success, cooperative cancellation of the rest via linked CTS)
- [ ] `AllOrNone` (fail fast, cancel remaining)
- [ ] `ParallelRequestResult`; responses of cancelled losers disposed (no pooled-body leaks)
- [ ] Assembly placement decided (Core extension vs optional module) per module rules

## Acceptance criteria
- [ ] Unit tests for each combinator incl. cancellation and disposal.

## Context
Source: roadmap Phase 16.5.

===ISSUE===
---
key: PH-19c
title: [Phase 19c] Zero-allocation hot path completion (body transport, H1 parser strings, H2 per-request lists)
milestone: v1.2: Features & performance
labels: type:phase, type:enhancement, area:transport, area:core, priority:P3, source:roadmap
---
## Summary
Remaining hot-path allocations after Phase 19a/22a.

## Scope (from `Development/docs/phases/phase-19c-body-zerocopy-transport.md`)
- [ ] 19c.1 Zero-copy body transport (`UHttpRequest.Body` → `ReadOnlyMemory<byte>`, remove `Memory.ToArray()` in `WithLeasedBody`, serializer and H2 send path updates). Re-check against the 22a request body model before starting — parts may already be superseded.
- [ ] 19c.2 HTTP/1.1 parser: no `Substring`/`Split` per header line (2–5 KB/response)
- [ ] 19c.3 HTTP/2: reuse header list per request, avoid per-encode allocations (coordinate with HPACK fixes [[H2-02]], [[H2-17]])
- [ ] Allocation gates extended ([[PH-22a6]])

## Acceptance criteria
- [ ] Allocation targets in the plan met and enforced by tests.

## Context
Source: roadmap Phase 19c.

===ISSUE===
---
key: PH-23
title: [Phase 23] API configuration & request ergonomics review
milestone: v1.2: Features & performance
labels: type:phase, type:enhancement, area:core, priority:P2, source:roadmap
---
## Summary
Phase 23 proposes client-level configuration and request-level ergonomics.

## Scope (from `Development/docs/phases/phase-23-api-config-extensions.md`)
Client (`UHttpClientOptions`):
- [ ] `IDnsResolver` / custom DNS resolution
- [ ] Automatic decompression toggle (gzip/deflate/brotli) — align with existing `DecompressionInterceptor`
- [ ] Cookie management toggle (`UseCookies`, `ICookieManager`) — align with `CookieInterceptor`/`CookieJar`
- [ ] `SocketOptions` (TcpNoDelay, keepalive interval — see [[TCP-02]], buffer sizes)
- [ ] `ServerCertificateCustomValidationCallback` for both TLS providers — must compose with, not replace, default validation unless explicitly opted out
Request (`UHttpRequest`):
- [ ] `WithQueryParam`, `WithCancellationToken`, `WithFormUrlEncoded`, `WithCookie`, `WithDownloadProgress`/`WithUploadProgress`, `WithUri`

Also resolve: `FollowRedirects` silent no-op ([[MOD-15]]) — options that need a module must fail loudly or auto-wire.

## Acceptance criteria
- [ ] API review sign-off; migration notes; tests per option.

## Context
Source: roadmap Phase 23.

===ISSUE===
---
key: PH-24.2
title: [Phase 24.2] AssetBundle handler
milestone: v1.2: Features & performance
labels: type:phase, type:enhancement, area:unity, priority:P2, source:roadmap
---
## Summary
README already claims AssetBundle support; it does not exist ([[PH-08]] removes the claim unless this ships first).

## Scope (from `Development/docs/phases/phase24/phase-24.2-assetbundle-handler.md`)
- [ ] API: `AsAssetBundleAsync` / `GetAssetBundleAsync`, owning wrapper (`Unload(false)` on dispose)
- [ ] Download with cancellation/progress; memory vs temp-file routing (default 16 MB threshold) via `UnityTempFileManager`; max size guard (512 MB)
- [ ] Main-thread load (`AssetBundle.LoadFromMemoryAsync` / `LoadFromFileAsync`), no leak on cancel (see [[UNI-08]])
- [ ] Integrity verification options (CRC / hash)
- [ ] Document: Unity `Caching` API not integrated

## Acceptance criteria
- [ ] Tests + sample; device check on iOS/Android.

## Context
Source: roadmap Phase 24.2.

===ISSUE===
---
key: PH-24.3
title: [Phase 24.3] Video content handler (download mode + URL passthrough)
milestone: v1.2: Features & performance
labels: type:phase, type:enhancement, area:unity, priority:P3, source:roadmap
---
## Summary
Two explicit modes for `VideoPlayer` integration.

## Scope (from `Development/docs/phases/phase24/phase-24.3-video-content-handler.md`)
- [ ] Download mode `GetVideoFileAsync` → temp file via `UnityTempFileManager`, streaming write, 1 GB guard, no orphaned partial files
- [ ] URL passthrough `PrepareVideoUrl` (document that passthrough bypasses TurboHTTP pipeline/auth)
- [ ] Cancellation/progress, lifecycle ownership

## Acceptance criteria
- [ ] Tests + sample.

## Context
Source: roadmap Phase 24.3.

===ISSUE===
---
key: PH-24.4
title: [Phase 24.4] glTF model handlers (download + importer hooks)
milestone: v1.2: Features & performance
labels: type:phase, type:enhancement, area:unity, priority:P3, source:roadmap
---
## Summary
Transport + handoff for glTF/GLB, importer-agnostic.

## Scope (from `Development/docs/phases/phase24/phase-24.4-gltf-model-handlers.md`)
- [ ] `GltfAsset` descriptor, `.gltf` vs `.glb` detection (content type / extension / signature)
- [ ] `IGltfImporter` hook for GLTFUtility / UnityGLTF without hard dependency
- [ ] Large payload handling without extra copies
- [ ] 24.5 tests and documentation for 24.2–24.4

## Acceptance criteria
- [ ] Tests + sample using one importer behind a define.

## Context
Source: roadmap Phase 24.4/24.5.

===ISSUE===
---
key: PLAN-03
title: [Plan] Correct the Phase 22d plan before implementation
milestone: v2.0: Advanced protocols
labels: type:plan, area:http2, area:proxy, priority:P2, source:review-2026-10
---
## Summary
Phase 22d (HTTP/2 proxy leg and proxy-aware H2 pooling) is "Very High" complexity (14–23 days plus device work), allows a clean-break redesign of the public proxy API, and has no customer driver; 22c itself is device-pending. Defer to v2.0 ([[REL-10]]) and fix these plan defects first:

- **22d.2 H1 reuse contradiction:** Principle 9 and Step 3 say the CONNECT-backed HTTP/1.1 owner always closes; Step 5.3 allows reuse. Always-close means a new CONNECT stream + origin TLS handshake per request — a regression vs 22b.2 tunnel pooling. Add a benchmark acceptance criterion.
- **22d.2 Step 2.6:** `Dispose` must send END_STREAM on clean write-close and RST_STREAM(CANCEL) only on abort.
- **RFC 9113 §8.5 gaps:** map proxy RST_STREAM(CONNECT_ERROR) to an error class; HEADERS (trailers) after the 2xx on a CONNECT stream is a stream error; tests for both.
- **22d.1 Step 3.8:** `SemaphoreSlim` for MAX_CONCURRENT_STREAMS cannot shrink when the peer lowers it (RFC 9113 §6.5.2) → over-admission and REFUSED_STREAM. Use counter + waiter queue; add REFUSED_STREAM retry rule (safe per §8.7); define interaction of "queue 256 / up to 4 connections per route" with `TcpConnectionPool` per-host limits. Reuse the fix from [[H2-06]].
- **22d.3:** `H2ForwardUnsupported` trigger undefined; Host vs `:authority` rule missing (RFC 9113 §8.3.1); 407 retry scope (which challenge schemes) undefined.
- **22d.1 `ProxyRouteKey.AuthBucket`:** unsalted SHA-256 of username+domain+password recomputed per lookup → HMAC with per-process random key, cached on `ProxySettings`.
- **Consistency:** 22d.2 Step 4.5 "no TLS retry" contradicts 22c code ([[PLAN-02]]).
- **Doc hygiene:** duplicate "### 7." sections; ENABLE_CONNECT_PROTOCOL reasoning muddled (server-side setting); non-numeric criteria ("bounded memory").

## Acceptance criteria
- [ ] `Development/docs/phases/phase22d/*` updated; review sign-off by both specialist agents.

## Context
Source: 2026-10 full review (plan quality).

===ISSUE===
---
key: PH-22d.1
title: [Phase 22d.1] Proxy HTTP/2 foundation: https:// proxies, ALPN to proxy, route keys, proxy H2 manager
milestone: v2.0: Advanced protocols
labels: type:phase, type:enhancement, area:proxy, area:http2, priority:P3, source:roadmap
---
## Summary
Client-to-proxy HTTP/2 over `https://` proxy endpoints. Blocked on [[PLAN-03]].

## Scope (from `Development/docs/phases/phase22d/phase-22d.1-proxy-h2-foundation.md`)
- [ ] Accept `http://` and `https://` proxy URIs, reject others; scheme default ports
- [ ] `http://` proxies HTTP/1.1-only; `AllowPlaintextProxyAuth` gate kept
- [ ] Internal `ProxyRouteKey` (scheme participates, lowercase host, HMAC auth bucket)
- [ ] ALPN negotiation to the proxy; proxy H2 connection manager skeleton
- [ ] Dispatch routing: H2 proxy handling vs H1 fallback

## Acceptance criteria
- [ ] 22d.1 checklist + specialist reviews pass.

## Context
Source: roadmap Phase 22d.1.

===ISSUE===
---
key: PH-22d.2
title: [Phase 22d.2] CONNECT tunnels over an HTTP/2 proxy connection
milestone: v2.0: Advanced protocols
labels: type:phase, type:enhancement, area:proxy, area:http2, priority:P3, source:roadmap
---
## Summary
RFC 9113 §8.5 CONNECT streams over the proxy H2 connection. Blocked on [[PH-22d.1]] and [[PLAN-03]].

## Scope (from `Development/docs/phases/phase22d/phase-22d.2-http2-connect-tunnels.md`)
- [ ] CONNECT stream creation (`:method CONNECT`, `:authority`, no `:scheme`/`:path`, no END_STREAM, `Proxy-Authorization` never-indexed)
- [ ] 1xx discard, 2xx establish, 407/421/405/501 handling
- [ ] `Http2ConnectTunnelStream` with correct outer WINDOW_UPDATE timing (deadlock prevention)
- [ ] END_STREAM vs RST semantics on dispose; CONNECT_ERROR mapping
- [ ] Inner HTTP/1.1 and HTTP/2 origins over the tunnel

## Acceptance criteria
- [ ] 22d.2 checklist + specialist reviews pass.

## Context
Source: roadmap Phase 22d.2.

===ISSUE===
---
key: PH-22d.3
title: [Phase 22d.3] HTTP forward-proxy requests over HTTP/2 (no h2c)
milestone: v2.0: Advanced protocols
labels: type:phase, type:enhancement, area:proxy, area:http2, priority:P3, source:roadmap
---
## Summary
Plain-`http://` origin requests forwarded through an HTTPS proxy speaking HTTP/2. Blocked on [[PH-22d.1]] and [[PLAN-03]].

## Scope (from `Development/docs/phases/phase22d/phase-22d.3-forward-proxy-over-h2.md`)
- [ ] Forward dispatch API
- [ ] Header mapping: `:scheme http`, `:authority`, `:path` (never absolute URI), hop-by-hop stripping, `Proxy-Authorization` proxy-only, no `Proxy-Connection`, header list size check
- [ ] Request body streaming (buffered + streamed)
- [ ] Define `H2ForwardUnsupported` detection and fallback

## Acceptance criteria
- [ ] 22d.3 checklist + specialist reviews pass.

## Context
Source: roadmap Phase 22d.3.

===ISSUE===
---
key: PH-22d.4
title: [Phase 22d.4] Proxy HTTP/2 validation: pooling matrix, ownership, security, device runs
milestone: v2.0: Advanced protocols
labels: type:phase, type:test, area:proxy, area:http2, priority:P3, needs-device-validation, source:roadmap
---
## Summary
Validation for 22d.1–22d.3.

## Scope (from `Development/docs/phases/phase22d/phase-22d.4-validation.md`)
- [ ] Proxy H2 pooling matrix
- [ ] Stream ownership and disposal (forward bodies, inner H1/H2 over CONNECT, outer connection dispose, transport dispose)
- [ ] Security: never-indexed `Proxy-Authorization`, hop scopes, no credentials in logs/timeline/keys, distinct pool keys per credential set
- [ ] Numeric memory/latency budgets (replace "bounded memory")
- [ ] iOS/Android IL2CPP device runs

## Acceptance criteria
- [ ] All checks green; journal entry; CLAUDE.md status updated.

## Context
Source: roadmap Phase 22d.4.

===ISSUE===
---
key: PH-16.2
title: [Phase 16.2] WebGL support via browser fetch transport
milestone: v2.0: Advanced protocols
labels: type:phase, type:enhancement, area:transport, priority:P2, source:roadmap
---
## Summary
WebGL cannot use raw sockets; Phase 16.2 adds a `.jslib` fetch transport. Until then WebGL must fail at build time ([[REL-09]]).

## Scope (from `Development/docs/phases/phase16/phase-16.2-webgl-support.md`)
- [ ] `.jslib` wrapping `fetch()` (all methods, headers, body, timeout, `AbortController` cancellation, `ReadableStream` streaming where available)
- [ ] C#↔JS marshaling (`UTF8ToString`, `HEAPU8`, `_malloc`/`_free`), collision-free request IDs
- [ ] `IHttpTransport` implementation registered on WebGL; CORS/network/timeout/abort error mapping
- [ ] Feature matrix: what interceptors work (no proxy, no custom TLS, no HTTP/2 control, cookies via browser)
- [ ] WebSocket via browser `WebSocket` API (separate sub-task)
- [ ] Re-enable WebGL in asmdefs that become supported

## Acceptance criteria
- [ ] WebGL sample runs in Chrome/Firefox/Safari.

## Context
Source: roadmap Phase 16.2.

===ISSUE===
---
key: PH-16.4
title: [Phase 16.4] GraphQL client
milestone: v2.0: Advanced protocols
labels: type:phase, type:enhancement, area:json, priority:P3, source:roadmap
---
## Summary
GraphQL-over-HTTP query builder and typed responses on top of `TurboHTTP.JSON`.

## Scope (from `Development/docs/phases/phase16/phase-16.4-graphql-client.md`)
- [ ] `GraphQLRequest` + reusable fluent `GraphQLQueryBuilder` (omit empty `operationName`/`variables`)
- [ ] `GraphQLResponse<T>` and `GraphQLError` (spec fields)
- [ ] Note: requires a POCO-capable serializer — LiteJson cannot deserialize POCOs ([[MOD-26]])
- [ ] Module placement per module rules

## Acceptance criteria
- [ ] Tests against a sample schema; sample.

## Context
Source: roadmap Phase 16.4.

===ISSUE===
---
key: PH-25
title: [Phase 25] gRPC client
milestone: v2.0: Advanced protocols
labels: type:phase, type:enhancement, area:http2, priority:P3, platform:il2cpp, source:roadmap
---
## Summary
gRPC over the native HTTP/2 stack. Depends on the HTTP/2 hardening in v1.0-beta (trailers, flow control, stream lifecycle) and on ping policy changes ([[H2-12]] — gRPC servers GOAWAY on excessive pings).

## Scope (from `Development/docs/phases/phase-25-grpc-client.md`)
- [ ] Protobuf content handler (`AsProtobuf<T>`), Google.Protobuf, AOT-safe pre-generated serializers
- [ ] Unary calls: length-prefixed messages, `grpc-status`/`grpc-message` trailers, deadlines (`grpc-timeout`)
- [ ] Server-streaming, client-streaming, bidi streaming lifecycles
- [ ] gRPC interceptors (metadata, auth)
- [ ] IL2CPP validation on iOS/Android

## Acceptance criteria
- [ ] Interop with a grpc-go / grpc-dotnet test server for all four call types.

## Context
Source: roadmap Phase 25.

===ISSUE===
---
key: FEAT-01
title: [WebSocket] WebSocket over HTTP/2 (RFC 8441 extended CONNECT)
milestone: v2.0: Advanced protocols
labels: type:enhancement, area:websocket, area:http2, priority:P3, source:review-2026-10
---
## Summary
The WebSocket client always uses HTTP/1.1 Upgrade with empty ALPN. RFC 8441 bootstraps WebSockets on an existing HTTP/2 connection (`SETTINGS_ENABLE_CONNECT_PROTOCOL`, `:method CONNECT`, `:protocol websocket`), sharing the H2 connection with HTTP traffic.

## Scope
- [ ] Honor server `SETTINGS_ENABLE_CONNECT_PROTOCOL=1`
- [ ] Extended CONNECT stream, WebSocket framing over DATA frames, flow-control integration
- [ ] Fallback to HTTP/1.1 Upgrade when unsupported
- [ ] Tests against a server supporting RFC 8441

## Acceptance criteria
- [ ] wss over H2 works against at least one production server implementation; fallback path tested.

## Context
Source: 2026-10 full review (WebSocket platform notes).
