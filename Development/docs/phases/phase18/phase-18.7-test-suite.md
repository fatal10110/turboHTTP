# Phase 18.7: Test Suite

**Depends on:** Phases 18.1–18.6 (all WebSocket implementation)
**Assembly:** `TurboHTTP.Tests.Runtime`
**Files:** 7 new, 2 modified

---

## Step 0: Update Assembly Definitions for WebSocket Tests

**Files:**
- `Tests/Runtime/TurboHTTP.Tests.Runtime.asmdef` (modify)
- `Runtime/TurboHTTP.Complete.asmdef` (modify)

Required behavior:

1. Add test assembly references for WebSocket modules:
   - `TurboHTTP.WebSocket`
   - `TurboHTTP.WebSocket.Transport`
   - `TurboHTTP.Unity.WebSocket`
2. Update `TurboHTTP.Complete.asmdef` to include the new WebSocket assemblies so package consumers get consistent aggregated module coverage.
3. Keep test assembly reference mode explicit (`overrideReferences: true`) and ensure no existing references are removed.

Implementation constraints:

1. Preserve current `UNITY_INCLUDE_TESTS` define constraint in `TurboHTTP.Tests.Runtime.asmdef`.
2. Do not modify platform include/exclude settings for existing assemblies unless required by compilation errors.

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
   - Chunked masking produces identical output to full-buffer masking.
   - Batch mask key generation produces unique keys.
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
   - `MaxMessageSize` enforced during reassembly (reject before allocation).
   - `MaxFragmentCount` enforced during reassembly.
5. Protocol violation tests:
   - **Masked server-to-client frame is rejected** as protocol error (close code 1002).
   - **Reserved opcodes (0x3-0x7, 0xB-0xF) are rejected** as protocol errors.
   - **64-bit payload length with bit 63 set is rejected** (overflow protection).
   - RSV bit validation (reject non-zero RSV when no extensions negotiated).
   - Extended payload length boundary cases (125→126 and 65535→65536).
   - Close codes 1005 and 1006 rejected in send path.
6. Edge cases:
   - Zero-length payload for all frame types.
   - Maximum payload size enforcement (reject frames exceeding limit).
   - `ReadExactAsync` handles byte-at-a-time delivery without corruption.

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
3. Token-based header validation tests:
   - `Connection: keep-alive, Upgrade` is accepted.
   - `Upgrade: WebSocket` (different casing) is accepted.
   - `Connection: close` (no Upgrade token) is rejected.
4. URI handling tests:
   - URI with path, query string, and port.
   - Default port handling (80 for ws, 443 for wss).
   - **URI fragment stripped** from Request-URI (e.g., `ws://host/path#frag` sends `/path`).
   - Non-default port included in Host header (e.g., `Host: example.com:8080`).
   - Default port omitted from Host header.
5. Security tests:
   - Maximum header size enforcement (reject response > 8KB).
   - CRLF injection prevention in custom headers.
   - WSS connections pass empty ALPN to TLS provider.

---

## Step 3: Connection Lifecycle Tests

**File:** `Tests/Runtime/WebSocket/WebSocketConnectionTests.cs` (new)

Required behavior:

1. State machine tests:
   - Verify state transitions: None → Connecting → Open → Closing → Closed.
   - Verify invalid transitions throw `InvalidOperationException`.
   - Verify state is consistent under concurrent access.
   - Verify `TryTransitionState` CAS behavior with competing threads.
2. Clean close handshake tests:
   - Client-initiated close: send close → receive close → Closed.
   - Server-initiated close: receive close → send close response (NormalClosure, not echo) → Closed.
   - Close handshake timeout: server doesn't respond → abort after timeout.
   - Close with status code and reason preserved through the exchange.
   - **Close codes 1005 and 1006 rejected** by `CloseAsync`.
   - **Simultaneous client/server close** results in exactly one close frame sent.
3. Keep-alive tests:
   - Ping sent at configured interval (25s default).
   - Pong response received resets timeout.
   - Pong timeout triggers connection close.
   - Activity resets ping timer.
4. Error handling tests:
   - Network error during receive → transition to Closed with error.
   - `IOException` and `SocketException` mapped to `WebSocketException`.
   - Protocol violation (invalid frame) → close with ProtocolError code.
   - **Invalid UTF-8 in text frame** → close with InvalidPayload code (1007).
   - Disposal during active operations → clean resource release with correct disposal ordering.
5. Backpressure tests:
   - Receive `Channel` fills to capacity → receive loop blocks → verify no messages lost.
   - Control frames still processed when data channel is full.

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
5. Message type tests:
   - `WebSocketMessage.Text` returns eagerly-decoded string.
   - `WebSocketMessage.Data` returns UTF-8 bytes for text messages.
   - `WebSocketMessage.Dispose()` returns pooled buffer.
   - `Dispose()` / `DisposeAsync()` behavior matches contract.

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
   - Policy immutability — properties cannot be changed after construction.
2. Resilient client tests:
   - Auto-reconnect on unexpected disconnection.
   - Event ordering: `OnError` → `OnReconnecting` → `OnReconnected`.
   - Max retries exhausted → `OnClosed` fires.
   - Normal close does not trigger reconnection.
   - Disposal during reconnection backoff cancels cleanly.
   - **No dual receive loops** — verify old receive loop is cancelled before new one starts.
   - Old `WebSocketClient` fully disposed before new one created.
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
   - Dispatcher saturation logs dropped messages (not blocks).
2. Lifecycle binding tests:
   - `OnDestroy` disconnects WebSocket.
   - Destroyed owner cancels pending operations.
   - **Domain reload cleans up connections** (no leaked background tasks).
   - Application pause disconnects by default.
   - Application resume reconnects when AutoReconnect enabled.
3. `UnityWebSocketClient` component tests:
   - `AutoConnect` connects on `Start`.
   - Inspector events fire correctly.
   - Fire-and-forget `Send()` does not throw.
   - Multiple components on same GameObject work independently.
4. Coroutine wrapper tests:
   - Receive loop coroutine yields messages correctly.
   - Coroutine stops on disconnect.
   - Coroutine cancellation on owner destroy.
5. Message copy tests:
   - Dispatched messages use regular byte arrays (not pooled buffers).
   - Original pooled buffer is returned after bridge copy.

---

## Step 7: In-Process Echo Server and IL2CPP Platform Tests

**File:** `Tests/Runtime/WebSocket/WebSocketTestServer.cs` (new)

Required behavior:

1. **In-process WebSocket echo server** for deterministic testing:
   - Minimal implementation using the same framing layer (WebSocketFrameReader/Writer).
   - Loopback TCP listener on random port.
   - Echo mode: send back every received message.
   - Supports configurable behaviors: delayed response, close-after-N-messages, protocol violation injection.
   - No external dependencies — runs entirely within Unity Test Runner process.
2. **IL2CPP platform test scenarios** (documented, run on device):
   - SHA-1 availability and correct output.
   - `RandomNumberGenerator` produces non-zero bytes.
   - TLS handshake for WSS with BouncyCastle fallback.
   - Threading: receive loop runs on background thread without IL2CPP crashes.
   - Domain reload: no leaked connections or background tasks.
   - `link.xml` preservation: SHA1, RandomNumberGenerator not stripped.

---

## Verification Criteria

1. All framing tests pass — encode/decode round-trips for all frame types and sizes.
2. Handshake tests validate both success and all rejection scenarios.
3. Connection lifecycle tests verify deterministic state transitions and resource cleanup.
4. API tests confirm thread-safe concurrent send/receive without deadlocks.
5. Reconnection tests validate backoff computation, event ordering, and stress resilience.
6. Unity tests verify main-thread callback delivery and lifecycle binding correctness.
7. All tests run in Unity Test Runner using the in-process echo server (no external WebSocket server required).
8. No per-frame heap allocations in the framing hot path (verify with `GC.GetTotalMemory` in dedicated benchmark tests). Note: text message tests will allocate strings (UTF-8 decode) — this is expected and unavoidable.
9. IL2CPP platform tests pass on iOS and Android physical devices.
10. Assembly definition updates compile in Unity with explicit test references and no missing-assembly errors.
