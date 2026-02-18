# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

TurboHTTP is a commercial, closed-source HTTP client package for Unity (Asset Store distribution). It uses raw TCP sockets with custom HTTP/1.1 and HTTP/2 implementations — no UnityWebRequest dependency. Target: Unity 2021.3 LTS (.NET Standard 2.1). Platforms: Editor, Standalone, iOS, Android (WebGL deferred to v1.1).

## Architecture

**Layered design:** Public API → Middleware Pipeline → Core Engine → Transport → Serialization

**Module system (13 runtime assemblies + 1 editor):**
- **TurboHTTP.Core** — Client API, request/response types, pipeline infrastructure. `autoReferenced: true`. Zero external assembly references.
- **TurboHTTP.Transport** — Raw TCP sockets, TLS (SslStream + ALPN), HTTP/1.1 serializer/parser, HTTP/2 framing/HPACK/multiplexing. `allowUnsafeCode: true`, `excludePlatforms: ["WebGL"]`, `noEngineReferences: true`.
- **TurboHTTP.JSON** — JSON serialization facade (`JsonSerializer`, `IJsonSerializer`, `LiteJsonSerializer`), response extensions (`AsJson`, `TryAsJson`, `GetJsonAsync`, etc.), request builder extensions (`WithJsonBody`). References Core. `noEngineReferences: true`.
- **TurboHTTP.Middleware** — General-purpose middlewares (`DefaultHeadersMiddleware`). References Core.
- **TurboHTTP.Auth** — Auth middleware (`AuthMiddleware`, `IAuthTokenProvider`, `StaticTokenProvider`) and builder extensions (`WithBearerToken`). References Core.
- **8 optional modules** (Retry, Cache, RateLimit, Observability, Files, Unity, Testing, Performance) — all `autoReferenced: false`, reference only Core.
- **TurboHTTP.Complete** — Meta-assembly referencing all modules, `autoReferenced: true`.
- **TurboHTTP.Editor** — HTTP Monitor window, Editor-only.

All optional modules depend only on `TurboHTTP.Core`, never on each other. Transport is the sole assembly with unsafe code permission.

**Core types (Phase 2):**
- `HttpMethod` — Enum (GET/POST/PUT/DELETE/PATCH/HEAD/OPTIONS) + zero-alloc extensions (`IsIdempotent`, `HasBody`, `ToUpperString`)
- `HttpHeaders` — Case-insensitive multi-value header collection (`Set`, `Add`, `Get`, `GetValues`, `Clone`)
- `UHttpRequest` — Immutable request (defensive header cloning, `With*` builder pattern, `Metadata` dictionary)
- `UHttpResponse` — Response (`IsSuccessStatusCode`, `GetBodyAsString`, `EnsureSuccessStatusCode`)
- `UHttpError` / `UHttpException` — Error taxonomy with `IsRetryable()` logic (network/timeout retryable, 5xx retryable, 4xx not)
- `RequestContext` — Thread-safe execution context (timeline events, state dictionary, stopwatch)
- `IHttpTransport` — Transport interface (`SendAsync` + `IDisposable`)
- `HttpTransportFactory` — Static factory (throws until Phase 3 registers transport)

**Phase 3 components:**
- `UHttpClient` / `UHttpRequestBuilder` — Fluent request building (raw HTTP primitives only: `WithHeader`, `WithHeaders`, `WithBody`, `WithTimeout`, `WithMetadata`). Module-specific builder methods are extension methods from their own assemblies (e.g., `WithJsonBody` from TurboHTTP.JSON, `WithBearerToken` from TurboHTTP.Auth).
- `RawSocketTransport` — Default transport wiring pool, serializer, parser, and retry-on-stale
- `TcpConnectionPool` / `TlsStreamWrapper` — Per-host pooling, DNS/Connect timeouts, TLS handshake + ALPN
- `Http11RequestSerializer` / `Http11ResponseParser` — HTTP/1.1 wire formatting and parsing

**Phase 4 components (Middleware Pipeline):**
- `HttpPipeline` — Delegate chain built once at `UHttpClient` construction, reused across requests. Empty middleware list = zero overhead.
- `LoggingMiddleware` (Observability) — Configurable request/response logging (None/Minimal/Standard/Detailed levels)
- `DefaultHeadersMiddleware` (Middleware) — Adds headers without overwriting existing (optional override mode)
- `RetryMiddleware` / `RetryPolicy` (Retry) — Exponential backoff, idempotency-aware, retries 5xx and retryable transport errors
- `AuthMiddleware` / `IAuthTokenProvider` / `StaticTokenProvider` (Auth) — Bearer/custom auth token injection
- `MetricsMiddleware` / `HttpMetrics` (Observability) — Thread-safe request metrics with Interlocked (32-bit IL2CPP safe)
- `MockTransport` (Testing) — Thread-safe test transport with deterministic response queueing, request capture history, delay simulation, and JSON/error fixture helpers
- `RecordReplayTransport` (Testing, Phase 7) — Record/replay wrapper with schema-versioned artifacts, strict request-key matching, configurable mismatch policies, and redaction support

## Key Technical Decisions

- **Transport:** Raw `System.Net.Sockets.Socket` with connection pooling (not UnityWebRequest)
- **TLS:** `System.Net.Security.SslStream` with ALPN for HTTP/2 negotiation
- **JSON:** `TurboHTTP.JSON` assembly (references Core) provides `JsonSerializer` static facade with pluggable `IJsonSerializer` interface, response extensions (`AsJson`, `GetJsonAsync`, etc.), and all request builder JSON extensions (`WithJsonBody(string)`, `WithJsonBody<T>`). Default: `LiteJsonSerializer` (AOT-safe). Optional: `System.Text.Json` via `TURBOHTTP_USE_SYSTEM_TEXT_JSON` define. Core has zero dependency on JSON — users add `TurboHTTP.JSON` only if needed.
- **HTTP/2:** Native binary framing, HPACK compression, stream multiplexing, flow control
- **Middleware:** ASP.NET Core-style pipeline pattern for request/response interception
- **Namespaces:** Each module uses `TurboHTTP.<ModuleName>` (e.g., `TurboHTTP.Core`, `TurboHTTP.Transport`)
- **Transport Factory:** `HttpTransportFactory` uses `Register(Func<IHttpTransport>)` pattern with `Lazy<T>` thread-safe initialization. Phase 3 registers `RawSocketTransport` via C# 9 `[ModuleInitializer]` in the Transport assembly — auto-runs when assembly loads, no Unity bootstrap needed. `RawSocketTransport.EnsureRegistered()` fallback for IL2CPP timing issues. This avoids Core → Transport circular dependency while keeping `TurboHTTP.Unity` optional.
- **Headers:** `HttpHeaders` uses `Dictionary<string, List<string>>` for multi-value support (RFC 9110 Section 5.3, Set-Cookie per RFC 6265). All header names/values validated for CRLF injection during serialization.
- **Immutability:** `UHttpRequest` defensively clones headers in constructor. `byte[]` body uses shared ownership (documented, not copied). Migration to `ReadOnlyMemory<byte>` deferred to Phase 6.
- **Thread Safety:** `RequestContext` uses lock-based synchronization for timeline and state access. Required for HTTP/2 async continuations.
- **IHttpTransport:** Extends `IDisposable` for connection pool cleanup.
- **Connection Lifecycle:** `ConnectionLease` (IDisposable **class**, not struct) wraps connection + semaphore to guarantee per-host permit is always released. Idempotent `Dispose()` prevents double semaphore release. Prevents deadlock on non-keepalive, exception, and cancellation paths.
- **TLS Strategy:** `SslProtocols.None` (OS-negotiated, Microsoft-recommended) with post-handshake TLS 1.2 minimum enforcement. Runtime probe for `SslClientAuthenticationOptions` overload (.NET 5+); falls back to 4-arg overload with dispose-on-cancel pattern if unavailable.
- **Encoding:** `Encoding.GetEncoding(28591)` cached in static field with custom `Latin1Encoding` fallback for IL2CPP code stripping. `Encoding.Latin1` static property is .NET 5+ only.
- **Error Model:** Transport throws exceptions (`UHttpException`). Client catches and wraps. HTTP 4xx/5xx are normal responses with body intact (not exceptions).
- **Pipeline:** Delegate chain built once at `UHttpClient` construction. Empty middleware list = direct transport call (zero overhead). Request flow: M[0] → M[1] → ... → Transport. Response flow: Transport → ... → M[1] → M[0].
- **Timeout Enforcement:** Handled exclusively by `RawSocketTransport` via `CancellationTokenSource.CancelAfter(request.Timeout)`. Timeout throws `UHttpException(UHttpErrorType.Timeout)` — same exception path as DNS/connect/network failures. No separate TimeoutMiddleware (removed: transport already handles it, and RetryMiddleware catches timeout exceptions via `IsRetryable()`).
- **Retry Middleware:** Only retries idempotent methods by default (`HttpMethod.IsIdempotent()`). Two retry paths: response path (5xx) returns last failed response when exhausted; exception path (`UHttpException` with `IsRetryable()`, including `UHttpErrorType.Timeout`) re-throws when exhausted. Exponential backoff capped by `RetryPolicy.MaxDelay` (30s default) to prevent unbounded delay growth.
- **Metrics Thread Safety:** `HttpMetrics` uses public fields for `Interlocked` compatibility. `AverageResponseTimeMs` stored as `long` bits via `BitConverter.DoubleToInt64Bits` for atomic read/write on 32-bit IL2CPP.
- **Builder Extension Pattern:** `UHttpRequestBuilder` in Core exposes only raw HTTP primitives (`WithHeader`, `WithBody`, `WithTimeout`, `WithMetadata`). Convenience methods live in their respective module assemblies as C# extension methods on `UHttpRequestBuilder` — e.g., `WithJsonBody` in `TurboHTTP.JSON.JsonRequestBuilderExtensions`, `WithBearerToken` in `TurboHTTP.Auth.AuthBuilderExtensions`. Users get these methods by adding a `using` for the module namespace. This keeps Core content-format agnostic and prevents convenience-alias bloat.
- **Object Pooling:** `ObjectPool<T>` uses array-backed storage with `Interlocked.CompareExchange` for atomic capacity enforcement (not `ConcurrentBag` — avoids IL2CPP thread-local storage issues). `ByteArrayPool` wraps `ArrayPool<byte>.Shared` (BCL battle-tested, no custom bucketing needed for Phase 6).
- **Concurrency Control:** `ConcurrencyLimiter` acquires global semaphore before per-host semaphore (prevents starvation). Cancellation-safe: if per-host acquire fails after global acquired, global is released in catch block. `ConcurrencyMiddleware` wraps limiter for pipeline integration.
- **Sensitive Header Redaction:** `LoggingMiddleware` redacts `Authorization`, `Cookie`, `Set-Cookie`, `Proxy-Authorization`, `WWW-Authenticate`, `X-Api-Key` by default. Configurable via `redactSensitiveHeaders` parameter.
- **Disposal Hardening:** `UHttpClient.Dispose()` disposes `IDisposable` middlewares (not just transport). `RawSocketTransport.Dispose()` uses `Interlocked.CompareExchange` for atomic CAS (prevents double-dispose race). All disposal is idempotent.

## Development Status

Implementation follows 14 phases documented in `Development/docs/phases/`.

- **Phase 1 (Project Foundation):** COMPLETE — Directory structure, assembly definitions, package files.
- **Phase 2 (Core Type System):** COMPLETE — All 8 core types implemented in `Runtime/Core/`, 3 test files in `Tests/Runtime/Core/`. Reviewed by both specialist agents.
- **Phase 3 (Client API & HTTP/1.1 Transport):** COMPLETE — Detailed sub-plans in `Development/docs/phases/phase3/` (5 sub-phases). The old summary at `Development/docs/phases/phase-03-client-api.md` is **deprecated** and must not be used for implementation. External reviews (GPT, Gemini) and specialist agent reviews incorporated.
  - **Phase 3.1 (Client API):** COMPLETE
  - **Phase 3.2 (TCP Connection Pool & TLS):** COMPLETE
  - **Phase 3.3 (HTTP/1.1 Serializer & Parser):** COMPLETE
  - **Phase 3.4 (RawSocketTransport & Wiring):** COMPLETE
  - **Phase 3.5 (Tests & Integration):** COMPLETE — Added Core/HTTP1/Pool unit tests and manual integration harness.
- **Phase 3B (HTTP/2 Protocol):** COMPLETE — Full HTTP/2 support with binary framing, HPACK compression, stream multiplexing, and flow control. R9 fixes: reusable frame header buffers, ArrayPool for payloads, MemoryStream for header block accumulation, MaxResponseBodySize limit. See `Development/docs/implementation-journal/2026-02-phase3b-http2.md`.
- **Phase 3C (BouncyCastle TLS Fallback):** COMPLETE — Optional BouncyCastle TLS module for IL2CPP platforms where SslStream ALPN may fail. TLS abstraction layer (ITlsProvider, TlsProviderSelector), BouncyCastle source bundled under TurboHTTP.SecureProtocol namespace.
- **Phase 4 (Pipeline Infrastructure):** COMPLETE — ASP.NET Core-style middleware pipeline. See `Development/docs/implementation-journal/2026-02-phase4-pipeline.md`.
  - **Phase 4.1 (Pipeline Executor):** COMPLETE — `HttpPipeline` delegate chain built once, integrated into `UHttpClient.SendAsync`.
  - **Phase 4.2 (Core Middlewares):** COMPLETE — Originally in Core; since extracted: `LoggingMiddleware` → Observability, `DefaultHeadersMiddleware` → Middleware, `TimeoutMiddleware` → deleted (transport handles timeouts).
  - **Phase 4.3 (Module Middlewares):** COMPLETE — `RetryMiddleware` (Retry), `AuthMiddleware` (Auth), `MetricsMiddleware` (Observability) in separate assemblies.
  - **Phase 4.4 (MockTransport):** COMPLETE — `MockTransport` foundation (3 constructor overloads), later extended in Phase 7 with queue/capture/helper APIs.
  - **Phase 4.5 (Tests):** COMPLETE — 8 test files covering pipeline, all middlewares, and integration.
- **Phase 5 (Content Handlers):** COMPLETE — JSON extensions (AsJson, TryAsJson, GetJsonAsync, PostJsonAsync, PutJsonAsync, PatchJsonAsync, DeleteJsonAsync), FileDownloader with resume/checksum/progress, MultipartFormDataBuilder, ContentTypes constants, GetBodyAsString(Encoding) + GetContentEncoding(). See `Development/docs/implementation-journal/2026-02-phase5-content-handlers.md`.
- **Core Extraction (Post-M1):** COMPLETE — Extracted non-core concerns from TurboHTTP.Core: LoggingMiddleware → Observability, DefaultHeadersMiddleware → new Middleware assembly, JSON extensions → JSON assembly, TimeoutMiddleware deleted (redundant with transport timeout). Removed convenience aliases (`Accept`, `ContentType`, `WithBearerToken`) from `UHttpRequestBuilder` — builder now has only raw HTTP primitives. `WithBearerToken` moved to `TurboHTTP.Auth.AuthBuilderExtensions`. Core now has zero external assembly references. See `Development/docs/implementation-journal/2026-02-core-extraction.md`.
- **Phase 6 (Performance & Hardening):** COMPLETE — ObjectPool, ByteArrayPool, ConcurrencyLimiter, ConcurrencyMiddleware, RequestQueue. Disposal hardening (UHttpClient disposes middlewares, RawSocketTransport atomic disposal). Timeline optimization (lazy dict in TimelineEvent). ALPN reflection caching in SslStreamTlsProvider. Logging redaction for sensitive headers. Stress tests (1000-request, concurrency enforcement, multi-host, pool leak detection). See `Development/docs/implementation-journal/2026-02-phase6-performance.md`.
- **Security Hardening (Post-Phase 6):** COMPLETE — Fixes from unified review: M-2 (connection drain check), M-3 (TE+CL RFC compliance), M-4 (path traversal protection), H-3 (CRLF injection defense-in-depth), HPACK decompression bomb protection (128KB limit), IPv6 preference in address sorting, DNS task observation, multipart boundary quoting. Phase docs updated with redirect/cookie middleware (Phase 10) and background networking (Phase 14). See `Development/docs/implementation-journal/2026-02-security-hardening.md`.
- **Phase 7 (Testing Infrastructure):** COMPLETE — Extended `MockTransport` (queue/capture/helpers), added `RecordReplayTransport` (record/replay/passthrough, strict mismatch by default, redaction, SHA-256 hashing), added Testing `link.xml` guidance, added `TestHelpers`, `CoreTypesTests`, deterministic `IntegrationTests` + optional `ExternalNetwork` category, and `BenchmarkTests` quality gates. See `Development/docs/implementation-journal/2026-02-phase7-testing.md`.
- **Phase 10 (Advanced Middleware):** COMPLETE — Cache/revalidation middleware, redirect middleware, cookie middleware, and HTTP/1.1 parser streaming improvements. See `Development/docs/implementation-journal/2026-02-phase10-advanced-middleware.md`.
- **Phase 11 (Unity Integration):** COMPLETE — Added `MainThreadDispatcher`, `Texture2DHandler`, `AudioClipHandler`, `UnityExtensions`, and `CoroutineWrapper` with dedicated Unity runtime tests. See `Development/docs/implementation-journal/2026-02-phase11-unity-integration.md`.
- **Phase 8 + Phases 12–14:** Not started.

Check `Development/docs/00-overview.md` for the full roadmap and `Development/docs/phases/phase-NN-*.md` for each phase's tasks and validation criteria.

## Implementation Milestones

- **M0 (Spike):** Phases 1–3B — Foundation, core types, raw socket transport, HTTP/2
- **M1 (Usable):** Phases 4–5 — Middleware pipeline, content handlers
- **M2 (Hardening gate):** Phases 6–9 — Performance, testing, documentation, platform validation
- **M3 (Feature-complete + release):** Phases 10–13 — Advanced middleware, Unity integration, editor tools, release

## Critical Risk Areas

1. **SslStream ALPN under IL2CPP** — Must validate HTTP/2 negotiation on physical iOS/Android devices before scaling past Phase 3B. Phase 3C defines BouncyCastle TLS fallback if SslStream ALPN fails on mobile platforms.
2. **System.Text.Json + IL2CPP/AOT** — Serialization behavior needs early validation
3. **HTTP/2 flow control** — Stream multiplexing, window updates, HPACK correctness require rigorous testing
4. **Memory target (phased):**
   - Phase 3: ~50KB GC per request (correctness focus; measured via profiler snapshot in Phase 3.5). Dominated by byte-by-byte ReadAsync Task allocations (~29KB for headers alone).
   - Phase 6: <500 bytes GC per request (zero-alloc patterns, ArrayPool, buffered I/O)
   - Phase 3 uses StringBuilder + Encoding for serialization (~600–700 bytes) and byte-by-byte ReadLineAsync (~400 Task allocations per response, ~29KB). Both are documented GC hotspots for Phase 6 rewrite.
5. **Timeout enforcement is best-effort for some operations (.NET Standard 2.1):**
   - `Dns.GetHostAddressesAsync` has no CancellationToken — DNS hangs on mobile networks can consume the entire request timeout. Known limitation.
   - `Socket.ConnectAsync` has no CancellationToken — mitigated with `ct.Register(() => socket.Dispose())` pattern (abrupt close, may throw `ObjectDisposedException`).
   - `SslStream.AuthenticateAsClientAsync(SslClientAuthenticationOptions, CancellationToken)` — fully cancellable (correct overload used since Phase 3).

## Testing

Tests use Unity Test Runner with NUnit. Test assemblies and key suites:
- `Tests/Runtime/` — Runtime tests (`TurboHTTP.Tests.Runtime.asmdef`), references all modules
- `Tests/Editor/` — Editor tests (`TurboHTTP.Tests.Editor.asmdef`), Editor-only
- HTTP/1.1 serializer/parser tests: `Tests/Runtime/Transport/Http11SerializerTests.cs`, `Tests/Runtime/Transport/Http11ResponseParserTests.cs`
- Core client/factory tests: `Tests/Runtime/Core/UHttpClientTests.cs`
- TCP pool + transport behavior tests: `Tests/Runtime/Transport/TcpConnectionPoolTests.cs`, `Tests/Runtime/Transport/Http1/RawSocketTransportTests.cs`
- Manual integration harness (Editor/Play Mode): `Tests/Runtime/TestHttpClient.cs`
- Pipeline tests: `Tests/Runtime/Pipeline/HttpPipelineTests.cs`, `LoggingMiddlewareTests.cs`, `DefaultHeadersMiddlewareTests.cs`
- Module middleware tests: `Tests/Runtime/Retry/RetryMiddlewareTests.cs`, `Tests/Runtime/Auth/AuthMiddlewareTests.cs`, `Tests/Runtime/Observability/MetricsMiddlewareTests.cs`
- Pipeline integration tests: `Tests/Runtime/Integration/PipelineIntegrationTests.cs`
- Phase 7 integration tests: `Tests/Runtime/Integration/IntegrationTests.cs` (deterministic suite + `ExternalNetwork` category split)
- Phase 7 core tests: `Tests/Runtime/Core/CoreTypesTests.cs`
- Phase 7 benchmark tests: `Tests/Runtime/Performance/BenchmarkTests.cs`
- Performance tests: `Tests/Runtime/Performance/ObjectPoolTests.cs`, `ConcurrencyLimiterTests.cs`, `StressTests.cs`, `BenchmarkTests.cs`
- Shared test utilities: `Tests/Runtime/TestHelpers.cs`

Both use `defineConstraints: ["UNITY_INCLUDE_TESTS"]` and `precompiledReferences: ["nunit.framework.dll"]`.

## Conventions

- UPM package structure (`package.json` at root, `Runtime/`, `Editor/`, `Tests/`, `Samples~/`, `Documentation~/`)
- `Samples~` uses tilde suffix to hide from Unity Project window
- Assembly definitions use GUID-less string references (e.g., `"TurboHTTP.Core"`)
- Optional modules must remain independently includable — never add cross-module references

## Post-Implementation Review

After completing each implementation step (phase task, new type, transport change, middleware, etc.), invoke both specialist agents for review before marking the step as done:

- **unity-infrastructure-architect** (`.claude/agents/unity-infrastructure-architect.md`) — Reviews architecture, memory efficiency, thread safety, IL2CPP/AOT concerns, module dependency rules, and resource disposal.
- **unity-network-architect** (`.claude/agents/unity-network-architect.md`) — Reviews platform compatibility, protocol correctness, TLS/security, zero-allocation patterns, and validates against relevant RFCs.

Both reviews must pass before proceeding to the next step. When a review identifies issues that require code changes, fix the issues and then run both reviews again as a verification pass. Repeat until all issues are resolved or explicitly deferred with documented rationale and target phase.

After reviews pass, **update the implementation journal** (`Development/docs/implementation-journal/`) with a session file documenting the completed step, then update this file's Development Status section.

## Implementation Journal

An implementation journal is maintained in the `Development/docs/implementation-journal/` folder. Each implementation session gets its own file in this folder. After completing each implementation step (phase task, new type, transport change, middleware, test, etc.), you **must** create or update a session file in the journal folder before marking the step as done. Each session file should include:

- **What** was implemented (brief description)
- **Files created/modified** (paths + what each file contains)
- **Decisions made** (architectural choices, trade-offs, deferred items)

File naming convention: `YYYY-MM-<short-description>.md` (e.g., `2025-04-phase3.2-tcp-tls.md`).

This journal serves as a cumulative record of all work done on the project. Read the journal folder at the start of each session to understand the full implementation history.

## Keeping This File Current

This file must be updated whenever a phase is completed, a new module is implemented, conventions change, or architectural decisions are made. After each significant step — new types, new middleware, transport changes, test infrastructure, etc. — update the relevant sections here (especially Development Status, Architecture, and Conventions) so the next Claude Code session starts with accurate context.
