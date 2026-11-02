# Changelog

All notable changes to TurboHTTP will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0-pre.1] - 2026-11-02

First pre-release. TurboHTTP is released under the MIT License (`LICENSE.md`); third-party notices are in
`Third-Party Notices.md`.

### Modules
Assemblies shipped in this package: `TurboHTTP.Core`, `TurboHTTP.Transport` (raw-socket HTTP/1.1 and
HTTP/2 over `SslStream`), `TurboHTTP.Transport.BouncyCastle` (opt-in TLS fallback over the vendored `TurboHTTP.SecureProtocol`
library, compiled with the `TURBOHTTP_BOUNCYCASTLE` scripting symbol), `TurboHTTP.Middleware` (redirect, cookie, decompression and
default-header interceptors), `TurboHTTP.Auth` (OAuth 2.0, OIDC, PKCE), `TurboHTTP.Retry`,
`TurboHTTP.Cache`, `TurboHTTP.RateLimit`, `TurboHTTP.Observability`, `TurboHTTP.Files`, `TurboHTTP.JSON`
(with a built-in LiteJson reader/writer), `TurboHTTP.Testing` (`MockTransport`, `RecordReplayTransport`),
`TurboHTTP.Unity` (asset handlers, decoders, mobile networking), `TurboHTTP.WebSocket`,
`TurboHTTP.WebSocket.Transport`, `TurboHTTP.Unity.WebSocket`, `TurboHTTP.UniTask` (needs the UniTask
package), `TurboHTTP.Complete` (startup registration) and the Editor HTTP Monitor window. Retry, cache,
authentication and rate limiting are interceptors on `UHttpClientOptions.Interceptors`.


### Added
- **Phase 14 Features**:
  - Happy Eyeballs support for fast IPv4/IPv6 dual-stack connections.
  - Proxy Support with authenticated HTTP/HTTPS proxies.
  - Background Networking for native iOS/Android seamless connection persistence.
  - Adaptive Networking for dynamic timeouts based on network quality.
  - OAuth 2.0 & OIDC integration with automated token refresh and PKCE flow.
  - Interceptor pipeline (`IHttpInterceptor`) for request/response mutation.
  - `MockTransport` and `RecordReplayTransport` for offline unit and integration testing.
  - Plugin System for zero-overhead, modular extensions.
- Initial project structure
- Modular assembly definitions
- **Documentation Rework**: Restructured documentation, added contributing guidelines, and polished user guides.
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
  `UHttpException`) and the BouncyCastle assemblies are not linked into player builds. `TlsBackend.Auto` is unchanged
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
