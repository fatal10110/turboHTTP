# Phase 18.4: Send/Receive API Surface

**Depends on:** Phase 18.3 (WebSocket connection and lifecycle)
**Assembly:** `TurboHTTP.WebSocket`
**Files:** 3 new

---

## Step 1: Define WebSocket Client Interface and Message Types

**File:** `Runtime/WebSocket/IWebSocketClient.cs` (new)

Required behavior:

1. Define `IWebSocketClient` interface:
   - `ConnectAsync(Uri uri, CancellationToken ct)` — establish connection.
   - `ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)` — establish with custom options.
   - `SendAsync(string message, CancellationToken ct)` — send text frame.
   - `SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)` — send binary frame.
   - `SendAsync(byte[] data, CancellationToken ct)` — send binary frame (convenience overload).
   - `ReceiveAsync(CancellationToken ct)` — receive next message.
   - `CloseAsync(WebSocketCloseCode code, string reason, CancellationToken ct)` — initiate clean close.
   - `Abort()` — force close without handshake.
   - `State` property (`WebSocketState`).
   - `SubProtocol` property (negotiated sub-protocol, null if none).
2. Define `WebSocketMessage` struct:
   - `Type` property: `Text` or `Binary`.
   - `Text` property: decoded UTF-8 string (for text messages).
   - `Data` property: `ReadOnlyMemory<byte>` (for binary messages, also available for text as raw bytes).
   - `IsText` / `IsBinary` convenience properties.
3. Define `WebSocketState` enum: `None`, `Connecting`, `Open`, `Closing`, `Closed`.
4. Interface must extend `IDisposable` for resource cleanup.
5. All async methods must throw `ObjectDisposedException` after disposal.
6. All async methods must throw `InvalidOperationException` if called in wrong state (e.g., `SendAsync` when not `Open`).

Implementation constraints:

1. `WebSocketMessage` must avoid unnecessary string allocation — lazy UTF-8 decode for `Text` property.
2. Binary message `Data` must reference pooled buffer — consumer is responsible for processing before next receive (or copying).
3. Interface design must accommodate future `IAsyncEnumerable<WebSocketMessage>` without breaking changes.

---

## Step 2: Implement WebSocket Client

**File:** `Runtime/WebSocket/WebSocketClient.cs` (new)

Required behavior:

1. Implement `IWebSocketClient` wrapping `WebSocketConnection`.
2. `ConnectAsync`:
   - Create underlying `WebSocketConnection`.
   - Call `WebSocketConnection.ConnectAsync` with provided URI and options.
   - Return after connection is open and ready.
3. `SendAsync(string)`:
   - Validate state is `Open`.
   - Encode string to UTF-8 bytes using pooled buffer.
   - Delegate to `WebSocketConnection` frame writer with `Text` opcode.
   - Apply fragmentation if message exceeds threshold.
4. `SendAsync(ReadOnlyMemory<byte>)`:
   - Validate state is `Open`.
   - Delegate to `WebSocketConnection` frame writer with `Binary` opcode.
   - Apply fragmentation if data exceeds threshold.
5. `ReceiveAsync`:
   - Validate state is `Open` or `Closing` (may still receive during close handshake).
   - Dequeue next message from `WebSocketConnection` receive queue.
   - Return `WebSocketMessage` wrapping the received data.
   - Throw `WebSocketException` if connection closes unexpectedly while waiting.
6. `CloseAsync`:
   - Delegate to `WebSocketConnection.CloseAsync`.
   - After close completes, return normally.
7. Thread safety:
   - Multiple concurrent `SendAsync` calls must be serialized (no interleaved frames).
   - One `ReceiveAsync` call at a time (throw if concurrent receive attempted).
   - `SendAsync` and `ReceiveAsync` may run concurrently (send does not block receive).
8. Event callbacks:
   - `OnConnected` — fired after successful handshake.
   - `OnMessage` — fired for each received message (alternative to polling `ReceiveAsync`).
   - `OnError` — fired on connection errors.
   - `OnClosed` — fired when connection transitions to `Closed` (includes close code and reason).

Implementation constraints:

1. Client must be usable in both pull-based (explicit `ReceiveAsync` loop) and push-based (event callbacks) patterns.
2. Event callbacks must fire on the thread that detected the event (receive loop thread) — Unity integration (Phase 18.6) handles main-thread marshalling.
3. Disposal must cancel all pending async operations and dispose the underlying connection.
4. Client instances are not reusable after close — create a new instance for reconnection (reconnection policy in Phase 18.5 handles this transparently).

---

## Step 3: Add WebSocket Exception Types

**File:** `Runtime/WebSocket/WebSocketException.cs` (new)

Required behavior:

1. Define `WebSocketException` extending `UHttpException`:
   - `CloseCode` property (`WebSocketCloseCode?`) — present when exception relates to a close frame.
   - `CloseReason` property (string) — server-provided close reason if available.
   - Constructor overloads: message-only, message + inner exception, close code + reason.
2. Define `WebSocketError` cases that map to exception scenarios:
   - `HandshakeFailed` — upgrade handshake rejected or invalid.
   - `ConnectionClosed` — connection closed while operation was pending.
   - `SendFailed` — frame write failed (broken pipe, etc.).
   - `ReceiveFailed` — frame read failed (network error, protocol violation).
   - `ProtocolViolation` — invalid frame, bad opcode, unmasked client frame, etc.
   - `MessageTooLarge` — received message exceeds configured maximum size.
   - `PongTimeout` — keep-alive pong not received within timeout.
3. `WebSocketException.IsRetryable()` — returns `true` for transient errors (`ConnectionClosed` due to network, `PongTimeout`), `false` for protocol violations and handshake failures.

Implementation constraints:

1. Exception hierarchy must integrate with existing `UHttpError` / `UHttpException` patterns.
2. Close code and reason must be preserved through exception propagation for diagnostics.
3. Exception messages must be descriptive enough for debugging without exposing sensitive data.

---

## Verification Criteria

1. `WebSocketClient` can connect, send text/binary messages, receive messages, and close cleanly.
2. Concurrent send operations are serialized — no interleaved frame bytes on the wire.
3. Concurrent send + receive does not deadlock.
4. Event callbacks fire for all connection lifecycle events.
5. `ReceiveAsync` throws `WebSocketException` when connection closes unexpectedly.
6. Disposal cancels pending operations and releases all resources.
7. State property reflects current connection state accurately at all times.
8. Exception types correctly indicate retryable vs non-retryable errors.
