# Temp Unity Follow-Up PlayMode Fixes

**Date:** 2026-03-21  
**Scope:** Fix the remaining temp-project PlayMode failures found during the documented Unity 2021.3 validation workflow, then rerun the full temp-project suite to green.

## What Was Implemented

This follow-up pass fixed three runtime behaviors that were still failing under the temporary Unity project workflow:

1. **HTTP/1.1 committed non-replayable send failures now preserve the dedicated retryability-aware transport error even when the peer closes before sending a status line.**
   - `RawSocketTransport` now treats the internal `FormatException("Empty HTTP status line")` case as the same transport-failure class as socket/stream I/O when a non-replayable body already committed bytes.
   - This keeps the Step 22a.2 retry/failure contract intact for partial-send failures that surface during response-head parsing rather than during the body write itself.

2. **HTTP/2 buffered request cancellation once again completes promptly after headers have been sent.**
   - Restored the explicit wait-vs-cancellation race in `Http2Connection.DispatchAsync(...)` so a canceled buffered request no longer sits indefinitely on `stream.CompletionTask`.
   - On cancellation, the dispatch path removes the active stream if needed, faults the stream with `OperationCanceledException`, and best-effort sends `RST_STREAM(CANCEL)`.

3. **WebSocket `ReceiveAllAsync(...)` no longer terminates early during automatic reconnect transitions.**
   - Removed the shared async-enumerator pre-check that returned `false` immediately whenever `State == Closed`.
   - The enumerator now lets the underlying receive path decide whether to block, complete, or fault, which preserves the resilient-client contract that receives wait through reconnect and only finish once reconnection is exhausted or the queue is truly closed.

## Files Modified

Runtime:

- `Runtime/Transport/RawSocketTransport.cs`
- `Runtime/Transport/Http2/Http2Connection.cs`
- `Runtime/WebSocket/WebSocketAsyncEnumerable.cs`
- `Runtime/WebSocket/WebSocketClient.cs`
- `Runtime/WebSocket/ResilientWebSocketClient.cs`

Docs:

- `Development/docs/implementation-journal/2026-03-temp-unity-followup-playmode-fixes.md`

## Decisions / Trade-Offs

1. **Kept the HTTP/1.1 fix surgical in transport classification instead of changing the parser's general empty-status-line behavior.**  
   The failing case only needed reclassification when a non-replayable body had already committed bytes. Broadening the parser itself would have changed unrelated malformed-response behavior.

2. **Restored the HTTP/2 cancellation race at the dispatch wait boundary rather than relying only on the cancellation registration callback.**  
   The explicit `Task.WhenAny(...)` path makes the buffered dispatch completion deterministic again under Unity batch runs and preserves the required `RST_STREAM(CANCEL)` behavior.

3. **Fixed WebSocket reconnection at the shared async-enumerator layer instead of adding resilient-client-specific state handling.**  
   The bug was a generic early-return optimization that incorrectly treated transient reconnect closure as terminal. Removing that pre-check keeps both the base client and resilient client behavior driven by their actual receive queues.

## Specialist Review Pass

Applied both required rubrics explicitly to this fix slice:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

### Review Notes

- No asmdef boundaries or public API surfaces changed.
- No new transport/module dependencies were introduced.
- HTTP/1.1 still preserves the existing `UHttpException` / `UHttpError` network-failure model.
- HTTP/2 cancellation remains best-effort on the wire and keeps the stream/connection cleanup localized to the existing dispatch lifecycle.
- The WebSocket change does not add buffering or extra allocations; it only removes a premature state-based completion check.
- IL2CPP/mobile validation remains pending for the broader transport and WebSocket reconnect paths; this pass only revalidated the documented Unity Editor temp-project workflow.

## Validation

### Repository Checks

- `git diff --check`

### Focused Unity Verification

- PlayMode XML: `test-results-http1-nonreplayable-20260321-185133.xml`
  - total `1`
  - passed `1`
  - failed `0`
- PlayMode XML: `test-results-http2-cancel-20260321-185159.xml`
  - total `1`
  - passed `1`
  - failed `0`
- PlayMode XML: `test-results-websocket-reconnect-20260321-185516.xml`
  - total `5`
  - passed `5`
  - failed `0`
- PlayMode XML: `test-results-websocket-streaming-20260321-185539.xml`
  - total `4`
  - passed `4`
  - failed `0`

### Temp Unity Project Validation

Ran the documented workflow from `Development/docs/how-to-run-tests-with-temp-unity-project.md` against:

- package sync target: `/tmp/turboHTTP-package`
- Unity project: `/Users/arturkoshtei/workspace/turboHTTP-testproj`
- Unity editor: `/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity`

Observed results:

- PlayMode XML: `test-results-all-playmode-sync-20260321-185604.xml`
  - total `1146`
  - passed `1145`
  - failed `0`
  - skipped `1`
  - duration `40.5639709`
- EditMode XML: `test-results-all-editmode-sync-20260321-185728.xml`
  - total `5`
  - passed `4`
  - failed `0`
  - skipped `1`
  - duration `0.1494877`
- Phase 19 allocation gate XML: `test-results-phase19-allocation-gate-20260321-185707.xml`
  - total `2`
  - passed `2`
  - failed `0`
  - skipped `0`
  - duration `0.4782659`

## Deferred / Remaining Work

1. Physical-device IL2CPP/mobile validation is still pending for the broader HTTP/1.1, HTTP/2, and WebSocket reconnect paths.
2. This pass did not change any phase status or architecture guidance, so `AGENTS.md` and `CLAUDE.md` required no updates.
