# 2026-11: RefusingLoopbackPort refuses on Darwin (#1286)

## What
- `RefusingLoopbackPort` (#1259) binds `127.0.0.1:0` and never listens. Windows and Linux refuse a connect to
  such a port; Darwin drops the SYN, so the connect timed out after about 8 s, and
  `RefusingLoopbackPortTests.Connect_IsRefused` failed on the macOS Mono and IL2CPP players. On Darwin the
  helper now holds no socket and reports port 1. TurboHTTP tests only bind port 0, and Darwin refuses a
  connect to a port with no socket at once.
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

`WebSocketProxyTests.ProxyConnectionFailure_ThrowsProxyConnectionFailed` passed on both players before and
after; before, it passed only because its 15 s handshake bound outlasted the timeout. The same kernel
behavior was reproduced outside Unity (python: `ETIMEDOUT` after 7.78 s for a bound non-listening port,
`ECONNREFUSED` at once for an unbound one). `./Tools~/ci/check.sh`: OK.

## Not run
- iOS players (Darwin; the same branch applies). Windows and Linux keep the old path; not rerun here.
