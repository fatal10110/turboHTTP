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
3. **[Client API & Request Builder](phases/phase-03-client-api.md)** - UHttpClient, fluent API, transport

#### Core Features (M1 Usable)
4. **[Pipeline Infrastructure](phases/phase-04-pipeline.md)** - Middleware system, basic middlewares
5. **[Content Handlers](phases/phase-05-content-handlers.md)** - JSON, file downloads

#### Advanced Features (M2 Feature-Complete)
6. **[Advanced Middleware](phases/phase-06-advanced-middleware.md)** - Caching, rate limiting
7. **[Unity Integration](phases/phase-07-unity-integration.md)** - Texture2D, AudioClip, main thread sync
8. **[Editor Tooling](phases/phase-08-editor-tools.md)** - HTTP Monitor window

#### Production Ready (M3 v1.0)
9. **[Testing Infrastructure](phases/phase-09-testing.md)** - Unit/integration tests, record/replay
10. **[Performance & Hardening](phases/phase-10-performance.md)** - Memory pooling, concurrency
11. **[Platform Compatibility](phases/phase-11-platform-compat.md)** - iOS/Android testing, IL2CPP
12. **[Documentation & Samples](phases/phase-12-documentation.md)** - QuickStart, API docs, samples
13. **[CI/CD & Release](phases/phase-13-release.md)** - Asset Store submission

#### Future (M4 v1.x)
14. **[Post-v1.0 Roadmap](phases/phase-14-future.md)** - WebGL, faster transports, adaptive policies

## Project Configuration

- **Licensing:** Closed source, Unity Asset Store (per-seat)
- **Unity Version:** 2021.3 LTS minimum (.NET Standard 2.1)
- **Platforms (v1.0):** Editor, Standalone (Win/Mac/Linux), iOS, Android
- **JSON:** System.Text.Json (built-in)
- **Architecture:** Modular - 1 Core + 9 optional runtime modules (+ optional Editor module)

## Module Structure

### Core Module (Required)
`TurboHTTP.Core` - Client, request/response, pipeline, transport, JSON

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
2. **Follow** phases sequentially (1-14)
3. **Validate** each phase before moving to next
4. **Milestone** gates: Complete M0, M1, M2 before M3 (release)

## Key Differentiators

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

**Start with Phase 1 and work through sequentially for best results.**
