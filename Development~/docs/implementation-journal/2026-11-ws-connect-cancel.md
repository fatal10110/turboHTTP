# 2026-11 ws-connect-cancel (#226, #208)

Wave run w1101, lane ws-connect-cancel. Two commits, one PR.

## What

- **#226, WebSocket connect ignores mid-read cancellation on Mono/IL2CPP.** Same pattern as #162
  (`RawSocketTransport.RegisterDisposeOnCancel`, journal `2026-10-connect-tunnel.md`).
  - `RawSocketWebSocketTransport.ConnectAsync`: once the socket is connected, the token is
    registered to dispose the `NetworkStream` and the socket. The registration covers the proxy
    CONNECT exchange and the TLS handshake (moved into `EstablishStreamAsync`). It is removed before
    the stream is returned. If the token fired in that window, the stream is disposed and the call
    throws `OperationCanceledException`.
  - Any failure while the token is cancelled (`ObjectDisposedException`, `IOException`, TLS errors,
    a `WebSocketException` from the tunnel reader that wraps one) becomes
    `OperationCanceledException(ct)`. The CONNECT catch no longer wraps `OperationCanceledException`
    or a cancellation-caused fault as `ProxyTunnelFailed`. The socket-connect catch no longer wraps
    it as `ProxyConnectionFailed`.
  - `WebSocketConnection.ConnectAsync`: the handshake token disposes the returned stream while the
    upgrade request is written and the 101 is read and validated. The registration is removed
    before the connection goes Open, then `handshakeToken.ThrowIfCancellationRequested()` runs, so
    the connection never opens on a stream the callback disposed.
  - Caller cancellation: any failure while the caller token is cancelled is rethrown as
    `OperationCanceledException`, including a disposed-stream fault. Before, only an
    `OperationCanceledException` was.
  - `HandshakeTimeout` expiry (handshake token cancelled, caller token not) is still
    `WebSocketException(ConnectionClosed)`. The message is now "WebSocket opening handshake timed
    out after N ms." and the inner exception is the cancellation. Before, a proxy timeout surfaced
    as `ProxyTunnelFailed`/`ProxyConnectionFailed`.
  - `WebSocketHandshakeValidator.ValidateAsync`: a read failure while the token is cancelled is
    `OperationCanceledException`, no longer a failed handshake result (status 0 → `HandshakeFailed`).
- **#208, reconnecting the same `WebSocketClient`.**
  - `RunReceiveListenerAsync(connection, queue, ct)`: the listener enqueues to, publishes to and
    completes only the queue created with its connection, never the `_messageQueue` field.
  - `ConnectAsync` takes the previous `_listenerCts`/`_listenerTask` under `_sync`. It cancels the
    previous listener outside the lock, before the new connect. It disposes the old CTS only after
    observing the old listener task. The old listener is awaited after the new connection is
    connected and before the new listener starts and `OnConnected` fires. That keeps the old
    connection's last `OnMessage` ahead of the new connection's events. The failure path observes
    it too.
  - `HandleConnectionStateChanged` completes the queue and raises `OnClosed` only when the
    connection raising the event is still `_connection` (checked under `_sync`). A late event from a
    replaced connection is ignored, so it can no longer reset the new connection's `OnClosed` latch.
  - `CleanupAfterFailedConnectAsync` releases only its own attempt's queue, listener CTS and (when
    still current) connection and transport. A reconnect started from `OnClosed` of a failed
    attempt is not torn down.
  - `_messageQueue` is written with `Volatile.Write` under `_sync` and read with `Volatile.Read`
    outside it. `StopListenerAsync` takes the CTS/task under `_sync`. Cancels tolerate a CTS that a
    concurrent path already disposed (`CancelQuietly`).

## Files

- `Runtime/WebSocket.Transport/RawSocketWebSocketTransport.cs`
- `Runtime/WebSocket/WebSocketConnection.cs`, `Runtime/WebSocket/WebSocketHandshakeValidator.cs`
- `Runtime/WebSocket/WebSocketClient.cs`
- Tests (new): `Tests/Runtime/WebSocket/WebSocketConnectCancellationTests.cs`,
  `Tests/Runtime/WebSocket/WebSocketClientReconnectTests.cs`
- Test (changed): `Tests/Runtime/WebSocket/WebSocketProxyTests.cs`
  (`ProxyConnectionFailure_ThrowsProxyConnectionFailed` gets a 15 s handshake timeout, see Decisions)

## Decisions

- **Failing-before-fix tests for #226.** Unity runs Mono, and the loopback silent-peer tests
  (`TcpListener` that accepts and never writes) hang there on the old code. That reproduces the
  defect directly. Two extra tests use a transport whose stream ignores the read token, as a
  pending Mono NetworkStream read does. They prove that `WebSocketConnection` disposes the stream,
  whatever the platform.
- **Timeout vs. caller cancellation.** The caller sees `OperationCanceledException` when it
  cancelled. It sees `WebSocketException(ConnectionClosed)` "timed out" wrapping the cancellation
  when `HandshakeTimeout` fired. No new error code; that is the code a handshake timeout already
  had without a proxy.
- **`ProxyConnectionFailure_ThrowsProxyConnectionFailed`.** On Windows a refused loopback connect
  takes about 2 s (SYN retries, measured 2.03 s), the same as the test's 2 s `HandshakeTimeout`. The
  test passed before only because the timeout was wrapped as `ProxyConnectionFailed`, which is the
  bug being fixed. It now uses a 15 s handshake timeout, so the refusal itself is what it observes.
- **Observing the old listener.** It is awaited, not given a timeout. Its token is cancelled, its
  connection disposed and its queue completed, so every await in it ends promptly. It can only be
  held by a user `OnMessage` callback that blocks, which already stalls that listener today.

## Deferred

- #226 device criterion (iOS/Android IL2CPP run in the #99 matrix): not possible here. The PR says
  "Part of #226" and the issue stays open with `needs-device-validation`.
- #84 (close/dispose races inside `WebSocketConnection`) is untouched.

## Verified

- `'./Tools~/ci/check.sh'`: OK.
- Unity 2021.3.45f2 PlayMode, scoped:
  - Old code, `--filter 'WebSocketConnectCancellationTests|WebSocketClientReconnectTests'`: 6 of 7
    failed. The four silent-peer/ignored-token tests outlived their timeout (the Mono hang). The
    proxy caller-cancel test got `ProxyTunnelFailed`. `ReconnectFromOnClosed` got the completed
    queue. The `AbortThenConnect` test passed on that first run because of a race in the test.
    After the test was changed to wait for the held listener to return, both reconnect tests failed
    against the old `WebSocketClient.cs`.
  - Fixed code: 7/7 pass. `--filter 'TurboHTTP\.Tests\.WebSocket\.|UnityWebSocket'` gave 160 tests,
    all passing after the proxy-failure test change above.
- Not run: the full EditMode/PlayMode suites (host-editor periodic run), IL2CPP player builds,
  devices.
