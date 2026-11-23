# 2026-11: RefusingLoopbackPort refuses on Darwin (#1286)

## What
- `RefusingLoopbackPort` (#1259) binds `127.0.0.1:0` and never listens. Windows and Linux refuse a connect to
  such a port; Darwin drops the SYN, so the connect timed out after about 8 s, and
  `RefusingLoopbackPortTests.Connect_IsRefused` failed on the macOS Mono and IL2CPP players and on an iPhone.
  Off Windows the helper now holds no socket and reports port 1. Only Windows needs the exclusive hold
  (#1259); TurboHTTP tests only bind port 0, and every other OS refuses a connect to a port with no socket
  at once. A first version detected Darwin through `RuntimeInformation`, which does not report iOS IL2CPP as
  Darwin, so the iPhone still timed out; the Windows-only branch needs no detection.
- `Connect_IsRefused` accepts the raw Darwin ECONNREFUSED (61) that IL2CPP reports in `SocketErrorCode`
  instead of `SocketError.ConnectionRefused`. The product side of that runtime behavior is #1287.

## Files
- `Tests/Runtime/Support/RefusingLoopbackPort.cs`
- `Tests/Runtime/Support/RefusingLoopbackPortTests.cs`

## How verified
macOS 26.7.1 arm64, Unity 2021.3.45f2. Run: `run-tests.sh --platform StandaloneOSX` (PR #1279) with
`--filter 'RefusingLoopbackPortTests|ProxyConnectionFailure_ThrowsProxyConnectionFailed'`, main 86bf65f plus
this change.

| Player | Before | After |
| --- | --- | --- |
| Mono | `Connect_IsRefused` failed: TimedOut after 7.9 s | passed: refused after 12 ms |
| IL2CPP, Stripping High | the same | passed: refused after 1 ms (code 61) |
| iPhone 13 Pro Max, iOS 27.0.1, IL2CPP High (`run-ios-player.sh`) | failed in the full suite at 86bf65f: code 60 (ETIMEDOUT) | passed: refused after 3 ms (code 61) |

`WebSocketProxyTests.ProxyConnectionFailure_ThrowsProxyConnectionFailed` passed on both players before and
after; before, it passed only because its 15 s handshake bound outlasted the timeout. The same kernel
behavior was reproduced outside Unity (python: `ETIMEDOUT` after 7.78 s for a bound non-listening port,
`ECONNREFUSED` at once for an unbound one). `./Tools~/ci/check.sh`: OK.

## Not run
- Windows (keeps the held socket, unchanged) and Linux/Android (now port 1; refusal there is immediate) were
  not rerun here.
