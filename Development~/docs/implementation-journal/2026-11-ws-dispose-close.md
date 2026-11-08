# 2026-11 ws-dispose-close (#301)

Wave run w1110, lane ws-dispose-close. One PR in `TurboHTTP.WebSocket` plus tests. No new asmdef
references, no public API change.

## What

- `WebSocketConnection.DisposeAsync` now performs the closing handshake it was written for (RFC 6455
  section 7.1.2): on an Open or Closing connection it sends a Close frame with 1000 ("Disposing
  connection.") and waits, bounded by a 1 s budget, for the peer's Close and the server's FIN
  (section 7.1.1).
- Before, it set `_disposed` and then called the public `CloseAsync`, whose `ThrowIfDisposed()` threw.
  The `catch` aborted, so the transport dropped without a Close frame and the peer saw 1006.
- `CloseAsync` is split:
  - The public wrapper keeps `ThrowIfDisposed`, the close-code validation and the None/Connecting
    `InvalidOperationException`.
  - The private `CloseCoreAsync` holds the handshake. It does not read `_disposed`, and it returns on
    Closed, None or Connecting.
- `DisposeAsync` calls the core. The disposed flag is still set first, so concurrent sends fail with
  `ObjectDisposedException` while the Close goes out.

## Files

- `Runtime/WebSocket/WebSocketConnection.SendReceive.cs`: `CloseAsync` wrapper, new `CloseCoreAsync`.
- `Runtime/WebSocket/WebSocketConnection.Lifecycle.cs`: `DisposeAsync` calls `CloseCoreAsync` with
  `DisposeCloseBudget` (1 s).
- `Tests/Runtime/WebSocket/InMemoryWebSocketPeer.cs`: `ReplyToClose` (default true). When false, the
  peer records the client's Close and keeps reading, but never replies or closes the transport.
- `Tests/Runtime/WebSocket/WebSocketDisposeCloseTests.cs` (new):
  - `DisposeAsync_Open_SendsNormalClosureFrame`: the peer receives Close 1000. The connection ends
    Closed with NormalClosure and no terminal error.
  - `DisposeAsync_SilentPeer_ReturnsWithinBudgetAndCloses`: `CloseHandshakeTimeout` is 60 s, so only
    the dispose budget can end the wait inside the 10 s guard. The Close was sent and the connection
    ends Closed.
  - `DisposeAsync_WhileCloseInFlight_CompletesBothAndCloses`: a user `CloseAsync` is waiting in
    Closing. Dispose sends no second Close frame, ends after its budget and releases the user's close.

## Decisions

- The issue's design (internal close core) was chosen over a second "disposing" flag. The core is the
  smallest change and keeps every other `ThrowIfDisposed` caller as it is.
- The budget stays 1 s. `CloseHandshakeTimeout` still applies inside the core (the wait for the peer's
  Close is linked to it and the receive loop bounds the FIN wait with it), so the smaller of the two
  ends the wait.
- When the budget expires, the canceled close aborts (#86): Closed, `AbnormalClosure`, terminal error
  "close was canceled". The same holds when the peer answered but has not sent its FIN within 1 s.
  This matches a canceled `CloseAsync`, so the change adds no new terminal state.
- `Dispose()` (sync) and `Abort()` are unchanged: they still abort without a Close frame. A sync
  dispose cannot wait for the handshake.
- `TrackAbandonedReceiveLoop` (#375) is untouched. It still runs from `FinalizeClose`, before the
  stream is disposed on the abort path, and from the close-on-timeout registration.

## Side effects

- Every caller that disposes an Open connection now waits for the peer's Close and FIN, at most 1 s.
  `WebSocketClient.DisposeAsync`, `ResilientWebSocketClient` and `UnityWebSocketClient` dispose through
  it. A peer that answers (the default `InMemoryWebSocketPeer`) costs one round trip.

## Verification

- `'./Tools~/ci/check.sh'`: pass.
- Unity scoped run: see the PR body ("Local gates").
- Not run: the full Unity EditMode/PlayMode suites (host editor, periodic), IL2CPP players, devices.
