# Temp Unity Fixes: Gzip, MockTransport, Cancellation

**Date:** 2026-03-10  
**Scope:** Address the targeted temp-project PlayMode failures for truncated gzip handling, `MockTransport` missing-response setup, and the HTTP/1 cancellation expectation without changing the known cache or buffered dispatch-completion issues.

## What Was Implemented

1. `DecompressionHandler` now validates the gzip trailer for single-layer gzip responses after buffered decompression completes, so truncated payloads that do not throw from Unity's `GZipStream` are still rejected as decompression failures.
2. `MockTransport` gained an explicit `useDefaultFallback` constructor so tests can opt into the "no fallback response" path without changing the default `new MockTransport()` behavior used broadly across the test suite.
3. The `RawSocketTransport` connect-cancellation test was aligned with the current Phase 22 cancellation contract by expecting `OperationCanceledException` instead of `UHttpException`.

## Files Modified

| File | Purpose |
|------|---------|
| `Runtime/Middleware/DecompressionHandler.cs` | Added single-gzip trailer validation using CRC32 + ISIZE checks after decompression. |
| `Runtime/Testing/MockTransport.cs` | Added an explicit opt-in constructor for disabling the default 200 OK fallback. |
| `Tests/Runtime/Testing/MockTransportTests.cs` | Updated the missing-response test to use the no-fallback transport setup. |
| `Tests/Runtime/Transport/Http1/RawSocketTransportTests.cs` | Updated the connect-cancellation assertion to the current cancellation contract. |
| `Development/docs/implementation-journal/2026-03-temp-unity-fixes-gzip-mocktransport-cancellation.md` | Recorded the implementation and validation results. |

## Decisions / Trade-Offs

1. The gzip fix stays scoped to single-layer gzip payloads because that is the failing path and it can be validated from the buffered wire payload without redesigning the broader multi-encoding pipeline.
2. `MockTransport` default constructor behavior was intentionally preserved because many existing tests rely on `new MockTransport()` producing a default success response.
3. The cache failures and buffered dispatch-completion behavior were left untouched by design for this slice.

## Specialist Review Pass

Applied the required checklists from:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

Review notes:

- No asmdef or module-boundary changes were needed.
- The gzip change remains IL2CPP-safe and allocation-conscious within the already-buffered Phase 22 decompression design.
- The test-side cancellation change aligns with the documented Phase 22 transport contract: cancellation propagates as `OperationCanceledException`.

## Validation

Executed the documented temp-project workflow from `Development/docs/how-to-run-tests-with-temp-unity-project.md` after syncing to `/tmp/turboHTTP-package`.

Observed results after the fixes:

- PlayMode XML: `test-results-all-playmode-sync-20260310-221732.xml`
  - total `981`
  - passed `951`
  - failed `29`
  - skipped `1`
  - duration `36.2696428`
- EditMode XML: `test-results-all-editmode-sync-20260310-221841.xml`
  - total `5`
  - passed `4`
  - failed `0`
  - skipped `1`
  - duration `0.1442202`

Compared to the prior PlayMode rerun (`33` failures), the following targeted failures are now gone:

- `TurboHTTP.Tests.Middleware.DecompressionInterceptorTests.TruncatedGzip_ReportsResponseError`
- `TurboHTTP.Tests.Testing.MockTransportTests.DispatchAsync_WithoutQueuedResponse_ReportsNetworkErrorAfterRequestStart`
- `TurboHTTP.Tests.Transport.Http1.RawSocketTransportTests.CancellationDuringConnect_NoLeaks`

## Remaining Work

- The remaining PlayMode failures are still concentrated in the known cache behavior bucket, the buffered dispatch-completion / plugin bucket, and the HTTP/2 exception-contract expectation bucket.
- Those areas were intentionally left out of this fix slice.
