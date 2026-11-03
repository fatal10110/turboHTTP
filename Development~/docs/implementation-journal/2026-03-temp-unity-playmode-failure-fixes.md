# Temp Unity PlayMode Failure Fixes

**Date:** 2026-03-15  
**Scope:** Resolve the remaining PlayMode failures found by the temporary Unity project workflow after the Unity-compatible async assertion fix restored batch runs.

## What Was Implemented

This pass fixed the remaining 12 PlayMode failures from the temp Unity project validation run.

The changes fell into three buckets:

1. **Plugin capability attribution fix:** `PluginContext` no longer blames an outer read-only plugin for request mutations performed by an inner plugin later in the chain. The invocation guard now advances its request-mutation baseline after each downstream dispatch completes.
2. **Pipeline argument validation fix:** `DispatchBridge.CollectResponseAsync(...)` now rejects a null `request` up front instead of letting that failure surface later through `UHttpResponse`.
3. **Test expectation adaptation to the Phase 22 error surface:** tests that still expected raw `ObjectDisposedException`, `InvalidOperationException`, or `Http2ProtocolException` were updated to assert the current buffered-dispatch contract:
   - collector/pipeline path failures surface as `UHttpException` with the original exception preserved in `HttpError.InnerException`
   - HTTP/2 protocol failures surfaced through buffered request helpers remain `UHttpException` with `UHttpErrorType.NetworkError` and an inner `Http2ProtocolException`

## Files Modified

| File | Purpose |
|------|---------|
| `Runtime/Core/PluginContext.cs` | Prevented false mutation attribution across nested plugin dispatch. |
| `Runtime/Core/Pipeline/DispatchBridge.cs` | Added null-request validation at the buffered dispatch entry point. |
| `Tests/Runtime/Performance/StressTests.cs` | Updated disposed-concurrency assertion to the current `UHttpException` surface. |
| `Tests/Runtime/Pipeline/InterceptorPipelineTests.cs` | Updated synchronous-fault expectation and kept null-request validation coverage. |
| `Tests/Runtime/Transport/Http2/Http2ConnectionTests.cs` | Updated protocol-failure assertions to verify wrapped `UHttpException` + inner `Http2ProtocolException`. |
| `Tests/Runtime/Transport/Http2/Http2FlowControlTests.cs` | Same protocol-failure expectation update for stream-level `WINDOW_UPDATE` errors. |

## Decisions / Trade-Offs

1. **Preserved the Phase 22 error model instead of weakening runtime normalization:** transport and buffered collector failures continue to surface as `UHttpException` where that is already the documented contract. The tests were adapted rather than changing HTTP/2 or collector runtime code back to raw exception propagation.
2. **Fixed only the actual runtime contract violations:** the plugin false-positive mutation blame and the missing null-request validation were corrected in runtime code because those were real behavior issues, not stale tests.
3. **Kept HTTP/2 protocol assertions strict on the underlying cause:** even after switching to `UHttpException`, the tests still verify `UHttpErrorType.NetworkError` and `Http2ProtocolException` as the preserved inner exception so protocol behavior remains visible.

## Specialist Review Pass

Applied both required rubrics explicitly to this fix slice:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

### Notes

- No asmdef changes were required.
- No transport protocol logic changed in the HTTP/2 runtime path; only the plugin guard and dispatch argument validation changed at runtime.
- The plugin guard change stays within the existing capability-enforcement design and avoids broadening plugin permissions.

## Validation

Re-ran the documented workflow from `Development/docs/how-to-run-tests-with-temp-unity-project.md` after syncing the updated package to `/tmp/turboHTTP-package`.

- PlayMode XML: `test-results-all-playmode-sync-20260315-214855.xml`
  - total `986`
  - passed `985`
  - failed `0`
  - skipped `1`
  - duration `47.9273577`
- EditMode XML: `test-results-all-editmode-sync-20260315-215008.xml`
  - total `5`
  - passed `4`
  - failed `0`
  - skipped `1`
  - duration `0.1723614`

- `git diff --check` passes for the files changed in this fix slice.

## Deferred / Remaining Work

- IL2CPP/mobile validation for the broader Phase 22 surface remains pending outside this temp Unity project workflow.
