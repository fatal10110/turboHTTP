# Temp Unity Redirect AssertAsync Fix

**Date:** 2026-03-11  
**Scope:** Restore Unity 2021.3 temp-project test compatibility after a lingering NUnit API usage regressed `RedirectInterceptorTests`.

## What Was Implemented

- Replaced the remaining `Assert.ThrowsAsync<InvalidOperationException>(...)` call in `RedirectInterceptorTests` with the repo-local `AssertAsync.ThrowsAsync<InvalidOperationException>(...)` helper.
- Kept the test intent unchanged: the redirect completion bridge must still surface the downstream terminal callback failure as `InvalidOperationException`.

## Files Modified

| File | Purpose |
|------|---------|
| `Tests/Runtime/Middleware/RedirectInterceptorTests.cs` | Removed the unsupported Unity NUnit async assertion API usage. |

## Decisions / Trade-Offs

1. **Used the repo compatibility helper instead of newer NUnit APIs:** Unity 2021.3 in the temp project still does not expose `Assert.ThrowsAsync(...)`, and the repository already standardizes on `AssertAsync` for this compatibility layer.
2. **Kept the fix test-only:** no runtime or middleware behavior changed; this is strictly a compile/test compatibility correction.

## Specialist Review Pass

Applied both required rubrics explicitly to this fix slice:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

### Notes

- No asmdef or module-boundary changes were required.
- No transport, TLS, cancellation, or runtime callback behavior changed.
- The fix preserves the existing redirect failure-propagation coverage while restoring Unity test-runner compatibility.

## Deferred / Remaining Work

- Re-run the documented temp Unity project PlayMode/EditMode workflow to confirm the compile blocker is cleared and identify any remaining runtime test failures.

## Validation

Re-ran the documented workflow from `Development/docs/how-to-run-tests-with-temp-unity-project.md` after syncing the updated package to `/tmp/turboHTTP-package`.

- PlayMode XML: `test-results-all-playmode-sync-20260311-124234.xml`
  - total `986`
  - passed `973`
  - failed `12`
  - skipped `1`
  - duration `36.221106`
- EditMode XML: `test-results-all-editmode-sync-20260311-124335.xml`
  - total `5`
  - passed `4`
  - failed `0`
  - skipped `1`
  - duration `0.1756135`

The original compile blocker is resolved: Unity now completes compilation and emits XML for both runs. Remaining PlayMode failures are behavior-level test failures outside this assertion-compatibility fix slice.
