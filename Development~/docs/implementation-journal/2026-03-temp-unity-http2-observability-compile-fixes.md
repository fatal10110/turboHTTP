# Temp Unity HTTP/2 And Observability Compile Fixes

**Date:** 2026-03-21  
**Scope:** Restore Unity 2021.3 temp-project compilation after the latest HTTP/2 streaming and observability changes introduced Unity-incompatible runtime APIs and test-shape regressions.

## What Was Implemented

This fix slice addressed the compiler blockers surfaced by the documented temporary Unity project workflow:

1. Replaced the new HTTP/2 `Environment.TickCount64` usage with a Unity-compatible monotonic clock.
   - Added `Http2MonotonicClock` in `TurboHTTP.Transport.Http2` using `Stopwatch.GetTimestamp()`.
   - Updated HTTP/2 stall detection and recently-reset stream retention to compare elapsed time through the monotonic helper instead of relying on `TickCount64`, which is unavailable in the Unity 2021.3 profile used here.

2. Restored `TurboHTTP.Observability` access to the transport behavior state needed by streaming upload metrics.
   - Added `InternalsVisibleTo("TurboHTTP.Observability")` in `TurboHTTP.Core`.
   - This preserves the existing Phase 22a.5 design where `MetricsHandler` consumes `TransportBehaviorFlags.RequestBodyBytesSent` from `RequestContext`.

3. Re-aligned Unity runtime tests with the repository’s supported Unity NUnit profile.
   - Replaced `Assert.ThrowsAsync(...)` calls with the repo-local `AssertAsync.ThrowsAsync(...)` helper in the affected PlayMode tests.
   - Removed a stale `Http2Stream.AppendResponseData(...)` reference in `Http2BodySizeTests`; the test now stays focused on the read-loop size guard it was already simulating.

## Files Modified

Runtime:

- `Runtime/Core/AssemblyInfo.cs`
- `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`
- `Runtime/Transport/Http2/Http2ResponseBodySource.cs`
- `Runtime/Transport/Http2/Http2MonotonicClock.cs`

Tests:

- `Tests/Runtime/Observability/MetricsInterceptorTests.cs`
- `Tests/Runtime/Observability/MonitorInterceptorTests.cs`
- `Tests/Runtime/Pipeline/LoggingInterceptorTests.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.StreamingResponses.cs`
- `Tests/Runtime/Transport/Http2BodySizeTests.cs`

Docs:

- `Development/docs/implementation-journal/2026-03-temp-unity-http2-observability-compile-fixes.md`

## Decisions / Trade-Offs

1. **Used a monotonic `Stopwatch` helper instead of falling back to `Environment.TickCount`.**  
   `TickCount` wraparound math would have been workable for the current timeout budget, but `Stopwatch` keeps the fix monotonic, avoids 32-bit wrap concerns, and aligns better with the repo’s existing timing guidance for Unity-sensitive code.

2. **Kept `TransportBehaviorFlags` internal and granted targeted friend-assembly access instead of widening the public API.**  
   The metrics path is already designed around Core-owned transport state keys. `InternalsVisibleTo("TurboHTTP.Observability")` is the narrowest fix that restores the intended Phase 22a.5 behavior.

3. **Fixed Unity test compatibility at the test layer, not the runtime layer.**  
   The failing tests had drifted back to unsupported `Assert.ThrowsAsync(...)` usage and one removed helper call. The runtime behavior did not need to change to satisfy those compiler errors.

## Specialist Review Pass

Applied both required rubrics explicitly to this fix slice:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

### Review Notes

- No asmdef dependency graph changed.
- No public Core or Transport API was broadened.
- The only cross-assembly access change is the targeted friend-assembly visibility required by the existing observability design.
- HTTP/2 stall detection remains monotonic and timeout-based; the fix does not alter frame handling, flow-control rules, TLS behavior, or cancellation semantics.
- The new timing helper stays transport-local and avoids Unity-profile API gaps without introducing reflection or platform-specific branches.

## Validation

### Repository Checks

- `git diff --check`

### Temp Unity Project Validation

Ran the documented workflow from `Development/docs/how-to-run-tests-with-temp-unity-project.md` against:

- package sync target: `/tmp/turboHTTP-package`
- Unity project: `/Users/arturkoshtei/workspace/turboHTTP-testproj`
- Unity editor: `/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity`

Observed results:

- Full PlayMode after the compile fixes no longer aborted on compiler errors, but the unfiltered lane hung after entering PlayMode execution and did not emit XML:
  - log: `unity-test-all-playmode-sync-20260321-142620.log`
- A deterministic retry using `--where 'cat != ExternalNetwork'` also progressed into PlayMode execution but still hung before XML emission:
  - log: `unity-test-all-playmode-sync-no-external-20260321-143420.log`
- Full EditMode succeeded:
  - XML: `test-results-all-editmode-sync-20260321-143643.xml`
  - total `5`
  - passed `4`
  - failed `0`
  - skipped `1`
  - duration `0.1683922`

Focused PlayMode verification for the touched tests all passed:

- `TurboHTTP.Tests.Pipeline.LoggingInterceptorTests.DetailedLogging_StreamingResponseReadFailure_LogsErrorOnDispose`
- `TurboHTTP.Tests.Observability.MetricsInterceptorTests.StreamingResponseReadFailure_IsTrackedAsFailure`
- `TurboHTTP.Tests.Observability.MonitorInterceptorTests.StreamingResponseReadFailure_IsCapturedAsTransportFailure`
- `TurboHTTP.Tests.Transport.Http2.Http2ConnectionTests.SendStreamingRequest_PerStreamBufferFull_SendsCancelRstAndFaultsBody`
- `TurboHTTP.Tests.Transport.Http2.Http2BodySizeTests.ExceedingMaxResponseBodySize_StreamShouldFail`

Each focused PlayMode run produced `1` passing test, `0` failures, and exit code `0`.

## Deferred / Remaining Work

1. The temp-project compiler regressions in this slice are fixed.
2. A separate PlayMode hang remains in the full suite after compilation succeeds. The hang reproduces both in the unfiltered lane and in a retry with `--where 'cat != ExternalNetwork'`, so it should be isolated separately from this compile-fix slice.
