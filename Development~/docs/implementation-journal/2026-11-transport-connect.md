# 2026-11 transport-connect (#163, #167, #38, #168, #170)

Wave run w1030, lane transport-connect.

## What

- **#163, connect deadline and same-family racing.**
  - New `ConnectionPoolOptions.ConnectTimeout` (`TimeSpan`, default 10 s, `Timeout.InfiniteTimeSpan` allowed, must be positive and at most `int.MaxValue` ms). It is in `Clone()` and `IsDefault()`, flows through the `RawSocketTransport.EnsureRegistered` factory into a new optional `TcpConnectionPool(connectTimeout:)` parameter, and replaces the `Timeout.InfiniteTimeSpan` the pool passed to the connector.
  - `HappyEyeballsConnector.ConnectAsync` builds the deadline before choosing a path, so it now covers the sequential path (`Enable = false`) too. Before, the sequential path (also taken by every single-family host) ignored `connectTimeout`.
  - With `Enable = true`, a single family no longer takes the sequential shortcut. It goes through the same schedule, so same-family addresses start `AttemptSpacingDelay` apart and race, at most `MaxConcurrentAttempts` at once. The first success cancels the others; losing sockets are disposed (existing `DrainLosersAsync` / `ObserveOutstandingAttemptsAsync`).
  - Deadline expiry now always surfaces as `TimeoutException`. Before, the dual-stack loop hit `ct.ThrowIfCancellationRequested()` on the linked token first, so an expired deadline surfaced as `OperationCanceledException`, which `RawSocketTransport` reported as a caller cancellation.
  - `TcpConnectionPool.ConnectWithDeadlineAsync` (internal static, the pool's connect) wraps that `TimeoutException` as `UHttpException(UHttpErrorType.Timeout)`, mirroring the pool DNS timeout. An all-failed race still rethrows its first inner exception, now with the original stack.
  - A race with exactly one failure rethrows that failure instead of a one-element `AggregateException`.
- **#167, IPv4 literals on IPv6-only / NAT64 networks.**
  - New `Transport/Connection/Nat64AddressSynthesizer` (internal): RFC 7050 section 3 prefix discovery from the AAAA answers for `ipv4only.arpa` (well-known address searched at every RFC 6052 position; must be unique; `.170` then `.171`; "u" octet zero), RFC 6052 section 2.2 synthesis for /32 to /96, and `ConnectWithLiteralFallbackAsync`.
  - Used by `TcpConnectionPool.CreateConnectionAsync` and `RawSocketWebSocketTransport`. An IPv4 literal is first connected as-is. Only when that fails with "no IPv4 route" (`NetworkUnreachable`, `HostUnreachable`, `AddressFamilyNotSupported`, `AddressNotAvailable`) are the prefixes discovered through the transport's own resolver (with its DNS timeout) and the synthesized addresses, one per prefix in answer order, connected under the same deadline. No prefix: the original error is rethrown.
- **#38, PollSelect busy-spin.**
  - The poll thread blocks on a `ManualResetEventSlim` when no operation is pending. `ReceiveAsync`, `SendAsync` and `Dispose` set it; the loop resets it before sampling the pending flags, so a wake-up is never lost.
  - With one direction pending it polls that direction; with both pending it waits on one `Socket.Select` over read and write interest. Every wait is at most 20 ms (`PollSliceUs`).
  - The send path no longer loops inside the poll thread until the kernel accepts the whole payload. It writes what the kernel takes, records progress and returns to the loop, so receives keep draining while a send is blocked (the full-duplex deadlock).
  - Array-backed sends are first written inline on the caller's thread (non-blocking, straight from the caller's array). Only the rest is queued. The pooled `_sendBuffer` (used only for non-array memory) stays poll-thread-only, so the existing "join, then return buffers" rule in `Dispose` still holds.
  - An operation published after `Dispose` raced the entry check is failed by the publisher (`PublishWork`), instead of waiting on a thread that may be gone.
  - The "2-10% CPU per idle connection" claim (class remarks, and `SocketIoMode.PollSelect` remarks in `ConnectionPoolOptions.cs`) is replaced: no wake-ups with nothing pending, about 50 per second with a receive pending. This supersedes the trade-off and "not a defect" note in `2026-02-phase19a.4-saea-poll-select.md` (lines 132 and 140), which is left unedited.
- **#168, SAEA dispose bound.**
  - `SaeaSocketChannel.Dispose` waits at most 500 ms (`DisposeDrainTimeoutMs`, matching the PollSelect join) for in-flight completions. On timeout it leaks the SAEAs and both buffers on purpose (the OS may still write into them), counts it in `BuffersLeakedOnDispose`, and logs once through `TurboDiagnostics.LogOnce(OnceSaeaDisposeDrainTimeout, ...)` (Core's internal sink, no UnityEngine in Transport).
- **#170, cancellation surfacing as `ObjectDisposedException`.**
  - Both channels check the token before the disposed flag, so an already-cancelled token wins over the disposal that cancellation caused.
  - SAEA: an `ObjectDisposedException` or abort from the socket call while the token is cancelled (the registration disposed the socket inline) becomes `OperationCanceledException(ct)`.
  - PollSelect: poll-thread cancellations are rethrown as `OperationCanceledException(ct)` by the cancellation wrappers; inline send failures are mapped the same way.

## Files

- `Runtime/Core/ConnectionPoolOptions.cs`, `Runtime/Core/Internal/TurboDiagnostics.cs`
- `Runtime/Transport/Connection/HappyEyeballsConnector.cs`, `Runtime/Transport/Connection/Nat64AddressSynthesizer.cs` (new)
- `Runtime/Transport/Tcp/TcpConnectionPool.cs`, `Runtime/Transport/RawSocketTransport.cs`
- `Runtime/Transport/Tcp/PollSelectSocketChannel.cs`, `Runtime/Transport/Tcp/SaeaSocketChannel.cs`
- `Runtime/WebSocket.Transport/RawSocketWebSocketTransport.cs`
- Tests (new): `Tests/Runtime/Transport/HappyEyeballsSameFamilyTests.cs`, `Nat64AddressSynthesizerTests.cs`, `PollSelectSocketChannelTests.cs`, `SocketChannelLifecycleTests.cs`, `Tests/Runtime/Core/ConnectionPoolOptionsConnectTimeoutTests.cs`

## Decisions

- Same-family attempt delay: kept `AttemptSpacingDelay` at 125 ms (phase 14.1) instead of raising it to 250 ms. RFC 8305 section 5 calls 250 ms "one recommended value" and sets the floor at 10 ms (recommended minimum 100 ms); 125 ms is inside that. One option set covers both families. `MaxConcurrentAttempts` (default 2) still caps parallel attempts, so a third address waits while two blackholed attempts are open; the connect deadline bounds that.
- `ConnectTimeout` is a `TimeSpan` (like `WebSocketConnectionOptions.ConnectTimeout`), default 10 s. The request timeout already bounds the connect through the linked token, so the effective bound is min(10 s, request timeout) as the issue suggests.
- The connector keeps throwing `TimeoutException` (the WebSocket transport relies on it); the HTTP pool wraps it as `UHttpException(Timeout)`.
- #167: synthesis as a fallback, not up front. A literal's connect only discovers the prefix after a "no IPv4 route" failure, so IPv4 and dual-stack networks pay no extra DNS lookup and a LAN without DNS never waits on `ipv4only.arpa`. Prefixes are not cached: the lookup happens only on that failure path, and a cache would go stale across network changes.
- #38: one thread per channel stays. `Socket.Select` is used only when both directions are pending (Mono's `Select` allocates per call; `Poll` does not).
- #168: the leak signal is a counter plus a once-only diagnostic; the bound is 500 ms.

## Deferred

- #167 acceptance 1 (an IPv4-literal request on a real NAT64 network) and #168 acceptance 4 (Android/iOS device note) need a device; both stay open as needs-device-validation. The PR says "Part of" for them.

## How verified

- `'./Tools~/ci/check.sh'`: pass (invariants, format, shadow build with analyzers).
- The new tests and the touched existing ones (`HappyEyeballsTests`, `TcpStreamAdapterTests`, `RawSocketTransportCancellationTests`, the last also rewritten locally for the PollSelect and SAEA modes) were compiled into a scratch .NET 8 NUnit project with the Core, Transport and Testing sources: 50/50 pass, three runs in a row; cancellation tests 9/9 in all three socket modes.
- Against the origin/main connector and PollSelect channel, the same tests fail: the deadline tests (20 s watchdog or `OperationCanceledException` instead of `TimeoutException`), both idle-CPU tests and the full-duplex test (deadlock after 30 s).
- Not run: Unity EditMode/PlayMode (host editor, periodic), IL2CPP, iOS/Android devices (NAT64, SAEA completion behavior on Mono/IL2CPP).
