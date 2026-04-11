# Phase 22c: HTTP/2 Through CONNECT Proxy Tunnels

**Date:** 2026-04-11  
**Phase:** 22c.1 `Http2ConnectionManager` key extension  
**Status:** 22c.1 implemented and validated with focused Unity PlayMode tests

## What was implemented

Completed the first Phase 22c implementation slice by extending `Http2ConnectionManager` to
support HTTP/2 connections established through CONNECT proxy tunnels.

1. Added origin x proxy key support.
   - Direct HTTP/2 keys remain unchanged as `{host}:{port}`.
   - Tunneled HTTP/2 keys use `{originHost}:{originPort}|via|{proxyHost}:{proxyPort}`.
   - The proxy coordinates are used only for the manager key; `Http2Connection` still receives
     the origin host and origin port.

2. Added proxy-aware overloads.
   - `GetIfExists(originHost, originPort, proxyHost, proxyPort)`
   - `GetOrCreateAsync(originHost, originPort, proxyHost, proxyPort, tlsStream, ct)`
   - `Remove(originHost, originPort, proxyHost, proxyPort)`
   - `HasConnection(originHost, originPort, proxyHost, proxyPort)`

3. Refactored the slow creation path behind `GetOrCreateCoreAsync(...)`.
   - Direct and tunneled overloads now share the same `_connections` dictionary and `_initLocks`
     mechanism.
   - The direct overload keeps the same public signature and fast-path behavior.

4. Hardened slow-path stream cleanup.
   - If `initLock.WaitAsync(ct)` or any pre-connection step throws, the unowned `tlsStream` is
     disposed before rethrowing.
   - If `Http2Connection.InitializeAsync(...)` fails, the partially constructed connection owns
     and disposes the stream.

5. Added focused runtime tests for the 22c.1 contract.
   - Direct and tunneled keys are independent entries.
   - Two different proxies to the same origin produce independent tunneled entries.
   - `GetIfExists` does not alias direct and tunneled keys.
   - Removing a tunneled key does not affect the direct key.
   - Slow-path cancellation before connection ownership transfer disposes the caller stream.

## Files modified

Runtime:

- `Runtime/Transport/Http2/Http2ConnectionManager.cs`

Tests:

- `Tests/Runtime/Transport/Http2/Http2ConnectionManagerTests.cs`

Documentation:

- `Development/docs/implementation-journal/2026-04-phase22c-proxy-http2-alpn.md`

## Decisions and trade-offs

1. Kept direct key construction inline.
   - The direct path remains the hot path and still builds `{host}:{port}` exactly where it did
     before this slice.
   - The new helper is used only for tunneled origin x proxy keys.

2. Used string overloads instead of introducing a key struct.
   - This matches the Phase 22c plan and avoids changing existing dictionary and lock plumbing.
   - The one-time tunneled key string allocation is amortized across multiplexed proxy requests.

3. Validated stream cleanup with a canceled-token test rather than a real proxy lease.
   - The leak-prone condition is inside `Http2ConnectionManager` before connection ownership
     transfer, so an in-memory stream test covers the ownership invariant without involving
     `RawSocketTransport`.

## Specialist rubric pass

### Infrastructure review

- Module boundaries remain unchanged; changes stay inside `TurboHTTP.Transport` plus runtime tests.
- No new Unity engine dependencies, assembly references, unsafe code, reflection-heavy runtime
  paths, or optional-module dependencies were introduced.
- Shared state continues to use `ConcurrentDictionary<string, Http2Connection>` and per-key
  `SemaphoreSlim` initialization locks.
- Resource disposal was explicitly rechecked; failed or canceled slow-path creation now disposes
  the unowned stream.

### Network review

- HTTP/2 connection identity remains origin-based; proxy coordinates affect manager lookup only.
- Direct and tunneled connection keys do not collide, so direct HTTP/2 behavior is unaffected.
- The slice does not change TLS/ALPN negotiation or proxy request routing yet; those remain 22c.2
  and 22c.3.
- Physical-device IL2CPP validation for ALPN through CONNECT remains deferred to 22c.4.

## Validation

### Repository check

- `git diff --check -- Runtime/Transport/Http2/Http2ConnectionManager.cs Tests/Runtime/Transport/Http2/Http2ConnectionManagerTests.cs`

Result: passed.

### Focused Unity PlayMode

- Synced package to `/tmp/turboHTTP-package`
- Ran:
  `Unity -batchmode -nographics -projectPath /Users/arturkoshtei/workspace/turboHTTP-testproj -runTests -testPlatform PlayMode -testFilter TurboHTTP.Tests.Transport.Http2.Http2ConnectionManagerTests`
- Result file:
  `/Users/arturkoshtei/workspace/turboHTTP-testproj/phase22c1-manager-playmode-20260411-071457.xml`

Result:

- total: 5
- passed: 5
- failed: 0
- skipped: 0

## Deferred / still required

1. 22c.2 still needs the CONNECT tunnel ALPN extension.
2. 22c.3 still needs proxy protocol routing and HTTP/2 manager integration from `RawSocketTransport`.
3. 22c.4 still needs edge-case validation and physical-device IL2CPP validation for ALPN through
   CONNECT tunnels.

---

## 2026-04-11 22c.2: CONNECT Tunnel ALPN Extension

**Status:** Implemented and validated with focused Unity PlayMode tests

## What was implemented

Completed the second Phase 22c implementation slice by enabling HTTP/2 ALPN advertisement during
the TLS handshake inside an HTTP CONNECT tunnel.

1. Updated CONNECT tunnel ALPN protocols.
   - `s_connectTunnelAlpnProtocols` now advertises `{ "h2", "http/1.1" }`.
   - `EstablishConnectTunnelAsync(...)` continues to return `Task<TlsResult>`.
   - `AcquirePreparedProxyLeaseAsync(...)` and `RebindConnectTunnelLease(...)` remain structurally
     unchanged.

2. Added SslStream viability marking for successful tunnel handshakes.
   - After a successful tunnel TLS wrap, Auto mode marks SslStream viable only when the actual
     provider was `"SslStream"`.
   - BouncyCastle results do not promote SslStream viability.
   - No BouncyCastle runtime catch/retry was added; TLS provider selection remains pre-handshake.

3. Added focused proxy tunnel tests.
   - CONNECT tunnel ALPN list is exactly `h2`, then `http/1.1`.
   - `RebindConnectTunnelLease(...)` preserves negotiated ALPN values including `h2`,
     `http/1.1`, and `null`.
   - The SslStream viability helper marks Auto/SslStream success and ignores Auto/BouncyCastle.
   - A synthetic CONNECT stream verifies TLS provider failures propagate without being converted
     to `UHttpException`.

## Files modified

Runtime:

- `Runtime/Transport/RawSocketTransport.cs`

Tests:

- `Tests/Runtime/Transport/Http1/RawSocketTransportProxyTunnelTests.cs`

Documentation:

- `Development/docs/implementation-journal/2026-04-phase22c-proxy-http2-alpn.md`

## Decisions and trade-offs

1. Kept TLS provider selection static.
   - `TlsProviderSelector.GetProvider(_tlsBackend)` remains the single selection point.
   - Tests use reflection against private transport helpers instead of adding a production-facing
     provider-injection seam for this narrow phase.

2. Preserved the current `TlsResult` flow.
   - The existing lease rebind path already propagates `NegotiatedAlpn` onto
     `PooledConnection.NegotiatedAlpnProtocol`.
   - No intermediate tunnel result type was introduced.

3. Left end-to-end HTTP/2 proxy routing for 22c.3.
   - 22c.2 only enables negotiation and metadata propagation.
   - Dispatch still needs the 22c.3 protocol routing step before h2-through-CONNECT is usable.

## Specialist rubric pass

### Infrastructure review

- Module boundaries remain unchanged; the runtime change stays inside `TurboHTTP.Transport`.
- No new assembly references, unsafe code, public API, or Unity engine dependencies were added.
- The helper added for SslStream viability is private and has no allocation impact on the steady
  HTTP/1.1 tunnel fallback path beyond the existing `TlsResult` object.
- Tests use reflection only in the test assembly; no runtime reflection path was added.

### Network review

- ALPN advertisement now follows the direct TLS path ordering: `h2` first, `http/1.1` fallback.
- TLS failures through the tunnel propagate from the active provider; no runtime BouncyCastle
  switch was added.
- Null ALPN remains represented as `null` on `PooledConnection.NegotiatedAlpnProtocol`, preserving
  the later 22c.3 default-to-HTTP/1.1 routing behavior.
- Physical-device validation of SslStream/BouncyCastle ALPN through CONNECT remains a 22c.4 gate.

## Validation

### Repository check

- `git diff --check -- Runtime/Transport/RawSocketTransport.cs Tests/Runtime/Transport/Http1/RawSocketTransportProxyTunnelTests.cs`

Result: passed.

### Focused Unity PlayMode

- Synced package to `/tmp/turboHTTP-package`
- Ran:
  `Unity -batchmode -nographics -projectPath /Users/arturkoshtei/workspace/turboHTTP-testproj -runTests -testPlatform PlayMode -testFilter TurboHTTP.Tests.Transport.Http1.RawSocketTransportProxyTunnelTests`
- Result file:
  `/Users/arturkoshtei/workspace/turboHTTP-testproj/phase22c2-proxy-tunnel-playmode-20260411-071954.xml`

Result:

- total: 11
- passed: 7
- failed: 0
- skipped: 4

Skipped tests are the pre-existing local CONNECT tunnel scenarios that require the optional
BouncyCastle/certificate environment.

## Deferred / still required

1. 22c.3 still needs protocol routing in `DispatchViaProxyAsync`.
2. 22c.4 still needs edge-case validation and physical-device IL2CPP validation for ALPN through
   CONNECT tunnels.

---

## 2026-04-11 22c.3: Protocol Routing in `DispatchViaProxyAsync`

**Status:** Implemented and validated with focused Unity PlayMode tests

## What was implemented

Completed the third Phase 22c implementation slice by routing HTTPS proxy tunnels to HTTP/2 when
the prepared CONNECT tunnel negotiated `h2`.

1. Added the tunneled HTTP/2 fast path.
   - HTTPS proxy requests now check `_h2Manager.GetIfExists(host, port, proxyHost, proxyPort)`
     before establishing any CONNECT tunnel.
   - The fast path builds the sanitized tunneled request lazily only when an alive tunneled h2
     connection exists.
   - Stale tunneled h2 dispatch removes the origin x proxy manager entry and falls through to a
     fresh tunnel only when the original request is replayable.

2. Added protocol routing after prepared tunnel acquisition.
   - `AcquirePreparedProxyLeaseAsync(...)` remains the tunnel setup and reuse entry point.
   - If `lease.Connection.NegotiatedAlpnProtocol == "h2"`, the lease transfers ownership and the
     stream is handed to the proxy-aware `Http2ConnectionManager.GetOrCreateAsync(...)` overload.
   - If ALPN is `http/1.1` or `null`, dispatch stays on the existing lease-based HTTP/1.1 proxy
     helper path.

3. Preserved HTTP/1.1 proxy stale-retry behavior.
   - `DispatchProxyHttp11WithRetryAsync(...)` now accepts an optional prepared lease.
   - The helper still owns stale-dispatch retry and can reacquire a fresh prepared tunnel when the
     initial lease fails before a retry-safe body commit.

4. Updated diagnostics.
   - Added `TransportProxyH2Reuse`, `TransportProxyH2StaleRetry`, `TransportProxyH2Init`, and
     `TransportProxyH1Dispatch`.
   - Replaced `TransportProxyConnectHttp11Only` with
     `TransportProxyConnectTunnelEstablished`.
   - Added a proxy-aware `HasHttp2Connection(...)` diagnostic helper for tests.

5. Added focused tunneled h2 coverage.
   - The test seeds a prepared proxy tunnel lease with `NegotiatedAlpnProtocol = "h2"`.
   - The first request initializes HTTP/2 under the origin x proxy tunnel key.
   - The second request reuses the tunneled h2 manager fast path.
   - The HTTP/2 server-side header capture verifies `Proxy-Authorization` is stripped before the
     origin dispatch.

## Files modified

Runtime:

- `Runtime/Transport/RawSocketTransport.cs`

Tests:

- `Tests/Runtime/Transport/Http1/RawSocketTransportProxyTunnelTests.cs`

Documentation:

- `Development/docs/implementation-journal/2026-04-phase22c-proxy-http2-alpn.md`

## Decisions and trade-offs

1. Kept protocol routing inline in `DispatchViaProxyAsync`.
   - This makes `lease = null` happen immediately after the HTTP/2 ownership transfer and after
     the HTTP/1.1 helper handoff.
   - The lease ownership transitions are easier to audit than passing mutable ownership state
     through an async helper.

2. Retained the 22b.2 lease-based HTTP/1.1 path.
   - HTTP/1.1 through CONNECT continues to use `DispatchProxyHttp11WithRetryAsync(...)`.
   - No raw stream fallback was reintroduced.

3. Used a prepared in-memory h2 tunnel test instead of a real TLS ALPN server for this slice.
   - 22c.3 routing depends on `PooledConnection.NegotiatedAlpnProtocol`, not on certificate
     validation or provider-specific TLS behavior.
   - Real ALPN-through-CONNECT device validation remains the 22c.4 gate.

## Specialist rubric pass

### Infrastructure review

- Module boundaries remain unchanged; all runtime changes stay in `TurboHTTP.Transport`.
- The h2 path transfers lease ownership before handing the stream to `Http2ConnectionManager`.
- The HTTP/1.1 fallback path hands the prepared lease to the existing helper and nulls the caller
  reference after normal completion.
- No new public API, assembly references, unsafe code, or Unity engine dependencies were added.
- The new test uses existing internal helpers and reflection patterns already present in the
  transport test suite.

### Network review

- Proxy h2 keys are origin x proxy scoped, avoiding cross-proxy or cross-origin tunnel reuse.
- The sanitized tunneled request is used for HTTP/2 dispatch, preventing proxy-only headers from
  reaching the origin.
- Null ALPN routes to HTTP/1.1, matching RFC 7301's no-extension behavior for this context.
- TLS failures still propagate from the selected provider; no runtime BouncyCastle retry was added.
- Full real-network ALPN negotiation through CONNECT still requires 22c.4 validation.

## Validation

### Repository check

- `git diff --check -- Runtime/Transport/Http2/Http2ConnectionManager.cs Runtime/Transport/RawSocketTransport.cs Tests/Runtime/Transport/Http2/Http2ConnectionManagerTests.cs Tests/Runtime/Transport/Http1/RawSocketTransportProxyTunnelTests.cs Development/docs/implementation-journal/2026-04-phase22c-proxy-http2-alpn.md`

Result: passed.

### Focused Unity PlayMode

- Synced package to `/tmp/turboHTTP-package`
- Ran:
  `Unity -batchmode -nographics -projectPath /Users/arturkoshtei/workspace/turboHTTP-testproj -runTests -testPlatform PlayMode -testFilter TurboHTTP.Tests.Transport.Http1.RawSocketTransportProxyTunnelTests`
- Result file:
  `/Users/arturkoshtei/workspace/turboHTTP-testproj/phase22c3-proxy-tunnel-playmode-20260411-072452.xml`

Result:

- total: 12
- passed: 8
- failed: 0
- skipped: 4

Skipped tests are the pre-existing local CONNECT tunnel scenarios that require the optional
BouncyCastle/certificate environment.

## Deferred / still required

1. 22c.4 edge-case validation still needs to cover stale tunneled h2 retry, non-replayable h2
   stale failure, direct-vs-tunnel key separation through `RawSocketTransport`, and HTTP/1.1
   fallback regressions.
2. Physical-device IL2CPP validation for SslStream and BouncyCastle ALPN through CONNECT tunnels
   remains open.

---

## 2026-04-11 22c.4: Validation and Edge Cases

**Status:** Local edge-case validation implemented and passed; physical-device IL2CPP ALPN
through CONNECT remains open

## What was implemented

Completed the locally feasible Phase 22c.4 validation pass for tunneled HTTP/2 connection
lifecycle, GOAWAY behavior, concurrent manager creation, proxy header isolation, and existing
HTTP/2 lifecycle regression coverage.

1. Fixed one lifecycle issue found by 22c.4 disposal-chain validation.
   - `Http2Connection.Dispose()` already disposed the owned stream, but resource finalization could
     call `_stream.Dispose()` a second time.
   - Added a small `DisposeStreamOnce()` guard so the TLS/tunnel/proxy stream chain is closed
     exactly once while keeping shutdown idempotent.

2. Added tunnel stream ownership validation.
   - A nested TLS stream -> tunnel stream -> proxy TCP stream test now verifies
     `Http2Connection.Dispose()` disposes all layers.
   - The same test verifies calling `Dispose()` twice does not double-close the chain.

3. Added GOAWAY-through-tunnel validation.
   - A proxy-keyed `Http2ConnectionManager` connection receives a GOAWAY frame through the in-memory
     tunnel stream.
   - The pending stream fails with `UHttpErrorType.NetworkError`.
   - `IsAlive` becomes false, the proxy-aware `GetIfExists(...)` returns null, and a replacement
     tunnel connection can be created under the same origin x proxy key.

4. Added concurrent tunnel creation race validation.
   - Two concurrent `GetOrCreateAsync(origin, originPort, proxy, proxyPort, stream, ct)` calls for
     the same tunnel key retain exactly one `Http2Connection`.
   - The discarded second stream is disposed once.

5. Added proxy-aware diagnostic validation.
   - `HasConnection(origin, port, proxy, pport)` is false before tunnel creation, true after
     creation, false after removal, and false after connection death.
   - The direct `HasConnection(origin, port)` entry remains independent.

6. Added proxy authentication/header isolation validation.
   - `PrepareHttpsProxyTunnelRequest(...)` strips `Proxy-Authorization` before origin dispatch and
     preserves unrelated headers.
   - A synthetic 407 -> authenticated CONNECT -> 200 flow verifies `Proxy-Authorization` is sent
     only to the proxy during CONNECT authentication.
   - The existing prepared h2 tunnel integration test still verifies `Proxy-Authorization` is not
     present in HTTP/2 origin headers.

7. Revalidated the existing HTTP/2 lifecycle suite after the stream-dispose-once fix.
   - This covers non-proxy HTTP/2 disposal, GOAWAY, frame handling, streaming response cleanup, and
     protocol error paths against the lifecycle change.

## Files modified

Runtime:

- `Runtime/Transport/Http2/Http2Connection.cs`
- `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`

Tests:

- `Tests/Runtime/Transport/Http2/Http2ConnectionManagerTests.cs`
- `Tests/Runtime/Transport/Http1/RawSocketTransportProxyTunnelTests.cs`

Documentation:

- `Development/docs/implementation-journal/2026-04-phase22c-proxy-http2-alpn.md`

## Decisions and trade-offs

1. Fixed the double-dispose instead of relying on stream idempotency.
   - Most .NET streams tolerate repeated `Dispose()`, but the proxy tunnel ownership contract is
     clearer when the HTTP/2 owner closes the chain exactly once.
   - The guard does not change shutdown ordering: GOAWAY best-effort still happens before stream
     disposal, and background task/resource finalization remains best effort.

2. Validated the concurrent race at the manager layer.
   - The accepted 22c race is specifically that two prepared TLS streams can reach the same
     origin x proxy manager key.
   - The manager test isolates the ownership invariant directly: one retained connection and one
     disposed discarded stream.

3. Kept BouncyCastle runtime fallback out of 22c.4.
   - The corrected Phase 22c plan states provider selection is pre-handshake only.
   - The local validation therefore keeps the existing failure-propagation test and does not add an
     obsolete SslStream-to-BouncyCastle retry path.

4. Left physical-device ALPN validation as the remaining non-local gate.
   - Editor PlayMode cannot prove IL2CPP stripping, mobile TLS behavior, or device-level
     BouncyCastle ALPN through CONNECT.
   - This must be run on a physical iOS or Android IL2CPP build before marking Phase 22c complete.

## RFC compliance matrix

| RFC | Section | Claim | Local validation |
|-----|---------|-------|------------------|
| RFC 9110 | 9.3.6 | CONNECT request format, 200 handling, tunnel lifetime | Existing proxy tests plus authenticated CONNECT validation |
| RFC 9113 | 3.2-3.3 | HTTP/2 connection preface and SETTINGS through the tunnel | Prepared h2 tunnel integration test |
| RFC 7301 | 3 | ALPN offers `h2`, then `http/1.1`; selected protocol drives routing | ALPN list and rebind tests |
| RFC 9113 | 6.8 / 9.1 | GOAWAY prevents reuse and fails affected pending streams | GOAWAY-through-tunnel manager test |

## Specialist rubric pass

### Infrastructure review

- Module boundaries remain unchanged; production changes stay inside `TurboHTTP.Transport`.
- No asmdef changes, new public API, unsafe expansion, Unity engine dependency, reflection-heavy
  runtime path, or optional-module dependency was introduced.
- Stream ownership is now stricter: `Http2Connection` disposes its owned stream once, which closes
  the TLS/tunnel/proxy TCP chain without double-releasing wrapper state.
- Concurrent manager creation preserves one retained connection per origin x proxy key and disposes
  the discarded stream.
- Proxy header isolation is covered at both request-preparation and CONNECT-authentication levels.

### Network review

- GOAWAY handling remains stream-abstraction based and works through the tunneled stream wrapper.
- Proxy h2 reuse is disabled after GOAWAY because `IsAlive` becomes false and proxy-aware
  `GetIfExists(...)` returns null.
- CONNECT authentication sends `Proxy-Authorization` to the proxy only; tunneled origin dispatch
  receives a sanitized request.
- ALPN through CONNECT remains locally validated at protocol-list/rebind/routing levels; physical
  IL2CPP device validation remains required for provider/platform behavior.

## Validation

### Repository check

- `git diff --check -- Runtime/Transport/Http2/Http2Connection.cs Runtime/Transport/Http2/Http2Connection.Lifecycle.cs Runtime/Transport/Http2/Http2ConnectionManager.cs Runtime/Transport/RawSocketTransport.cs Tests/Runtime/Transport/Http2/Http2ConnectionManagerTests.cs Tests/Runtime/Transport/Http1/RawSocketTransportProxyTunnelTests.cs Development/docs/implementation-journal/2026-04-phase22c-proxy-http2-alpn.md`

Result: passed.

### Focused Unity PlayMode

- Synced package to `/tmp/turboHTTP-package`
- Ran:
  `/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity -batchmode -nographics -projectPath /Users/arturkoshtei/workspace/turboHTTP-testproj -runTests -testPlatform PlayMode -testFilter TurboHTTP.Tests.Transport.Http2.Http2ConnectionManagerTests`
- Result file:
  `/Users/arturkoshtei/workspace/turboHTTP-testproj/phase22c4-manager-playmode-20260411-073959.xml`

Result:

- total: 9
- passed: 9
- failed: 0
- skipped: 0

- Ran:
  `/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity -batchmode -nographics -projectPath /Users/arturkoshtei/workspace/turboHTTP-testproj -runTests -testPlatform PlayMode -testFilter TurboHTTP.Tests.Transport.Http1.RawSocketTransportProxyTunnelTests`
- Result file:
  `/Users/arturkoshtei/workspace/turboHTTP-testproj/phase22c4-proxy-tunnel-playmode-20260411-074030.xml`

Result:

- total: 14
- passed: 10
- failed: 0
- skipped: 4

Skipped tests are the pre-existing local CONNECT tunnel scenarios that require local certificate
generation/BouncyCastle environment support.

- Ran:
  `/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity -batchmode -nographics -projectPath /Users/arturkoshtei/workspace/turboHTTP-testproj -runTests -testPlatform PlayMode -testFilter TurboHTTP.Tests.Transport.Http2.Http2ConnectionTests`
- Result file:
  `/Users/arturkoshtei/workspace/turboHTTP-testproj/phase22c4-http2-connection-playmode-20260411-074048.xml`

Result:

- total: 77
- passed: 77
- failed: 0
- skipped: 0

## Deferred / still required

1. Physical-device IL2CPP validation remains open:
   - force `TlsBackend.BouncyCastle`
   - connect to an HTTPS origin through an HTTP CONNECT proxy
   - verify ALPN negotiates `h2`
   - verify HTTP/2 frames are exchanged through the tunnel
2. Do not mark Phase 22c complete in project status docs until the physical-device validation
   result is recorded.
