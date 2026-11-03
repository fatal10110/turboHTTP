# Phase 18a.2: `IAsyncEnumerable` Streaming Receive

**Depends on:** Phase 18, Spike 3
**Assembly:** `TurboHTTP.WebSocket`
**Files:** 1 new, 3 modified
**Estimated Effort:** 2-3 days
**Gated by:** Spike 3

---

## Motivation

Phase 18.4 reserved the method name `ReceiveAllAsync` for `IAsyncEnumerable<WebSocketMessage>` but did not implement it. The `await foreach` pattern is the idiomatic C# 8+ way to consume infinite message streams and dramatically reduces consumer boilerplate.

---

## Step 1: Implement `ReceiveAllAsync`

**File:** `Runtime/WebSocket/WebSocketAsyncEnumerable.cs` (new)

Required behavior:

1. Implement `IAsyncEnumerable<WebSocketMessage>` adapter over the existing `BoundedAsyncQueue`.
2. `GetAsyncEnumerator(CancellationToken)` returns `IAsyncEnumerator<WebSocketMessage>`.
3. `MoveNextAsync()` calls the connection's `ReceiveAsync` and returns `true`/`false`.
4. Enumeration ends when the connection transitions to `Closed` (returns `false`, no exception).
5. `Current` returns the most recent `WebSocketMessage` (caller owns disposal).
6. Cancellation via the `CancellationToken` passed to `GetAsyncEnumerator`.

Implementation constraints:

1. `IAsyncEnumerable<T>` is natively available in .NET Standard 2.1 — **no NuGet dependency** (no `Microsoft.Bcl.AsyncInterfaces` needed). Unity 2021.3 LTS with .NET Standard 2.1 profile includes it. IL2CPP stripping risks are validated by Spike 3; if stripping occurs, `link.xml` entries will be added.
2. Caller must dispose each `WebSocketMessage` yielded by the enumerator.
3. Only one active enumerator per client — concurrent enumeration throws `InvalidOperationException`. Track via `Interlocked.CompareExchange` on an `int` flag. `DisposeAsync()` on the enumerator resets the flag so a new enumerator can be created.

---

## Step 2: Add to `IWebSocketClient` and Implementations

**Files:** `Runtime/WebSocket/IWebSocketClient.cs` (modify), `Runtime/WebSocket/WebSocketClient.cs` (modify), `Runtime/WebSocket/ResilientWebSocketClient.cs` (modify)

Required behavior:

1. Add `ReceiveAllAsync(CancellationToken ct = default)` returning `IAsyncEnumerable<WebSocketMessage>` to `IWebSocketClient`.
2. Implement in `WebSocketClient` using the adapter from Step 1.
3. Implement in `ResilientWebSocketClient` — during reconnection, the enumerator **blocks** on `MoveNextAsync` until reconnection succeeds (then resumes yielding messages) or reconnection is exhausted (then returns `false`). Messages that were queued in the send buffer during reconnection are replayed on the new connection; the receive enumerator starts fresh (no buffering of inbound messages across reconnection boundaries — this is the safe default).

---

## Verification Criteria

1. `await foreach` consumes messages correctly.
2. Enumeration ends on connection close (returns `false`, no exception).
3. Cancellation of `IAsyncEnumerable` via `CancellationToken`.
4. Resilient client: enumeration blocks during reconnection, resumes after reconnect, returns `false` when exhausted.
5. Concurrent enumeration rejection (`InvalidOperationException`).
6. Enumerator `DisposeAsync` resets the tracking flag, allowing new enumerator creation.
7. No `Microsoft.Bcl.AsyncInterfaces` dependency in compilation output.
