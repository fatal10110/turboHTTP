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
   - `SendAsync(byte[] data, CancellationToken ct)` — send binary frame (convenience overload; document: prefer `ReadOnlyMemory<byte>` in performance-critical code to avoid array allocation).
   - `ReceiveAsync(CancellationToken ct)` — receive next message. Returns `ValueTask<WebSocketMessage>` to avoid `Task` allocation when the receive channel is already populated.
   - `CloseAsync(WebSocketCloseCode code, string reason, CancellationToken ct)` — initiate clean close.
   - `Abort()` — force close without handshake.
   - `State` property (`WebSocketState`).
   - `SubProtocol` property (negotiated sub-protocol, null if none).
2. Define `WebSocketMessage` as a **class** (not struct):
   - `Type` property: `Text` or `Binary`.
   - `Text` property: **eagerly decoded** UTF-8 string at construction (for text messages). Single `Encoding.UTF8.GetString()` call — accept that text messages always allocate a string (unavoidable in .NET Standard 2.1). For low-GC scenarios, users should prefer binary frames and handle encoding themselves.
   - `Data` property: `ReadOnlyMemory<byte>` — for binary messages this is the raw payload, for text messages this is the UTF-8 bytes (wire format).
   - `IsText` / `IsBinary` convenience properties.
   - Implements `IDisposable` for **buffer lease** pattern: binary message `Data` references a pooled buffer that must be returned. `Dispose()` returns the buffer to `ArrayPool`. Consumer must process the data before calling `Dispose()` (or copy if needed beyond the message lifetime). Text messages also implement `IDisposable` for consistency but the underlying buffer is returned at construction after string decode.
   - Document clearly: "Binary frames: `.Data` is application-defined raw bytes. Text frames: `.Data` is UTF-8 encoded bytes (wire format). `.Text` is the decoded string."
3. Define `WebSocketState` enum: `None`, `Connecting`, `Open`, `Closing`, `Closed`.
4. Interface must extend both `IDisposable` and `IAsyncDisposable`:
   - `Dispose()` calls `Abort()` (synchronous, unclean).
   - `DisposeAsync()` attempts `CloseAsync(NormalClosure, ..., timeout)` then falls back to `Abort()`.
5. All async methods must throw `ObjectDisposedException` after disposal.
6. All async methods must throw `InvalidOperationException` if called in wrong state (e.g., `SendAsync` when not `Open`).
7. Interface design must accommodate future `ReceiveAllAsync(CancellationToken)` returning `IAsyncEnumerable<WebSocketMessage>` without breaking changes (method name reserved).

Implementation constraints:

1. `WebSocketMessage` is a class with eager string decode — no lazy decode (lazy decode on a struct is fundamentally broken because struct copies don't propagate cached values).
2. Binary message buffer lease uses `IDisposable` pattern (similar to `ConnectionLease`). If consumer does not dispose, buffer is eventually collected by GC but pool is degraded.
3. `SendAsync(byte[])` and `SendAsync(ReadOnlyMemory<byte>)` may be ambiguous in some call sites — document preference for `ReadOnlyMemory<byte>` overload.

---

## Step 2: Implement WebSocket Client

**File:** `Runtime/WebSocket/WebSocketClient.cs` (new)

Required behavior:

1. Implement `IWebSocketClient` wrapping `WebSocketConnection`.
2. `ConnectAsync`:
   - Create underlying `WebSocketConnection`.
   - Resolve `IWebSocketTransport` (from options or default `RawSocketWebSocketTransport`).
   - Call `WebSocketConnection.ConnectAsync` with provided URI, transport, and options.
   - Return after connection is open and ready.
3. `SendAsync(string)`:
   - Validate state is `Open`.
   - Encode string to UTF-8 bytes: use `Encoding.UTF8.GetByteCount(str)` first to determine exact buffer size, then rent exact-fit buffer from pool (avoids `GetMaxByteCount` 3x overestimation for ASCII-heavy strings).
   - Delegate to `WebSocketConnection` frame writer with `Text` opcode.
   - Return rented buffer to pool after write completes.
   - Apply fragmentation if message exceeds threshold.
4. `SendAsync(ReadOnlyMemory<byte>)`:
   - Validate state is `Open`.
   - Delegate to `WebSocketConnection` frame writer with `Binary` opcode.
   - Apply fragmentation if data exceeds threshold.
5. `ReceiveAsync`:
   - Validate state is `Open` or `Closing` (may still receive during close handshake).
   - Read next message from `WebSocketConnection` receive `Channel`.
   - On channel completion (connection closed): throw `WebSocketException` with `ConnectionClosed` error.
   - Return `WebSocketMessage` wrapping the received data.
6. `CloseAsync`:
   - Delegate to `WebSocketConnection.CloseAsync`.
   - After close completes, return normally.
7. Thread safety:
   - Multiple concurrent `SendAsync` calls are serialized via the connection's `SemaphoreSlim(1,1)` (no interleaved frames).
   - One `ReceiveAsync` call at a time (throw if concurrent receive attempted — tracked via atomic flag).
   - `SendAsync` and `ReceiveAsync` may run concurrently (send does not block receive).
8. Event callbacks (using C# `event` keyword with null-safe snapshot invoke pattern):
   - `OnConnected` — fired after successful handshake.
   - `OnMessage` — fired for each received message (alternative to polling `ReceiveAsync`).
   - `OnError` — fired on connection errors.
   - `OnClosed` — fired when connection transitions to `Closed` (includes close code and reason).

Implementation constraints:

1. Client must be usable in both pull-based (explicit `ReceiveAsync` loop) and push-based (event callbacks) patterns.
2. Event callbacks must fire on the thread that detected the event (receive loop thread) — Unity integration (Phase 18.6) handles main-thread marshalling. Use `var handler = EventName; handler?.Invoke(args);` pattern for thread safety.
3. Disposal must cancel all pending async operations and dispose the underlying connection. `Dispose()` is synchronous (`Abort()`), `DisposeAsync()` attempts clean close.
4. Client instances are not reusable after close — create a new instance for reconnection (reconnection policy in Phase 18.5 handles this transparently).

---

## Step 3: Add WebSocket Exception Types

**File:** `Runtime/WebSocket/WebSocketException.cs` (new)

Required behavior:

1. Define `WebSocketException` extending `UHttpException`:
   - `CloseCode` property (`WebSocketCloseCode?`) — present when exception relates to a close frame.
   - `CloseReason` property (string) — server-provided close reason if available.
   - Constructor overloads: message-only, message + inner exception, close code + reason, error type + message.
2. Define `WebSocketError` cases that map to exception scenarios:
   - `HandshakeFailed` — upgrade handshake rejected or invalid.
   - `ConnectionClosed` — connection closed while operation was pending.
   - `SendFailed` — frame write failed (broken pipe, etc.).
   - `ReceiveFailed` — frame read failed (network error, protocol violation).
   - `ProtocolViolation` — invalid frame, bad opcode, masked server frame, reserved opcode, etc.
   - `MessageTooLarge` — received message exceeds configured maximum size.
   - `InvalidUtf8` — received text frame payload is not valid UTF-8.
   - `PongTimeout` — keep-alive pong not received within timeout.
3. `WebSocketException.IsRetryable()` — returns `true` for transient errors (`ConnectionClosed` due to network, `PongTimeout`), `false` for protocol violations, handshake failures, and `InvalidUtf8`.

Implementation constraints:

1. Exception hierarchy must integrate with existing `UHttpError` / `UHttpException` patterns.
2. Close code and reason must be preserved through exception propagation for diagnostics.
3. Exception messages must be descriptive enough for debugging without exposing sensitive data.
4. Map both `IOException` (from SslStream) and `SocketException` (from plain TCP) inner exceptions to appropriate `WebSocketError` cases with `IsRetryable = true` for transient network errors.

---

## Verification Criteria

1. `WebSocketClient` can connect, send text/binary messages, receive messages, and close cleanly.
2. Concurrent send operations are serialized — no interleaved frame bytes on the wire.
3. Concurrent send + receive does not deadlock.
4. Event callbacks fire for all connection lifecycle events.
5. `ReceiveAsync` throws `WebSocketException` when connection closes unexpectedly.
6. Disposal cancels pending operations and releases all resources.
7. `Dispose()` calls `Abort()`, `DisposeAsync()` attempts clean close then falls back.
8. State property reflects current connection state accurately at all times.
9. Exception types correctly indicate retryable vs non-retryable errors.
10. `WebSocketMessage.Dispose()` returns pooled buffer to `ArrayPool`.
11. Text message `Text` property returns eagerly-decoded string without per-access allocation.
12. UTF-8 encode for `SendAsync(string)` uses `GetByteCount` for exact buffer sizing.
