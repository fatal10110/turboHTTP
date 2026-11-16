# Changelog

All notable changes to TurboHTTP will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

User-visible changes merged since 1.0.0-pre.1 (2026-11-03 onward). Feature freeze still applies: these are
fixes, hardening, validation and documentation.

### Added
- `Http2Options.EnableHttp2ThroughConnectTunnels` (default false): HTTP/2 inside CONNECT proxy tunnels is
  opt-in (#382, #103). `Http2Options` also gained optional extra connections per origin and an idle-reuse
  PING probe (#238, #396).
- The HTTP/2 response handler chain runs off the read loop (#29, #393).
- `UHttpClient.DisposeAsync` awaits plugin shutdown before disposing interceptors and the transport (#385).
- Public API reference documentation: the public Core, Transport, proxy, middleware, auth, cache, retry,
  WebSocket and Unity surfaces carry XML docs, and CS1591 is enforced for Core and Transport
  (#1228-#1233).

### Changed
- `WebSocket` `DisposeAsync` sends a Close frame (1000) instead of aborting (#301, #390).
- `LiteJson` follows the RFC 8259 grammar strictly (invalid JSON that parsed before now throws), accepts a
  BOM, reads UTC timestamps, has `MaxInputBytes` and throws `NotSupportedException` for POCOs (#75, #395).
- Download verification runs whenever an expected hash is set, not only with `VerifyChecksum` (#1201).
- Unity texture and audio decodes are bounded by default and fail closed on unreadable image headers (#1200).
- `SslStream` connections request ALPN through the netstandard2.1 API without reflection, and report no
  ALPN or HTTP/2 where the runtime cannot negotiate it (#303, #381, #418, #430).
- The wire host is checked as an RFC 3986 host before any socket opens (#340, #358); `localhost` names
  resolve to loopback without the platform resolver (#322, #351).
- Happy Eyeballs interleaves address families (#361, #362, #373).
- WebSocket sends the A-label wire host in the `Host` header and CONNECT authority (#309, #371).
- Decompression is bounded: chain cap, per-layer work budget and all `Content-Encoding` lines are
  honored (#1162); Range requests skip content coding and 206 passes through undecoded (#1172).
- HTTP/1.1: empty POST/PUT/PATCH are framed, request `Connection: close` is honored (#1165); response field
  values with control characters are rejected and only SP/HTAB are trimmed (#1186); one stale-connection
  retry for direct and proxied requests (#1193).
- Proxy decision is made per dispatch and request-level `ProxySettings` are validated (#1196); the wire
  (A-label) host is used for proxy bypass, cleartext policy, pool and DNS (#378, #368).
- `'/path'` requests resolve against `BaseUrl` on every runtime and client policy applies to standalone
  requests (#563, #564).
- Cache: sits inside Redirect and Cookie and guards stores on the request as sent (#1198); Vary variants
  are bounded per key and stale entries that can be revalidated are kept (#1179); delta-seconds clamp to
  2^31 and responses without freshness are stale (#1175); only complete streamed bodies are stored
  (#1191); `response_delay` is added to the stored Age (#306, #383).
- Redirects: credential defaults are origin-bound, hops dispatch after the previous hop (#1189).
- OAuth: `redirect_uri` is sent as registered, one refresh is shared per key and rejected tokens recover
  (#1185); `AuthInterceptor` scopes tokens to allowed origins and keeps a caller-set `Authorization` (#1182).
- Cookies: `CookieJar` rejects control characters, parses RFC 6265 cookie-date `Expires` and a
  trailing-dot `Domain` (#1178).
- HTTP/2: stream lifecycle hardening (late signals, upload failure after response start, `:status` rules)
  (#1170); trailers kept on buffered responses (#539, #1188); one dial for a cold origin (#736, #1181);
  consumed-chunk `WINDOW_UPDATE` sent under the connection token (#687, #689); a request deadline after
  response start reports `Timeout` (#409, #420).
- WebSocket: connect failures are classified by cause, `MaxFragmentCount` raised, transport TLS backend
  honored (#1194); keep-alive is correct under backpressure and suspension (#1190); the resilient
  reconnect stops on `CloseAsync`/`Abort` (#1168); `client.WebSocket(url)` holds early messages, inherits
  only safe headers and applies the client proxy (#1199).
- Observability: URLs are redacted in timeline events, errors, URL-valued headers and Monitor exports
  (#1203); compressed bodies are decoded before redaction (#1187).
- Happy Eyeballs keeps racing at the attempt cap and after a failure (#814, #815, #1197).
- Android: deferred-work jobs end on their first run and orphans are cleared at startup (#1211); the
  cleartext policy is rooted in the Complete bootstrap (#1214).
- Tooling: `Tools~/ci/check.sh` gates compile the opt-in define code, replay the fuzz corpus and pin
  asmdef settings (#1224); allocation gates use counted bytes with measured baselines (#881).

### Fixed
- HTTP/1.1 repeats a request once without `Expect: 100-continue` after an early 417 (#214, #392).
- WebSocket: a disconnect of the replacement connection is never lost during a reconnect (#427).
- Socket channels: cancellation wins over any I/O error (#410, #415, #425); no new socket is opened while
  an abandoned socket read is pending (IL2CPP descriptor reuse, #369, #377); `PollSelect` `SendAsync` no
  longer blocks on a full send buffer (#329, #339).
- The HTTP/1.1 head read and the CONNECT reply read race the cancellation token (#293, #346).
- IL2CPP: exceptions thrown in catch clauses stay out of sibling clauses and the HTTP/2 stream is claimed
  before cancel (#318, #320, #334).
- Android: JNI calls run on one attached thread (#328, #335).
- Apple TLS: "no chain to a trusted anchor" (-25318) is reported as `UntrustedRoot` (#333).
- Managed Stripping Medium/High keeps Mono's configuration init (#453).
- The vendored BouncyCastle library (`TurboHTTP.SecureProtocol`) compiles only with `TURBOHTTP_BOUNCYCASTLE`,
  like its glue: without the define no player contains it, including Mono builds with managed stripping
  Disabled, which shipped it before (#870).
- HPACK: the `{` Huffman code and the encoder dynamic table cap (#1161).
- HTTP/1.1 body, 407-drain and WebSocket handshake reads race their token (#345, #226, #1204); pending body
  reads fail on Abort/Dispose and a connection with unread bytes is never pooled (#1174).
- Socket channels: abandoned-read descriptors are held until the read ends (#384, #448, #455); the
  PollSelect poll thread owns the socket close (#1206).
- BouncyCastle TLS: the subject CN is never used as a server identity (#929, #1208); Dispose never blocks
  on a write and data survives a failed reply write (#910, #912); the handshake returns at its deadline
  or cancel (#344).
- Dropped response bodies keep their buffers off the pool and an abandoned streaming body aborts
  (#582, #583, #1183).
- Unity: coroutine requests clean up outside the iterator (#1205); Metrics and Monitor capture request
  data before a streamed body disposes the request (#1171).
- WebSocket user handlers are guarded, connect cancels on Abort and `Create()` works without a transport
  (#1177).

### Breaking
- WebSocket plumbing types are now `internal` (#1145, #1215): `WebSocketFrameReader`,
  `WebSocketFrameReadLease`, `WebSocketFrameWriter`, `WebSocketFrame`, `WebSocketCloseStatus`,
  `MessageAssembler`, `WebSocketAssembledMessage`, `WebSocketHandshakeRequest`, `WebSocketHandshake`,
  `WebSocketHandshakeResult`, `WebSocketHandshakeException`, `WebSocketHandshakeValidator`,
  `WebSocketExtensionNegotiator`, `WebSocketExtensionNegotiationResult`, `WebSocketConstants`,
  `WebSocketProtocolException` and `WebSocketConnection`. `WebSocketClient` is now `sealed`.
- HTTP/2 inside CONNECT tunnels is off by default (#382); set
  `Http2Options.EnableHttp2ThroughConnectTunnels` to opt in.

## [1.0.0-pre.1] - 2026-11-02

First pre-release. TurboHTTP is released under the MIT License (`LICENSE.md`); third-party notices are in
`Third-Party Notices.md`.

### Release status
- Feature freeze until 1.0.0 GA: bug fixes, validation and docs only (#10). Phases 16, 19c, 22d, 23, 24
  (except decompression, already shipped) and 25 are post-1.0 and not in this package.
- Gated in 1.0: HTTP/2 through a CONNECT proxy tunnel is opt-in and off by default
  (`Http2Options.EnableHttp2ThroughConnectTunnels`, #382); device validation (#103) is required before 1.0.0;
  WebSocket permessage-deflate is experimental until its verification gates close (#104); background
  networking is experimental and opt-in (#266); BouncyCastle TLS is opt-in (`TURBOHTTP_BOUNCYCASTLE`, #259).
- `1.0.0-pre.N` tags are cut from `main` while the physical-device matrix (#99) waits for hardware;
  `1.0.0` follows #99 and a green full host-editor run. Unity tests run only in the host editor (#188).
- Distribution: Git URL with a release tag (`https://github.com/fatal10110/turboHTTP.git#v1.0.0-pre.1`) and
  OpenUPM (`com.turbohttp.complete`). There is no `.unitypackage` or Asset Store listing for 1.0 (#6, #101).
  Internal docs moved to `Development~/`, so Unity no longer imports them into projects.
- The roadmap and milestone mapping are in `Development~/docs/00-overview.md`.

### Modules
Assemblies shipped in this package: `TurboHTTP.Core`, `TurboHTTP.Transport` (raw-socket HTTP/1.1 and
HTTP/1.1 over `SslStream`; Unity's `SslStream` sends no ALPN, so HTTP/2 over TLS needs the BouncyCastle backend,
see `Documentation~/PlatformNotes.md`), `TurboHTTP.Transport.BouncyCastle` (opt-in TLS fallback over the vendored `TurboHTTP.SecureProtocol`
library, compiled with the `TURBOHTTP_BOUNCYCASTLE` scripting symbol; the vendored `TurboHTTP.SecureProtocol` assembly itself is always compiled), `TurboHTTP.Middleware` (redirect, cookie, decompression and
default-header interceptors), `TurboHTTP.Auth` (OAuth 2.0, OIDC, PKCE), `TurboHTTP.Retry`,
`TurboHTTP.Cache`, `TurboHTTP.RateLimit`, `TurboHTTP.Observability`, `TurboHTTP.Files`, `TurboHTTP.JSON`
(with a built-in LiteJson reader/writer), `TurboHTTP.Testing` (`MockTransport`, `RecordReplayTransport`),
`TurboHTTP.Unity` (asset handlers, decoders, mobile networking), `TurboHTTP.WebSocket`,
`TurboHTTP.WebSocket.Transport`, `TurboHTTP.Unity.WebSocket`, `TurboHTTP.UniTask` and
`TurboHTTP.UniTask.WebSocket` (both need the UniTask package), `TurboHTTP.Complete` (startup registration) and the Editor HTTP Monitor window. Retry, cache,
authentication and rate limiting are interceptors on `UHttpClientOptions.Interceptors`.


### Added
- Initial feature set (this is the first release, so these are the shipped features rather than changes
  since an earlier one):
  - Happy Eyeballs (RFC 8305) dual-stack connection racing (`HappyEyeballsOptions`).
  - Proxy support: HTTP/HTTPS proxies with `Proxy-Authorization` and CONNECT tunnels; HTTP/2 through
    CONNECT is opt-in via `Http2Options.EnableHttp2ThroughConnectTunnels` (default false, #382); device
    validation is tracked in #103 (required before 1.0.0).
  - Adaptive networking (`UseAdaptiveNetworking`): timeouts adapted to measured network quality
    (`TurboHTTP.Unity`).
  - Background networking (`UseBackgroundNetworking`): experimental and opt-in with
    `TURBOHTTP_BACKGROUND_NETWORKING`; iOS and Android device validation is pending (#99, #266).
  - OAuth 2.0 and OIDC token acquisition and refresh with PKCE (`TurboHTTP.Auth`).
  - Interceptor pipeline (`IHttpInterceptor`) for request and response mutation.
  - `MockTransport` and `RecordReplayTransport` for offline unit and integration testing.
  - Plugin hooks (`IHttpPlugin`, `PluginContext`) for modular extensions.
  - Modular assembly definitions, documentation (`Documentation~/`), samples and contributing guidelines.
- `UHttpClientOptions.UseAdaptiveNetworking(...)` and `UseBackgroundNetworking(...)` extension methods
  (`TurboHTTP.Unity.Mobile`, `TurboHTTP.Unity` module) replace the removed options properties.
- `BouncyCastleModuleRegistration.EnsureRegistered()` (`TurboHTTP.Transport.BouncyCastle`, only with
  `TURBOHTTP_BOUNCYCASTLE`): registers the BouncyCastle TLS provider explicitly; it also runs at
  startup automatically (#196).
- `JsonUtilitySerializer` (`TurboHTTP.JSON` namespace, `TurboHTTP.Complete` assembly): an
  `IJsonSerializer` backed by Unity's `JsonUtility`, so `AsJson<T>()`, `WithJsonBody(...)` and
  `JsonWebSocketSerializer<T>` work with `[Serializable]` classes with public fields (#271). Types
  without a serializable field (property-only DTOs) and anonymous types throw
  `JsonSerializationException` instead of producing `{}`.
- CI check `check_repo.py coreapi`: the public types of `TurboHTTP.Core` must match
  `Tools~/ci/api-baseline/core-public-types.txt` (at most 60).

### Changed
- `JsonSerializer.Default` is `JsonUtilitySerializer` after startup when `TurboHTTP.Complete` is in the
  project (installed only over the built-in LiteJson default, so an earlier `SetDefault` wins), and
  `JsonSerializer.HasCustomSerializer()` returns true after startup. `ResetToDefault()` still restores
  LiteJson (#271).
- `DecompressionInterceptor`: the default decoded-size limit (`maxDecompressedBodySizeBytes`) is now
  32 MB instead of 256 MB, sized for mobile memory budgets (#74). The same value caps a compressed body
  with a known length. Pass a larger value to the constructor to keep the old limit.
- `DecompressionInterceptor`: buffered responses are decoded into pooled 16 KB segments that the
  response takes over, instead of a growing `MemoryStream` and a final copy (about half the peak
  memory) (#74).
- WebSocket permessage-deflate: on Unity Mono (and IL2CPP) client messages to a server that keeps
  its inflater context (no `client_no_context_takeover`) are now compressed by a managed sync-flush
  encoder instead of being sent uncompressed (#270).

### Fixed
- Background networking (experimental): Android builds no longer break on the Kotlin plugin's missing
  `androidx.work` dependency; the plugin is now `Plugins/Android/TurboHttpBackground.androidlib`
  (Java) declaring `androidx.work:work-runtime` itself (#89). iOS begin/end and Android JNI calls run
  on the calling thread instead of through `MainThreadDispatcher`, so they work while the player loop
  is paused and no worker blocks on the main thread (#88). An iOS foreground request is no longer
  cancelled after 25 s: `backgroundTimeRemaining` = `DBL_MAX` reads as unlimited and expiry comes only
  from iOS's expiration handler (#87). The iOS native plugins are no longer shipped to tvOS (#176).
- `Content-Encoding: deflate` is decoded as the zlib format (RFC 9110 section 8.4.1.2) with its
  Adler-32 checked; raw DEFLATE bodies still decode. A gzip body made of several members decodes, and
  each member's CRC32 and size trailer is checked (#219).

### Breaking
- `JsonWebSocketSerializer<T>`: the reflection mapper is removed; messages go through the registered
  JSON serializer only (#192). DTOs with properties are no longer read or written (with
  `JsonUtilitySerializer`, a property-only DTO fails with `WebSocketError.SerializationFailed`; use
  public fields or register a full serializer with `JsonSerializer.SetDefault`), and a serializer failure
  is no longer retried through dictionary normalization or private-constructor activation.
- Background networking is experimental and opt-in (#106): define `TURBOHTTP_BACKGROUND_NETWORKING`
  per build target in Player Settings to build in the iOS/Android native bridges (an Editor gate
  leaves the plugins out of builds without it). Without the define, `UseBackgroundNetworking()` runs
  with foreground semantics. `IosBackgroundTaskBridge` lost its `fallbackBudget` constructor
  parameter (expiry no longer uses a timer).
- BouncyCastle TLS is opt-in (#196): define `TURBOHTTP_BOUNCYCASTLE` per build target in
  Player Settings > Scripting Define Symbols to compile the `TurboHTTP.Transport.BouncyCastle`
  glue. Without the define, TLS connections with `TlsBackend.BouncyCastle` fail (the provider
  lookup throws `InvalidOperationException`, the inner exception of the request's
  `UHttpException`) and nothing roots the BouncyCastle library, so IL2CPP builds and Mono builds with managed stripping enabled leave `TurboHTTP.SecureProtocol` out (recorded at stripping High; a Mono build with stripping Disabled ships it). `TlsBackend.Auto` is unchanged
  (it never selects BouncyCastle).

Core public surface reduced before the API freeze (#77, #156). Moved types keep their names; add
`using TurboHTTP.Unity.Mobile;` and reference the `TurboHTTP.Unity` assembly.
- `AdaptiveInterceptor` moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile` (`TurboHTTP.Unity`).
- `AdaptivePolicy` moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile`.
- `BackgroundNetworkingInterceptor` moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile`.
- `BackgroundNetworkingPolicy` moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile`.
- `BackgroundRequestQueuedException` moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile`.
- `BackgroundRequestBuilderExtensions` (`WithBackgroundReplayKey`) moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile`.
- `IBackgroundExecutionBridge` moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile`.
- `IBackgroundExecutionScope` moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile`.
- `IDeferredBackgroundWorkBridge` moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile`.
- `NetworkQualityDetector` moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile`.
- `NetworkQuality` moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile`.
- `NetworkQualitySample` moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile`.
- `NetworkQualitySnapshot` moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile`.
- `NetworkQualityThresholds` moved from `TurboHTTP.Core` to `TurboHTTP.Unity.Mobile`.
- `UHttpClientOptions.AdaptivePolicy`, `.BackgroundNetworkingPolicy`, `.BackgroundExecutionBridge` and
  `.NetworkQualityDetector` removed, and `UHttpClient` no longer inserts the adaptive/background
  interceptors itself: call `options.UseAdaptiveNetworking(...)` / `options.UseBackgroundNetworking(...)`.
  `UnityExtensions.CreateUnityClient` no longer reads a background policy; `UseBackgroundNetworking`
  picks the platform bridge when none is passed.
- `ObjectPool<T>` (`TurboHTTP.Core.Internal`) is now internal.
- `ObjectPoolDiagnostics` (`TurboHTTP.Core.Internal`) is now internal.
- `ByteArrayPool` (`TurboHTTP.Core.Internal`) is now internal.
- `ByteArrayPoolDiagnostics` (`TurboHTTP.Core.Internal`) is now internal.
- `SegmentedBuffer` (`TurboHTTP.Core.Internal`) is now internal.
- `SegmentedReadStream` (`TurboHTTP.Core.Internal`) is now internal.
- `PlatformConfig` is now internal.
- `PlatformInfo` is now internal.
- `IL2CPPCompatibility` is now internal (its checks run in the package's PlayMode tests).
- `ProjectJsonBridge` is now internal: `TurboHTTP.JSON` registers itself as before (bootstrap or
  `JsonModuleRegistration.EnsureRegistered()`); plug a custom serializer in with `JsonSerializer.SetDefault`.
- `IJsonBridge` is now internal (it was only usable through `ProjectJsonBridge.Register`).
- `PoolHealthReporter.Register<T>(string, ObjectPool<T>)` (`TurboHTTP.Observability`) is now internal.
- `LegacyHttpHandlerCompatExtensions` removed from Core (it compiled only with `UNITY_INCLUDE_TESTS`; now test code).
- Obsolete `MockTransport.SendAsync(UHttpRequest, RequestContext, CancellationToken)` and
  `RecordReplayTransport.SendAsync(...)` (`TurboHTTP.Testing`) removed: use `DispatchAsync` or
  `TransportDispatchHelper.CollectResponseAsync`.
