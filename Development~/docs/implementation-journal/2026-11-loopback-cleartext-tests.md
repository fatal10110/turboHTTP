# 2026-11 Loopback tests opt into cleartext (#321, part 1)

## What
- The loopback fixtures that send `http://` or `ws://` through `RawSocketTransport` or
  `RawSocketWebSocketTransport` now opt into cleartext. `AllowCleartext = true` is set on every
  `UHttpClientOptions` / `WebSocketConnectionOptions` initializer in `RawSocketTransportTests`,
  `RawSocketTransportProxyTunnelTests`, `RawSocketTransportProxy407Tests`,
  `RawSocketTransportConnectionReuseTests`, `ReusedConnectionFirstByteTimeoutTests`, `WebSocketProxyTests`
  and `WebSocketBouncyCastleTlsTests`.
- New `TestHelpers.AllowLoopbackCleartext(request)` covers requests dispatched straight to the transport
  (`RawSocketTransportTests` x2, `RawSocketTransportCancellationTests` x2, `RecordReplayStreamingTests`).
- testing.md, "Writing tests": new loopback tests opt in the same way.

## Why
`CleartextTrafficPolicy.DefaultAllowCleartext` is a compile-time `false` in iOS and Android players and
`true` in the Editor (#85; raw sockets bypass ATS and the Android network security config, so the
library enforces the policy itself). These fixtures relied on the Editor default. In players they were
refused before any socket opened. The refusal showed up as "Cleartext ... is not permitted", or
indirectly as `InvalidRequest` / `CleartextNotPermitted` where the test expected
`NetworkError` / `Timeout` / a 407 retry count.
- Android device run (2021.3.45f2, main 466935d): 70 failures carried the cleartext message, plus
  ~20 indirect ones in the same fixtures.
- iOS simulator run 2 (Unity 6000.3.26f1, ARM64 Simulator SDK, iOS 26.4): 59 direct failures plus ~24
  indirect ones in the same fixtures.

## Decisions
- **Fix the tests, not the default.** The product default is correct (#85). The tests now state what they
  need, so they behave the same in the Editor and in players.
- **No Info.plist / ATS change.** `NSAllowsLocalNetworking` would not change anything, because the library
  does not consult ATS. The Android test player still needs `usesCleartextTraffic`, which
  `run-device-tests.sh` writes into its own test project, because the Android platform policy is
  consulted (`AndroidCleartextTrafficPolicy`).
- **Fixtures whose purpose is the policy are untouched**: `RawSocketTransportCleartextTests`,
  `WebSocketCleartextPolicyTests`, `CleartextTrafficPolicyTests`. `WebSocketConnectCancellationTests`
  already opted in; its two device failures are cancellation classification (#320).
- **Initializers stay inline (`AllowCleartext = true`).** Wrapping 51 multi-line initializers in a helper
  would hide the setting. The helper exists only for requests that bypass the client.

## Deferred
The rest of #321 (temp path, `X509Chain` ECDSA cases, `CoreSurfaceTests` type lookup, the NUL-literal
case, the Phase19 baseline file, file locking, reflection on stripped members) stays on #321, which this
PR does not close.

## Verified
- `check.sh`: invariants, format and the shadow build with analyzers all pass.
- Unity 2021.3.45f2 Editor PlayMode, filtered to the nine touched fixtures (`run-tests.sh --platform
  PlayMode --filter '<fixture names>'`): 117/117 passed, which is every test case in those fixtures.
- iOS simulator run 3 with this change: see `2026-11-ios-simulator-run.md`.

## Not run
- The full Editor suites; the Android device run with this change.
