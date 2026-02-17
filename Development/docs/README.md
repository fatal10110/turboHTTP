# TurboHTTP Implementation Documentation

This directory contains the complete implementation plan for TurboHTTP, a production-grade modular HTTP client for Unity.

## Quick Navigation

### Start Here
- **[00-overview.md](00-overview.md)** - Project summary, architecture, and success criteria

### Implementation Phases

All detailed phase documents are in the [`phases/`](phases/) directory:

#### Foundation (M0 Spike)
1. **[Project Foundation](phases/phase-01-project-foundation.md)** - Package structure, assembly definitions
2. **[Core Type System](phases/phase-02-core-types.md)** - Request/Response types, error handling
3. **[Client API & Raw Socket Transport](phases/phase-03-client-api.md)** - UHttpClient, fluent API, TCP/TLS, HTTP/1.1 transport
3B. **[HTTP/2 Protocol](phases/phase-03b-http2.md)** - Binary framing, HPACK, stream multiplexing, flow control, ALPN

#### Core Features (M1 Usable)
4. **[Pipeline Infrastructure](phases/phase-04-pipeline.md)** - Middleware system, basic middlewares
5. **[Content Handlers](phases/phase-05-content-handlers.md)** - JSON, file downloads

#### Hardening (M2 Gate)
6. **[Performance & Hardening](phases/phase-06-performance.md)** - Memory pooling, concurrency
7. **[Testing Infrastructure](phases/phase-07-testing.md)** - Unit/integration tests, record/replay
8. **[Documentation & Samples](phases/phase-08-documentation.md)** - QuickStart, API docs, samples
9. **[Platform Compatibility](phases/phase-09-platform-compat.md)** - iOS/Android testing, IL2CPP

#### Feature Complete (M3)
10. **[Advanced Middleware](phases/phase-10-advanced-middleware.md)** - Caching, rate limiting
11. **[Unity Integration](phases/phase-11-unity-integration.md)** - Texture2D, AudioClip, main thread sync
12. **[Editor Tooling](phases/phase-12-editor-tools.md)** - HTTP Monitor window

#### Expansion Track (M4 pre-release)
14. **[Post-v1.0 Roadmap](phases/phase-14-future.md)** - transport resilience, mobile reliability, extensibility
15. **[Unity Runtime Hardening](phases/phase-15-unity-runtime-hardening.md)** - advanced correctness/performance hardening for Unity asset workflows
16. **[Platform, Protocol, and Security Expansion](phases/phase-16-platform-protocol-security.md)** - WebGL, WebSocket, GraphQL, security/privacy hardening

#### Deferred Release (M5)
17. **[CI/CD & Release](phases/phase-17-release.md)** - Asset Store submission (execute after phases 14-16)

## Project Configuration

- **Licensing:** Closed source, Unity Asset Store (per-seat)
- **Unity Version:** 2021.3 LTS minimum (.NET Standard 2.1)
- **Platforms (v1.0):** Editor, Standalone (Win/Mac/Linux), iOS, Android
- **JSON:** System.Text.Json (built-in)
- **Transport:** Raw TCP sockets with HTTP/1.1 and HTTP/2 (no UnityWebRequest dependency)
- **Architecture:** Modular - Core + Transport + 9 optional runtime modules (+ optional Editor module)

## Module Structure

### Core Modules (Required)
- `TurboHTTP.Core` - Client, request/response, pipeline, JSON
- `TurboHTTP.Transport` - Raw TCP sockets, HTTP/1.1, HTTP/2, TLS/SslStream, connection pooling

### Optional Runtime Modules
- `TurboHTTP.Retry` - Advanced retry with idempotency
- `TurboHTTP.Cache` - HTTP caching with ETag
- `TurboHTTP.Auth` - Authentication middleware
- `TurboHTTP.RateLimit` - Rate limiting
- `TurboHTTP.Observability` - Timeline tracing
- `TurboHTTP.Files` - File downloads with resume
- `TurboHTTP.Unity` - Unity asset handlers
- `TurboHTTP.Testing` - Record/replay, mocking
- `TurboHTTP.Performance` - Memory pooling, concurrency

### Optional Editor Module
- `TurboHTTP.Editor` - HTTP Monitor window (Editor-only)

## Implementation Workflow

1. **Read** [00-overview.md](00-overview.md) to understand the architecture
2. **Follow** phases in this order: 1-12, 14-16, then 17
3. **Validate** each phase before moving to next
4. **Milestone** gates: Complete M0, M1, M2, M3, M4 before M5 (release)

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

**Start with Phase 1 and follow the planned order (1-12, 14-16, then 17).**
