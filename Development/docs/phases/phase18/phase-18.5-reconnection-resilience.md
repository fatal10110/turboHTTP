# Phase 18.5: Reconnection & Resilience

**Depends on:** Phase 18.3 (connection lifecycle), Phase 18.4 (client API)
**Assembly:** `TurboHTTP.WebSocket`
**Files:** 2 new, 1 modified

---

## Step 1: Implement Reconnection Policy

**File:** `Runtime/WebSocket/WebSocketReconnectPolicy.cs` (new)

Required behavior:

1. Define `WebSocketReconnectPolicy` with configurable parameters:
   - `MaxRetries` (int, default 5; 0 = no reconnection; -1 = unlimited).
   - `InitialDelay` (TimeSpan, default 1s).
   - `MaxDelay` (TimeSpan, default 30s).
   - `BackoffMultiplier` (double, default 2.0).
   - `JitterFactor` (double, 0.0-1.0, default 0.1) — random jitter as fraction of current delay to prevent thundering herd.
   - `ReconnectOnCloseCode` predicate (`Func<WebSocketCloseCode, bool>`) — determines which server close codes trigger reconnection (default: reconnect on `GoingAway`, `AbnormalClosure`, `InternalServerError`; do not reconnect on `NormalClosure`, `ProtocolError`, `PolicyViolation`).
2. Expose `ComputeDelay(int attempt)` method returning the backoff delay for a given retry attempt (with jitter applied).
3. Expose `ShouldReconnect(int attempt, WebSocketCloseCode? code)` method returning whether reconnection should be attempted.
4. Provide static factory methods:
   - `WebSocketReconnectPolicy.None` — no reconnection.
   - `WebSocketReconnectPolicy.Default` — default policy (5 retries, exponential backoff).
   - `WebSocketReconnectPolicy.Infinite` — unlimited retries with exponential backoff.

Implementation constraints:

1. **Jitter RNG:** thread-safe random must be used. Seed a `System.Random` instance once at policy construction from a crypto-seeded value (`RandomNumberGenerator` → 4 bytes → seed int). **Wrap access to `Random.NextDouble()` in a `lock (rngLock)`** since instances may be shared concurrently across multiple reconnecting clients (e.g., global default policy).
2. Delay computation must cap at `MaxDelay` regardless of multiplier accumulation.
3. Policy must be immutable after construction — use `readonly` fields and no setters. Consider `record` type (C# 9, Unity 2021.2+ supports it).
4. Predicate-based close code filtering must have sensible defaults that prevent reconnection loops on permanent rejection.
5. Validation at construction: `InitialDelay > TimeSpan.Zero`, `BackoffMultiplier >= 1.0`, `JitterFactor` in [0.0, 1.0].

---

## Step 2: Implement Resilient WebSocket Client

**File:** `Runtime/WebSocket/ResilientWebSocketClient.cs` (new)

Required behavior:

1. Wrap `WebSocketClient` with automatic reconnection on unexpected disconnection.
2. Monitor the underlying connection state — when `Closed` unexpectedly, evaluate reconnect policy.
3. Reconnection flow:
   - Fire `OnReconnecting` event with attempt number and delay.
   - Wait for the computed backoff delay (with jitter).
   - **Cancel the old connection's receive loop** and fully dispose the old `WebSocketClient` before creating a new one. Prevent two receive loops running simultaneously during the transition.
   - Create a new `WebSocketClient` instance and attempt `ConnectAsync` with the original URI and options.
   - On success: fire `OnReconnected` event, reset retry counter, start new receive loop.
   - On failure: increment attempt counter, check `ShouldReconnect`, and either retry or give up.
4. When reconnection is exhausted (max retries exceeded): fire `OnClosed` event with final error details.
5. Event surface:
   - `OnConnected` — initial connection established.
   - `OnMessage` — message received (same as `WebSocketClient`).
   - `OnReconnecting(int attempt, TimeSpan delay)` — reconnection attempt starting.
   - `OnReconnected` — reconnection succeeded.
   - `OnError(WebSocketException)` — error occurred (may be followed by reconnection).
   - `OnClosed(WebSocketCloseCode, string reason)` — permanently closed (no more reconnection attempts).
6. Send operations during reconnection:
   - `SendAsync` throws `InvalidOperationException` while not connected (default behavior).
   - Optional: configurable send buffer that queues messages during reconnection. **Bounded by byte size** (default disabled; when enabled, default max 1MB). Messages are dropped (not blocked) when buffer is full. Queued messages are replayed after successful reconnection in FIFO order.
7. Receive operations during reconnection:
   - `ReceiveAsync` blocks until reconnection succeeds or is exhausted (then throws).

Implementation constraints:

1. Reconnection must not block the receive loop thread — use async/await throughout.
2. Reconnection delay must be cancellable via `CancellationToken` (e.g., client disposal during reconnection). `ResilientWebSocketClient.Dispose()` must cancel the backoff `CancellationTokenSource`, then dispose the current underlying `WebSocketClient`.
3. The resilient client must present the same `IWebSocketClient` interface as the base client.
4. Old `WebSocketClient` instances must be fully disposed (receive loop cancelled and awaited) before creating new ones during reconnection — no two receive loops running simultaneously.
5. Reconnection must preserve the original connection options (sub-protocols, headers, timeouts).
6. Thread safety: event callbacks use C# `event` keyword with null-safe snapshot invoke pattern. Subscribe/unsubscribe is safe from any thread.
7. Disposal must be idempotent and safe from any state (connecting, connected, reconnecting, closed).

---

## Step 3: Integrate Reconnection into Client Options

**Files:**
- `Runtime/WebSocket/WebSocketConnectionOptions.cs` (modify)

Required behavior:

1. Add `ReconnectPolicy` property to `WebSocketConnectionOptions` (default: `WebSocketReconnectPolicy.None`).
2. When a non-none policy is configured, `WebSocketClient` factory/builder returns `ResilientWebSocketClient` wrapping the base client.
3. Add convenience builder method: `WithReconnection(WebSocketReconnectPolicy policy)`.
4. Document that reconnection is opt-in — default behavior is no automatic reconnection.

Implementation constraints:

1. Policy configuration must be validated at construction time (e.g., `InitialDelay > TimeSpan.Zero`, `BackoffMultiplier >= 1.0`).
2. `WebSocketConnectionOptions` remains a simple POCO — no behavior, just configuration.

---

## Verification Criteria

1. Reconnection policy computes correct backoff delays with jitter for each attempt.
2. `ShouldReconnect` correctly filters by attempt count and close code.
3. Resilient client reconnects automatically after unexpected disconnection.
4. Reconnection events fire in correct order: `OnError` → `OnReconnecting` → `OnReconnected`.
5. Reconnection stops after max retries and fires `OnClosed`.
6. Normal close (`CloseAsync` with `NormalClosure`) does not trigger reconnection.
7. Disposal during reconnection backoff cancels cleanly and disposes underlying client.
8. Reconnection preserves original connection options across reconnect cycles.
9. Send buffer (when enabled with byte size limit) replays queued messages after successful reconnection in FIFO order.
10. No two receive loops run simultaneously during reconnection transition.
11. Old `WebSocketClient` is fully disposed before new one is created.
