# 2026-11 small fixes (#73 #154 #212 #153 #185 #173 #184 #47)

Wave w1102, lane small-fixes, milestone v1.1. One commit per issue.

## What changed

- **#154 multipart filename** (`Runtime/Files/MultipartFormDataBuilder.cs`): no wire change. Every file name,
  ASCII or not, is sent as UTF-8 inside the quoted `filename="..."` (quoted-string escaped), which RFC 7578
  sections 4.2 and 5.1.1 permit; `filename*` is never sent. The XML doc no longer calls this non-compliant or
  promises `filename*`. CR/LF are still rejected. Tests pin the UTF-8 bytes for Latin-1, CJK and surrogate-pair
  names and that `filename*` is absent: `Tests/Runtime/Files/MultipartFilenameEncodingTests.cs`.
- **#153 AdaptivePolicy** (`Runtime/Unity/Mobile/AdaptivePolicy.cs`): the no-op `AllowConcurrencyAdjustment` and
  `AllowRetryAdjustment` were removed (public API removal in `TurboHTTP.Unity.Mobile`); the type is documented as
  timeout-only. Tests: `Tests/Runtime/Mobile/AdaptivePolicySurfaceTests.cs`.
- **#185 NetworkQualityDetector** (`Runtime/Unity/Mobile/NetworkQualityDetector.cs`): one lock owns every field;
  the long-bit atomic doubles are plain doubles and `GetSnapshot` reads under the lock, so a snapshot never mixes
  two samples. Tests: `Tests/Runtime/Core/NetworkQualityDetectorConsistencyTests.cs`.
- **#184 ConnectionPoolOptions.IsDefault** (`Runtime/Core/ConnectionPoolOptions.cs`, `Runtime/Core/HappyEyeballsOptions.cs`):
  every setter sets an explicit-set flag (also on `HappyEyeballsOptions`, whose object is reachable and mutable),
  `Clone` copies it, `IsDefault()` = never configured. Tests: `Tests/Runtime/Core/ConnectionPoolOptionsExplicitSetTests.cs`.
- **#173 ProxySettings** (`Runtime/Core/ProxySettings.cs`, `Documentation~/Modules/Transport.md`): a non-`http://`
  env proxy fails with `UHttpException(InvalidRequest)` naming the variable but not its value (it can carry
  credentials); `*.example.com` matches subdomains only; `.example.com` keeps curl semantics (apex + subdomains);
  CIDR rules match IP literals only and that is documented. Tests: `Tests/Runtime/Core/ProxyBypassRulesTests.cs`.
- **#73 ConcurrencyLimiter** (`Runtime/RateLimit/ConcurrencyLimiter.cs`, `ConcurrencyInterceptor.cs`, new
  `Runtime/RateLimit/AssemblyInfo.cs` for test internals): host entries live in a lock-owned dictionary with a
  ref count (pinned from lookup to release or failed acquire); only unpinned entries are evicted (and then disposed),
  when the cache is above 1024 entries. Release therefore always hits the semaphore the permit came from. The permit
  lifetime is documented: it covers the dispatch up to the response headers, so for `SendStreamingAsync` it is
  released at headers (evidence: `StreamingDispatchBridge` resolves the response at `OnResponseStartAsync`, the
  HTTP/1.1 and HTTP/2 dispatch tasks complete at headers in streaming mode). Tests:
  `Tests/Runtime/Performance/ConcurrencyLimiterEvictionTests.cs`, `ConcurrencyStreamingPermitTests.cs`; the
  reflection-based eviction test in `ConcurrencyLimiterTests.cs` was replaced by one on the public API.
- **#212 Cookie outside Redirect** (`Runtime/Middleware/CookieInterceptor.cs`, `RedirectHandler.cs`): the internal
  `IRedirectResponseObserver` gained `OnRedirectRequest(hopRequest, context)`, called after the hop request is built
  (cross-origin stripping already applied). The cookie interceptor recomputes the jar part for the hop's request-uri
  and merges it with the caller's own Cookie header, which it keeps in context state; once a cross-origin hop
  strips Cookie, the caller part is dropped for the rest of the chain, as with Redirect outside Cookie. Tests:
  `Tests/Runtime/Middleware/CookieRedirectRequestHeaderTests.cs` (both orders).
- **#47 keepalive and idle timeout** (`HappyEyeballsConnector.cs`, `PlatformConfig.cs`, `ConnectionPoolOptions.cs`,
  `TcpConnectionPool.cs`, `RawSocketTransport.cs`, `Http11ResponseParser.cs`, `Documentation~/PlatformNotes.md`):
  SO_KEEPALIVE on every socket (guarded); idle timeout 45 s on iOS/Android players, 120 s elsewhere; new public
  `ConnectionPoolOptions.ReusedConnectionFirstByteTimeout` (default 10 s, `InfiniteTimeSpan` = off). On a reused
  HTTP/1.1 connection, for an idempotent replayable request without `Expect: 100-continue`, the transport waits for
  the first response byte under a watchdog; expiry disposes the connection (Mono/IL2CPP ignore read tokens) and
  raises an `IOException`, which the existing stale-connection path retries once. The retry takes a newly connected
  socket (`TcpConnectionPool.GetNewConnectionAsync`, skips the idle queue; RFC 9112 section 9.3.1 "open a new
  connection"), because a NAT drop kills every idle connection to the host and `IsAlive` (`Socket.Poll`) cannot tell
  (review r1). Tests:
  `Tests/Runtime/Transport/Http1/ReusedConnectionFirstByteTimeoutTests.cs`, `Tests/Runtime/Platform/PlatformConnectionIdleTimeoutTests.cs`.

## Decisions

- #153: removed instead of `[Obsolete]`: an obsolete member is still an option without behavior (the AC), and the
  package is `1.0.0-pre.1` with a feature freeze for fixes.
- #185: a single lock, not all-atomic: one sample per completed request, not per frame.
- #184: one flag per options object instead of per-property nullables; `IsDefault` only feeds the
  dedicated-vs-shared transport choice. `Http2Options`/`StreamingOptions` keep value comparison (not in #184).
- #173: typed exception rather than ignoring the env proxy: silently going direct when the environment names a
  proxy would hide the misconfiguration. CIDR is literal-only (curl does the same; resolving would add DNS to the
  bypass decision).
- #154: the issue asks for `filename*` (RFC 8187); RFC 7578 section 4.2 says it MUST NOT be used in
  multipart/form-data, and CLAUDE.md ranks the RFC above issue text. A first revision sent an ASCII `_` fallback
  plus `filename*`; review r1 showed that 7578 receivers (e.g. PHP `$_FILES`) then lose the real name. The final
  change keeps UTF-8 in `filename=` (what main and browsers send) and only corrects the doc and adds the tests the
  AC asks for.
- #212: observer hook rather than enforcing Redirect-outer ordering; both orders stay supported (#55 model).
- #47: keepalive probe timing stays at OS defaults: Unity's .NET Standard 2.1 profile has no
  `TcpKeepAliveTime/Interval` (the shadow build rejects them). Configurable socket tuning is Phase 23. The
  first-byte watchdog covers HTTP/1.1 only (not the Expect: 100-continue path, proxies or HTTP/2).

## Verification

- `./Tools~/ci/check.sh`: pass (see PR for the head SHA).
- Unity PlayMode, scoped filter over the touched fixtures: see the PR "Local gates" section.
- Not run: full Unity EditMode/PlayMode suites (periodic host-editor run), IL2CPP builds, devices. Mobile idle
  timeout and keepalive on iOS/Android are needs-device-validation (#99).
