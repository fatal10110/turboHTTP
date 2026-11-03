# 2026-11 client-lifecycle (#178, #55, #64, #180, #181)

Wave run w1030, lane client-lifecycle.

## What

- **#178, Dispose vs plugin register/unregister.**
  - `_pluginLifecycleGate` is no longer disposed. It has no wait handle. Disposing it under waiters made the holder's `Release` throw `ObjectDisposedException` over the real result, and left queued callers waiting forever.
  - Both lifecycle calls re-check `_disposed` after entering the gate.
  - `RegisterPluginAsync` re-checks `_disposed` under `_pluginLock`, the lock Dispose clears `_plugins` under.
    - A plugin that finished initializing after Dispose is not added.
    - That plugin is shut down inline, and the caller gets `ObjectDisposedException`.
  - `RebuildPipelineSnapshot_NoLock` never resurrects a pipeline after Dispose.
  - Field ownership is now commented.
- **#55, interceptor order.**
  - There is a Core-internal `InterceptorRole` and `IInterceptorRole`, implemented explicitly by:
    - `RedirectInterceptor`
    - `AuthInterceptor`
    - `CacheInterceptor`
    - `RetryInterceptor`
    - `ConcurrencyInterceptor` (for this, Core gained an IVT for `TurboHTTP.RateLimit`)
    - the plugin `CapabilityEnforcedInterceptor` wrapper, which forwards its inner role
  - `InterceptorOrdering.FindViolation` rejects two orders:
    - Cache outside Auth (RFC 9111 section 3.5).
    - Concurrency outside Retry.
  - Where it is checked:
    - In the `UHttpClient` ctor, before any transport is created. Failure is an `ArgumentException`.
    - In `RegisterPluginAsync`, against base plus plugin interceptors. Failure is a `PluginException`, and the plugin is shut down.
  - The canonical order and its reasons are in `Documentation~/Modules/Middleware.md`.
- **#64, FollowRedirects.**
  - When `FollowRedirects` is true and no interceptor with the Redirect role is configured, the ctor writes a one-time warning through the diagnostic sink.
  - The stale "NOT enforced in Phase 3" XML docs were rewritten.
- **#180, diagnostic sink.**
  - `Core.Internal.TurboDiagnostics` provides `Log`, `Log(message, ex)`, `LogOnce`, `SetSink`, `ResetSink` and `Reset`.
  - The default sink is `UnityEngine.Debug.LogWarning` under `UNITY_5_3_OR_NEWER`, else `Trace.WriteLine`.
  - It is reset on `SubsystemRegistration`. A throwing sink is ignored.
  - Public override: `TurboHTTP.Observability.TurboHttpDiagnostics.SetSink/ResetSink`.
  - All 20 `Debug.WriteLine` sites in `Runtime/` were moved to the sink. Core gained an IVT for `TurboHTTP.WebSocket.Transport` (ProxyTunnelConnector).
- **#181, ValueTask sync-over-async.**
  - `ValueTaskAwaiter.GetResult` and ``ValueTaskAwaiter`1.GetResult`` are banned in `Tools~/ci/BannedSymbols.txt`.
  - Five new ValueTask sites were baselined, each with a `#` rationale comment:
    - HandlerCallbackSafetyWrapper and UHttpStreamingResponse: completed-only.
    - Two in DecompressionHandler and one in ResponseBodyStream: synchronous `Stream.Read` overrides.
  - Every other sync-over-async baseline entry got a rationale comment.

## Files

- Core:
  - `Runtime/Core/UHttpClient.cs`
  - `Runtime/Core/Pipeline/InterceptorOrdering.cs` (new)
  - `Runtime/Core/Internal/TurboDiagnostics.cs` (new)
  - `Runtime/Core/PluginContext.cs`
  - `Runtime/Core/UHttpClientOptions.cs` (docs)
  - `Runtime/Core/AssemblyInfo.cs`
  - `Runtime/Core/EditorMonitorHook.cs`
  - `Runtime/Core/UHttpStreamingResponse.cs`
- Modules:
  - `Runtime/{Auth,Cache,Retry,RateLimit,Middleware}/*Interceptor.cs` (role)
  - Cache, Decompression, Testing, Transport and WebSocket.Transport sink call sites
  - `Runtime/Observability/TurboHttpDiagnostics.cs` (new, public)
- CI: `Tools~/ci/BannedSymbols.txt` and `Tools~/ci/analyzer-baseline.txt`.
- Docs: `Documentation~/Modules/{Middleware,Core}.md`, `Documentation~/APIReference.md` and `Documentation~/Troubleshooting.md`.
- Tests, all in `Tests/Runtime/Core/`:
  - `UHttpClientDisposeRaceTests.cs`
  - `InterceptorOrderTests.cs`
  - `FollowRedirectsWarningTests.cs`
  - `DiagnosticSinkTests.cs`

## Decisions

- **#55: reject, do not auto-sort.**
  - This is the conservative option. Reordering silently would change behavior the caller wrote.
  - Only the two orders that are still unsafe are rejected.
  - Redirect/Auth (#56) and Redirect/Cookie (#58) are already order-independent. `AuthRedirectOrderTests` and `CookieRedirectHopTests` prove both orders on the wire.
  - `InterceptorPipeline`, a public type used directly by tests, is not validated. Only `UHttpClient` pipelines are.
- **#64: warn once, do not auto-wire.**
  - Auto-wiring would change wire behavior for every client without `RedirectInterceptor`, because it would start following 3xx.
  - It would also need a new module-registration hook.
  - Warning once makes the no-op visible and keeps behavior as configured.
- **#180: type-only messages.**
  - Messages carry exception type names, never exception text. Exception text can contain URLs, query strings or paths, and the default sink now reaches release player logs.
  - The precedent is `EditorMonitorHook` (#186).
  - Plugin `LogDiagnostic` text is passed through as written by the plugin.
  - `SaeaPlatformDetection.LogSelectedMode` is success-path info. It is now `[Conditional("TURBOHTTP_VERBOSE_DIAGNOSTICS")]`, so it stays silent by default.
- **#181: no Android bridge fix in this lane.** The lane note says real fixes ride the owning lanes. The Android bridge criterion is #88's second criterion (comment posted on #88). #181 is therefore "Part of".

## Deferred

- #88: Android bridge blocking on `MainThreadDispatcher` (the #181 criterion).
- #232: customer docs still name removed `*Middleware` types. This lane fixed only `Interceptors`, `AuthInterceptor` and the new sections.
- #63 (v1.1) was left alone: the client disposes interceptors it does not own.

## How verified

- `'./Tools~/ci/check.sh'`: pass. This covers invariants, format, and the editor-variant shadow build with analyzers.
- `shadow_build.py --variant android`: clean.
- Scratch violation for #181:
  - A file with `new ValueTask<int>(1).GetAwaiter().GetResult()` and `default(ValueTask).GetAwaiter().GetResult()` failed the shadow build with 2 new RS0030 warnings.
  - The file was deleted and not committed.
- Off-Unity NUnit run (.NET 8, NUnit 3.14):
  - Setup: an ad-hoc test project, outside the repo, compiled the 4 new test files plus `TestHelpers`/`AssertAsync` against the android shadow-build assemblies. The android variant avoids the Editor-only `Application.isPlaying` ECall.
  - With this change: 24/24 pass.
  - At origin/main 77f5820, with only the files that compile there (race and order tests), all 8 regression tests fail. Example failures:
    - "a plugin initialized after Dispose must be shut down"
    - "the plugin's own failure must surface"
    - the queued-call hang
    - "unregister must not fail"
    - no `ArgumentException`
    - no `PluginException`
- Not run:
  - Unity EditMode/PlayMode: lanes do not run Unity. Unity tests: host editor (periodic).
  - IL2CPP and devices.
  - Seeing the cache-store and transport-dispose warnings in a player's Unity console needs a host editor or device check.
