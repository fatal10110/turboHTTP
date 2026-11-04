# Temp Unity HTTP/2 Cancel Optimization

**Date:** 2026-03-21  
**Scope:** Replace the temporary `Task.WhenAny(...)` HTTP/2 buffered-request cancellation workaround with the lower-allocation registration-based design, then validate it against the documented Unity 2021.3 temp-project workflow.

## What Was Implemented

This follow-up converted the HTTP/2 buffered-request cancellation path to the lower-allocation shape that Phase 22a review work originally wanted, while preserving the Unity temp-project behavior that had regressed.

1. **Moved buffered request completion back to a direct await of `stream.CompletionTask`.**
   - `Http2Connection.DispatchAsync(...)` now again waits on `AwaitResponseCompletionOrCancellationAsync(stream)` without the `AsTask()` / `Task.Delay(...)` / `Task.WhenAny(...)` fallback wrapper.
   - Request-scoped cancellation is now owned by the registration callback rather than by an extra waiting task pair.

2. **Made the cancellation registration authoritative for stream completion.**
   - `Http2Connection` now installs a static request-cancellation callback using the `CancellationToken.Register(..., state, useSynchronizationContext: false)` overload.
   - The callback routes through the captured `Http2Stream`, then into `Http2Connection.HandleRequestCancellation(...)`.
   - That path always faults/cancels the captured stream first so the dispatch waiter can complete even if `_activeStreams` bookkeeping has already raced with response completion or cleanup.
   - Active-stream removal and best-effort `RST_STREAM(CANCEL)` are now follow-up cleanup steps instead of the gate for whether cancellation wakes the stream.

3. **Fixed the underlying pre-response stream completion bug that the temporary `Task.WhenAny(...)` workaround had been masking.**
   - `Http2Stream.Cancel(...)` and `Http2Stream.Fail(...)` were both pre-setting `_completionSignaled` before calling helpers that also guard on `_completionSignaled`.
   - That meant pre-response cancellation/failure could return without ever completing the `ManualResetValueTaskSourceCore`.
   - The temporary `Task.WhenAny(...)` workaround hid this by letting `DispatchAsync(...)` escape on token cancellation even when the stream itself never completed.
   - The fix removes the redundant outer completion-claim in `Cancel(...)` and `Fail(...)`, letting `TrySetException(...)` / `TrySetResult(...)` perform the single authoritative completion transition.

## Root Cause

The earlier removal of `Task.WhenAny(...)` was motivated by a valid allocation concern, but the repo still had a stream-level completion bug:

- The Phase 22a review correctly identified that `AwaitResponseCompletionOrCancellationAsync(...)` was allocating per request on the cancelable path.
- A later cleanup removed those allocations and assumed the request-cancellation registration path was already sufficient.
- Under Unity temp-project validation, that assumption failed because the registration path depended on `Http2Stream.Cancel(...)` completing the stream, and the pre-response implementation was silently failing to signal completion due to the double `_completionSignaled` guard.

With the stream completion bug fixed, the registration-owned design now works as intended and the extra `Task.WhenAny(...)` wrapper is no longer needed.

## Files Modified

Runtime:

- `Runtime/Transport/Http2/Http2Connection.cs`
- `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`
- `Runtime/Transport/Http2/Http2Stream.cs`

Docs:

- `Development/docs/implementation-journal/2026-03-temp-unity-http2-cancel-optimization.md`

## Decisions / Trade-Offs

1. **Kept the callback-based optimization, but only after fixing the actual stream completion bug.**  
   The first attempt to optimize by relying on the cancellation registration still failed in Unity because `Http2Stream.Cancel(...)` did not actually complete the pre-response stream. The correct move was to fix the stream primitive, not to keep layering wait-side workarounds on top.

2. **Used the registration callback for deterministic completion and transport cleanup, not just for dictionary removal.**  
   The callback now always cancels/faults the captured stream first and only then performs active-stream removal and best-effort `RST_STREAM(CANCEL)`. This makes cancellation robust against bookkeeping races.

3. **Used the static `CancellationToken.Register(..., state, useSynchronizationContext: false)` form to avoid per-request closure capture.**  
   This keeps the callback allocation profile narrower than the earlier closure-based version while preserving Unity batch-mode behavior.

## Specialist Review Pass

Applied both required rubrics explicitly to this fix slice:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

### Review Notes

- No asmdef boundaries or module dependencies changed.
- No public API surface changed.
- The callback-based cancellation path remains transport-local and does not add Unity-engine dependencies.
- The stream completion fix preserves the existing error model: caller/request cancellation remains `OperationCanceledException`, while transport/protocol faults remain `UHttpException` / `UHttpError`.
- `RST_STREAM(CANCEL)` remains best-effort and only depends on the stream still being active at cleanup time.
- IL2CPP/mobile validation remains a separate follow-up concern for the broader transport layer.

## Validation

### Repository Checks

- `git diff --check`

### Focused Unity Verification

Ran the key regression-sensitive PlayMode tests after syncing the package to `/tmp/turboHTTP-package`:

- PlayMode XML: `focused-http2-20260321-202440.xml`
  - filter: `TurboHTTP.Tests.Transport.Http2.Http2ConnectionTests.SendRequest_Cancelled_SendsRstStream`
  - total `1`
  - passed `1`
  - failed `0`
  - skipped `0`
  - duration `0.1476096`
- PlayMode XML: `focused-http2-20260321-202453.xml`
  - filter: `TurboHTTP.Tests.Transport.Http2.Http2ConnectionTests.SendStreamingRequest_RequestCancellationAfterHeaders_DoesNotAbortBody`
  - total `1`
  - passed `1`
  - failed `0`
  - skipped `0`
  - duration `0.1479285`

### Temp Unity Project Validation

Ran the documented workflow from `Development/docs/how-to-run-tests-with-temp-unity-project.md` against:

- package sync target: `/tmp/turboHTTP-package`
- Unity project: `/Users/arturkoshtei/workspace/turboHTTP-testproj`
- Unity editor: `/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity`

Observed results:

- PlayMode XML: `test-results-all-playmode-sync-20260321-202512.xml`
  - total `1146`
  - passed `1145`
  - failed `0`
  - skipped `1`
  - duration `40.857875`
- EditMode XML: `test-results-all-editmode-sync-20260321-202609.xml`
  - total `5`
  - passed `4`
  - failed `0`
  - skipped `1`
  - duration `0.1528933`

## Deferred / Remaining Work

1. Physical-device IL2CPP/mobile validation remains pending for the broader HTTP/2 transport path.
2. This follow-up did not change architecture guidance or dependency rules, so `AGENTS.md` and `CLAUDE.md` required no updates.
