# Temp Unity HTTP/2 Cancel Race Regression Fix

**Date:** 2026-03-21  
**Scope:** Investigate the remaining temp-project PlayMode failure in `Http2ConnectionTests.SendRequest_Cancelled_SendsRstStream`, restore the intended HTTP/2 buffered-request cancellation behavior, and revalidate the documented Unity 2021.3 temp-project workflow.

## What Was Implemented

This follow-up fixed a regression in the HTTP/2 buffered request cancellation path:

1. **Restored the explicit response-completion-vs-cancellation race in `Http2Connection.DispatchAsync(...)`.**
   - The current working tree had regressed from the earlier passing implementation and simplified the wait path back to a bare `await stream.CompletionTask`.
   - That change removed the deterministic request-cancellation escape hatch described in the earlier temp-project journal entry.
   - Under the Unity 2021.3 batch-run environment, the buffered request could remain parked on `stream.CompletionTask` long enough for `SendRequest_Cancelled_SendsRstStream` to hit its 1-second timeout.

2. **Reintroduced the cancellation-aware wait helper.**
   - `DispatchAsync(...)` now calls `AwaitResponseCompletionOrCancellationAsync(stream, streamId, ct)` again.
   - The helper races `stream.CompletionTask` against `Task.Delay(Timeout.Infinite, ct)`.
   - If request cancellation wins first, it removes the active stream if still present, tracks it in the recently-reset set, faults the stream with `OperationCanceledException`, and best-effort sends `RST_STREAM(CANCEL)`.
   - If stream completion wins first, behavior remains unchanged.

## Root Cause

The failure was not a new protocol bug in the read loop or stream state machine. It was a regression in the buffered dispatch wait path:

- The last passing implementation of `Runtime/Transport/Http2/Http2Connection.cs` had an explicit `Task.WhenAny(...)` cancellation race.
- A later staged change removed that logic and reverted the method to an unconditional wait on `stream.CompletionTask`.
- That made buffered request cancellation depend entirely on the cancellation registration path completing the stream quickly enough under Unity batch execution, which was not deterministic for this test lane.

## Files Modified

Runtime:

- `Runtime/Transport/Http2/Http2Connection.cs`

Docs:

- `Development/docs/implementation-journal/2026-03-temp-unity-http2-cancel-race-regression-fix.md`

## Decisions / Trade-Offs

1. **Restored the previously passing transport-local fix instead of weakening the test timeout.**  
   The failing test was already asserting the intended behavior: a canceled buffered request should complete promptly and emit `RST_STREAM(CANCEL)`. Relaxing the timeout would have hidden the regression instead of fixing it.

2. **Kept the fix inside `DispatchAsync(...)` rather than broadening stream-level cancellation semantics again.**  
   The regression was in the dispatch wait path, not in the lower-level stream cancellation primitives. Restoring the local race keeps the change narrow and preserves the existing stream/body-source behavior.

3. **Accepted the `AsTask()` allocation on the cancelable wait path.**  
   This only applies when the caller supplied a cancelable token and the request has already reached the response wait boundary. The deterministic cancellation behavior is worth the small allocation in this non-hot-path transition.

## Specialist Review Pass

Applied both required rubrics explicitly to this fix slice:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

### Review Notes

- No asmdef boundaries or module dependencies changed.
- No public API surface changed.
- Cancellation still preserves the existing transport contract: caller cancellation surfaces as `OperationCanceledException`, while transport/protocol failures remain `UHttpException` / `UHttpError`.
- `RST_STREAM(CANCEL)` remains best-effort and transport-local.
- The fix does not add Unity-engine dependencies or broaden unsafe code.
- IL2CPP/mobile validation remains a separate follow-up concern for the broader transport layer; this pass revalidated the documented Unity Editor temp-project workflow.

## Validation

### Repository Checks

- `git diff --check`

### Focused Unity Verification

Ran the isolated failing PlayMode test after syncing the package to `/tmp/turboHTTP-package`:

- PlayMode XML: `test-http2-cancel-20260321-200902.xml`
  - total `1`
  - passed `1`
  - failed `0`
  - skipped `0`
  - duration `0.1534616`

### Temp Unity Project Validation

Ran the documented workflow from `Development/docs/how-to-run-tests-with-temp-unity-project.md` against:

- package sync target: `/tmp/turboHTTP-package`
- Unity project: `/Users/arturkoshtei/workspace/turboHTTP-testproj`
- Unity editor: `/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity`

Observed results:

- PlayMode XML: `test-results-all-playmode-sync-20260321-200944.xml`
  - total `1146`
  - passed `1145`
  - failed `0`
  - skipped `1`
  - duration `41.2434378`
- EditMode XML: `test-results-all-editmode-sync-20260321-201044.xml`
  - total `5`
  - passed `4`
  - failed `0`
  - skipped `1`
  - duration `0.155294`

## Deferred / Remaining Work

1. Physical-device IL2CPP/mobile validation remains pending for the broader HTTP/2 transport path.
2. This fix did not change phase status, architecture guidance, or dependency rules, so `AGENTS.md` and `CLAUDE.md` required no updates.
