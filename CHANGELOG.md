# Changelog

All notable changes to TurboHTTP will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Phase 14 Features**:
  - Happy Eyeballs support for fast IPv4/IPv6 dual-stack connections.
  - Proxy Support with authenticated HTTP/HTTPS proxies.
  - Background Networking for native iOS/Android seamless connection persistence.
  - Adaptive Networking for dynamic timeouts based on network quality.
  - OAuth 2.0 & OIDC integration with automated token refresh and PKCE flow.
  - Interceptor Middleware architecture for request/response mutation.
  - Mock Server capability for robust offline unit and integration testing.
  - Plugin System for zero-overhead, modular extensions.
- Initial project structure
- Modular assembly definitions
- **Documentation Rework**: Restructured documentation, added contributing guidelines, and polished user guides.
- `UHttpClientOptions.UseAdaptiveNetworking(...)` and `UseBackgroundNetworking(...)` extension methods
  (`TurboHTTP.Unity.Mobile`, `TurboHTTP.Unity` module) replace the removed options properties.
- `BouncyCastleModuleRegistration.EnsureRegistered()` (`TurboHTTP.Transport.BouncyCastle`, only with
  `TURBOHTTP_BOUNCYCASTLE`): registers the BouncyCastle TLS provider explicitly; it also runs at
  startup automatically (#196).
- CI check `check_repo.py coreapi`: the public types of `TurboHTTP.Core` must match
  `Tools~/ci/api-baseline/core-public-types.txt` (at most 60).

### Changed
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

## [1.0.0] - TBD

### Added
- Core HTTP client with fluent API
- Retry middleware with idempotency awareness
- Cache middleware with ETag support
- Authentication middleware
- Rate limiting middleware
- Observability module with timeline tracing
- File downloader with resume support
- Unity content handlers (Texture2D, AudioClip)
- Testing module with record/replay
- Performance module with memory pooling
- Editor HTTP Monitor window
- Comprehensive samples and documentation
