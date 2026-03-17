# Temp Unity Import And Retry-After Fixes

**Date:** 2026-03-16  
**Scope:** Restore the temporary Unity project test workflow after compile-shape regressions in runtime/test files and stabilize the remaining flaky retry timing assertion.

## What Was Implemented

This pass fixed the issues that prevented the documented temp-project workflow from finishing cleanly:

1. Restored missing framework namespace imports in runtime files that Unity could not compile:
   - `System.Buffers` for `LoggingHandler`
   - `System.Threading` / `System.Threading.Tasks` for `DecompressionInterceptor`
2. Restored missing test-file imports introduced by the current test-file split:
   - `System.Net` for `UHttpClientTests.Execution`
   - `TurboHTTP.Tests.Transport.Http2.Helpers` for `Http2ConnectionTests.FrameHandling`
3. Stabilized `RetryInterceptorTests.RetryAfterHeader_OverridesConfiguredDelay` by using a deterministic second-precision HTTP-date `Retry-After` value instead of `UtcNow + 120ms`, which could collapse to near-zero after RFC1123 formatting.

## Files Modified

| File | Purpose |
|------|---------|
| `Runtime/Observability/LoggingHandler.cs` | Restored `System.Buffers` import required by `ArrayPool<byte>`. |
| `Runtime/Middleware/DecompressionInterceptor.cs` | Restored `System.Threading` and `System.Threading.Tasks` imports required by the local async retry/decompression dispatch helper. |
| `Tests/Runtime/Core/UHttpClientTests.Execution.cs` | Restored `System.Net` import for `HttpStatusCode`. |
| `Tests/Runtime/Transport/Http2/Http2ConnectionTests.FrameHandling.cs` | Restored helper namespace import for `SendRequestAsync(...)` and `TestDuplexStream`. |
| `Tests/Runtime/Retry/RetryInterceptorTests.cs` | Made the `Retry-After` HTTP-date assertion deterministic for Unity batch runs. |

## Decisions / Trade-Offs

1. Kept the runtime fixes minimal and compile-only. The runtime files already contained the required APIs; Unity was failing because their framework namespace imports were missing.
2. Fixed the remaining retry failure in the test, not the runtime retry logic. The issue was test flakiness caused by RFC1123 second precision, not an incorrect `Retry-After` implementation.
3. Avoided asmdef or public API changes. The current module boundaries were already correct.

## Specialist Review Pass

Applied both required rubrics explicitly:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

Review notes:

- No asmdef dependency graph changed.
- No Core/Transport public API changed.
- No transport, TLS, cancellation, or retry runtime semantics were broadened by the import fixes.
- The retry test adjustment matches HTTP-date precision rules and improves determinism without weakening runtime behavior checks.

## Validation

Executed the documented workflow from `Development/docs/how-to-run-tests-with-temp-unity-project.md` after syncing `/Users/arturkoshtei/workspace/turboHTTP` to `/tmp/turboHTTP-package`.

Focused validation:

- PlayMode filter: `TurboHTTP.Tests.Retry.RetryInterceptorTests.RetryAfterHeader_OverridesConfiguredDelay`
  - XML: `test-results-retry-after-focus-20260316-102103.xml`
  - Result: passed

Full validation:

- PlayMode XML: `test-results-all-playmode-sync-20260316-102127.xml`
  - total `1007`
  - passed `1006`
  - failed `0`
  - skipped `1`
  - duration `37.7084678`
- EditMode XML: `test-results-all-editmode-sync-20260316-102225.xml`
  - total `5`
  - passed `4`
  - failed `0`
  - skipped `1`
  - duration `0.1412467`

- `git diff --check` passes for the files changed in this fix slice.

## Deferred / Remaining Work

- The temp-project workflow is green again.
- Existing unrelated worktree changes outside this fix slice remain untouched.
