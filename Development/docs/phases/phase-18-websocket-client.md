# Phase 18: WebSocket Client

**Milestone:** M4 (v1.2)
**Dependencies:** Phase 3/3B (raw socket transport, HTTP/2), Phase 3C (TLS), Phase 15 (main-thread dispatcher)
**Estimated Complexity:** High
**Estimated Effort:** 3-4 weeks
**Critical:** No - v1.2 feature

## Overview

Add a full-featured WebSocket client alongside the existing HTTP client. WebSocket is a distinct protocol (RFC 6455) that requires its own transport, framing, lifecycle management, and API surface. This is comparable in scope to Phase 3B (HTTP/2) — a new protocol layered on top of the existing socket/TLS infrastructure.

## Use Cases

- Real-time multiplayer state synchronization
- Live chat and messaging
- Server-push notifications
- Streaming market data, telemetry, or game events

## Architecture

```
IWebSocketTransport (new abstraction)
├── RawSocketWebSocketTransport          ← Desktop/Mobile (ws:// and wss://)
└── WebGLWebSocketTransport              ← WebGL: browser WebSocket API via .jslib (Phase 16 WebGL)
```

## Tasks

### Task 18.1: WebSocket Framing & Protocol Layer

**Goal:** Implement RFC 6455 framing (opcodes, masking, fragmentation, control frames).

**Deliverables:**
- `WebSocketFrame` struct (opcode, mask, payload, fin bit)
- `WebSocketFrameReader` — reads frames from a stream
- `WebSocketFrameWriter` — writes masked frames to a stream
- Support for text, binary, ping, pong, and close frames
- Fragmented message reassembly

**Estimated Effort:** 1 week

---

### Task 18.2: HTTP Upgrade Handshake

**Goal:** Implement the HTTP/1.1 → WebSocket upgrade handshake.

**Deliverables:**
- `WebSocketHandshake` — constructs the `Upgrade: websocket` request with `Sec-WebSocket-Key`
- Server response validation (`101 Switching Protocols`, `Sec-WebSocket-Accept`)
- Support for sub-protocols (`Sec-WebSocket-Protocol`)
- Support for extensions (e.g., `permessage-deflate`) — extensibility hook, not full implementation

**Estimated Effort:** 3-4 days

---

### Task 18.3: WebSocket Connection & Lifecycle

**Goal:** Manage connection state machine and clean shutdown.

**Deliverables:**
- `WebSocketConnection` — wraps a raw socket/TLS stream after handshake
- State machine: `Connecting → Open → Closing → Closed`
- Clean close handshake (close frame exchange, timeout)
- Ping/pong keep-alive with configurable interval
- Idle timeout detection

**Estimated Effort:** 1 week

---

### Task 18.4: Send/Receive API Surface

**Goal:** Provide a clean async API for sending and receiving messages.

**Deliverables:**
- `IWebSocketClient` interface
- `SendAsync(string message)` / `SendAsync(byte[] data)` / `SendAsync(ReadOnlyMemory<byte> data)`
- `ReceiveAsync()` returning `WebSocketMessage` (text or binary)
- `IAsyncEnumerable<WebSocketMessage>` streaming receive (where supported)
- Cancellation support on all operations
- Thread-safe concurrent send/receive

**Estimated Effort:** 3-4 days

---

### Task 18.5: Reconnection & Resilience

**Goal:** Automatic reconnection with configurable backoff.

**Deliverables:**
- `WebSocketReconnectPolicy` (max retries, backoff strategy, jitter)
- Auto-reconnect on unexpected disconnection
- `OnReconnecting` / `OnReconnected` / `OnClosed` event callbacks
- Configurable message buffering during reconnection (optional)

**Estimated Effort:** 3-4 days

---

### Task 18.6: Unity Integration

**Goal:** Bridge WebSocket events to the Unity main thread.

**Deliverables:**
- Integration with Phase 15 `MainThreadDispatcher` for callback delivery
- `MonoBehaviour`-friendly event pattern (`UnityEvent` or callback-based)
- Coroutine wrapper for WebSocket receive loop
- Lifecycle binding (auto-disconnect on `OnDestroy`)

**Estimated Effort:** 2-3 days

---

### Task 18.7: Test Suite

**Goal:** Comprehensive tests covering protocol correctness and edge cases.

**Deliverables:**
- Unit tests for framing (encode/decode, masking, fragmentation)
- Handshake tests (success, rejection, invalid key)
- Integration tests with a local WebSocket echo server
- Reconnection stress tests
- Cancellation and timeout tests
- Thread-safety tests for concurrent send/receive

**Estimated Effort:** 1 week

---

## Prioritization Matrix

| Task | Priority | Effort | Dependencies |
|------|----------|--------|--------------|
| 18.1 Framing | Highest | 1w | None |
| 18.2 Handshake | Highest | 3-4d | 18.1 |
| 18.3 Connection | High | 1w | 18.1, 18.2 |
| 18.4 API Surface | High | 3-4d | 18.3 |
| 18.5 Reconnection | Medium | 3-4d | 18.3, 18.4 |
| 18.6 Unity Integration | Medium | 2-3d | 18.4, Phase 15 |
| 18.7 Test Suite | High | 1w | All above |

## Verification Plan

1. All unit tests pass for framing, handshake, and connection state machine.
2. Integration test: connect → send → receive → close against a real echo server.
3. Reconnection test: kill server mid-session, verify auto-reconnect.
4. Unity test: verify message callbacks arrive on main thread.
5. IL2CPP build validation on iOS and Android.

## Notes

- WSS (secure WebSocket) reuses existing TLS infrastructure from Phase 3C.
- WebGL WebSocket transport is a separate concern and lives in Phase 16 (WebGL Support).
- If `permessage-deflate` is needed, it can be added as Task 18.8 later.
