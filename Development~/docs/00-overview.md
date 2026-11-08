# TurboHTTP Implementation Plan - Overview

## Project Summary

**TurboHTTP** is a production-grade HTTP client for Unity designed to compete with BestHTTP while offering modern architecture, superior observability, and modular design.

## Project Configuration

- **Licensing:** MIT (`LICENSE.md`); third-party notices in `Third-Party Notices.md`
- **Minimum Unity Version:** Unity 2021.3 LTS (.NET Standard 2.1)
- **Target Platforms (v1.0):** Editor, Standalone (Windows/Mac/Linux), Mobile (iOS/Android). WebGL is not supported by the socket transport; browser `fetch()` support is [Phase 16.2](phases/phase-16-advanced-capabilities.md) (v2.0, #130)
- **Transport Layer:** Raw TCP sockets with custom HTTP/1.1 and HTTP/2 implementation (no UnityWebRequest dependency)
- **TLS:** `System.Net.Security.SslStream` with ALPN for HTTP/2 negotiation; the vendored BouncyCastle provider is an opt-in fallback (`TURBOHTTP_BOUNCYCASTLE`, [Phase 3C](phases/phase-03c-bouncy-castle-tls.md)) and never a bypass for certificate failures
- **JSON Library:** the built-in LiteJson reader/writer (`TurboHTTP.JSON`) by default; `System.Text.Json` is opt-in via `TURBOHTTP_USE_SYSTEM_TEXT_JSON`. With `TurboHTTP.Complete` in the project, `JsonUtilitySerializer` (Unity `JsonUtility`) is installed over LiteJson after startup (#271)
- **Distribution:** `1.0.0-pre.N` tags from `main` (Git URL / OpenUPM preview) while the device matrix waits for hardware; `1.0.0` GA comes after the device matrix (#99) and a green full host-editor run: GA is the `v1.0.0` tag, its GitHub Release and the OpenUPM listing ([release checklist](release-checklist.md), #101); the Unity Asset Store is a post-1.0 decision

## Architecture Philosophy

TurboHTTP is built on a **modular architecture** with a required core and optional feature modules. This provides:

1. **Flexibility:** Users can use only the features they need
2. **Performance:** Smaller builds when optional modules are excluded
3. **Maintainability:** Clear separation of concerns
4. **Testability:** Each module can be tested independently
5. **Future-proofing:** Easy to add new modules without breaking existing code

## Module Structure

### Core Module (Required)

- `TurboHTTP.Core` - Client, request/response types, interceptor pipeline
- `TurboHTTP.Transport` - Raw socket transport: TCP connection pool, HTTP/1.1 serializer/parser, HTTP/2 framing + HPACK + multiplexing, TLS via SslStream with ALPN, proxy and CONNECT tunnels

### Optional Runtime Modules

Each depends only on `TurboHTTP.Core` unless `Development~/docs/agents/architecture.md` documents an exception.

1. `TurboHTTP.Middleware` - Redirect, cookie, decompression and default-header interceptors
2. `TurboHTTP.Retry` - Retry logic with idempotency awareness
3. `TurboHTTP.Cache` - HTTP caching with validators (RFC 9111)
4. `TurboHTTP.Auth` - Authentication interceptors, OAuth 2.0, OIDC, PKCE
5. `TurboHTTP.RateLimit` - Rate limiting and concurrency control
6. `TurboHTTP.Observability` - Timeline tracing, metrics, redaction
7. `TurboHTTP.Files` - File downloads with resume support
8. `TurboHTTP.JSON` - JSON serializer abstraction with the built-in LiteJson default
9. `TurboHTTP.Testing` - Record/replay and mock transports
10. `TurboHTTP.Unity` - Unity asset handlers (Texture2D, AudioClip, etc.), main-thread helpers, mobile networking (adaptive and, experimental and opt-in, background)
11. `TurboHTTP.WebSocket` - WebSocket client (RFC 6455, permessage-deflate RFC 7692)
12. `TurboHTTP.WebSocket.Transport` - WebSocket transport over the raw-socket stack
13. `TurboHTTP.Unity.WebSocket` - Unity bridge for the WebSocket client
14. `TurboHTTP.UniTask` - UniTask adapters for HTTP (needs the UniTask package); `TurboHTTP.UniTask.WebSocket` holds the WebSocket adapters (#116)
15. `TurboHTTP.Transport.BouncyCastle` - Opt-in TLS fallback over the vendored `TurboHTTP.SecureProtocol` library (`TURBOHTTP_BOUNCYCASTLE`)
16. `TurboHTTP.Complete` - Aggregation assembly holding the startup registration (`Runtime/Bootstrap`, `TurboHttpBootstrap`) and `JsonUtilitySerializer`

### Optional Editor Module

- `TurboHTTP.Editor` - HTTP Monitor window and debugging tools (Editor-only)

## Early Risk Spikes (Do Before Scaling Scope)

These are the easiest "false assumptions" to make early and the most expensive to fix late:

1. **JSON + IL2CPP/AOT reality check:** The default JSON path is the built-in LiteJson reader/writer, which needs no reflection and is stripping-safe; `System.Text.Json` is opt-in (`TURBOHTTP_USE_SYSTEM_TEXT_JSON`). The serializer abstraction (`IJsonSerializer`, `JsonSerializer.SetDefault`) exists so an implementation can be swapped without rewriting the client (Phase 3D, deprecated stub; plans in `phases/phase3d/`).
2. **SslStream + ALPN under IL2CPP (CRITICAL):** Validate that `System.Net.Security.SslStream` with ALPN protocol negotiation works on physical iOS and Android devices with IL2CPP enabled. This is the single biggest failure point. If the underlying Mono/IL2CPP runtime doesn't expose ALPN correctly to C#, HTTP/2 negotiation will fail. The original rule was "do not proceed past Phase 3B without validating this on a physical iOS device"; that gate was never met before the later phases were built, and it now maps to the physical-device matrix **#99 (v1.0 GA)**, pending hardware. BouncyCastle (Phase 3C) is the opt-in fallback.
3. **HTTP/2 flow control correctness:** Getting stream multiplexing, window updates, and HPACK right is subtle. **Suggestion:** Evaluate porting internal HTTP/2 logic from Kestrel or dotnet/runtime (MIT licensed) rather than writing from spec. Build a compliance test suite against known HTTP/2 servers (e.g., `nghttp2`, httpbin.org) early. Broken flow control causes silent failures under load.
4. **Deterministic testing feasibility:** Confirm record/replay can be done safely (see Phase 9) without leaking secrets and without adding too much friction to the workflow.

## Review Notes (resolved or tracked)

The early review notes on this page (connectivity, platform networking, security/enterprise features, memory management, testing strategy, quality of life) are resolved or tracked by issues:

| Topic | State |
| --- | --- |
| Proxy support (HTTP/HTTPS, CONNECT) | Shipped (Phase 14, 22b); HTTP/2 through CONNECT is off by default, opt-in via `Http2Options.EnableHttp2ThroughConnectTunnels`; device validation open (#103) |
| IPv6 dual-stack | Shipped (Happy Eyeballs, Phase 14); NAT64 on device is part of #99 |
| iOS ATS, Android Network Security Config | Documented in `Documentation~/`; device checks in #99 |
| Decompression (gzip, deflate), cookies, multipart | Shipped (`TurboHTTP.Middleware`, `TurboHTTP.Files` multipart builder); Brotli not implemented, tracked in #121 |
| Memory strategy (pooling from day one) | Shipped (Phase 6, 19a); target below |
| Load, concurrency and chaos testing | Partly shipped (concurrency, adversarial and TLS interop suites); soak test #114 |
| Offline/connectivity monitor (`IConnectivityMonitor`) | Not planned for 1.0 (feature freeze); adaptive networking in `TurboHTTP.Unity` covers quality-based timeouts |
| Certificate pinning | Post-1.0: TLS pinning hooks in #117 (v1.1) |
| Background/suspend handling | Experimental and opt-in (`TURBOHTTP_BACKGROUND_NETWORKING`, #265); device validation #99 |

**Memory target:** less than 500 B GC per request on the buffered fast path (Phase 6 goal). This is a target, not a gate: the allocation gates in `Tests/Runtime/Performance/` are regression guardrails at 32 KB per warm request (`Tests/Benchmarks/phase19-allocation-baselines.json`).

## Implementation Phases

The plan is organized into numbered phases; the list below covers every phase and sub-phase plan in `phases/`. Each phase doc carries a `**Milestone:**` header with its GitHub milestone (see [Development Milestones](#development-milestones)). Phase 13 has no plan document. `Development~/docs/agents/project-status.md` holds the live status map; GitHub issues win over both.

### Foundation (Phases 1-3D)

- **[Phase 1](phases/phase-01-project-foundation.md):** Project Foundation & Structure
- **[Phase 2](phases/phase-02-core-types.md):** Core Type System
- **[Phase 3](phases/phase-03-client-api.md):** Client API, Request Builder & HTTP/1.1 Raw Socket Transport
- **[Phase 3B](phases/phase-03b-http2.md):** HTTP/2 Protocol Implementation
- **[Phase 3C](phases/phase-03c-bouncy-castle-tls.md):** BouncyCastle TLS Fallback (opt-in)
- **[Phase 3D](phases/phase-03d-json-fallback.md):** Internal JSON Parser Fallback (deprecated stub; plans in `phases/phase3d/`)

### Core Features (Phases 4-5)

- **[Phase 4](phases/phase-04-pipeline.md):** Pipeline Infrastructure
- **[Phase 5](phases/phase-05-content-handlers.md):** Content Handlers

### Hardening Gate (Phases 6-9)

- **[Phase 6](phases/phase-06-performance.md):** Performance & Hardening
- **[Phase 7](phases/phase-07-testing.md):** Testing Infrastructure
- **[Phase 8](phases/phase-08-documentation.md):** Documentation & Samples
- **[Phase 9](phases/phase-09-platform-compat.md):** Platform Compatibility (device matrix #99 open)

### Feature Expansion (Phases 10-12)

- **[Phase 10](phases/phase-10-advanced-middleware.md):** Advanced Middleware
- **[Phase 11](phases/phase-11-unity-integration.md):** Unity Integration
- **[Phase 12](phases/phase-12-editor-tools.md):** Editor Tooling

### Expansion Track (Phases 14-16)

- **[Phase 14](phases/phase-14-future.md):** Post-v1.0 Roadmap (shipped; background networking experimental and opt-in)
- **[Phase 15](phases/phase-15-unity-runtime-hardening.md):** Unity Runtime Hardening and Advanced Asset Pipeline
- **[Phase 16](phases/phase-16-advanced-capabilities.md):** Rate limiting, WebGL, security/privacy, GraphQL, parallel helpers (post-1.0)

### Release (Phase 17)

- **[Phase 17](phases/phase-17-release.md):** CI/CD & Release. The release process (versioning, Git URL and OpenUPM
  distribution, tag-triggered GitHub Release) is [`release-checklist.md`](release-checklist.md) (#101).

### Protocol and Runtime Rewrites (Phases 18-22c)

- **[Phase 18](phases/phase-18-websocket-client.md):** WebSocket Client
- **[Phase 18a](phases/phase-18a-websocket-advanced.md):** Advanced WebSocket (permessage-deflate; opt-in via `WithCompression`, Editor (Mono) verified, IL2CPP/device rows open, #99/#104)
- **[Phase 19](phases/phase-19-async-runtime-refactor.md):** Async Runtime Refactor
- **[Phase 19a](phases/phase-19a-zero-allocation.md):** Zero-Allocation Hot Path
- **Phase 19b:** Unified pipeline (plan in `phases/phase19b/`)
- **[Phase 19c](phases/phase-19c-body-zerocopy-transport.md):** Body Zero-Copy Transport (post-1.0, v1.2)
- **Phase 22:** Interceptor rewrite (plans in `phases/phase22/`)
- **[Phase 22a](phases/phase-22a-end-to-end-streaming.md):** End-to-End Streaming
- **[Phase 22b](phases/phase-22b-streaming-extensions.md):** Streaming Extensions (Expect: 100-continue, proxy streaming, trailers)
- **[Phase 22c](phases/phase-22c-proxy-http2-alpn.md):** HTTP/2 through CONNECT (off by default, opt-in via `Http2Options.EnableHttp2ThroughConnectTunnels`; device validation open, #103)
- **Phase 22d:** HTTP/2 proxy leg and proxy-aware pooling (post-1.0, v2.0; plan docs pending, #160, plan fix #125)

### Post-1.0 Plans (feature freeze until GA)

- **[Phase 23](phases/phase-23-api-config-extensions.md):** API Configuration & Extension Review (v1.2)
- **[Phase 24](phases/phase-24-advanced-content-handlers.md):** Advanced Content Handlers (decompression shipped; the rest v1.2)
- **[Phase 25](phases/phase-25-grpc-client.md):** gRPC Client (v2.0)

## Development Milestones

The roadmap uses the GitHub milestones; the old `M0`-`M5` labels are retired. A "v1.0" phase header means the code ships in the repository today and its remaining hardening or validation sits in the three v1.0 milestones below. Issue state on GitHub is the authority; one tracking issue per milestone (#134-#139) lists its content.

| # | GitHub milestone | Content |
| --- | --- | --- |
| 1 | `v1.0-alpha: Release blockers` | Packaging, license, defects that block a first preview |
| 2 | `v1.0-beta: Protocol & correctness hardening` | RFC conformance, security, ownership and concurrency fixes for the shipped transport and modules |
| 3 | `v1.0 GA: Docs, validation & release` | Docs and samples (#98), device matrix (#99), IL2CPP/CI (#100), release process: tags, GitHub Release, OpenUPM (#101), validation gates #102 (22a.6), #103 (22c), #104 (18a), #105 (Phase 15) |
| 4 | `v1.1: Post-release hardening` | 16.3 security and privacy hardening (#117) |
| 5 | `v1.2: Features & performance` | 16.1 rate limiting (#118), 16.5 parallel helpers (#119), 19c (#120), 23 (#121), 24.2-24.4 (#122-#124) |
| 6 | `v2.0: Advanced protocols` | 16.2 WebGL (#130), 16.4 GraphQL (#131), 25 gRPC (#132), 22d (#125-#129) |

### Re-baseline (2026-11-02, #10)

- **Feature freeze until v1.0 GA:** bug fixes, validation and docs only. Phases 16, 19c, 22d, 23, 24 (except decompression, already shipped) and 25 stay post-1.0 (v1.1/v1.2/v2.0).
- **v1.0 ships everything in the repository today, with these gates:**
  - HTTP/2 through CONNECT (Phase 22c) is off by default, opt-in via `Http2Options.EnableHttp2ThroughConnectTunnels` (`Runtime/Core/Http2Options.cs`); device validation open (#103).
  - Advanced WebSocket (Phase 18a, permessage-deflate) ships as opt-in `WithCompression`; Editor (Mono) verified, IL2CPP/device rows open (#99/#104).
  - Background networking (Phase 14.3) is experimental and opt-in (`TURBOHTTP_BACKGROUND_NETWORKING`, #265).
  - BouncyCastle is opt-in (`TURBOHTTP_BOUNCYCASTLE`, #259).
- **Release train:** `1.0.0-pre.N` tags from `main` (Git URL / OpenUPM preview) while the device matrix waits for hardware. `1.0.0` GA comes after #99 and a green full host-editor run.
- **Unity tests run only in the local host editor.** There is no CI Unity license (#188, #100); PR CI runs repo invariants, format, the shadow build with analyzers, CodeQL, the TLS interop job and the docs/samples compile checks (`Development~/docs/agents/testing.md`).

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
├── Development~/docs/                          # Implementation documentation (this folder)
│   ├── 00-overview.md            # This file
│   └── phases/                    # Detailed phase documentation
│       ├── phase-01-project-foundation.md
│       ├── phase-02-core-types.md
│       ├── ... (through phase 25)
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
│   │   ├── Http2/                 # HTTP/2 framing, HPACK, stream multiplexing, flow control
│   │   └── BouncyCastle/          # Opt-in TLS fallback (Lib/ is vendored)
│   ├── Middleware/                # Redirect, cookie, decompression, default-header interceptors
│   ├── Retry/                     # Retry module
│   ├── Cache/                     # Cache module
│   ├── Auth/                      # Auth module
│   ├── RateLimit/                 # Rate limit module
│   ├── Observability/             # Observability module
│   ├── Files/                     # Files module
│   ├── JSON/                      # JSON abstraction + LiteJson
│   ├── Unity/                     # Unity handlers and mobile networking
│   ├── Testing/                   # Mock and record/replay transports
│   ├── WebSocket/                 # WebSocket client
│   ├── WebSocket.Transport/       # WebSocket transport
│   ├── Unity.WebSocket/           # Unity WebSocket bridge
│   ├── UniTask/                   # UniTask adapters (HTTP)
│   ├── UniTask.WebSocket/         # UniTask adapters (WebSocket)
│   ├── Bootstrap/                 # Startup registration (TurboHTTP.Complete)
│   └── TurboHTTP.Complete.asmdef  # Aggregation assembly
├── Editor/                        # Editor module
├── Documentation~/                # Customer docs
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

v1.0 GA is ready when:

- ✓ Core + Transport + the optional runtime modules listed above implemented and tested
- ✓ Raw socket transport with HTTP/1.1 and HTTP/2 support
- ✓ TLS/SslStream with ALPN negotiation working on all target platforms (device matrix #99)
- ✓ Connection pooling with keep-alive for HTTP/1.1 and multiplexing for HTTP/2
- ✓ Editor module (HTTP Monitor) functional in supported Unity versions
- ✓ Full EditMode and PlayMode suites green in a host editor (Editor, Standalone); iOS and Android device runs per #99
- ✓ IL2CPP builds work on all platforms (SslStream validated, #99)
- ✓ Allocation regression gates green (`Tests/Runtime/Performance/`, 32 KB per warm request guardrail); < 500 B GC per request on the buffered fast path remains the target
- ✓ Documentation complete (README quick start, `Documentation~/`, platform notes)
- ✓ 5 working samples (`Samples~/01-BasicUsage` to `05-AdvancedFeatures`)
- ✓ Record/replay mode working
- ✓ No open release blockers (milestones v1.0-alpha, v1.0-beta and v1.0 GA)
- ✓ Gated features (22c, 18a, background networking, BouncyCastle) default-off or labelled experimental as described above (22c default-off is #103)
- ✓ Release package ready (`1.0.0-pre.N` train, then the `v1.0.0` tag, GitHub Release and OpenUPM listing per the [release checklist](release-checklist.md), #101)

Code coverage is not a gate; the deterministic suites and the allocation gates are.

## Next Steps

1. Review each phase document in the `phases/` directory; its `**Milestone:**` header says where it lands.
2. Work the open issues by milestone: v1.0-alpha, v1.0-beta, v1.0 GA (feature freeze: bug fixes, validation and docs only).
3. Tag `1.0.0-pre.N` from `main` while the device matrix (#99) waits for hardware.
4. Release `1.0.0` after #99 and a green full host-editor run: tag `v1.0.0`, publish the GitHub Release and update the OpenUPM listing ([release checklist](release-checklist.md), #101). The Unity Asset Store is a post-1.0 decision.
5. Post-1.0: v1.1, v1.2, v2.0 as listed in [Development Milestones](#development-milestones).

---

**Read the detailed phase documents to understand the specific implementation tasks, file structures, code examples, and validation criteria for each phase.**
