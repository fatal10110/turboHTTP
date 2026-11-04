# 2026-11 accept-count-sync: order AcceptCount assertions after the server counted the injected connection (#370)

## What
Test-only. A client `ConnectAsync` completes when the kernel queues the connection, before the server's accept loop returns and increments its counter. An assertion that counts a connection the test opened itself (an injected idle connection) could read one too few on a slow player (the #359 hazard, fixed in PR #372 for one test). The fix is the #372 pattern: the test awaits a signal that the server counted the connection before it sends the request. No product code changed.

## Files
- `Tests/Runtime/Transport/Http1/RawSocketTransportTests.cs`: `RawSocketTransport_StaleConnection_NonIdempotent_NoRetry` and `HttpViaProxy_StaleProxyConnection_NonIdempotent_NoRetry` set a `TaskCompletionSource` in the `index == 1` handler (handlers run after the count) and await it after enqueuing the injected connection.
- `Tests/Runtime/Transport/Http1/RawSocketTransportConnectionReuseTests.cs`: `LoopbackServer.FirstConnectionCounted` (set in the accept loop right after the increment); awaited in `StaleConnection_BodilessPost_ResetAfterRequestWritten_NotReplayed`.
- `Tests/Runtime/Transport/Http1/ReusedConnectionFirstByteTimeoutTests.cs`: `SilentThenOkServer.FirstConnectionCounted`; awaited in `SilentReusedConnection_Post_IsNotRetried` and `InfiniteFirstByteTimeout_DisablesTheRetry`.
- `Tests/Runtime/Transport/TcpConnectionPoolTests.cs`: `PassiveServer.WaitForAcceptCountAsync(n)` (waiter list completed in the accept loop) replaces the `Stopwatch` + `Task.Delay(10)` poll, which gave up after 1 s and then asserted anyway.

## Decisions
- The `== N` assertions stay, so the tests still fail when the transport opens an unexpected extra connection; only the ordering was added.
- The awaits that guard a hang use a 10 s guard (`WithGuard` / `Task.WhenAny`), never as synchronization.
- Audited and left unchanged, because the connection counted is transport-opened and its response was read before the assertion (so it was necessarily counted first), or the assertion is `>=`, or it is an upper bound:
  - `RawSocketTransportTests` `:321 :368 :737 :1187 :2158` and the streaming / partial-send tests; `:1130` was fixed by #372.
  - `RawSocketTransportConnectionReuseTests` other `AcceptCount` assertions (retry connection got a 200, or fresh connection).
  - `ReusedConnectionFirstByteTimeoutTests` `==2` / `==3` (the retry got a 200 and accepts follow connect order).
  - `RawSocketTransportProxyTunnelTests` (transport-opened connections whose response was read; the injected tunnel assertion is `>=`).
  - `ReusedConnectionRetryInterleavingTests` (injected sockets go to a `SilentListener` the counted server never sees).
  - `SoakShortTests` (`Assert.Less` upper bound).
- Not changed: the negative `Task.Delay(100)` in `GetConnection_WithPoolKeyOverride_SemaphoreUsesPhysicalEndpoint` (not an accept-count ordering; it asserts a lease stays pending).

## Verified
- `./Tools~/ci/check.sh`: OK.
- Host Editor PlayMode, filtered on the touched fixtures, 3 runs; API 29 emulator filtered run (see the PR body for results).

## Not run
Unity EditMode (nothing touched), iOS, physical devices, the full suites (periodic host-editor run).
