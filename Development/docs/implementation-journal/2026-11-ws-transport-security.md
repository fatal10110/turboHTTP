# 2026-11 ws-transport-security: cleartext policy and challenge-driven proxy Basic (#85)

## What
- Shared cleartext policy, `TurboHTTP.Core.CleartextTrafficPolicy`:
  - `DefaultAllowCleartext` is a compile-time constant: false on iOS/Android players, true in the Editor and on desktop.
  - `PlatformPolicy` is a `Func<string,bool>` read and written through `Volatile`.
  - `IsPermitted(host, allowCleartext)` returns false when `AllowCleartext` is false. Otherwise it asks the platform policy, and a missing policy permits. A throwing policy refuses (fail closed).
- `UHttpClientOptions.AllowCleartext` and `WebSocketConnectionOptions.AllowCleartext` (public, XML-documented) both default to `DefaultAllowCleartext`, and both are copied in `Clone()`.
  - `UHttpClient` passes the value to the transport via the `RequestMetadataKeys.AllowCleartext` metadata key. When the key is absent, the transport uses the platform default.
  - `client.WebSocket(...)` (Unity.WebSocket) forwards the client's value.
  - `UnityWebSocketClient` gets an `AllowCleartext` property and inspector field. Leaving it off keeps the platform default.
- `RawSocketTransport` refuses an `http://` target and an `http://` proxy leg with `UHttpException(InvalidRequest)`. The check runs at the entry point, before any socket.
- `RawSocketWebSocketTransport` refuses `ws://` and `http://` proxy legs before the DNS lookup. It throws `WebSocketException(WebSocketError.CleartextNotPermitted)`, a new enum member that is non-retryable and maps to InvalidRequest.
- Proxy Basic credentials are now challenge-driven. `Core.Internal.AuthChallengeParser.OffersScheme` reads `Proxy-Authenticate` per RFC 9110 11.6.1: comma lists, auth-param vs scheme tokens, quoted strings, case-insensitive matching.
  - `ProxyTunnelConnector` (WS) retries with Basic only when Basic is offered, and only with `allowCleartextCredentials`. It has a new parameter.
  - The HTTP CONNECT 407 retry also requires a Basic challenge, and its error reason names the missing scheme.
- Android `AndroidCleartextTrafficPolicy` (Runtime/Unity) is installed by `RuntimeInitializeOnLoadMethod(SubsystemRegistration)` on Android players, unless the app already installed a policy.
  - It attaches the current thread to JNI, then calls `NetworkSecurityPolicy.isCleartextTrafficPermitted(host)` on API 24+, the no-argument overload on API 23, and permits below 23.
  - Results are cached per host, up to 64 entries.
- Docs: a `PlatformNotes.md` "## Cleartext traffic" section, corrected ATS and Android text, Troubleshooting, and the WebSocket and Transport module notes.

## Decisions
- Semantics are AND: cleartext needs `AllowCleartext=true` and the platform policy's consent. Explicit opt-in never overrides an OS refusal, which is the conservative choice. On iOS no policy is installed, so `AllowCleartext=true` is the explicit opt-in the issue allows. `NSAllowsArbitraryLoads` is not parsed.
- An `http://` proxy counts as cleartext even for https/wss targets, because CONNECT and Proxy-Authorization travel in the clear. This follows criterion 2 literally.
- HTTP keeps `ProxySettings.AllowPlaintextProxyAuth`. Basic over the proxy leg requires both flags. WebSocket has only `AllowCleartext`.
- A 407 without a Basic challenge throws `ProxyAuthenticationRequired` (WS) or `InvalidRequest` (HTTP), with no retry and no credentials sent. Two existing fixtures had a 407 with no `Proxy-Authenticate`, which violates RFC 9110 15.5.8. They now carry `Basic`.
- The policy lives in Core, so Transport and WebSocket.Transport need no new asmdef references. The JNI implementation lives in the Unity module (engine refs allowed).

## Deferred
- #236: the HTTP forward-proxy path (http target via http proxy) still sends Basic preemptively. It has no 407 retry loop, and two opt-ins gate it.
- Device validation of the Android JNI policy, iOS players and ATS behaviour: #99 (needs-device-validation).
- `Development/docs/agents/platform.md` "Mobile" still says "cleartext policy is our responsibility (#85)". Lanes leave the guides alone, so the owner should update the wording.

## Verification
- `./Tools~/ci/check.sh` passes. `shadow_build.py --variant all` (editor, ios, android) compiles the Android JNI path with no new warnings.
- Host NUnit harness (net8.0, sources compiled directly with a UnityEngine stub; not Unity): 48 tests pass. The suites were CleartextTrafficPolicyTests, WebSocketCleartextPolicyTests, ProxyTunnelConnectorTests, WebSocketProxyTests and RawSocketTransportCleartextTests.
- Red runs:
  - With the main `RawSocketTransport.cs`, all 5 HTTP behaviour tests fail.
  - With the WS checks removed, all 8 WS behaviour tests fail.
- Not run: Unity EditMode/PlayMode (host editor, periodic), IL2CPP, iOS/Android devices.

## Review round 1
- Removed `[NonParallelizable]` from the two new fixtures (Unity's NUnit lacks it; Unity runs tests serially, so the static `PlatformPolicy` the tests set is not shared across concurrent tests).
- PlatformNotes and Modules/Transport.md now limit the "Basic only on a Basic challenge" claim to CONNECT tunnels and document the preemptive forward-proxy path (#236).
