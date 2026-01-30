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
- **Transport Factory:** `HttpTransportFactory` throws `InvalidOperationException` if no transport is configured. Phase 3 registers `RawSocketTransport` as the default. This avoids Core → Transport circular dependency.
- **Headers:** `HttpHeaders` uses `Dictionary<string, List<string>>` for multi-value support (RFC 9110 Section 5.3, Set-Cookie per RFC 6265).
- **Immutability:** `UHttpRequest` defensively clones headers in constructor. `byte[]` body uses shared ownership (documented, not copied). Migration to `ReadOnlyMemory<byte>` deferred to Phase 3.
- **Thread Safety:** `RequestContext` uses lock-based synchronization for timeline and state access. Required for HTTP/2 async continuations.
- **IHttpTransport:** Extends `IDisposable` for connection pool cleanup.

## Development Status

Implementation follows 14 phases documented in `docs/phases/`.

- **Phase 1 (Project Foundation):** COMPLETE — Directory structure, assembly definitions, package files.
- **Phase 2 (Core Type System):** COMPLETE — All 8 core types implemented in `Runtime/Core/`, 3 test files in `Tests/Runtime/Core/`. Reviewed by both specialist agents.
- **Phases 3–14:** Not started.

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
4. **Memory target:** <1KB GC per request requires buffer pooling from Phase 3, not deferred to Phase 10

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

## Keeping This File Current

This file must be updated whenever a phase is completed, a new module is implemented, conventions change, or architectural decisions are made. After each significant step — new types, new middleware, transport changes, test infrastructure, etc. — update the relevant sections here (especially Development Status, Architecture, and Conventions) so the next Claude Code session starts with accurate context.
