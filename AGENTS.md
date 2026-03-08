# AGENTS.md

This file provides repository-specific guidance for coding agents working in TurboHTTP.

## Start Here

- Read this file before making changes.
- Read the relevant phase document in `Development/docs/phases/` before starting architectural or module-level work.
- Skim the latest relevant entries in `Development/docs/implementation-journal/` to avoid repeating past mistakes or regressing prior decisions.
- Check affected `.asmdef` files before adding dependencies or moving code between modules.
- Keep `AGENTS.md` and `CLAUDE.md` aligned when architectural rules or workflow expectations change.

## Project Snapshot

- TurboHTTP is a commercial, closed-source UPM package for Unity.
- Minimum Unity version is Unity 2021.3 LTS targeting .NET Standard 2.1.
- Primary targets are Editor, Standalone, iOS, and Android.
- Main HTTP transport uses raw `System.Net.Sockets.Socket` with custom HTTP/1.1 and HTTP/2 implementations.
- The main HTTP transport must not depend on `UnityWebRequest`.
- WebGL is not part of the raw socket transport path and requires its own browser-specific strategy.

## Architecture

- Layering: public API -> middleware/pipeline -> core engine -> transport -> serialization/content handlers.
- `TurboHTTP.Core` is the stable foundation. Do not add dependencies from Core to optional feature modules.
- `TurboHTTP.Transport` is the low-level HTTP transport assembly. It is the only runtime assembly with `allowUnsafeCode: true` and it excludes `WebGL`.
- Keep `TurboHTTP.Transport` and transport-adjacent assemblies free of Unity engine dependencies where the current asmdefs already do so.
- Avoid Core -> Transport circular dependencies. Keep transport registration patterns self-contained.
- Core request-building APIs should stay limited to raw HTTP primitives. Module-specific conveniences belong in extension methods inside the owning module.
- Preserve the current error model: transport/network failures become `UHttpException`/`UHttpError`, while HTTP 4xx/5xx remain normal responses with bodies intact.
- Timeout enforcement belongs in transport cancellation logic, not in a dedicated timeout middleware.
- Keep sensitive-header redaction enabled by default in logging and observability paths.

## Module Rules

Current runtime assemblies in the repo:

- Foundation: `TurboHTTP.Core`, `TurboHTTP.Transport`, `TurboHTTP.Complete`
- HTTP feature modules: `TurboHTTP.JSON`, `TurboHTTP.Middleware`, `TurboHTTP.Auth`, `TurboHTTP.Retry`, `TurboHTTP.Cache`, `TurboHTTP.RateLimit`, `TurboHTTP.Observability`, `TurboHTTP.Files`, `TurboHTTP.Testing`, `TurboHTTP.Unity`
- Transport variants/integration: `TurboHTTP.Transport.BouncyCastle`, `TurboHTTP.UniTask`
- WebSocket track: `TurboHTTP.WebSocket`, `TurboHTTP.WebSocket.Transport`, `TurboHTTP.Unity.WebSocket`

Dependency constraints:

- Optional HTTP modules should remain independently includable and should generally depend only on `TurboHTTP.Core`.
- Existing explicit exceptions should not be expanded casually:
  - `TurboHTTP.Transport.BouncyCastle` depends on `TurboHTTP.Core` and `TurboHTTP.Transport`
  - `TurboHTTP.WebSocket.Transport` depends on `TurboHTTP.Core`, `TurboHTTP.WebSocket`, and `TurboHTTP.Transport`
  - `TurboHTTP.Unity.WebSocket` depends on `TurboHTTP.Core`, `TurboHTTP.WebSocket`, and `TurboHTTP.Unity`
  - `TurboHTTP.UniTask` depends on `TurboHTTP.Core`, `TurboHTTP.WebSocket`, and `UniTask`
- `TurboHTTP.Complete` is the aggregation assembly. Only add new module references there when the feature is intended to ship in the complete package.
- Prefer string assembly references in asmdefs, not GUID-based references.

## Design Constraints

- Optimize for IL2CPP/AOT safety first, not just Editor correctness.
- Preserve thread safety for connection pools, transport state machines, metrics, and shared buffer ownership.
- Minimize allocations in hot paths. Reuse the existing pooling and writer infrastructure in `TurboHTTP.Core.Internal` where possible.
- Prefer `ArrayPool<T>`, pooled buffers/writers, `ReadOnlySequence<byte>`, and `ValueTask` in hot paths when the surrounding design already supports them.
- Avoid reflection-heavy or runtime-codegen-heavy approaches unless the repo already uses them for that subsystem and the path is known to be IL2CPP-safe.
- Preserve immutability and ownership rules around requests, responses, and pooled resources.
- Do not broaden unsafe code beyond transport-level code without a documented reason.

## Known Risk Areas

- `SslStream` ALPN behavior on IL2CPP/mobile remains a critical validation area.
- BouncyCastle fallback is a transport fallback, not a bypass for certificate or authentication failures.
- JSON must stay behind the `TurboHTTP.JSON` abstraction. Default behavior is the built-in `LiteJsonSerializer`, with optional `System.Text.Json` support gated by `TURBOHTTP_USE_SYSTEM_TEXT_JSON`.
- WebGL cannot use the socket transport path.
- Mobile networking changes must account for ATS, Android network security configuration, IPv6, backgrounding/suspension, and physical-device TLS differences.

## Source Of Truth

- Roadmap overview: `Development/docs/00-overview.md`
- Detailed phase plans: `Development/docs/phases/`
- Implementation history: `Development/docs/implementation-journal/`
- For Phase 3 work, use the detailed docs in `Development/docs/phases/phase3/`
- Treat `Development/docs/phases/phase-03-client-api.md` as historical summary only; do not use it as the implementation source of truth

## Required Workflow

1. Read the relevant phase documentation and recent implementation journal entries before making significant changes.
2. Check asmdef boundaries before introducing dependencies, moving files, or creating new modules.
3. Keep optional modules independently usable unless there is a documented, intentional exception.
4. Add or update tests for behavior you change, especially transport, parser, pooling, concurrency, platform, or disposal logic.
5. After each significant implementation step, update the implementation journal with:
   - what was implemented
   - files created or modified
   - decisions, trade-offs, and deferred items
6. When phase status, conventions, or architecture change, update both `AGENTS.md` and `CLAUDE.md`.

## Specialist Review Rubrics

The repo already contains two specialist review definitions under `.claude/agents/`. Even if you are not running Claude sub-agents, treat them as mandatory review checklists for relevant work:

- Infrastructure rubric: `.claude/agents/unity-infrastructure-architect.md`
  - Validate architecture, module boundaries, API surface area, memory behavior, thread safety, disposal, IL2CPP/AOT concerns, and missing tests.
- Network rubric: `.claude/agents/unity-network-architect.md`
  - Validate platform compatibility, protocol correctness, TLS/security, cancellation, buffer-pooling strategy, RFC alignment, and required device validation.

Before considering transport, core, networking, pooling, middleware, or platform-sensitive work complete:

- run both rubrics explicitly
- fix findings and re-review
- document any intentional deferrals with rationale and target phase

## Testing

- Use Unity Test Runner with NUnit.
- Runtime tests live under `Tests/Runtime/` with `TurboHTTP.Tests.Runtime.asmdef`.
- Editor tests live under `Tests/Editor/` with `TurboHTTP.Tests.Editor.asmdef`.
- Preserve `UNITY_INCLUDE_TESTS` constraints and `nunit.framework.dll` references in test asmdefs.
- Favor tests that cover parser/serializer correctness, connection reuse, pool safety, cancellation, disposal, concurrency, and platform-sensitive behavior.

## Repository Conventions

- Keep the UPM layout canonical: `Runtime/`, `Editor/`, `Tests/`, `Samples~/`, `Documentation~/`.
- `Samples~` uses the tilde suffix to stay hidden from the Unity Project window.
- Editor tooling belongs under `Editor/`.
- Avoid introducing `UnityWebRequest` into the main HTTP transport path.
- Keep documentation current enough that a new session can recover the project's real state from repo files instead of tribal knowledge.
