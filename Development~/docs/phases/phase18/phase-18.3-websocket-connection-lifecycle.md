# Phase 18.3: WebSocket Connection & Lifecycle

**Depends on:** Phase 18.1 (framing), Phase 18.2 (handshake)
**Assembly:** `TurboHTTP.WebSocket`, `TurboHTTP.WebSocket.Transport`
**Files:** 4 new, 1 modified

---

## Step 1: Implement Connection State Machine

**File:** `Runtime/WebSocket/WebSocketConnection.cs` (new)

Required behavior:

1. Define connection states: `None`, `Connecting`, `Open`, `Closing`, `Closed`.
2. Implement deterministic state transitions:
   - `None → Connecting`: when `ConnectAsync` is called.
   - `Connecting → Open`: after successful handshake.
   - `Connecting → Closed`: on handshake failure or cancellation.
   - `Open → Closing`: when close handshake is initiated (by either side).
   - `Closing → Closed`: after close frame exchange completes or timeout.
   - `Open → Closed`: on abnormal disconnection (network error, protocol violation).
3. **State machine implementation:** define `TryTransitionState(WebSocketState expected, WebSocketState next)` helper using `Interlocked.CompareExchange` on an `int` field (enum cast to int). Maintain a static allowed-transition map. Return `bool` — caller decides retry vs throw based on result and current state.
4. Invalid state transitions must throw `InvalidOperationException` with descriptive message including current and attempted states.
5. Expose `State` property and `StateChanged` event for external observation. Event invoke pattern: `var handler = StateChanged; handler?.Invoke(this, newState);` (null-safe snapshot, no lock needed).
6. Track connection metadata: remote endpoint, negotiated sub-protocol, connection start time, last activity time.

Implementation constraints:

1. State transitions must be atomic — no intermediate states visible to concurrent observers.
2. Event callbacks must not block state transitions — fire-and-forget using snapshot pattern.
3. Connection object must implement both `IDisposable` and `IAsyncDisposable` with idempotent behavior.
4. `Dispose()` is synchronous: calls `Abort()` (unclean close, no handshake). Users wanting clean close must explicitly `await CloseAsync()` before dispose.
5. `DisposeAsync()` attempts `CloseAsync` with a short timeout (1s), then falls back to `Abort()`.
6. **Close frame sent tracking:** maintain a separate `_closeFrameSent` atomic flag (`Interlocked.CompareExchange(ref _closeFrameSent, 1, 0) == 0`) to ensure close frame is sent exactly once, even when client-initiated and server-initiated close race.

---

## Step 2: Implement Connection Establishment and Frame I/O Loop

**Files:**
- `Runtime/WebSocket/WebSocketConnection.cs` (modify)
- `Runtime/WebSocket/WebSocketConnectionOptions.cs` (new)

Required behavior:

1. `ConnectAsync(Uri uri, IWebSocketTransport transport, WebSocketConnectionOptions options, CancellationToken ct)`:
   - Delegate to `IWebSocketTransport.ConnectAsync` for stream acquisition (TCP + optional TLS).
   - Perform HTTP upgrade handshake via `WebSocketHandshake` + `WebSocketHandshakeValidator`.
   - Transition to `Open` state on success.
2. Start background receive loop as a long-running `async Task` (not `Task.Run` — `await` async I/O naturally, avoid unnecessary thread pool work). Store task reference for `CloseAsync` to await with timeout. **Never use `async void`.**
   - Continuously read frames using `WebSocketFrameReader`.
   - Feed data frames to `MessageAssembler`; complete messages are written to the receive `Channel<WebSocketMessage>`.
   - **Validate inbound text frame payloads as valid UTF-8** (RFC 6455 Section 5.6 / 8.1 MUST requirement). Validate after reassembly for fragmented messages. Invalid UTF-8 → fail connection with close code 1007 (`InvalidPayload`).
   - Handle control frames **inline** (never queued to data channel — backpressure on data messages does not block control frame handling):
     - **Ping**: auto-respond with pong (echo payload).
     - **Pong**: update last pong received timestamp, notify keep-alive tracker.
     - **Close**: initiate close handshake response if not already closing.
   - Catch both `IOException` and `SocketException` from stream reads, map to `WebSocketException` with appropriate `IsRetryable` semantics. `IOException` wrapping `SocketException` (from SslStream) and direct `SocketException` (from plain TCP) must both be handled.
3. Send operations write frames via `WebSocketFrameWriter`:
   - **Serialize via `SemaphoreSlim(1,1)`** with `try/finally` to guarantee semaphore release (matching `ConnectionLease` pattern). If cancellation fires after acquiring the semaphore but before send completes, the semaphore MUST still be released in the finally block.
   - Support concurrent send and receive (send semaphore does not block receive loop).
4. **Receive queue:** `Channel<WebSocketMessage>` with bounded capacity (configurable, default 100). Receive loop calls `channel.Writer.WriteAsync()` which blocks when full — natural backpressure propagates through TCP to server. `channel.Writer.TryComplete()` on connection close.
5. `WebSocketConnectionOptions` configuration:
   - `MaxFrameSize` (default 16MB)
   - `MaxMessageSize` (default 4MB)
   - `MaxFragmentCount` (default 64)
   - `FragmentationThreshold` (default 64KB)
   - `ReceiveQueueCapacity` (default 100)
   - `CloseHandshakeTimeout` (default 5s)
   - `HandshakeTimeout` (default 10s)
   - `PingInterval` (default 25s, `TimeSpan.Zero` to disable) — 25s default for mobile NAT/firewall compatibility (many have 30-60s idle timeouts)
   - `PongTimeout` (default 10s)
   - `IdleTimeout` (default `TimeSpan.Zero` — disabled) — max time with no application messages (not including pings/pongs), orthogonal to `PongTimeout`
   - `SubProtocols` (list of requested sub-protocols)
   - `CustomHeaders` (dictionary for upgrade request headers)
   - `TlsProvider` (optional override for wss:// connections)
   - `ReconnectPolicy` (default `WebSocketReconnectPolicy.None`)

Implementation constraints:

1. Receive loop must be a long-running `async Task` — never block the caller's context, never use `async void`.
2. Receive `Channel<WebSocketMessage>` backpressure: slow consumer → blocked `WriteAsync` → TCP backpressure to server. Control frames bypass this entirely.
3. Send serialization uses `SemaphoreSlim(1,1)` with `try/finally` — not `lock` — for async compatibility and cancellation safety.
4. Connection must handle stream read errors gracefully: catch `IOException` and `SocketException`, transition to `Closed`, surface error via event/exception.
5. Ping/pong must not interfere with data frame ordering — control frames are handled inline by receive loop.
6. Idle timeout checks must use monotonic time (`Stopwatch`), not wall clock.
7. **Socket creation:** `IWebSocketTransport.ConnectAsync` handles TCP connection and TLS wrapping. The connection does NOT use `TcpConnectionPool` lease/return lifecycle — WebSocket connections are long-lived. The transport reuses socket creation and DNS resolution patterns only.

---

## Step 3: Implement Clean Close Handshake

**Files:**
- `Runtime/WebSocket/WebSocketConnection.cs` (modify)

Required behavior:

1. `CloseAsync(WebSocketCloseCode code, string reason, CancellationToken ct)`:
   - Validate close code: reject 1005 and 1006 (reserved, must never be sent on wire — RFC 6455 Section 7.4.1).
   - Send close frame with status code and optional reason (guarded by `_closeFrameSent` atomic flag).
   - Transition to `Closing` state.
   - Wait for server's close frame response (up to `CloseHandshakeTimeout`).
   - Transition to `Closed` after receiving server close frame or timeout.
   - Await the receive loop task (with timeout) to ensure clean shutdown.
2. Handle server-initiated close:
   - Receive close frame from server.
   - Send close frame response with appropriate code (typically `NormalClosure` 1000 — do NOT blindly echo the server's code; the responding endpoint sends its own code per RFC 6455 Section 5.5.1).
   - Transition to `Closed`.
3. Abort path for unclean shutdown:
   - `Abort()` — immediately close the underlying stream/socket without close handshake.
   - Use when close handshake times out or on unrecoverable errors.
4. After close, complete the receive `Channel` writer (`channel.Writer.TryComplete()`). Pending `ReceiveAsync` calls get `ChannelClosedException` (or return null sentinel, decided in Phase 18.4).

Implementation constraints:

1. Close frame must be sent exactly once — guarded by `_closeFrameSent` atomic flag via `Interlocked.CompareExchange`.
2. Close handshake timeout must be enforced via `CancellationTokenSource.CancelAfter`.
3. `Dispose()` must call `Abort()` if still connected — no blocking wait in synchronous dispose.
4. Close reason must be truncated to 123 bytes at the byte level (see Phase 18.1 Step 1 constraint 3).
5. After sending close frame, no more data frames may be sent (enforce in send path by checking state).
6. **TLS stream disposal ordering:** dispose SslStream → NetworkStream → Socket (inner to outer). Wrap in try-catch (disposal after network error may throw). Reuse existing `TlsStreamWrapper` disposal logic from Phase 3C.

---

## Step 4: Implement Keep-Alive (Ping/Pong)

**Files:**
- `Runtime/WebSocket/WebSocketConnection.cs` (modify)

Required behavior:

1. When `PingInterval` is configured (non-zero), start a periodic ping timer after connection opens.
2. Send ping frames at the configured interval. Use a static 8-byte payload (network-order counter) from a pre-allocated buffer — no per-ping allocation. Rent from `ByteArrayPool` for the write, return immediately after write completes.
3. Track outstanding pings — if no pong received within `PongTimeout`, consider the connection dead.
4. On pong timeout: transition to `Closed` state with `AbnormalClosure` code and surface timeout error.
5. Reset ping timer on any received data (activity indicates the connection is alive).
6. Stop ping timer during `Closing` state and after `Closed`.

Implementation constraints:

1. Ping timer must use `Task.Delay` with cancellation — not `System.Timers.Timer` (avoid threading issues). Ping timer task must have a dedicated `CancellationTokenSource` (canceled on close/dispose).
2. Ping/pong tracking must be thread-safe (concurrent with send/receive operations).
3. Pong responses must match the ping payload — log mismatches at Warning level but do not treat as fatal.
4. Keep-alive must not interfere with application-level send/receive throughput.
5. `IdleTimeout` (no application messages) is orthogonal to `PongTimeout` (no pong response). Both can be configured independently.

---

## Step 5: Define WebSocket Transport Interface

**File:** `Runtime/WebSocket/IWebSocketTransport.cs` (new)

Required behavior:

1. Define `IWebSocketTransport` interface in `TurboHTTP.WebSocket` assembly (no Transport dependency):
   - `ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)` → returns `Stream` (plain TCP or TLS-wrapped).
   - `IDisposable` for resource cleanup.
2. Transport is responsible for: DNS resolution, TCP socket creation, TLS handshake (for wss://), returning the connected stream. **Ownership of the returned `Stream` is transferred to the connection — the connection must dispose the stream. Transport `IDisposable` is strictly for cleaning up internal factories or DNS resolvers, NOT the active sockets.**
3. Transport is NOT responsible for: HTTP upgrade handshake, framing, lifecycle management (those are `WebSocketConnection`'s job).

Implementation constraints:

1. Interface lives in `TurboHTTP.WebSocket` — no dependency on `TurboHTTP.Transport`.
2. `RawSocketWebSocketTransport` implementation lives in `TurboHTTP.WebSocket.Transport` assembly (references both WebSocket and Transport).
3. `RawSocketWebSocketTransport` reuses socket creation and DNS resolution patterns from `TcpConnectionPool` but does NOT use pool lease/return lifecycle. Each WebSocket gets its own dedicated socket.
4. WSS connections must pass empty/null ALPN protocols to `ITlsProvider.WrapAsync`.

---

## Step 6: Add WebSocket Transport Assembly Definition

**File:** `Runtime/WebSocket.Transport/TurboHTTP.WebSocket.Transport.asmdef` (new)

Required behavior:

1. Create `TurboHTTP.WebSocket.Transport` assembly definition.
2. References: `TurboHTTP.Core`, `TurboHTTP.WebSocket`, `TurboHTTP.Transport`.
3. Set `excludePlatforms` to `["WebGL"]` to match `TurboHTTP.Transport`.
4. Set `autoReferenced` to `false` for modular opt-in usage.

Implementation constraints:

1. Set `noEngineReferences` to `true` (transport layer remains UnityEngine-independent).
2. Keep this assembly free of protocol APIs beyond the `IWebSocketTransport` contract.

---

## Verification Criteria

1. State machine transitions are deterministic and thread-safe under concurrent access.
2. `TryTransitionState` correctly rejects invalid transitions.
3. Connection establishment works for both `ws://` and `wss://` URIs.
4. Clean close handshake completes successfully with both client-initiated and server-initiated close.
5. Close handshake timeout triggers abort after configured duration.
6. Close codes 1005 and 1006 are rejected by `CloseAsync`.
7. Simultaneous client/server close results in exactly one close frame sent.
8. Ping/pong keep-alive detects dead connections within the configured timeout window.
9. Concurrent send and receive operations do not deadlock or corrupt frame ordering.
10. Connection disposal releases all resources (socket, TLS stream, buffers) without leaks, with correct disposal ordering.
11. Receive `Channel` backpressure prevents unbounded memory growth under slow consumers.
12. Control frames are processed even when data channel is full.
13. Inbound text frames with invalid UTF-8 cause connection failure with close code 1007.
14. `IOException` and `SocketException` during reads are caught and mapped to `WebSocketException`.
15. `TurboHTTP.WebSocket.Transport.asmdef` excludes WebGL and compiles with explicit references to Core/WebSocket/Transport.
