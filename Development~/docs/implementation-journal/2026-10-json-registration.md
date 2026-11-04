# 2026-10: JSON bridge and transport-capability registration (#67, #95, #179, part of #186)

## What
Hidden by-name reflection between assemblies is replaced by registration APIs:

- **JSON bridge (#67, #95, #186 part 3).** New Core interface `IJsonBridge`
  (`object Deserialize(string, Type)`, `string Serialize(object, Type)`). `ProjectJsonBridge` keeps
  its public static `Serialize/Deserialize(..., requiredBy)` surface (8 callers in Auth, Testing,
  WebSocket unchanged) but is now backed by one `volatile IJsonBridge` field: `Register`,
  `IsRegistered`, `GetRequired(requiredBy)`, internal `Reset()` for tests. No `Type.GetType`,
  `GetMethod` or `Invoke` remains. Missing bridge -> `InvalidOperationException` naming
  TurboHTTP.JSON, the feature, and the fix (`JsonModuleRegistration.EnsureRegistered()`).
- TurboHTTP.JSON: `JsonModuleRegistration.EnsureRegistered()` registers a bridge forwarding to
  `JsonSerializer` (so `JsonSerializer.SetDefault` is honored).
- TurboHTTP.Complete bootstrap: new `TurboHttpBootstrap.RegisterJsonBridge()` with the same
  `[Preserve]`, `SubsystemRegistration` and `InitializeOnLoadMethod` hooks as
  `RegisterDefaultTransports` (added lines only; see `2026-10-bootstrap.md`).
- `CoroutineWrapper.GetJsonCoroutine<T>`: no reflection or `MakeGenericMethod`; reproduces
  `JsonExtensions.GetJsonAsync<T>` (GET, `Accept: application/json`, `EnsureSuccessStatusCode`,
  empty body -> `default(T)`, UTF-8 body) and deserializes through `ProjectJsonBridge.GetRequired`,
  so serializer exceptions reach `onError` unwrapped as before. **Public API change:** the
  `where T : class` constraint is removed, value types work (boxed result unboxed).
- `RecordReplayTransport` serialization routes through the bridge (own wrap messages kept).
- **PlatformConfig (#179).** `RecommendedTimeout`/`RecommendedMaxConcurrency` are compile-time
  constants (`#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR`: 45 s / 8, else 30 s / 16, the
  values `PlatformInfo.IsMobile` produced). No static fields, no static constructor, no reflection,
  no UnityEngine call when constructing `UHttpClientOptions`/`ConnectionPoolOptions`.
- **Transport capabilities (#179, #186 part 1).** New `[Flags] TransportCapabilities { None, Http2 }`,
  `HttpTransportFactory.RegisterCapabilities(Func<TransportCapabilities>)` and
  `HttpTransportFactory.Capabilities`. Every `Register` overload and `Reset()` clear it (capabilities
  belong to the registered transport). `RawSocketTransport.EnsureRegistered()` registers a lazy probe
  (SslStream ALPN or BouncyCastle available). `PlatformConfig.SupportsHttp2` now reads it.
- **CI check (#186).** `Tools~/ci/check_repo.py reflect` fails on an assembly-qualified
  `"TurboHTTP.X, TurboHTTP.Y"` literal in `Runtime/**/*.cs` (vendored Lib excluded), with a
  temporary allowlist: `Runtime/Core/UHttpClient.cs` (editor monitor, redaction lane, #186 part 2)
  and `Runtime/Transport/Tls/TlsProviderSelector.cs` (BouncyCastle lookup, TLS lanes).
- `Runtime/JSON/link.xml`: entries kept (`TurboHttpLinkerProcessorTests` asserts them), comment
  rewritten since nothing reflects into TurboHTTP.JSON any more.

## Files
Runtime/Core/{IJsonBridge,ProjectJsonBridge,TransportCapabilities,HttpTransportFactory,PlatformConfig}.cs,
Runtime/JSON/{JsonModuleRegistration.cs,link.xml}, Runtime/Bootstrap/TurboHttpBootstrap.cs,
Runtime/Transport/RawSocketTransport.cs, Runtime/Unity/CoroutineWrapper.cs,
Runtime/Testing/RecordReplayTransport.Serialization.cs, Tools~/ci/check_repo.py.
Tests: Tests/Runtime/Core/JsonBridgeRegistrationTests.cs,
Tests/Runtime/Platform/PlatformConfigNoReflectionTests.cs,
Tests/Runtime/Unity/CoroutineWrapperJsonBridgeTests.cs.

## Decisions
- Registration over a flat Auth token parser or link.xml preservation (lane note; #186 design).
- Bridge is non-generic (`Type` argument) so no generic virtual method crosses the interface (AOT).
- Capabilities are a lazy probe so startup registration does not initialize the TLS providers.
- `SupportsHttp2` kept (now registration-backed) rather than removed: no public API removal.
- No `[ModuleInitializer]` in TurboHTTP.JSON: Unity 2021.3 does not run it reliably and it would add
  a CA2255 analyzer warning (new warnings are fixed, not baselined). TurboHTTP.Complete excludes
  WebGL, so WebGL projects (and projects that strip Complete) call
  `JsonModuleRegistration.EnsureRegistered()`; without it every JSON path fails with the clear error.
- `Type.GetType` itself is not added to BannedSymbols (IL2CPPCompatibility self-test and the
  allowlisted sites use it); the string check in check_repo.py plus the PlatformConfig test cover it.
- `Development/docs/agents/architecture.md` (Auth reaches JSON through reflection, #67) is now
  stale; lanes leave the guides alone, the owner updates it.

## Deferred
- #186 part 2 (editor monitor hook) and removing the two allowlisted check_repo entries: redaction
  lane and TLS lanes; #186 stays open.
- `JsonWebSocketSerializer` `Activator.CreateInstance`: #192.
- IL2CPP High-stripping device runs (OAuth refresh, GetJsonCoroutine class/struct): #99.

## Verification
- `./Tools~/ci/check.sh`: pass (repo invariants incl. the new reflect check, format, shadow build
  with analyzers). The reflect check was exercised with the allowlist emptied: it reports exactly
  UHttpClient.cs:17 and TlsProviderSelector.cs:99/120.
- Not run: Unity EditMode/PlayMode (lanes do not run Unity; host editor, periodic), IL2CPP, device.
  The new tests are not compiled by the shadow build (test assemblies need Unity).
