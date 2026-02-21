# Phase 18 Implementation Plan - Overview

Phase 18 is split into 7 sub-phases. Framing and handshake ship first, then connection lifecycle and API surface build on top. Unity integration starts after the core API exists, and reconnect-specific Unity hooks finalize after reconnection is implemented. The test suite is the final gate.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [18.1](phase-18.1-websocket-framing.md) | WebSocket Framing & Protocol Layer | 6 new | None |
| [18.2](phase-18.2-http-upgrade-handshake.md) | HTTP Upgrade Handshake | 2 new | 18.1 |
| [18.3](phase-18.3-websocket-connection-lifecycle.md) | WebSocket Connection & Lifecycle | 4 new, 1 modified | 18.1, 18.2 |
| [18.4](phase-18.4-send-receive-api.md) | Send/Receive API Surface | 3 new | 18.3 |
| [18.5](phase-18.5-reconnection-resilience.md) | Reconnection & Resilience | 2 new, 1 modified | 18.3, 18.4 |
| [18.6](phase-18.6-unity-integration.md) | Unity Integration | 4 new | 18.4, 18.5, Phase 15 |
| [18.7](phase-18.7-test-suite.md) | Test Suite | 7 new, 2 modified | All above |

## Dependency Graph

```text
Phase 3/3B/3C (done — raw socket transport, HTTP/2, TLS)
Phase 15 (done — MainThreadDispatcher V2)
    │
    ├── 18.1 WebSocket Framing & Protocol Layer
    │    └── 18.2 HTTP Upgrade Handshake
    │         └── 18.3 WebSocket Connection & Lifecycle
    │              └── 18.4 Send/Receive API Surface
    │                   └── 18.5 Reconnection & Resilience
    │                        └── 18.6 Unity Integration
    │
    18.1-18.6
        └── 18.7 Test Suite
```

18.6 core work (bridge, component, extension methods) can start once 18.4 is merged, but reconnect event bridging and final verification depend on 18.5.

## Existing Foundation

### Existing Types Used in Phase 18

| Type | Key APIs for Phase 18 |
|------|----------------------|
| `TcpConnectionPool` / `TlsStreamWrapper` | Socket creation patterns and TLS handshake for wss:// (pool lifecycle NOT reused — WebSocket connections are long-lived, not request/response) |
| `ITlsProvider` / `TlsProviderSelector` | TLS provider abstraction for wss:// connections |
| `Http11RequestSerializer` | Reusable patterns for HTTP/1.1 wire formatting (upgrade request) |
| `Http11ResponseParser` | Reusable patterns for HTTP/1.1 response parsing (101 Switching Protocols) |
| `MainThreadDispatcher` | Main-thread callback delivery for Unity WebSocket events |
| `CoroutineWrapper` | Coroutine-based wrappers for async WebSocket receive loops |
| `LifecycleCancellation` | Owner-bound cancellation for MonoBehaviour lifecycle binding |
| `UHttpError` / `UHttpException` | Error taxonomy patterns to extend for WebSocket errors |
| `ObjectPool<T>` / `ByteArrayPool` | Buffer pooling for frame read/write operations |
| `ConnectionLease` | IDisposable resource lease pattern (model for buffer lease) |

### Assembly Structure

| Assembly | References | autoReferenced | Notes |
|----------|-----------|----------------|-------|
| `TurboHTTP.WebSocket` (new) | Core | false | WebSocket protocol, framing, client API, `IWebSocketTransport` interface. **No Transport reference** — keeps WebGL-compatible. |
| `TurboHTTP.WebSocket.Transport` (new) | Core, WebSocket, Transport | false | `RawSocketWebSocketTransport` — socket creation, TLS wrapping. `excludePlatforms: ["WebGL"]` (same as Transport). |
| `TurboHTTP.Unity.WebSocket` (new) | Core, WebSocket, Unity | false | Unity integration: bridge, MonoBehaviour component. **Separate assembly** to avoid forcing `TurboHTTP.Unity` → `TurboHTTP.WebSocket` dependency. |
| `TurboHTTP.Tests.Runtime` | runtime modules | false | WebSocket unit and integration tests |

**Rationale for assembly split:**
- `TurboHTTP.WebSocket` contains protocol-only code (framing, handshake, state machine, client API, `IWebSocketTransport`). No reference to Transport assembly, so a future `WebGLWebSocketTransport` can live in a WebGL-compatible assembly that references only `TurboHTTP.WebSocket`.
- `TurboHTTP.WebSocket.Transport` contains `RawSocketWebSocketTransport` which depends on raw sockets and TLS from `TurboHTTP.Transport`. Excluded from WebGL (same as Transport).
- `TurboHTTP.Unity.WebSocket` contains Unity-specific integration (`UnityWebSocketBridge`, `UnityWebSocketClient`). References `TurboHTTP.Unity` (for `MainThreadDispatcher`, `LifecycleCancellation`) and `TurboHTTP.WebSocket` (for `IWebSocketClient`). This keeps `TurboHTTP.Unity` independently includable without WebSocket — no module isolation violation.

### Assembly Definition Deliverables (Required)

These are explicit implementation tasks (not implicit architecture notes):

1. `Runtime/WebSocket/TurboHTTP.WebSocket.asmdef` (new, Phase 18.1)
2. `Runtime/WebSocket.Transport/TurboHTTP.WebSocket.Transport.asmdef` (new, Phase 18.3)
3. `Runtime/Unity.WebSocket/TurboHTTP.Unity.WebSocket.asmdef` (new, Phase 18.6)
4. `Tests/Runtime/TurboHTTP.Tests.Runtime.asmdef` (modified, Phase 18.7)
5. `Runtime/TurboHTTP.Complete.asmdef` (modified, Phase 18.7)

## Architecture

```
IWebSocketClient (public API — in TurboHTTP.WebSocket)
├── WebSocketClient                      ← Default implementation
│   ├── WebSocketHandshake               ← HTTP/1.1 → WebSocket upgrade
│   ├── WebSocketConnection              ← State machine + frame I/O
│   │   ├── WebSocketFrameReader         ← Read frames from stream (stateless)
│   │   ├── WebSocketFrameWriter         ← Write masked frames to stream
│   │   └── MessageAssembler             ← Fragmentation reassembly (stateful, bounded)
│   └── WebSocketReconnectPolicy         ← Auto-reconnect with backoff
│
IWebSocketTransport (transport abstraction — in TurboHTTP.WebSocket)
├── RawSocketWebSocketTransport          ← Desktop/Mobile (ws:// and wss://) — in TurboHTTP.WebSocket.Transport
└── WebGLWebSocketTransport              ← Browser WebSocket API via .jslib (Phase 16 — future assembly)
```

## Cross-Cutting Design Decisions

1. WebSocket framing reuses `ByteArrayPool` / `ArrayPool<byte>.Shared` for payload buffers — no per-frame heap allocations for data.
2. Client masking keys are **batch-generated**: fill a 256-byte buffer (64 mask keys) from `RandomNumberGenerator`, consume sequentially, refill when exhausted. Avoids per-frame crypto syscall overhead at 60fps while maintaining RFC 6455 Section 10.3 compliance.
3. All stream I/O uses `CancellationToken` propagation for timeout and shutdown scenarios. For cancellation of blocked `Stream.ReadAsync` calls where the token is not respected, use `ct.Register(() => stream.Dispose())` pattern (same as existing HTTP/1.1 timeout enforcement).
4. WSS (secure WebSocket) reuses existing TLS infrastructure from Phase 3C. **WSS connections must pass empty/null ALPN protocols** to `ITlsProvider.WrapAsync` to prevent accidental HTTP/2 negotiation on the TLS layer (WebSocket uses HTTP/1.1 Upgrade, not ALPN).
5. Frame reader/writer operate on raw `Stream` (works with both plain TCP and `SslStream`).
6. Connection state machine transitions use `TryTransitionState(expected, next)` helper with `Interlocked.CompareExchange` on an `int` field. Allowed transitions are validated against a static transition map.
7. Close handshake follows RFC 6455 Section 7 — clean close frame exchange with configurable timeout. Close codes 1005 (`NoStatusReceived`) and 1006 (`AbnormalClosure`) must never be sent on the wire (RFC 6455 Section 7.4.1).
8. Core WebSocket protocol/client work is in Phase 18. WebGL-specific WebSocket transport integration is out of scope for Phase 18 and is handled in the Phase 16 WebGL workstream. The assembly split enables this without restructuring.
9. Error types extend existing `UHttpError` taxonomy with WebSocket-specific error codes.
10. Unity integration lives in `TurboHTTP.Unity.WebSocket` — bridges to Phase 15 `MainThreadDispatcher`. No separate dispatch mechanism.
11. Receive queue uses `Channel<WebSocketMessage>` with bounded capacity. Control frames (ping/pong/close) are handled inline by the receive loop and never enter the data queue — backpressure on data messages does not block control frame handling. Slow consumer → receive loop blocks on `channel.Writer.WriteAsync` → TCP backpressure propagates to server.
12. `WebSocketMessage` is a **class** (not struct) with eager UTF-8 string decode at construction. Lazy decode on a struct is fundamentally broken (struct copies don't propagate cached values). Text message `Text` property is decoded once via `Encoding.UTF8.GetString()`. Binary message `Data` uses `IDisposable` buffer lease pattern (similar to `ConnectionLease`).
13. `IWebSocketClient` extends both `IDisposable` and `IAsyncDisposable`. `Dispose()` calls `Abort()` (synchronous, unclean). `DisposeAsync()` attempts `CloseAsync` with timeout then falls back to `Abort()`. Users wanting clean close must explicitly `await CloseAsync()` before dispose.
14. Send serialization uses `SemaphoreSlim(1,1)` with try/finally to guarantee release (matching `ConnectionLease` pattern). `SendAsync` blocks if another send is in progress.
15. `WebSocketFrameReader` is stateless (parses individual frames). `MessageAssembler` is a separate class owned by `WebSocketConnection` that accumulates fragments with bounded size and fragment count.
16. Inbound text frame payloads must be validated as valid UTF-8 (RFC 6455 Section 5.6 / 8.1 MUST requirement). Invalid UTF-8 → fail connection with close code 1007 (`InvalidPayload`).
17. Certificate validation for WSS connections uses the same `ITlsProvider` certificate validation callbacks as HTTP connections. Certificate pinning is configurable via `WebSocketConnectionOptions.TlsProvider`.
18. `link.xml` entries must be added for `System.Security.Cryptography.SHA1` and `RandomNumberGenerator` to prevent IL2CPP code stripping on mobile platforms.
19. Sub-protocol dispatch is out of scope for Phase 18. Negotiated sub-protocol is informational — message handling is the application's responsibility. `permessage-deflate` (RFC 7692) is deferred to a future phase.
20. HTTP/2 WebSocket (RFC 8441) using CONNECT method with `:protocol` pseudo-header is a future consideration — not implemented in Phase 18 despite existing HTTP/2 support.

## Memory Limits (Defaults)

| Setting | Default | Notes |
|---------|---------|-------|
| `MaxFrameSize` | 16 MB | Max bytes in a single frame |
| `MaxMessageSize` | 4 MB | Max bytes in assembled message after defragmentation (enforced during reassembly, before allocation) |
| `MaxFragmentCount` | 64 | Max fragments per message (bounds metadata overhead) |
| `FragmentationThreshold` | 64 KB | When to fragment outgoing messages |
| `ReceiveQueueCapacity` | 100 | Bounded `Channel<T>` capacity (at 64KB per message = ~6.4MB max retention) |

**Invariant:** `FragmentationThreshold <= MaxFrameSize`, `MaxMessageSize <= MaxFrameSize * MaxFragmentCount`.

## All Files (28 new, 4 modified planned)

| Area | Planned New Files | Planned Modified Files |
|---|---|---|
| 18.1 Framing | 6 | 0 |
| 18.2 Handshake | 2 | 0 |
| 18.3 Connection | 4 | 1 |
| 18.4 API Surface | 3 | 0 |
| 18.5 Reconnection | 2 | 1 |
| 18.6 Unity Integration | 4 | 0 |
| 18.7 Test Suite | 7 | 2 |

## Verification Plan

1. All unit tests pass for framing (encode/decode, masking, fragmentation, UTF-8 validation).
2. Handshake tests validate success, rejection, invalid key, token-based header parsing.
3. Integration test: connect → send → receive → close against an in-process echo server.
4. Reconnection test: kill server mid-session, verify auto-reconnect with backoff.
5. Unity test: verify message callbacks arrive on main thread.
6. Thread-safety tests for concurrent send/receive under sustained load.
7. IL2CPP build validation on iOS and Android (SHA-1, RNG, TLS, threading, domain reload).

## Post-Implementation

1. Run WebSocket protocol conformance tests (framing edge cases, control frame handling).
2. Verify WSS handshake works across all supported TLS providers (SslStream, BouncyCastle fallback) with empty ALPN.
3. Confirm reconnection resilience under network partition simulation.
4. Validate Unity lifecycle binding (auto-disconnect on `OnDestroy`, domain reload).
5. Verify `link.xml` preserves SHA-1 and RNG on iOS/Android IL2CPP builds.
6. Gate Phase 18 completion on green CI plus documented platform compatibility matrix.
