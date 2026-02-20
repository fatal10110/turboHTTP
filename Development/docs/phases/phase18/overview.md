# Phase 18 Implementation Plan - Overview

Phase 18 is split into 7 sub-phases. Framing and handshake ship first, then connection lifecycle and API surface build on top. Reconnection and Unity integration are parallel tracks once the core API exists. The test suite is the final gate.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [18.1](phase-18.1-websocket-framing.md) | WebSocket Framing & Protocol Layer | 4 new | None |
| [18.2](phase-18.2-http-upgrade-handshake.md) | HTTP Upgrade Handshake | 2 new | 18.1 |
| [18.3](phase-18.3-websocket-connection-lifecycle.md) | WebSocket Connection & Lifecycle | 2 new, 1 modified | 18.1, 18.2 |
| [18.4](phase-18.4-send-receive-api.md) | Send/Receive API Surface | 3 new | 18.3 |
| [18.5](phase-18.5-reconnection-resilience.md) | Reconnection & Resilience | 2 new, 1 modified | 18.3, 18.4 |
| [18.6](phase-18.6-unity-integration.md) | Unity Integration | 2 new, 1 modified | 18.4, Phase 15 |
| [18.7](phase-18.7-test-suite.md) | Test Suite | 6 new | All above |

## Dependency Graph

```text
Phase 3/3B/3C (done — raw socket transport, HTTP/2, TLS)
Phase 15 (done — MainThreadDispatcher V2)
    │
    ├── 18.1 WebSocket Framing & Protocol Layer
    │    └── 18.2 HTTP Upgrade Handshake
    │         └── 18.3 WebSocket Connection & Lifecycle
    │              ├── 18.4 Send/Receive API Surface
    │              │    ├── 18.5 Reconnection & Resilience
    │              │    └── 18.6 Unity Integration
    │              └── 18.5 Reconnection & Resilience
    │
    18.1-18.6
        └── 18.7 Test Suite
```

Sub-phases 18.5 and 18.6 can run in parallel once 18.4 API surface is merged.

## Existing Foundation

### Existing Types Used in Phase 18

| Type | Key APIs for Phase 18 |
|------|----------------------|
| `TcpConnectionPool` / `TlsStreamWrapper` | TCP connection establishment and TLS handshake for wss:// |
| `Http11RequestSerializer` | Reusable patterns for HTTP/1.1 wire formatting (upgrade request) |
| `Http11ResponseParser` | Reusable patterns for HTTP/1.1 response parsing (101 Switching Protocols) |
| `MainThreadDispatcher` | Main-thread callback delivery for Unity WebSocket events |
| `CoroutineWrapper` | Coroutine-based wrappers for async WebSocket receive loops |
| `UHttpError` / `UHttpException` | Error taxonomy patterns to extend for WebSocket errors |
| `ObjectPool<T>` / `ByteArrayPool` | Buffer pooling for frame read/write operations |
| `ConcurrencyLimiter` | Connection limit patterns for WebSocket connection management |

### Assembly Structure

| Assembly | References | autoReferenced | Notes |
|----------|-----------|----------------|-------|
| `TurboHTTP.WebSocket` (new) | Core, Transport | false | WebSocket protocol, framing, client API |
| `TurboHTTP.Unity` | Core, WebSocket | false | Unity integration for WebSocket events |
| `TurboHTTP.Tests.Runtime` | runtime modules | false | WebSocket unit and integration tests |

## Architecture

```
IWebSocketClient (public API)
├── WebSocketClient                      ← Default implementation
│   ├── WebSocketHandshake               ← HTTP/1.1 → WebSocket upgrade
│   ├── WebSocketConnection              ← State machine + frame I/O
│   │   ├── WebSocketFrameReader         ← Read frames from stream
│   │   └── WebSocketFrameWriter         ← Write masked frames to stream
│   └── WebSocketReconnectPolicy         ← Auto-reconnect with backoff
│
IWebSocketTransport (transport abstraction)
├── RawSocketWebSocketTransport          ← Desktop/Mobile (ws:// and wss://)
└── WebGLWebSocketTransport              ← Browser WebSocket API via .jslib (Phase 16)
```

## Cross-Cutting Design Decisions

1. WebSocket framing reuses `ByteArrayPool` / `ArrayPool<byte>.Shared` for payload buffers — no per-frame heap allocations for data.
2. Client masking key generation uses `System.Security.Cryptography.RandomNumberGenerator` (RFC 6455 Section 5.3 requirement).
3. All stream I/O uses `CancellationToken` propagation for timeout and shutdown scenarios.
4. WSS (secure WebSocket) reuses existing TLS infrastructure from Phase 3C — no separate TLS implementation.
5. Frame reader/writer operate on raw `Stream` (works with both plain TCP and `SslStream`).
6. Connection state machine transitions are thread-safe (concurrent send/receive is a core requirement).
7. Close handshake follows RFC 6455 Section 7 — clean close frame exchange with configurable timeout.
8. WebGL WebSocket transport is explicitly out of scope — deferred to Phase 16 (WebGL Support).
9. Error types extend existing `UHttpError` taxonomy with WebSocket-specific error codes.
10. Unity integration bridges to Phase 15 `MainThreadDispatcher` — no separate dispatch mechanism.

## All Files (21 new, 3 modified planned)

| Area | Planned New Files | Planned Modified Files |
|---|---|---|
| 18.1 Framing | 4 | 0 |
| 18.2 Handshake | 2 | 0 |
| 18.3 Connection | 2 | 1 |
| 18.4 API Surface | 3 | 0 |
| 18.5 Reconnection | 2 | 1 |
| 18.6 Unity Integration | 2 | 1 |
| 18.7 Test Suite | 6 | 0 |

## Verification Plan

1. All unit tests pass for framing (encode/decode, masking, fragmentation).
2. Handshake tests validate success, rejection, and invalid key scenarios.
3. Integration test: connect → send → receive → close against a local echo server.
4. Reconnection test: kill server mid-session, verify auto-reconnect with backoff.
5. Unity test: verify message callbacks arrive on main thread.
6. Thread-safety tests for concurrent send/receive under sustained load.
7. IL2CPP build validation on iOS and Android.

## Post-Implementation

1. Run WebSocket protocol conformance tests (framing edge cases, control frame handling).
2. Verify WSS handshake works across all supported TLS providers (SslStream, BouncyCastle fallback).
3. Confirm reconnection resilience under network partition simulation.
4. Validate Unity lifecycle binding (auto-disconnect on `OnDestroy`, domain reload).
5. Gate Phase 18 completion on green CI plus documented platform compatibility matrix.
