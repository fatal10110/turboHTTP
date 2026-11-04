# 2026-10 WebSocket receive backpressure and send cancellation (#78, #79)

## What

- **#78**: event-only consumers stalled after about 100 messages. The client listener awaited the
  bounded pull queue before it raised `OnMessage`, so the connection queue filled, the read loop
  blocked, pongs went unread and the keep-alive aborted with 1006. The Resilient client then
  reconnected forever.
  - `WebSocketClient` / `ResilientWebSocketClient`: with an `OnMessage` handler subscribed, the
    listener/pump never waits. The message goes into the pull queue with drop-oldest, the dropped
    message is disposed, and the event is raised with a detached copy. With no handler, the enqueue
    is lossless and awaited, as before.
  - `WebSocketConnection` receive loop: `TryEnqueue` fast path. When the queue is full, the loop
    "parks" (`_receiveParked`, `_receiveUnparkedTimestamp`). While it is parked, the keep-alive
    sends no pings, and a pong timeout that overlaps a parked period does not abort the connection.
  - `BoundedAsyncQueue`: new internal methods `TryEnqueue` and `EnqueueDropOldest`.
  - The mixed-mode semantics are documented on `IWebSocketClient.OnMessage` and in
    `Documentation~/Modules/WebSocket.md`.
- **#79**: cancelling a send mid-message left a partial frame or message on the wire with the
  connection still Open. `SendLockedAsync` now uses the caller token only until the first byte.
  - A token already cancelled after the lock is acquired throws a plain OCE, and the connection
    stays Open.
  - From the first write on, an OCE (or any non-argument, non-WebSocketException failure) calls
    `FinalizeClose(1006, WebSocketException SendFailed)`.
  - Application sends throw that `WebSocketException`. Control-frame sends (close handshake,
    keep-alive ping) rethrow the OCE, so the existing CloseAsync timeout handling and keep-alive
    shutdown handling stay unchanged.

## Files

- Runtime/WebSocket/AsyncBoundedQueue.cs
- Runtime/WebSocket/WebSocketClient.cs
- Runtime/WebSocket/ResilientWebSocketClient.cs
- Runtime/WebSocket/WebSocketConnection.cs
- Runtime/WebSocket/WebSocketConnection.SendReceive.cs
- Runtime/WebSocket/IWebSocketClient.cs (XML docs)
- Documentation~/Modules/WebSocket.md
- Tests/Runtime/WebSocket/InMemoryWebSocketPeer.cs (new: in-memory strict peer and the `CancelAfterWriteStream` wrapper)
- Tests/Runtime/WebSocket/WebSocketReceiveBackpressureTests.cs (new)
- Tests/Runtime/WebSocket/WebSocketSendCancellationTests.cs (new)
- Tests/Runtime/Unity/UnityWebSocketBackpressureTests.cs (new)

## Decisions

- **#78 mechanism: drop-oldest while handlers are subscribed, not dispatch-only.** Dispatch-only
  ("do not queue when nobody pulls") loses messages for the common pattern of subscribing to
  `OnMessage` and then calling `ReceiveAsync`, where the reply can arrive before the pull call.
  `WebSocketClientTests.ConnectSendReceiveClose_AndEvents_Work` does exactly that. A sticky "pull
  mode" flag also stalls `OnMessage` again as soon as an occasional puller stops pulling.
  - Drop-oldest keeps OnMessage lossless and bounds memory to `ReceiveQueueCapacity` messages.
  - Pure-pull consumers stay lossless.
- **The detached copy is now taken before the original is published to the pull queue.** The old
  code copied after the enqueue, so a concurrent puller could dispose the original during the copy.
- **Pure-pull backpressure keeps TCP backpressure**, because the read loop cannot reach a control
  frame without reading the data frames before it. Instead, the keep-alive stops treating that
  local backpressure as a dead peer. The connection-level "Pong/close processed while the data queue
  is full" criterion holds for every consumer with a handler. For a pure puller, control frames are
  processed in order once it drains the queue (RFC 6455 keeps frame order).
- **#79: abort, not "finish with the lifecycle token".** The issue's acceptance criterion accepts
  either. Abort honours the caller's cancel promptly, for example when a Unity component is
  disabled. A cancel before the first byte (while waiting for the send lock, or with a token
  already cancelled) never aborts.
  - The error surfaced is `WebSocketException(SendFailed)` with the OCE as its inner exception
    (CLAUDE.md error model).
  - `ArgumentException` (UTF-8 validation before any byte) and the existing WebSocketException
    kinds keep their existing handling.

## Deferred

- none. UnityWebSocketClient has no transport seam, so its coverage goes through
  `UnityWebSocketBridge` over a real `WebSocketClient`, the exact delivery path the component uses.

## Verification

- `'./Tools~/ci/check.sh'`: see PR "Local gates".
- Unity cannot run on this host, so the Unity tests run on CI.
- Off-Unity NUnit run: a scratch net8.0 project compiled Runtime/Core and Runtime/WebSocket (with a
  minimal UnityEngine stub for PlatformInfo/PlatformConfig), all Tests/Runtime/WebSocket tests
  except the proxy tests, and the new tests.
  - On this branch, the 11 new tests pass.
  - On origin/main, 9 of the 11 fail. The guard tests (pull-only order, cancel before the first
    byte) pass on both.
  - The existing WebSocket tests show no regression: the same 3 tests fail on both trees in that
    harness (`ServerInitiatedClose_RaisesOnClosed_AndReceiveFails`,
    `ReceiveAllAsync_ReturnsFalse_WhenReconnectExhausted`, and
    `JsonSerializer_RoundTrip_WorksForComplexObject`, which needs TurboHTTP.JSON).
- Not run: Unity EditMode/PlayMode locally, IL2CPP, device.
