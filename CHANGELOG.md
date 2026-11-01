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
- CI check `check_repo.py coreapi`: the public types of `TurboHTTP.Core` must match
  `Tools~/ci/api-baseline/core-public-types.txt` (at most 60).

### Breaking
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
