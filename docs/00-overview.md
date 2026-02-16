# TurboHTTP Implementation Plan - Overview

## Project Summary

**TurboHTTP** is a production-grade HTTP client for Unity designed to compete with BestHTTP while offering modern architecture, superior observability, and modular design.

## Project Configuration

- **Licensing:** Closed source, commercial package for Unity Asset Store (per-seat licensing)
- **Minimum Unity Version:** Unity 2021.3 LTS (.NET Standard 2.1)
- **Target Platforms (v1.0):** Editor, Standalone (Windows/Mac/Linux), Mobile (iOS/Android)
- **WebGL Support:** Deferred to v1.1 (browser `fetch()` API via `.jslib` interop)
- **Transport Layer:** Raw TCP sockets with custom HTTP/1.1 and HTTP/2 implementation (no UnityWebRequest dependency)
- **TLS:** `System.Net.Security.SslStream` with ALPN for HTTP/2 negotiation
- **JSON Library:** System.Text.Json (built-in for Unity 2021.3+)
- **Distribution:** Unity Asset Store

## Architecture Philosophy

TurboHTTP is built on a **modular architecture** with a required core and optional feature modules. This provides:

1. **Flexibility:** Users can use only the features they need
2. **Performance:** Smaller builds when optional modules are excluded
3. **Maintainability:** Clear separation of concerns
4. **Testability:** Each module can be tested independently
5. **Future-proofing:** Easy to add new modules without breaking existing code

## Module Structure

### Core Module (Required)

- `TurboHTTP.Core` - Client, request/response types, basic pipeline, JSON support
- `TurboHTTP.Transport` - Raw socket transport: TCP connection pool, HTTP/1.1 serializer/parser, HTTP/2 framing + HPACK + multiplexing, TLS via SslStream with ALPN

### Optional Runtime Modules

1. `TurboHTTP.Retry` - Advanced retry logic with idempotency awareness
2. `TurboHTTP.Cache` - HTTP caching with ETag support
3. `TurboHTTP.Auth` - Authentication middleware
4. `TurboHTTP.RateLimit` - Rate limiting per host
5. `TurboHTTP.Observability` - Timeline tracing and metrics
6. `TurboHTTP.Files` - File downloads with resume support
7. `TurboHTTP.Unity` - Unity asset handlers (Texture2D, AudioClip, etc.)
8. `TurboHTTP.Testing` - Record/replay and mock transports
9. `TurboHTTP.Performance` - Memory pooling and concurrency control

### Optional Editor Module

- `TurboHTTP.Editor` - HTTP Monitor window and debugging tools (Editor-only)

## Early Risk Spikes (Do Before Scaling Scope)

These are the easiest "false assumptions" to make early and the most expensive to fix late:

1. **JSON + IL2CPP/AOT reality check:** Verify `System.Text.Json` behavior under IL2CPP on target platforms. If it's painful, introduce a small serialization abstraction so the implementation can be swapped without rewriting the client.
2. **SslStream + ALPN under IL2CPP (CRITICAL):** Validate that `System.Net.Security.SslStream` with ALPN protocol negotiation works on physical iOS and Android devices with IL2CPP enabled. This is the single biggest failure point. If the underlying Mono/IL2CPP runtime doesn't expose ALPN correctly to C#, HTTP/2 negotiation will fail. **Do not proceed past Phase 3B without validating this on a physical iOS device.** If it fails, a native TLS plugin or BouncyCastle fallback is required.
3. **HTTP/2 flow control correctness:** Getting stream multiplexing, window updates, and HPACK right is subtle. **Suggestion:** Evaluate porting internal HTTP/2 logic from Kestrel or dotnet/runtime (MIT licensed) rather than writing from spec. Build a compliance test suite against known HTTP/2 servers (e.g., `nghttp2`, httpbin.org) early. Broken flow control causes silent failures under load.
4. **Deterministic testing feasibility:** Confirm record/replay can be done safely (see Phase 9) without leaking secrets and without adding too much friction to the workflow.

## Review Notes

> **TODO: Missing Offline/Connectivity Handling** - The current plan lacks explicit handling for offline scenarios and network connectivity detection:
>
> - **Network reachability detection**: No utility to check device connectivity before making requests (Unity's `Application.internetReachability` is unreliable and doesn't detect captive portals)
> - **Graceful offline behavior**: No strategy for queuing requests when offline or returning cached responses automatically in offline-first patterns
> - **Connection quality awareness**: No mechanism to detect poor connectivity and adjust timeouts/retry behavior accordingly
>
> Consider adding to Phase 7 (Unity Integration) or creating a dedicated `TurboHTTP.Connectivity` module that provides:
>
> - `IConnectivityMonitor` interface with platform-specific implementations
> - Offline request queuing with automatic retry when connectivity returns
> - Integration with `CacheMiddleware` for offline-first patterns
> - Network quality estimation for adaptive timeout/retry policies

> **TODO: Platform-Specific Networking Requirements** - Phase 11 (Platform Compatibility) needs expanded validation criteria for platform-specific networking constraints:
>
> - **iOS App Transport Security (ATS)**: Cleartext HTTP blocked by default, requires Info.plist exceptions or HTTPS enforcement
> - **iOS IPv6 Requirement**: App Store requires IPv6 support on cellular networks (NAT64/DNS64)
> - **Android Network Security Config**: Android 9+ blocks cleartext traffic by default, requires network_security_config.xml
> - **Mobile Background Limitations**: iOS/Android aggressively suspend background network tasks, need handling for app pause/resume
> - **Certificate Validation Differences**: Each platform has different root CA stores and validation behaviors
>
> Add to Phase 11 validation:
>
> - Test ATS compliance on iOS, document cleartext workarounds
> - Verify IPv6-only network functionality (use iOS simulator IPv6 mode)
> - Test Android Network Security Config scenarios
> - Implement connection draining on application pause/resume events
> - Document platform-specific certificate trust behavior

> **TODO: Missing Critical Security & Enterprise Features** - Several features essential for production games and enterprise customers are not in scope:
>
> - **Certificate Pinning**: No strategy for pinning server certificates (critical for games handling payments or sensitive data)
> - **Proxy Support**: No HTTP/HTTPS/SOCKS proxy support (required for enterprise networks and some testing scenarios)
> - **IPv6 Support**: Not explicitly addressed (iOS App Store requirement)
> - **Connection Draining**: No graceful shutdown strategy when app suspends or closes
>
> Consider adding:
>
> - `TurboHTTP.Security` module with certificate pinning (Phase 6 or 7)
> - Proxy configuration in Transport layer (Phase 3 or defer to v1.1)
> - IPv6 dual-stack support in TCP connection pool (Phase 3)
> - Connection lifecycle management for app suspend/resume (Phase 7)

> **TODO: Memory Management Strategy Needs Earlier Definition** - Phase 10 (Performance & Hardening) defers memory pooling too late. Buffer management is architectural and affects Phase 3 (Transport) design:
>
> - **ArrayPool vs MemoryPool**: Need decision on buffer pooling strategy before implementing HTTP/1.1 parser and HTTP/2 framing
> - **HPACK Dynamic Table**: HTTP/2 compression state can consume significant memory (default 4KB per connection)
> - **Stream Buffer Management**: HTTP/2 multiplexing requires per-stream buffers, needs pooling strategy
> - **GC Target**: "<1KB GC per request" is achievable but requires pooling from day one, not as a Phase 10 optimization
>
> Recommendation:
>
> - Move memory architecture spike to Phase 3 (before transport implementation)
> - Define pooling abstractions (`IBufferPool`, `IObjectPool`) in Core module
> - Implement basic `ArrayPool<byte>` integration in Phase 3
> - Phase 10 can then optimize pool sizing and add advanced features (pre-warming, metrics)

> **TODO: Testing Strategy Gaps - Load, Concurrency, and Chaos Testing** - Phase 9 (Testing Infrastructure) focuses on record/replay and unit tests but lacks:
>
> - **Concurrency Testing**: HTTP/2 multiplexing with 100+ concurrent streams needs validation
> - **Connection Pool Testing**: Race conditions, connection reuse, timeout edge cases
> - **Load Testing**: Sustained request rates, memory stability over time
> - **Chaos Testing**: Network failures, partial responses, slow servers, connection drops mid-stream
> - **HTTP/2 Flow Control**: Deadlock scenarios, window exhaustion, priority starvation
>
> Consider adding Phase 9B (Load & Stress Testing) or expanding Phase 9:
>
> - Load test harness using realistic game traffic patterns (burst requests, background downloads)
> - HTTP/2 stream concurrency tests (spawn 200 streams, verify flow control, measure memory)
> - Chaos test suite with network failure injection (drop connections, delay packets, corrupt frames)
> - Soak tests for memory leak detection (run 10K+ requests, verify GC stability)
> - Add to M3 success criteria: "Passes 1 hour soak test with <10MB memory growth"

> **TODO: Missing Quality of Life Features** - The plan lacks several standard features expected in a commercial library:
>
> - **Decompression**: No automatic handling of Gzip/Brotli content encoding (essential for JSON APIs).
> - **Multipart/Form-Data**: Ensure there is a first-class builder + examples + tests for file uploads (covered in Phase 5, but should be treated as a v1.0 expectation).
> - **Cookie Management**: No "Cookie Jar" middleware for persisting session cookies (stateless by default).
>
> Recommendation:
>
> - Add `DecompressionMiddleware` to Phase 4 (Pipeline).
> - Keep `MultipartFormDataBuilder` in Phase 5 (Content Handlers) and add validation + sample coverage; optionally add a thin convenience API in Phase 3.
> - Add `CookieMiddleware` to Phase 4 or 6.

## Implementation Phases

The implementation is organized into 15 phases, progressing from foundation to production release:

### Foundation (Phases 1-3B)

- **[Phase 1](phases/phase-01-project-foundation.md):** Project Foundation & Structure
- **[Phase 2](phases/phase-02-core-types.md):** Core Type System
- **[Phase 3](phases/phase-03-client-api.md):** Client API, Request Builder & HTTP/1.1 Raw Socket Transport
- **[Phase 3B](phases/phase-03b-http2.md):** HTTP/2 Protocol Implementation

### Core Features (Phases 4-5)

- **[Phase 4](phases/phase-04-pipeline.md):** Pipeline Infrastructure
- **[Phase 5](phases/phase-05-content-handlers.md):** Content Handlers

### Hardening Gate (Phases 6-9)

- **[Phase 6](phases/phase-06-performance.md):** Performance & Hardening
- **[Phase 7](phases/phase-07-testing.md):** Testing Infrastructure
- **[Phase 8](phases/phase-08-documentation.md):** Documentation & Samples
- **[Phase 9](phases/phase-09-platform-compat.md):** Platform Compatibility

### Feature Expansion (Phases 10-12)

- **[Phase 10](phases/phase-10-advanced-middleware.md):** Advanced Middleware
- **[Phase 11](phases/phase-11-unity-integration.md):** Unity Integration
- **[Phase 12](phases/phase-12-editor-tools.md):** Editor Tooling

### Release (Phase 13)

- **[Phase 13](phases/phase-13-release.md):** CI/CD & Release

### Future (Phases 14-15)

- **[Phase 14](phases/phase-14-future.md):** Post-v1.0 Roadmap
- **[Phase 15](phases/phase-15-unity-runtime-hardening.md):** Unity Runtime Hardening and Advanced Asset Pipeline

## Development Milestones

### M0 — Spike (Phases 1-3B)

Core types, raw TCP socket transport, HTTP/1.1 serializer/parser, TLS via SslStream, HTTP/2 framing + HPACK + stream multiplexing, ALPN negotiation, connection pooling, simple GET/POST

### M1 — v0.1 "usable" (Phases 4-5)

Middleware pipeline, retry + logging + metrics, JSON helper, file download

### M2 — v0.5 "hardening gate" (Phases 6-9)

Performance optimization, testing infrastructure, documentation, platform validation

### M3 — v1.0 "feature-complete + release" (Phases 10-13)

Advanced middleware, Unity integration, editor tooling, CI/CD and Asset Store release

### M4 — v1.x "differentiators" (Phases 14-15)

WebGL support (browser fetch API via .jslib), adaptive network policies, WebSocket support, more content handlers

## Key Differentiators

TurboHTTP stands out from competitors through:

1. **HTTP/2 Support:** Native HTTP/2 with multiplexing, HPACK compression, and flow control — not available in UnityWebRequest
2. **Raw Socket Transport:** Full control over TCP connections, TLS, and protocol negotiation — no UnityWebRequest dependency
3. **Modular Architecture:** Use only what you need
4. **Superior Observability:** Timeline tracing for every request, including connection-level metrics
5. **Record/Replay Testing:** Deterministic offline testing
6. **Editor Monitor:** In-editor HTTP traffic inspection
7. **Adaptive Retry:** Intelligent retry with idempotency awareness
8. **Unity-First Design:** Native support for Texture2D, AudioClip, AssetBundles
9. **Platform Realistic:** Documented limitations and workarounds for each platform
10. **Production Ready:** Comprehensive testing, documentation, and samples

## Directory Structure

```
turboHTTP/
├── docs/                          # Implementation documentation (this folder)
│   ├── 00-overview.md            # This file
│   └── phases/                    # Detailed phase documentation
│       ├── phase-01-project-foundation.md
│       ├── phase-02-core-types.md
│       ├── ... (through phase 15)
├── package.json
├── README.md
├── CHANGELOG.md
├── LICENSE.md
├── Runtime/
│   ├── Core/                      # Core module (required)
│   ├── Transport/                 # Raw socket transport (TCP, TLS, HTTP/1.1, HTTP/2)
│   │   ├── Tcp/                   # Connection pool, socket management
│   │   ├── Tls/                   # SslStream wrapper, ALPN, cert validation
│   │   ├── Http1/                 # HTTP/1.1 request serializer, response parser, chunked encoding
│   │   └── Http2/                 # HTTP/2 framing, HPACK, stream multiplexing, flow control
│   ├── Retry/                     # Retry module
│   ├── Cache/                     # Cache module
│   ├── Auth/                      # Auth module
│   ├── RateLimit/                 # Rate limit module
│   ├── Observability/             # Observability module
│   ├── Files/                     # Files module
│   ├── Unity/                     # Unity handlers module
│   ├── Testing/                   # Testing module
│   └── Performance/               # Performance module
├── Editor/                        # Editor module
├── Tests/
│   ├── Runtime/
│   └── Editor/
└── Samples~/
    ├── 01-BasicUsage/
    ├── 02-JsonApi/
    ├── 03-FileDownload/
    ├── 04-Authentication/
    └── 05-AdvancedFeatures/
```

## Success Criteria

v1.0 is ready for Unity Asset Store when:

- ✓ Core + Transport + 9 runtime modules implemented and tested
- ✓ Raw socket transport with HTTP/1.1 and HTTP/2 support
- ✓ TLS/SslStream with ALPN negotiation working on all target platforms
- ✓ Connection pooling with keep-alive for HTTP/1.1 and multiplexing for HTTP/2
- ✓ Editor module (HTTP Monitor) functional in supported Unity versions
- ✓ Integration tests pass on Editor, Standalone, iOS, Android
- ✓ IL2CPP builds work on all platforms (SslStream validated)
- ✓ 80%+ code coverage
- ✓ Performance benchmarks met (<1KB GC per request)
- ✓ Documentation complete (QuickStart, API, platform notes)
- ✓ 5+ working samples
- ✓ HTTP Monitor window functional
- ✓ Record/replay mode working
- ✓ No critical bugs
- ✓ Asset Store submission package ready

## Next Steps

1. Review each phase document in the `phases/` directory
2. Start implementation with Phase 1 (Project Foundation)
3. Progress through phases sequentially
4. Validate each phase before moving to the next
5. Reach M0, M1, M2, M3 milestones
6. Release v1.0 on Unity Asset Store

---

**Read the detailed phase documents to understand the specific implementation tasks, file structures, code examples, and validation criteria for each phase.**
