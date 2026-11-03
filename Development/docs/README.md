# TurboHTTP Implementation Documentation

This directory contains the complete implementation plan for TurboHTTP, a production-grade modular HTTP client for Unity.

## Quick Navigation

### Start Here
- **[00-overview.md](00-overview.md)** - Project summary, architecture, and success criteria
- **[how-to-run-tests-with-temp-unity-project.md](how-to-run-tests-with-temp-unity-project.md)** - Sync package source to a temp path and run Unity tests from a separate test project

### Implementation Phases

All detailed phase documents are in the [`phases/`](phases/) directory:

Each phase doc carries a `**Milestone:**` header with its GitHub milestone (`v1.0-alpha: Release blockers`, `v1.0-beta: Protocol & correctness hardening`, `v1.0 GA: Docs, validation & release`, `v1.1: Post-release hardening`, `v1.2: Features & performance`, `v2.0: Advanced protocols`). See [00-overview.md](00-overview.md#development-milestones) for the mapping and the 2026-11-02 re-baseline (feature freeze until v1.0 GA).

#### Foundation (v1.0, shipped)
1. **[Project Foundation](phases/phase-01-project-foundation.md)** - Package structure, assembly definitions
2. **[Core Type System](phases/phase-02-core-types.md)** - Request/Response types, error handling
3. **[Client API & Raw Socket Transport](phases/phase-03-client-api.md)** - UHttpClient, fluent API, TCP/TLS, HTTP/1.1 transport
3B. **[HTTP/2 Protocol](phases/phase-03b-http2.md)** - Binary framing, HPACK, stream multiplexing, flow control, ALPN
3C. **[BouncyCastle TLS Fallback](phases/phase-03c-bouncy-castle-tls.md)** - opt-in TLS fallback (`TURBOHTTP_BOUNCYCASTLE`)
3D. **[JSON Fallback](phases/phase-03d-json-fallback.md)** - deprecated stub; plans in `phases/phase3d/`

#### Core Features (v1.0, shipped)
4. **[Pipeline Infrastructure](phases/phase-04-pipeline.md)** - Middleware system, basic middlewares (rewritten as interceptors in Phase 22)
5. **[Content Handlers](phases/phase-05-content-handlers.md)** - JSON, file downloads

#### Hardening (v1.0)
6. **[Performance & Hardening](phases/phase-06-performance.md)** - Memory pooling, concurrency
7. **[Testing Infrastructure](phases/phase-07-testing.md)** - Unit/integration tests, record/replay
8. **[Documentation & Samples](phases/phase-08-documentation.md)** - QuickStart, API docs, samples
9. **[Platform Compatibility](phases/phase-09-platform-compat.md)** - iOS/Android testing, IL2CPP (device matrix #99 open)

#### Feature Expansion (v1.0, shipped)
10. **[Advanced Middleware](phases/phase-10-advanced-middleware.md)** - Caching, rate limiting
11. **[Unity Integration](phases/phase-11-unity-integration.md)** - Texture2D, AudioClip, main thread sync
12. **[Editor Tooling](phases/phase-12-editor-tools.md)** - HTTP Monitor window

#### Expansion Track
14. **[Post-v1.0 Roadmap](phases/phase-14-future.md)** - transport resilience, mobile reliability, extensibility (shipped; background networking experimental and opt-in)
15. **[Unity Runtime Hardening](phases/phase-15-unity-runtime-hardening.md)** - advanced correctness/performance hardening for Unity asset workflows
16. **[Extended Capabilities](phases/phase-16-advanced-capabilities.md)** - rate limiting, WebGL, security/privacy, GraphQL, parallel helpers (post-1.0: v1.1, v1.2, v2.0)

#### Release
17. **[CI/CD & Release](phases/phase-17-release.md)** - CI done; `1.0.0-pre.N` tags, then Asset Store submission (#101)

#### Protocol and Runtime Rewrites
18. **[WebSocket Client](phases/phase-18-websocket-client.md)** and **[18a Advanced WebSocket](phases/phase-18a-websocket-advanced.md)** (experimental until #104)
19. **[Async Runtime Refactor](phases/phase-19-async-runtime-refactor.md)**, **[19a Zero-Allocation](phases/phase-19a-zero-allocation.md)**, 19b unified pipeline (`phases/phase19b/`), **[19c Body Zero-Copy](phases/phase-19c-body-zerocopy-transport.md)** (post-1.0, v1.2)
22. Interceptor rewrite (`phases/phase22/`), **[22a Streaming](phases/phase-22a-end-to-end-streaming.md)**, **[22b Streaming Extensions](phases/phase-22b-streaming-extensions.md)**, **[22c HTTP/2 through CONNECT](phases/phase-22c-proxy-http2-alpn.md)** (off by default until #103), 22d proxy HTTP/2 (post-1.0, v2.0; plan docs pending, #160)

#### Post-1.0 Plans (feature freeze until GA)
23. **[API Configuration Review](phases/phase-23-api-config-extensions.md)** (v1.2)
24. **[Advanced Content Handlers](phases/phase-24-advanced-content-handlers.md)** (decompression shipped; the rest v1.2)
25. **[gRPC Client](phases/phase-25-grpc-client.md)** (v2.0)

Phase 13 has no plan document.

## Project Configuration

- **Licensing:** MIT (`LICENSE.md`)
- **Unity Version:** 2021.3 LTS minimum (.NET Standard 2.1)
- **Platforms (v1.0):** Editor, Standalone (Win/Mac/Linux), iOS, Android
- **JSON:** built-in LiteJson by default; `System.Text.Json` is opt-in (`TURBOHTTP_USE_SYSTEM_TEXT_JSON`)
- **Transport:** Raw TCP sockets with HTTP/1.1 and HTTP/2 (no UnityWebRequest dependency); TLS via `SslStream`, BouncyCastle opt-in
- **Architecture:** Modular - Core + Transport + optional runtime modules (+ optional Editor module); the full list is in [00-overview.md](00-overview.md#module-structure)
- **Distribution:** `1.0.0-pre.N` (Git URL / OpenUPM preview), then Unity Asset Store (#101)

## Implementation Workflow

1. **Read** [00-overview.md](00-overview.md) to understand the architecture
2. **Follow** the open issues by milestone: v1.0-alpha, v1.0-beta, v1.0 GA (bug fixes, validation and docs only until GA), then v1.1, v1.2, v2.0
3. **Validate** each change with `Tools~/ci/check.sh`; Unity suites run in the host editor (`Development/docs/agents/testing.md`)
4. **Release:** `1.0.0-pre.N` tags from `main`; `1.0.0` GA after the device matrix (#99) and a green full host-editor run

## Key Differentiators

- **HTTP/2:** Native multiplexing, HPACK compression, flow control — not available in UnityWebRequest
- **Raw Sockets:** Full control over TCP, TLS, and protocol negotiation
- **Modular:** Use only what you need
- **Observable:** Timeline tracing for every request
- **Testable:** Record/replay mode for deterministic testing
- **Unity-First:** Native support for Texture2D, AudioClip, etc.
- **Production-Ready:** Comprehensive testing and documentation

## Support

For questions or issues during implementation:
- Review the specific phase document
- Check validation criteria in each phase
- Refer to code examples provided
- See [high-level.md](../high-level.md) for architectural context

---

**Each phase document contains:**
- Overview and goals
- Detailed tasks with code examples
- Validation criteria
- Next steps
- Implementation notes

**Start with [00-overview.md](00-overview.md); the phase plans are the background for the open issues.**
