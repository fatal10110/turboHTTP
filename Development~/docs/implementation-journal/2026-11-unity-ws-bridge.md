# 2026-11 Unity WebSocket bridge: typed drops, lifecycle delivery, connection ownership, coroutine handle

Issues: #90 (drop detection, lifecycle events under flood), #91 (pause/resume race), #177 (coroutine
cleanup, OnDisable/OnDestroy CTS race). Milestone v1.1.

## What changed

- `MainThreadDispatchRejectedException` (derives from `InvalidOperationException`) with
  `MainThreadDispatchRejectionReason` (`QueueFull`, `WaiterLimitReached`, `WaitTimedOut`, `DroppedOldest`).
  `MainThreadWorkQueue` throws or fails work with it for every user-queue backpressure loss. Work evicted
  by `DropOldest` (and by a capacity reduction) now faults with it instead of an `OperationCanceledException`
  that `ExecuteQueuedAsync` turned into a plain cancellation, so a drop is no longer indistinguishable
  from the caller's own cancellation. The `Wait` timeout keeps the `TimeoutException` as inner exception.
- `UnityWebSocketBridge`: drops are recognized by type (the old `"dropped"` text match never matched:
  Reject says "rejected" and DropOldest surfaced as canceled). A dropped callback disposes its message,
  raises `OnMessageDropped`, and at most one warning per 10 s (with the drop count since the previous
  one) is logged instead of a `Debug.LogException` per message. Review round 1: the first version reset
  the warning whenever a callback ran, which logs once per frame under a sustained flood. `OnConnected`/`OnClosed` take the user lane
  (so they stay behind queued messages) and are re-dispatched on the control lane when the user lane
  rejects or evicts them; a per-item started flag keeps them at most once and tells a drop from a fault
  thrown by the callback. `OnError` uses the same fallback, bounded to 32 pending per bridge.
- `UnityWebSocketBridge.ReceiveAsCoroutine` returns `UnityWebSocketReceiveCoroutine` (IEnumerator +
  IDisposable). Unity does not dispose a stopped coroutine, so the handle owns the lifecycle binding and
  the pending receive; `Dispose` cancels the receive, releases the binding and disposes a message that
  completes after the stop. A throwing `onMessage` disposes the handle before rethrowing (Unity stops
  that coroutine without disposing it either). The `receiveTask.Result` analyzer-baseline entry is gone with the iterator.
- `UnityWebSocketClient`: one `_stateGate` lock owns the connection (`Connection` = client + bridge +
  once-only disconnect flag), the connect/disconnect tasks, a generation counter and the lifecycle
  sources. `DisconnectAsync` closes only the connection it captured and clears the field only if it is
  still that one; concurrent disconnects share one close. `ConnectAsync` waits for a pending disconnect
  (and for a superseded connect) before creating its client; a disconnect or destroy during a connect
  bumps the generation and cancels a per-attempt source linked into the handshake token (review round 1),
  so the handshake ends at once and a resume connect is not held behind it; a handshake that completes
  anyway is closed and the connect reports cancellation.
  `OnDisconnectedEvent` is raised once per connection: by the client's close event, or by a fallback
  queued after it when the close failed or aborted (code 1006). `OnDestroy` takes the sources under the
  lock, cancels, aborts, then disposes; linked tokens are created only under the lock and a destroyed
  component hands out an already canceled one, so no reader can touch a disposed source. A destroy that
  cancels an in-flight close is not logged as an error, and no UnityEvent is raised after `OnDestroy`.
  `OnDisable`/pause also disconnect when only a connect is in flight. Internal test seams:
  `ClientFactory`, `ConfiguredUri` (`InternalsVisibleTo("TurboHTTP.Tests.Runtime")` added for the assembly).

## Files

Runtime/Unity/MainThreadDispatchRejectedException.cs (new), Runtime/Unity/MainThreadWorkQueue.cs,
Runtime/Unity.WebSocket/{UnityWebSocketBridge.cs, UnityWebSocketClient.cs, UnityWebSocketReceiveCoroutine.cs (new),
AssemblyInfo.cs (new)}, Tools~/ci/analyzer-baseline.txt, Documentation~/Modules/WebSocket.md,
Tests/Runtime/Unity/{UnityWebSocketBridgeDispatchTests.cs, UnityWebSocketClientLifecycleTests.cs,
UnityWebSocketReceiveCoroutineTests.cs, ScriptedWebSocketClient.cs, WebSocketCoroutineHost.cs} (new).

## Decisions

- Typed signal: one exception type for every user-queue backpressure loss, derived from
  `InvalidOperationException` so existing `catch (InvalidOperationException)` (MainThreadDispatcherV2Tests)
  stays valid. Evicted work faults rather than cancels: an OCE subclass would not survive
  `TaskCompletionSource.TrySetCanceled`.
- Lifecycle ordering: user lane first, control lane only on backpressure, instead of always the control
  lane, so `OnClosed` normally arrives after the messages that preceded it. Under saturation it may
  overtake queued messages (documented).
- "Exactly once per connection": the flag lives on the connection; destroy and quit mark it so nothing is
  raised on a destroyed component. A resilient client raises `OnClosed` only on its terminal close, so one
  logical connection per `Connect`.
- Destroy aborts (no close frame), as before; "closes the socket" in #177 is met by the abort.
- The coroutine handle is a source-compatible return-type change (`StartCoroutine(bridge.ReceiveAsCoroutine(...))`
  still compiles); it has no callers in Runtime, Samples~ or Documentation~.

## Verification

- `./Tools~/ci/check.sh`: pass (invariants, format, shadow build with analyzers; the shadow build does
  not compile Tests/).
- Unity 2021.3.45f2 PlayMode, scoped `--filter 'UnityWebSocket|MainThreadDispatcher'`: 23/23 passed
  (the 12 new tests plus the existing UnityWebSocket*/MainThreadDispatcher* tests). A first run caught that a
  `TaskCompletionSource` start gate does not inline its continuation on the main thread (Unity's
  SynchronizationContext disables inlining), so connect/disconnect now start synchronously outside the lock
  and publish a proxy task first.
- Review round 1: added `ThrowingOnMessage_EndsAndDisposesTheHandle`,
  `Dispose_WithAReceivedUndeliveredMessage_DisposesIt_WithoutDelivery`,
  `SustainedFlood_DropsInTwoDrainCycles_LogOneWarning`,
  `DisconnectDuringConnect_CancelsTheHandshake_AndAResumeConnectIsNotDelayed` (the old superseded-connect
  test is kept as `DisconnectDuringConnect_HandshakeThatCompletesAnyway_IsClosed`).
- Not run: full Unity suites (host editor, periodic), IL2CPP player builds, devices (pause/resume on
  iOS/Android is device behavior: `needs-device-validation`).
