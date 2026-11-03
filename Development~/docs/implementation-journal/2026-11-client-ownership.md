# 2026-11: client ownership (#63, #70, #186 status)

Lane `client-ownership` of the w1102 waves run.

## What changed

- **#63, interceptor ownership.** New `UHttpClientOptions.DisposeInterceptors` (default `false`), copied by
  `Clone()`. `UHttpClient` snapshots it into a readonly `_ownsInterceptors` in the constructor (like
  `_ownsTransport`) and calls `DisposeConfiguredInterceptors()` only when it is set. Two clients built from
  one options object and its clone can now share a `CacheInterceptor`, `CookieInterceptor` or
  `ConcurrencyInterceptor`, and disposing one of them leaves the other working.
- **#70, streaming context lifetime.** `SendStreamingAsync` no longer stops and clears the `RequestContext`
  when headers arrive. On success it attaches `RequestContext.CompleteAfterResponse` (new, internal) to the
  returned `UHttpStreamingResponse` through `AttachRequestRelease`. The response runs it after its body source
  has been disposed, so the observed-body completions of the logging, metrics and monitor handlers
  (22a.5 step 5) still read a running stopwatch and the request state. The failure and cancel paths still stop
  the context and clear it in `finally`. `CompleteAfterResponse` stops and clears at most once
  (`Interlocked.Exchange`).
- **#186.** No code. On `5b92ce8`, `rg 'Type\.GetType\("TurboHTTP' Runtime Editor Tests` finds nothing,
  `check_repo.py reflect` reports 0 problems, and no allowlist is left (PR #228, `2026-10-redaction.md`).
  Part 2 (the `EditorMonitorHook` registered by Observability) is already on main:
  `Runtime/Core/EditorMonitorHook.cs`, `Runtime/Observability/ObservabilityModuleRegistration.cs`, and the
  hook tests in `Tests/Runtime/Observability/MonitorRedactionTests.cs`. The only unchecked criterion is the
  IL2CPP High-stripping player smoke, which is device work tracked in #99. The "Deferred" lines in
  `2026-10-json-registration.md` are stale.

## Files

- `Runtime/Core/UHttpClientOptions.cs`: `DisposeInterceptors`, `Clone()`.
- `Runtime/Core/UHttpClient.cs`: `_ownsInterceptors`, `Dispose()`, `SendStreamingAsync`.
- `Runtime/Core/RequestContext.cs`: `CompleteAfterResponse()`.
- `Tests/Runtime/Core/UHttpClientInterceptorOwnershipTests.cs` (new),
  `Tests/Runtime/Core/UHttpClientStreamingContextLifetimeTests.cs` (new),
  `Tests/Runtime/Core/UHttpClientTests.Execution.cs` (the two dispose-order tests opt in with
  `DisposeInterceptors = true`).
- `Documentation~/APIReference.md`, `Documentation~/Modules/Interceptors.md` (new "Interceptor lifetime"
  section with a compiled snippet), `Samples~/05-AdvancedFeatures/AdvancedFeaturesExample.cs` (sets
  `DisposeInterceptors = true`, the client is the sole user).

## Decisions

- `DisposeInterceptors` flag, default `false`, rather than reference counting: it mirrors `DisposeTransport`
  and adds no shared mutable state. This is a behavior change: apps that relied on the client disposing its
  interceptors now set the flag or dispose them themselves (documented in both docs and the sample).
- Plugin-contributed interceptors and the editor `MonitorInterceptor` are not in `Interceptors` and are not
  affected; plugins still own theirs.
- #70 uses the existing dispose-callback hook of `UHttpStreamingResponse` instead of wrapping the body source.
  The callbacks run after `_bodySource` is disposed (both `Dispose` and `DisposeAsync`), which is after the
  observed-body completions. One delegate per streaming send; the buffered path is unchanged.
- Retry, redirect and cache interceptors never stop or clear the client's context (only the cache's background
  revalidation, which owns its own context), so intermediate hops cannot end it early.

## Deferred

- The 2026-10-30 comment on #63 (awaitable `IAsyncDisposable` shutdown, sealing, `SuppressFinalize`): #279 (v1.1).
- #186's IL2CPP High-stripping player smoke: #99 (device, owner provides hardware).
- Pre-existing, unchanged: a synchronous `Dispose()` of an unread streaming body aborts the observed source
  without running its completion, so metrics/logging do not record that request.

## Verification

- `./Tools~/ci/check.sh`: OK.
- Unity 2021.3.45f2 PlayMode, filter `UHttpClientInterceptorOwnership|UHttpClientStreamingContextLifetime|UHttpClientTests|UHttpClientDisposeRace|LoggingInterceptorTests|MetricsInterceptorTests|MonitorInterceptorTests|MonitorRedactionTests|CacheInterceptorTests|UHttpStreamingResponseTests|RetryInterceptorTests|RedirectInterceptorTests|FileDownloaderTests|PluginRegistryTests`:
  237/237 passed.
- With `UHttpClient.cs` and `RequestContext.cs` reverted to main, 5 of the 11 new tests fail
  (`SharedInterceptors_DisposingOneClient_DoesNotBreakTheOther`, `Dispose_ByDefault_...`,
  `DisposeInterceptors_IsSnapshottedAtConstruction`, `StreamingMetrics_DurationIncludesBodyDeliveryDelay`,
  `StreamingResponse_KeepsContextRunningAndReadable_UntilDisposed`); the rest guard the unchanged paths.
- Not run: full Unity suites (host editor, periodic), EditMode, IL2CPP player builds, devices.
