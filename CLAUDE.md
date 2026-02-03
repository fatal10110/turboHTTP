# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

TurboHTTP is a commercial, closed-source HTTP client package for Unity (Asset Store distribution). It uses raw TCP sockets with custom HTTP/1.1 and HTTP/2 implementations — no UnityWebRequest dependency. Target: Unity 2021.3 LTS (.NET Standard 2.1). Platforms: Editor, Standalone, iOS, Android (WebGL deferred to v1.1).

## Architecture

**Layered design:** Public API → Middleware Pipeline → Core Engine → Transport → Serialization

**Module system (11 runtime assemblies + 1 editor):**
- **TurboHTTP.Core** — Client API, request/response types, pipeline. `autoReferenced: true`.
- **TurboHTTP.Transport** — Raw TCP sockets, TLS (SslStream + ALPN), HTTP/1.1 serializer/parser, HTTP/2 framing/HPACK/multiplexing. `allowUnsafeCode: true`, `excludePlatforms: ["WebGL"]`, `noEngineReferences: true`.
- **9 optional modules** (Retry, Cache, Auth, RateLimit, Observability, Files, Unity, Testing, Performance) — all `autoReferenced: false`, reference only Core.
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

## Key Technical Decisions

- **Transport:** Raw `System.Net.Sockets.Socket` with connection pooling (not UnityWebRequest)
- **TLS:** `System.Net.Security.SslStream` with ALPN for HTTP/2 negotiation
- **JSON:** `System.Text.Json` (no external dependencies)
- **HTTP/2:** Native binary framing, HPACK compression, stream multiplexing, flow control
- **Middleware:** ASP.NET Core-style pipeline pattern for request/response interception
- **Namespaces:** Each module uses `TurboHTTP.<ModuleName>` (e.g., `TurboHTTP.Core`, `TurboHTTP.Transport`)
- **Transport Factory:** `HttpTransportFactory` uses `Register(Func<IHttpTransport>)` pattern with `Lazy<T>` thread-safe initialization. Phase 3 registers `RawSocketTransport` via C# 9 `[ModuleInitializer]` in the Transport assembly — auto-runs when assembly loads, no Unity bootstrap needed. `RawSocketTransport.EnsureRegistered()` fallback for IL2CPP timing issues. This avoids Core → Transport circular dependency while keeping `TurboHTTP.Unity` optional.
- **Headers:** `HttpHeaders` uses `Dictionary<string, List<string>>` for multi-value support (RFC 9110 Section 5.3, Set-Cookie per RFC 6265). All header names/values validated for CRLF injection during serialization.
- **Immutability:** `UHttpRequest` defensively clones headers in constructor. `byte[]` body uses shared ownership (documented, not copied). Migration to `ReadOnlyMemory<byte>` deferred to Phase 10.
- **Thread Safety:** `RequestContext` uses lock-based synchronization for timeline and state access. Required for HTTP/2 async continuations.
- **IHttpTransport:** Extends `IDisposable` for connection pool cleanup.
- **Connection Lifecycle:** `ConnectionLease` (IDisposable **class**, not struct) wraps connection + semaphore to guarantee per-host permit is always released. Idempotent `Dispose()` prevents double semaphore release. Prevents deadlock on non-keepalive, exception, and cancellation paths.
- **TLS Strategy:** `SslProtocols.None` (OS-negotiated, Microsoft-recommended) with post-handshake TLS 1.2 minimum enforcement. Runtime probe for `SslClientAuthenticationOptions` overload (.NET 5+); falls back to 4-arg overload with dispose-on-cancel pattern if unavailable.
- **Encoding:** `Encoding.GetEncoding(28591)` cached in static field with custom `Latin1Encoding` fallback for IL2CPP code stripping. `Encoding.Latin1` static property is .NET 5+ only.
- **Error Model:** Transport throws exceptions (`UHttpException`). Client catches and wraps. HTTP 4xx/5xx are normal responses with body intact (not exceptions).

## Development Status

Implementation follows 14 phases documented in `docs/phases/`.

- **Phase 1 (Project Foundation):** COMPLETE — Directory structure, assembly definitions, package files.
- **Phase 2 (Core Type System):** COMPLETE — All 8 core types implemented in `Runtime/Core/`, 3 test files in `Tests/Runtime/Core/`. Reviewed by both specialist agents.
- **Phase 3 (Client API & HTTP/1.1 Transport):** IN PROGRESS — Detailed sub-plans in `docs/phases/phase3/` (5 sub-phases). The old summary at `docs/phases/phase-03-client-api.md` is **deprecated** and must not be used for implementation. External reviews (GPT, Gemini) and specialist agent reviews incorporated.
  - **Phase 3.1 (Client API):** COMPLETE
  - **Phase 3.2 (TCP Connection Pool & TLS):** COMPLETE
  - **Phase 3.3 (HTTP/1.1 Serializer & Parser):** COMPLETE
  - **Phase 3.4 (RawSocketTransport & Wiring):** NEXT
  - **Phase 3.5 (Tests & Integration):** Not started
- **Phases 4–14:** Not started.

Check `docs/00-overview.md` for the full roadmap and `docs/phases/phase-NN-*.md` for each phase's tasks and validation criteria.

## Implementation Milestones

- **M0 (Spike):** Phases 1–3B — Foundation, core types, raw socket transport, HTTP/2
- **M1 (Usable):** Phases 4–5 — Middleware pipeline, content handlers
- **M2 (Feature-complete):** Phases 6–8 — Advanced middleware, Unity integration, editor tools
- **M3 (Production):** Phases 9–13 — Testing, performance, platform validation, release

## Critical Risk Areas

1. **SslStream ALPN under IL2CPP** — Must validate HTTP/2 negotiation on physical iOS/Android devices before scaling past Phase 3B
2. **System.Text.Json + IL2CPP/AOT** — Serialization behavior needs early validation
3. **HTTP/2 flow control** — Stream multiplexing, window updates, HPACK correctness require rigorous testing
4. **Memory target (phased):**
   - Phase 3: ~50KB GC per request (correctness focus; measured via profiler snapshot in Phase 3.5). Dominated by byte-by-byte ReadAsync Task allocations (~29KB for headers alone).
   - Phase 10: <500 bytes GC per request (zero-alloc patterns, ArrayPool, buffered I/O)
   - Phase 3 uses StringBuilder + Encoding for serialization (~600–700 bytes) and byte-by-byte ReadLineAsync (~400 Task allocations per response, ~29KB). Both are documented GC hotspots for Phase 10 rewrite.
5. **Timeout enforcement is best-effort for some operations (.NET Standard 2.1):**
   - `Dns.GetHostAddressesAsync` has no CancellationToken — DNS hangs on mobile networks can consume the entire request timeout. Known limitation.
   - `Socket.ConnectAsync` has no CancellationToken — mitigated with `ct.Register(() => socket.Dispose())` pattern (abrupt close, may throw `ObjectDisposedException`).
   - `SslStream.AuthenticateAsClientAsync(SslClientAuthenticationOptions, CancellationToken)` — fully cancellable (correct overload used since Phase 3).

## Testing

Tests use Unity Test Runner with NUnit. Test assemblies:
- `Tests/Runtime/` — Runtime tests (`TurboHTTP.Tests.Runtime.asmdef`), references all modules
- `Tests/Editor/` — Editor tests (`TurboHTTP.Tests.Editor.asmdef`), Editor-only

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

After reviews pass, **update the implementation journal** (`docs/implementation-journal/`) with a session file documenting the completed step, then update this file's Development Status section.

## Implementation Journal

An implementation journal is maintained in the `docs/implementation-journal/` folder. Each implementation session gets its own file in this folder. After completing each implementation step (phase task, new type, transport change, middleware, test, etc.), you **must** create or update a session file in the journal folder before marking the step as done. Each session file should include:

- **What** was implemented (brief description)
- **Files created/modified** (paths + what each file contains)
- **Decisions made** (architectural choices, trade-offs, deferred items)

File naming convention: `YYYY-MM-<short-description>.md` (e.g., `2025-04-phase3.2-tcp-tls.md`).

This journal serves as a cumulative record of all work done on the project. Read the journal folder at the start of each session to understand the full implementation history.

## Keeping This File Current

This file must be updated whenever a phase is completed, a new module is implemented, conventions change, or architectural decisions are made. After each significant step — new types, new middleware, transport changes, test infrastructure, etc. — update the relevant sections here (especially Development Status, Architecture, and Conventions) so the next Claude Code session starts with accurate context.
