# Temp Unity HTTP/1 And HTTP/2 PlayMode Runtime Fixes

**Date:** 2026-03-21  
**Scope:** Resolve the remaining temp-project PlayMode hangs and failures after compilation was restored, then rerun the documented Unity 2021.3 workflow to a green result.

## What Was Implemented

This fix slice addressed the runtime and test-shape issues that were still blocking the full PlayMode lane:

1. Restored deterministic HTTP/1 response-body cancellation and timeout handling in Unity.
   - `Http11ResponseBodySource` now races active body-read operations against the effective cancellation token instead of assuming Unity's underlying `NetworkStream.ReadAsync(...)` will honor token cancellation promptly.
   - When timeout or caller cancellation wins, the body source closes the lease before surfacing the cancellation so the connection is not reused in an indeterminate state.
   - This restores the intended contract for both buffered request timeouts and streaming consumer cancellation.

2. Fixed the remaining HTTP/2 buffered/streaming response regressions exposed by the isolated PlayMode batches.
   - `Http2Connection.SendRequestAsync(...)` now stops waiting indefinitely for stream completion when the caller cancels after headers were sent; it cancels the stream, removes it from active tracking, and sends `RST_STREAM(CANCEL)`.
   - `Http2Stream.Fail(...)` now normalizes transport failures to `UHttpException(UHttpErrorType.NetworkError, ...)` so buffered collection stays aligned with the repo's transport error model.

3. Re-aligned the affected tests with the current runtime contracts and Unity PlayMode constraints.
   - Converted remaining PlayMode-incompatible `async Task` test signatures to the repo's `AssertAsync.Run(...)` pattern.
   - Updated stale HTTP/2 protocol/error assertions to the current wrapped error surface and frame ordering reality.
   - Refreshed a few tests that had drifted behind current request-body, streaming-length, and buffered trailer behavior.

## Files Modified

Runtime:

- `Runtime/Transport/Http1/Http11ResponseBodySource.cs`
- `Runtime/Transport/Http2/Http2Connection.cs`
- `Runtime/Transport/Http2/Http2Stream.cs`

Tests:

- `Tests/Runtime/Core/BackgroundNetworkingTests.cs`
- `Tests/Runtime/Core/UHttpRequestBodyTests.cs`
- `Tests/Runtime/Core/UHttpStreamingResponseTests.cs`
- `Tests/Runtime/Files/FileRequestBodyTests.cs`
- `Tests/Runtime/Performance/StreamingAllocationGateTests.cs`
- `Tests/Runtime/Pipeline/BufferedDispatchBridgeTests.cs`
- `Tests/Runtime/Pipeline/InterceptorPipelineTests.cs`
- `Tests/Runtime/Testing/MockResponseBodySourceTests.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.AdditionalCoverage.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.ProtocolAndCleanup.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.StreamingResponses.cs`
- `Tests/Runtime/Transport/Http2/SingleReaderChannelTests.cs`

Docs:

- `Development/docs/implementation-journal/2026-03-temp-unity-http1-http2-playmode-runtime-fixes.md`

## Decisions / Trade-Offs

1. **Fixed HTTP/1 cancellation at the body-source boundary instead of weakening the test expectations.**  
   The failing timeout and user-cancellation tests reflected intended runtime behavior. The right fix was to make HTTP/1 body reads resilient to Unity's async stream cancellation quirks, not to relax the tests.

2. **Closed the HTTP/1 lease on cancellation/timeout rather than trying to preserve keep-alive reuse.**  
   Once a read is abandoned because the caller or timeout fired, the underlying stream may still have unread bytes or an in-flight read. Disposing the lease is the safer networking choice.

3. **Kept HTTP/2 failure normalization inside the transport stream path.**  
   Buffered collectors and middleware already expect transport faults to surface as `UHttpException` / `UHttpError`. Normalizing the failure where the stream transitions to faulted preserves that contract without widening test-only special cases.

## Specialist Review Pass

Applied both required rubrics explicitly to this fix slice:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

### Review Notes

- No asmdef boundaries or module dependencies changed.
- No public API surface changed.
- HTTP/1 cancellation now fails closed by disposing the affected lease, which is the correct pooling behavior for an interrupted read.
- HTTP/2 cancellation continues to emit `RST_STREAM(CANCEL)` and now avoids the previous indefinite wait when the caller cancels after request send.
- The runtime changes stay transport-local and avoid Unity engine dependencies.

## Validation

### Repository Checks

- `git diff --check`

### Temp Unity Project Validation

Ran the documented workflow from `Development/docs/how-to-run-tests-with-temp-unity-project.md` against:

- package sync target: `/tmp/turboHTTP-package`
- Unity project: `/Users/arturkoshtei/workspace/turboHTTP-testproj`
- Unity editor: `/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity`

Focused verification:

- PlayMode XML: `focused-http1-remaining-20260321-154909.xml`
  - total `3`
  - passed `3`
  - failed `0`
  - skipped `0`
  - duration `0.5141835`

Full validation:

- PlayMode XML: `test-results-all-playmode-sync-20260321-154944.xml`
  - total `1141`
  - passed `1140`
  - failed `0`
  - skipped `1`
  - duration `46.3091265`
- EditMode XML: `test-results-all-editmode-sync-20260321-155108.xml`
  - total `5`
  - passed `4`
  - failed `0`
  - skipped `1`
  - duration `0.1517036`

## Deferred / Remaining Work

1. The temp-project Unity workflow is green again for this branch.
2. IL2CPP/mobile device validation for the broader HTTP/1 and HTTP/2 cancellation paths remains a separate platform-validation task outside this editor batch run.
