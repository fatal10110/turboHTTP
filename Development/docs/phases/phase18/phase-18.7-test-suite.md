# Phase 18.7: Test Suite

**Depends on:** Phases 18.1–18.6 (all WebSocket implementation)
**Assembly:** `TurboHTTP.Tests.Runtime`
**Files:** 6 new

---

## Step 1: WebSocket Framing Unit Tests

**File:** `Tests/Runtime/WebSocket/WebSocketFramingTests.cs` (new)

Required behavior:

1. Frame encoding/decoding round-trip tests:
   - Single-frame text message (small payload, 0-125 bytes).
   - Single-frame text message (medium payload, 126-65535 bytes).
   - Single-frame binary message (large payload, >65535 bytes).
   - Empty payload frame (valid for ping/pong/close).
2. Masking tests:
   - Client-to-server frames are always masked.
   - Mask/unmask round-trip produces original payload.
   - Known test vectors from RFC 6455 examples.
3. Control frame tests:
   - Ping frame with payload (max 125 bytes).
   - Pong frame echoing ping payload.
   - Close frame with status code and reason.
   - Close frame with status code only (no reason).
   - Reject control frame with payload > 125 bytes.
   - Reject fragmented control frame.
4. Fragmentation tests:
   - Two-fragment text message reassembly.
   - Multi-fragment (5+) binary message reassembly.
   - Interleaved control frame during fragmentation.
   - Reject unexpected continuation frame (no preceding non-final frame).
   - Reject new data frame while fragmentation is in progress.
5. Edge cases:
   - Zero-length payload for all frame types.
   - Maximum payload size enforcement (reject frames exceeding limit).
   - RSV bit validation (reject non-zero RSV when no extensions negotiated).
   - Extended payload length boundary cases (125→126 and 65535→65536).

---

## Step 2: Handshake Tests

**File:** `Tests/Runtime/WebSocket/WebSocketHandshakeTests.cs` (new)

Required behavior:

1. Successful handshake tests:
   - Valid upgrade request wire format matches RFC 6455 Section 4.1.
   - `Sec-WebSocket-Accept` computed correctly for known key.
   - Sub-protocol negotiation: client requests multiple, server selects one.
   - Custom headers included in upgrade request.
   - Both `ws://` and `wss://` URI handling.
2. Handshake rejection tests:
   - Non-101 status code (e.g., 403 Forbidden) — verify error includes status.
   - Missing `Upgrade` header in response.
   - Missing `Connection` header in response.
   - Incorrect `Sec-WebSocket-Accept` value — verify specific error message.
   - Server selects sub-protocol not in client's list.
3. Edge cases:
   - URI with path, query string, and port.
   - Default port handling (80 for ws, 443 for wss).
   - Maximum header size enforcement.
   - CRLF injection prevention in custom headers.

---

## Step 3: Connection Lifecycle Tests

**File:** `Tests/Runtime/WebSocket/WebSocketConnectionTests.cs` (new)

Required behavior:

1. State machine tests:
   - Verify state transitions: None → Connecting → Open → Closing → Closed.
   - Verify invalid transitions throw `InvalidOperationException`.
   - Verify state is consistent under concurrent access.
2. Clean close handshake tests:
   - Client-initiated close: send close → receive close → Closed.
   - Server-initiated close: receive close → send close → Closed.
   - Close handshake timeout: server doesn't respond → abort after timeout.
   - Close with status code and reason preserved through the exchange.
3. Keep-alive tests:
   - Ping sent at configured interval.
   - Pong response received resets timeout.
   - Pong timeout triggers connection close.
   - Activity resets ping timer.
4. Error handling tests:
   - Network error during receive → transition to Closed with error.
   - Protocol violation (invalid frame) → close with ProtocolError code.
   - Disposal during active operations → clean resource release.

---

## Step 4: Send/Receive API Tests

**File:** `Tests/Runtime/WebSocket/WebSocketClientTests.cs` (new)

Required behavior:

1. Basic send/receive tests:
   - Send text message and receive echo.
   - Send binary message and receive echo.
   - Send large message (verify fragmentation).
   - Receive multiple messages in sequence.
2. Concurrency tests:
   - Concurrent `SendAsync` calls are serialized (no interleaved frames).
   - Concurrent send + receive does not deadlock.
   - Multiple rapid sends followed by receives.
3. Error condition tests:
   - `SendAsync` after close throws `InvalidOperationException`.
   - `ReceiveAsync` after close throws `WebSocketException`.
   - `SendAsync` / `ReceiveAsync` after disposal throws `ObjectDisposedException`.
   - Cancellation during `SendAsync` / `ReceiveAsync` propagates correctly.
4. Event callback tests:
   - `OnConnected` fires after successful connection.
   - `OnMessage` fires for each received message.
   - `OnClosed` fires with close code and reason.
   - `OnError` fires on connection failure.

---

## Step 5: Reconnection Tests

**File:** `Tests/Runtime/WebSocket/WebSocketReconnectionTests.cs` (new)

Required behavior:

1. Reconnection policy tests:
   - Backoff delay computation with multiplier.
   - Delay capped at `MaxDelay`.
   - Jitter applied within configured range.
   - `ShouldReconnect` respects `MaxRetries`.
   - `ShouldReconnect` filters by close code.
2. Resilient client tests:
   - Auto-reconnect on unexpected disconnection.
   - Event ordering: `OnError` → `OnReconnecting` → `OnReconnected`.
   - Max retries exhausted → `OnClosed` fires.
   - Normal close does not trigger reconnection.
   - Disposal during reconnection backoff cancels cleanly.
3. Stress tests:
   - Rapid connect/disconnect cycles (10+ iterations).
   - Reconnection under sustained message load.
   - Multiple concurrent resilient clients.

---

## Step 6: Unity Integration Tests

**File:** `Tests/Runtime/Unity/UnityWebSocketTests.cs` (new)

Required behavior:

1. Main-thread dispatch tests:
   - `OnMessage` callback arrives on main thread.
   - `OnConnected` / `OnClosed` callbacks arrive on main thread.
   - Rapid message flood does not starve main thread.
2. Lifecycle binding tests:
   - `OnDestroy` disconnects WebSocket.
   - Destroyed owner cancels pending operations.
   - Domain reload cleans up connections.
3. `UnityWebSocketClient` component tests:
   - `AutoConnect` connects on `Start`.
   - Inspector events fire correctly.
   - Fire-and-forget `Send()` does not throw.
   - Multiple components on same GameObject work independently.
4. Coroutine wrapper tests:
   - Receive loop coroutine yields messages correctly.
   - Coroutine stops on disconnect.
   - Coroutine cancellation on owner destroy.

---

## Verification Criteria

1. All framing tests pass — encode/decode round-trips for all frame types and sizes.
2. Handshake tests validate both success and all rejection scenarios.
3. Connection lifecycle tests verify deterministic state transitions and resource cleanup.
4. API tests confirm thread-safe concurrent send/receive without deadlocks.
5. Reconnection tests validate backoff computation, event ordering, and stress resilience.
6. Unity tests verify main-thread callback delivery and lifecycle binding correctness.
7. All tests run in Unity Test Runner without requiring an external WebSocket server (use `MockTransport` patterns or in-process echo server).
8. No per-test heap allocations beyond setup/teardown (verify with `GC.GetTotalMemory` in hot-path tests).
