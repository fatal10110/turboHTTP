# 2026-11 core-surface (#77, #156)

Wave run w1030, lane core-surface. Core's public surface goes from 76 type declarations to 50 before the API freeze.

## What

- **Mobile feature types moved out of Core (#77).**
  - Moved from `Runtime/Core` to `Runtime/Unity/Mobile` (assembly `TurboHTTP.Unity`, namespace `TurboHTTP.Unity.Mobile`): `AdaptiveInterceptor`, `AdaptiveHandler` (internal), `AdaptivePolicy`, `BackgroundNetworkingInterceptor`, `BackgroundNetworkingPolicy`, `BackgroundRequestQueuedException`, `BackgroundRequestBuilderExtensions`, `IBackgroundExecutionScope`, `IBackgroundExecutionBridge`, `IDeferredBackgroundWorkBridge`, `NetworkQuality`, `NetworkQualitySample`, `NetworkQualitySnapshot`, `NetworkQualityThresholds`, `NetworkQualityDetector`.
  - The files moved with `git mv`, so the `.meta` GUIDs are unchanged. The code changed only its namespace.
  - `UHttpClientOptions` lost `AdaptivePolicy`, `BackgroundNetworkingPolicy`, `BackgroundExecutionBridge` and `NetworkQualityDetector`. `UHttpClient.BuildInterceptors` no longer inserts these interceptors (`TryAppendAdaptiveInterceptor`, `TryAppendBackgroundNetworkingInterceptor` and `ContainsPipelineComponent<T>` are gone).
  - The replacement is `MobileNetworkingOptionsExtensions.UseAdaptiveNetworking` / `UseBackgroundNetworking` (new public API, XML-documented).
    - Both insert at the front of `options.Interceptors`, giving the same order as the old auto-insertion: adaptive, then background, then the caller's interceptors, whichever is called first.
    - Calling either a second time returns the existing instance.
    - A `null` policy means an enabled default. Policies are cloned, as before.
    - With a `null` bridge, `UseBackgroundNetworking` takes `BackgroundExecutionBridgeFactory.CreateDefault()`. That is iOS/Android in players and `null` elsewhere, as `CreateUnityClient` did. `CreateUnityClient` no longer reads a policy.
- **Plumbing made internal (#77).**
  - In `Core.Internal`: `ObjectPool<T>`, `ObjectPoolDiagnostics`, `ByteArrayPool`, `ByteArrayPoolDiagnostics`, `SegmentedBuffer`, `SegmentedReadStream`.
  - In Core: `PlatformConfig`, `PlatformInfo`, `IL2CPPCompatibility`, `ProjectJsonBridge`, and `IJsonBridge`. `IJsonBridge` is only reachable through `ProjectJsonBridge.Register`, and keeping it public with no public way to register made no sense.
  - `PoolHealthReporter.Register<T>(string, ObjectPool<T>)` (Observability) is internal too. Otherwise it fails with CS0051.
- **Core IVT gains `TurboHTTP.Unity`.** It is needed for `ProjectJsonBridge.GetRequired` (`CoroutineWrapper`) and for `UHttpRequest.CopyWithSharedContent` / `SetTimeoutInternal` (`AdaptiveInterceptor`). This widens the list that #76 wants to shrink, by one line, and #76 tracks the public safe-copy primitive that would remove it.
- **API baseline (#77 AC1).**
  - `Tools~/ci/api-baseline/core-public-types.txt` holds 50 sorted `Namespace.Name` entries. Its header states the counting rule (every `public` type declaration line under `Runtime/Core`, top-level or nested, ignoring `#if`) and a `# count:` line.
  - `check_repo.py coreapi` runs by default, so it runs in `check.sh` and in CI.
    - It fails on a public type missing from the list, on a stale or duplicate entry, on a wrong count header, and on more than 60 types.
- **#156 pulled in.**
  - `LegacyHttpHandlerCompatExtensions` moved from Core (it was `public`, behind `#if UNITY_INCLUDE_TESTS`) to `Tests/Runtime`, as an internal class in namespace `TurboHTTP.Tests`. The `RedirectInterceptorTests` and other callers resolve it through the enclosing namespace.
  - Removed the obsolete `MockTransport.SendAsync` and `RecordReplayTransport.SendAsync`. They had no callers.
  - Removed the obsolete internal test helper `Http11ResponseParser.ReadLineAsync`. Its one test now uses `BufferedStreamReader` directly.
- **Docs.**
  - CHANGELOG `### Breaking` has one bullet per moved or hidden type.
  - `Documentation~/APIReference.md`: the options table and the adaptive sample, which was stale (`AdaptiveMiddleware` did not exist).
  - `Documentation~/PlatformNotes.md` no longer tells users to call `IL2CPPCompatibility.Validate`.

## Decisions

- **Destination: `TurboHTTP.Unity`, not a new `TurboHTTP.Mobile` asmdef.**
  - Unity is the only non-test consumer: it implements the bridge interfaces in `Mobile/iOS` and `Mobile/Android`.
  - A separate module would need a new `TurboHTTP.Unity -> TurboHTTP.Mobile` reference, an `ALLOWED_REFS` exception, or would need the bridge interfaces to stay in Core.
  - The phase-14 plans call these "mobile" features.
  - Adaptive and NetworkQuality are engine-free and could move to an engine-free module later without another Core change.
- **`ProxySettings`, `ProxyBypassMatcher`, `ProxyEnvironmentResolver` stay public.** `ProxySettings` is transport configuration on `UHttpClientOptions.Proxy`. The two helpers are cheap candidates if the cap ever gets tight; they were not needed.
- **`IL2CPPCompatibility` is internal even though `PlatformNotes.md` documented it.** The issue lists it. The checks still run in the package's PlayMode tests (`PlatformTests`).
- **Options clones now share the adaptive/background interceptor instances**, like every other interceptor in `options.Interceptors`. Before, each client got its own auto-inserted instance. These interceptors are not `IDisposable`, so the client's interceptor dispose pass ignores them.

## Deferred

- #76 (v1.1): a public request-derivation/body-discard API, and shrinking the IVT list (now 15 entries, including `TurboHTTP.Unity`). Not pulled in: it is new public surface, against this issue's cap.

## Verification

- `./Tools~/ci/check.sh`: OK. That covers repo invariants including `coreapi`, format, and the shadow build of every runtime asmdef (editor variant).
- `shadow_build.py --variant all`: OK.
- Negative runs of `coreapi`: making `PlatformInfo` public again fails, and dropping a baseline line fails (plus the count header).
- New NUnit tests:
  - `Tests/Runtime/Core/CoreSurfaceTests.cs`: hidden types are not visible, moved types are absent from Core, Core exports at most 60 types, and the mock transports have no `SendAsync`. These fail on the old code.
  - `Tests/Runtime/Mobile/MobileNetworkingOptionsExtensionsTests.cs`: order, idempotence, and null handling.
- Migrated tests: `AdaptiveInterceptorTests` (now uses the extension), `BackgroundNetworkingTests`, `NetworkQualityDetectorTests`, `MobileBackgroundNetworkingSmokeTests`, `InterceptorPipelineTests`, `Http11ResponseParserTests`.
- **Not run:** Unity EditMode/PlayMode, IL2CPP, devices. The test assemblies are compile-checked only by Unity: the host-editor periodic run.
