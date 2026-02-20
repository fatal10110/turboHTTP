# Phase 18.3: WebSocket Connection & Lifecycle

**Depends on:** Phase 18.1 (framing), Phase 18.2 (handshake)
**Assembly:** `TurboHTTP.WebSocket`
**Files:** 2 new, 1 modified

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
3. State transitions must be thread-safe — use atomic compare-and-swap for state field.
4. Invalid state transitions must throw `InvalidOperationException` with descriptive message.
5. Expose `State` property and `StateChanged` event for external observation.
6. Track connection metadata: remote endpoint, negotiated sub-protocol, connection start time, last activity time.

Implementation constraints:

1. State transitions must be atomic — no intermediate states visible to concurrent observers.
2. Event callbacks must not block state transitions — fire-and-forget or queue to dispatcher.
3. Connection object must be `IDisposable` with idempotent dispose.
4. Dispose must transition to `Closed` regardless of current state and release all resources.

---

## Step 2: Implement Connection Establishment and Frame I/O Loop

**Files:**
- `Runtime/WebSocket/WebSocketConnection.cs` (modify)
- `Runtime/WebSocket/WebSocketConnectionOptions.cs` (new)

Required behavior:

1. `ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)`:
   - Establish TCP connection (reuse `TcpConnectionPool` patterns for socket creation).
   - Wrap with TLS for `wss://` URIs (reuse `TlsStreamWrapper` / `ITlsProvider`).
   - Perform HTTP upgrade handshake via `WebSocketHandshake` + `WebSocketHandshakeValidator`.
   - Transition to `Open` state on success.
2. Start background receive loop after connection opens:
   - Continuously read frames using `WebSocketFrameReader`.
   - Dispatch data frames (text/binary) to the receive queue.
   - Handle control frames inline:
     - **Ping**: auto-respond with pong (echo payload).
     - **Pong**: update last pong received timestamp, notify keep-alive tracker.
     - **Close**: initiate close handshake response if not already closing.
3. Send operations write frames via `WebSocketFrameWriter`:
   - Serialize send operations through a write lock or channel to prevent interleaved frame writes.
   - Support concurrent send and receive (send lock does not block receive loop).
4. `WebSocketConnectionOptions` configuration:
   - `MaxFrameSize` (default 16MB)
   - `MaxMessageSize` (default 64MB)
   - `FragmentationThreshold` (default 64KB)
   - `CloseHandshakeTimeout` (default 5s)
   - `PingInterval` (default 30s, `TimeSpan.Zero` to disable)
   - `PongTimeout` (default 10s)
   - `IdleTimeout` (default `TimeSpan.Zero` — disabled)
   - `SubProtocols` (list of requested sub-protocols)
   - `CustomHeaders` (dictionary for upgrade request headers)
   - `TlsProvider` (optional override for wss:// connections)

Implementation constraints:

1. Receive loop must run on a background thread/task — never block the caller's context.
2. Receive queue must be bounded (configurable, default 1000 messages) with backpressure.
3. Send serialization must use `SemaphoreSlim(1,1)` — not `lock` — for async compatibility.
4. Connection must handle stream read errors gracefully: transition to `Closed`, surface error via event/exception.
5. Ping/pong must not interfere with data frame ordering.
6. Idle timeout checks must use monotonic time (`Stopwatch`), not wall clock.

---

## Step 3: Implement Clean Close Handshake

**Files:**
- `Runtime/WebSocket/WebSocketConnection.cs` (modify)

Required behavior:

1. `CloseAsync(WebSocketCloseCode code, string reason, CancellationToken ct)`:
   - Send close frame with status code and optional reason.
   - Transition to `Closing` state.
   - Wait for server's close frame response (up to `CloseHandshakeTimeout`).
   - Transition to `Closed` after receiving server close frame or timeout.
2. Handle server-initiated close:
   - Receive close frame from server.
   - Send close frame response echoing the status code.
   - Transition to `Closed`.
3. Abort path for unclean shutdown:
   - `Abort()` — immediately close the underlying stream/socket without close handshake.
   - Use when close handshake times out or on unrecoverable errors.
4. After close, drain any pending receive queue items and mark them as cancelled.

Implementation constraints:

1. Close frame must be sent exactly once — guard with atomic state transition.
2. Close handshake timeout must be enforced via `CancellationTokenSource.CancelAfter`.
3. Dispose must call `Abort()` if still connected — no blocking wait in dispose.
4. Close reason must be truncated to 123 bytes if it exceeds the RFC limit.
5. After sending close frame, no more data frames may be sent (enforce in send path).

---

## Step 4: Implement Keep-Alive (Ping/Pong)

**Files:**
- `Runtime/WebSocket/WebSocketConnection.cs` (modify)

Required behavior:

1. When `PingInterval` is configured (non-zero), start a periodic ping timer after connection opens.
2. Send ping frames at the configured interval with an incrementing sequence payload.
3. Track outstanding pings — if no pong received within `PongTimeout`, consider the connection dead.
4. On pong timeout: transition to `Closed` state with `AbnormalClosure` code and surface timeout error.
5. Reset ping timer on any received data (activity indicates the connection is alive).
6. Stop ping timer during `Closing` state and after `Closed`.

Implementation constraints:

1. Ping timer must use `Task.Delay` with cancellation — not `System.Timers.Timer` (avoid threading issues).
2. Ping/pong tracking must be thread-safe (concurrent with send/receive operations).
3. Pong responses must match the ping payload — log mismatches but do not treat as fatal.
4. Keep-alive must not interfere with application-level send/receive throughput.

---

## Verification Criteria

1. State machine transitions are deterministic and thread-safe under concurrent access.
2. Connection establishment works for both `ws://` and `wss://` URIs.
3. Clean close handshake completes successfully with both client-initiated and server-initiated close.
4. Close handshake timeout triggers abort after configured duration.
5. Ping/pong keep-alive detects dead connections within the configured timeout window.
6. Concurrent send and receive operations do not deadlock or corrupt frame ordering.
7. Connection disposal releases all resources (socket, TLS stream, buffers) without leaks.
8. Receive queue backpressure prevents unbounded memory growth under slow consumers.
