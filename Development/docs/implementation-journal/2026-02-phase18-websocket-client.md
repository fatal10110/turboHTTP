# Phase 18: WebSocket Client — 2026-02-21

## Progress Snapshot

Phase 18 implementation is complete for sub-phases 18.1 through 18.7.

Completed in this session:
- Phase 18.1 (WebSocket framing and protocol layer)
- Phase 18.2 (HTTP upgrade handshake)
- Phase 18.3 (connection lifecycle and raw socket transport)
- Phase 18.4 (public send/receive API surface)
- Phase 18.5 (reconnection/resilience)
- Phase 18.6 (Unity integration)
- Phase 18.7 (test suite completion)

Outstanding gate (environment-dependent):
- Unity Test Runner execution and IL2CPP iOS/Android validation were not executed in this CLI environment.

## Implemented

### Phase 18.1 - Framing/Protocol

- Added `TurboHTTP.WebSocket` assembly definition with `TurboHTTP.Core` reference only.
- Added frame primitives and RFC enums:
  - `WebSocketOpcode`
  - `WebSocketFrame`
  - `WebSocketCloseCode`
  - `WebSocketCloseStatus`
- Added protocol constants/utilities:
  - GUID/version/default limits/timeouts
  - `ComputeAcceptKey(...)`
  - `GenerateClientKey()`
  - close code validation and opcode validation helpers
- Added frame reader:
  - RFC header parsing (including 16/64-bit payload lengths)
  - reserved opcode/RSV checks
  - masked server-frame rejection
  - fragmentation rule validation
  - cancellation-safe exact-read strategy
- Added frame writer:
  - mandatory client masking
  - batched RNG mask-key generation
  - chunked masking write path (avoids full-payload clone)
  - text/binary/ping/pong/close writes
  - message fragmentation support
- Added message assembler:
  - continuation reassembly
  - max message size / max fragment count enforcement
  - pooled-buffer ownership and reset cleanup
- Added linker preserve file for SHA1/RNG (`Runtime/WebSocket/link.xml`).

### Phase 18.2 - HTTP Upgrade Handshake

- Added `WebSocketHandshake` request builder/writer:
  - `GET` upgrade request with required RFC headers
  - host/port formatting rules for `ws`/`wss`
  - request-target as path+query (fragment stripped)
  - optional subprotocols/extensions/custom headers
  - CRLF injection protection for custom header values
- Added `WebSocketHandshakeValidator`:
  - bounded incremental response-head read (8KB default)
  - status-line/header parsing
  - token-based validation for `Upgrade` and `Connection`
  - accept-key validation (`Sec-WebSocket-Accept`)
  - subprotocol selection validation
  - negotiated extensions capture
  - bounded non-101 body capture for diagnostics (4KB default)
  - prefetched post-header bytes handoff support

### Phase 18.3 - Connection/Lifecycle/Transport

- Added `WebSocketConnectionOptions` with defaults and invariant validation.
- Added `IWebSocketTransport` contract in protocol assembly.
- Added `WebSocketConnection` with:
  - atomic state machine and transition map
  - connect flow (transport + handshake)
  - background receive loop (data/control separation)
  - UTF-8 validation for inbound text payloads
  - send serialization via `SemaphoreSlim(1,1)`
  - bounded receive queue with async backpressure
  - close handshake + close-frame-once guard
  - abort/dispose/async-dispose paths
  - keepalive ping loop with pong timeout and idle timeout checks
- Added helper domain types:
  - `WebSocketState`
  - `WebSocketMessage`
  - `WebSocketException`
  - `WebSocketReconnectPolicy`
- Added `TurboHTTP.WebSocket.Transport` assembly definition.
- Added `RawSocketWebSocketTransport` in transport assembly:
  - DNS resolution timeout
  - Happy Eyeballs socket connect
  - `ws://` and `wss://` stream creation
  - TLS provider resolution using existing transport TLS stack
  - empty ALPN for WSS handshake (HTTP/1.1 Upgrade path)

### Phase 18.4 - Send/Receive API Surface

- Added public API contract `IWebSocketClient`.
- Added default implementation `WebSocketClient` over `WebSocketConnection`.
- Added queue primitive `AsyncBoundedQueue` used by the receive path.
- Added `WebSocketConnection.SendTextAsync(ReadOnlyMemory<byte>, ...)` overload.

### Phase 18.5 - Reconnection/Resilience

- Implemented `ResilientWebSocketClient` auto-reconnect wrapper with backoff policy support.
- Replaced reconnect policy placeholder with immutable `WebSocketReconnectPolicy`:
  - `MaxRetries`, initial/max delay, multiplier, jitter
  - close code predicate
  - `ShouldReconnect(...)` and `ComputeDelay(...)`
  - static `None`, `Default`, and `Infinite` presets
- Added `WebSocketConnectionOptions.WithReconnection(...)`.

Validation-driven fixes:
- `ResilientWebSocketClient` now clones options and forces inner `WebSocketClient` connections to use `WebSocketReconnectPolicy.None` so reconnect-enabled user options are accepted at the wrapper layer.
- `WebSocketClient.ReceiveAsync(...)` now maps closed-state reads to `WebSocketException(ConnectionClosed)` for API consistency.

### Phase 18.6 - Unity Integration

- Added `TurboHTTP.Unity.WebSocket` assembly.
- Added `UnityWebSocketBridge` for lifecycle and event dispatch integration.
- Added `UnityWebSocketClient` wrapper and component-facing integration.
- Added `UnityWebSocketExtensions` convenience APIs.

### Phase 18.7 - Test Suite

- Added/expanded protocol, handshake, connection, API, reconnection, integration, and Unity tests:
  - `WebSocketFramingTests.cs`
  - `WebSocketHandshakeTests.cs`
  - `WebSocketConnectionTests.cs`
  - `WebSocketClientTests.cs`
  - `WebSocketReconnectionTests.cs`
  - `WebSocketIntegrationTests.cs`
  - `WebSocketApiSurfaceTests.cs`
  - `WebSocketReconnectPolicyTests.cs`
  - `WebSocketConnectionPhase18FixesTests.cs`
  - `UnityWebSocketTests.cs`
- Expanded `WebSocketTestServer` for test scenarios:
  - handshake rejection configuration
  - delayed echo
  - close/abort after N messages
  - forced disconnect after handshake
  - malformed frame modes (masked server frame, reserved opcode, invalid UTF-8)
  - in-process ws transport helper (`TestTcpWebSocketTransport`)
- Updated assembly references required by test and complete bundles:
  - `Tests/Runtime/TurboHTTP.Tests.Runtime.asmdef`
  - `Runtime/TurboHTTP.Complete.asmdef`

## Files Added

### Runtime/WebSocket
- `Runtime/WebSocket/TurboHTTP.WebSocket.asmdef`
- `Runtime/WebSocket/WebSocketConstants.cs`
- `Runtime/WebSocket/WebSocketFrame.cs`
- `Runtime/WebSocket/WebSocketFrameReader.cs`
- `Runtime/WebSocket/WebSocketFrameWriter.cs`
- `Runtime/WebSocket/MessageAssembler.cs`
- `Runtime/WebSocket/WebSocketHandshake.cs`
- `Runtime/WebSocket/WebSocketHandshakeValidator.cs`
- `Runtime/WebSocket/WebSocketConnectionOptions.cs`
- `Runtime/WebSocket/IWebSocketTransport.cs`
- `Runtime/WebSocket/WebSocketConnection.cs`
- `Runtime/WebSocket/WebSocketState.cs`
- `Runtime/WebSocket/WebSocketMessage.cs`
- `Runtime/WebSocket/WebSocketException.cs`
- `Runtime/WebSocket/WebSocketReconnectPolicy.cs`
- `Runtime/WebSocket/IWebSocketClient.cs`
- `Runtime/WebSocket/WebSocketClient.cs`
- `Runtime/WebSocket/ResilientWebSocketClient.cs`
- `Runtime/WebSocket/AsyncBoundedQueue.cs`
- `Runtime/WebSocket/link.xml`

### Runtime/WebSocket.Transport
- `Runtime/WebSocket.Transport/TurboHTTP.WebSocket.Transport.asmdef`
- `Runtime/WebSocket.Transport/RawSocketWebSocketTransport.cs`

### Runtime/Unity.WebSocket
- `Runtime/Unity.WebSocket/TurboHTTP.Unity.WebSocket.asmdef`
- `Runtime/Unity.WebSocket/UnityWebSocketBridge.cs`
- `Runtime/Unity.WebSocket/UnityWebSocketClient.cs`
- `Runtime/Unity.WebSocket/UnityWebSocketExtensions.cs`

### Tests/Runtime/WebSocket and Unity
- `Tests/Runtime/WebSocket/WebSocketFramingTests.cs`
- `Tests/Runtime/WebSocket/WebSocketHandshakeTests.cs`
- `Tests/Runtime/WebSocket/WebSocketConnectionTests.cs`
- `Tests/Runtime/WebSocket/WebSocketClientTests.cs`
- `Tests/Runtime/WebSocket/WebSocketReconnectionTests.cs`
- `Tests/Runtime/WebSocket/WebSocketIntegrationTests.cs`
- `Tests/Runtime/WebSocket/WebSocketApiSurfaceTests.cs`
- `Tests/Runtime/WebSocket/WebSocketReconnectPolicyTests.cs`
- `Tests/Runtime/WebSocket/WebSocketConnectionPhase18FixesTests.cs`
- `Tests/Runtime/WebSocket/WebSocketTestServer.cs`
- `Tests/Runtime/Unity/UnityWebSocketTests.cs`

## Files Modified

- `Runtime/WebSocket/WebSocketMessage.cs`
- `Runtime/WebSocket/WebSocketException.cs`
- `Runtime/WebSocket/WebSocketReconnectPolicy.cs`
- `Runtime/WebSocket/WebSocketConnectionOptions.cs`
- `Runtime/WebSocket/WebSocketConnection.cs`
- `Runtime/WebSocket/WebSocketClient.cs`
- `Runtime/TurboHTTP.Complete.asmdef`
- `Tests/Runtime/TurboHTTP.Tests.Runtime.asmdef`

## Validation Performed

- Isolated compile checks for the WebSocket runtime source sets succeeded with 0 errors.
- Isolated WebSocket-focused runtime test run passed:
  - `40` passed, `0` failed.

## Completion Status

- Planned Phase 18 runtime and test file set is present (missing count: `0` against spec paths).
- Implementation is complete for Phase 18.1-18.7 in repository scope.
- Remaining completion gate is external platform validation (Unity Test Runner and IL2CPP device/build verification).
