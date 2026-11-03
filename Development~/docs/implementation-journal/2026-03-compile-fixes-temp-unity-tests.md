# Compile Fixes For Temp Unity Test Runs

**Date:** 2026-03-10  
**Scope:** Fix the Phase 22 worktree compile breakage so the documented temporary Unity project test flow can execute again.

## What Was Implemented

Resolved the Unity compiler errors that were blocking both PlayMode and EditMode runs in the temporary test project wired to `/tmp/turboHTTP-package`.

The fixes covered:

1. `MockTransport` handler dispatch was corrected to call the `UHttpResponse` overload instead of the queued-response overload when using the fallback response path.
2. Transport exception mapping in `RawSocketTransport` and `Http2Connection` now preserves the original exception via `UHttpError.InnerException` instead of calling a removed `UHttpException(error, inner)` constructor.
3. `MonitorHandler` now snapshots the buffered body through `SegmentedBuffer.ToArray()` rather than relying on a `ReadOnlySequence<byte>.ToArray()` API that is not available in the Unity target.
4. `CookieInterceptor` regained the missing `System.Collections.Generic` import required by the new merge helper.
5. Editor monitor tooling was updated from the deleted `MonitorMiddleware` runtime type to the new `MonitorInterceptor`.
6. `TurboHTTP.Middleware` was granted `InternalsVisibleTo` access to the Phase 22 `ReadOnlySequenceStream` helper in `TurboHTTP.Core.Internal`.
7. Runtime test helpers and affected tests were updated to current Phase 22 async signatures:
   - `AssertAsync` now supports `Func<Task<TResult>>`
   - tests no longer call removed `RawSocketTransport.SendAsync(...)`
   - tests no longer call `.AsTask()` on APIs that already return `Task`
   - tests use the repo’s async assertion helpers instead of `Assert.ThrowsAsync(...)`, which is unavailable in the Unity NUnit profile here

## Files Modified

| File | Purpose |
|------|---------|
| `Runtime/Testing/MockTransport.cs` | Fixed fallback response callback dispatch. |
| `Runtime/Transport/RawSocketTransport.cs` | Updated network-exception wrapping to current `UHttpError` constructor shape. |
| `Runtime/Transport/Http2/Http2Connection.cs` | Same exception-wrapping fix for HTTP/2. |
| `Runtime/Observability/MonitorHandler.cs` | Replaced unsupported `ReadOnlySequence<byte>.ToArray()` usage. |
| `Runtime/Middleware/CookieInterceptor.cs` | Restored generic collection import needed by cookie-header merge logic. |
| `Runtime/Core/AssemblyInfo.cs` | Added `InternalsVisibleTo("TurboHTTP.Middleware")` for `ReadOnlySequenceStream`. |
| `Editor/Monitor/HttpMonitorWindow.cs` | Swapped monitor event/history hooks to `MonitorInterceptor`. |
| `Editor/Monitor/HttpMonitorWindow.Panels.cs` | Swapped history clear action to `MonitorInterceptor`. |
| `Editor/Settings/TurboHttpSettings.cs` | Swapped editor preference wiring to `MonitorInterceptor`. |
| `Tests/Runtime/AssertAsync.cs` | Added `Task<TResult>` async-exception helper overload. |
| `Tests/Runtime/Middleware/DecompressionInterceptorTests.cs` | Replaced unsupported NUnit async assertion usage. |
| `Tests/Runtime/Pipeline/InterceptorPipelineTests.cs` | Replaced unsupported NUnit async assertion usage. |
| `Tests/Runtime/Core/UHttpClientTests.cs` | Updated the direct transport test to use `TransportDispatchHelper`. |
| `Tests/Runtime/Cache/CacheInterceptorTests.cs` | Removed stale `.AsTask()` call on a `Task<UHttpResponse>`. |

## Decisions / Trade-Offs

1. **No new buffered transport API was reintroduced on `RawSocketTransport`:** tests were moved to `TransportDispatchHelper.CollectResponseAsync(...)` instead of restoring a legacy `SendAsync` method on the transport itself.
2. **`ReadOnlySequenceStream` stayed internal:** module access was granted through `InternalsVisibleTo("TurboHTTP.Middleware")` rather than expanding the public Core API surface.
3. **Test helpers were adapted instead of forcing newer NUnit APIs:** Unity 2021.3’s test environment does not expose `Assert.ThrowsAsync(...)` here, so the repo-local helpers remain the compatibility layer.

## Specialist Review Pass

Both required review rubrics were applied explicitly to this compile-fix slice:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

### Findings

No additional blockers were identified beyond the implemented fixes.

Infrastructure check notes:

- No asmdef dependency graph changed.
- No Core public API was broadened.
- The only new assembly-level coupling is the targeted `InternalsVisibleTo("TurboHTTP.Middleware")` required by the existing Phase 22 decompression design.

Network check notes:

- Transport exception mapping still produces `UHttpException` while preserving the underlying exception for diagnostics.
- No socket/TLS/cancellation logic changed; the transport edits are compile-shape corrections only.

## Validation

Executed the documented workflow from `Development/docs/how-to-run-tests-with-temp-unity-project.md`:

1. Synced the package repo to `/tmp/turboHTTP-package` via `rsync`.
2. Ran PlayMode tests from `/Users/arturkoshtei/workspace/turboHTTP-testproj`.
3. Ran EditMode tests from `/Users/arturkoshtei/workspace/turboHTTP-testproj`.

Observed results:

- PlayMode XML: `test-results-all-playmode-sync-20260310-082255.xml`
  - total `973`
  - passed `938`
  - failed `34`
  - skipped `1`
  - duration `39.2739322`
- EditMode XML: `test-results-all-editmode-sync-20260310-082401.xml`
  - total `5`
  - passed `4`
  - failed `0`
  - skipped `1`
  - duration `0.1552646`
- `git diff --check` passes for the files changed in this session.

## Deferred / Remaining Work

Compilation is no longer blocked, but PlayMode still reports runtime/assertion failures that were previously hidden by the compiler breakage. The failures are concentrated in:

- `CacheInterceptorTests` (cache hit/revalidation/invalidation expectations)
- selected interceptor/transport semantic tests (`PluginInterceptorCapabilityTests`, `MockTransportTests`, `RawSocketTransportTests`, `Http2ConnectionTests`, `Http2FlowControlTests`)
- one decompression assertion that now reports the underlying corruption message text rather than the older wrapper text

Those failures need a separate behavior-level follow-up; they are not compiler errors anymore.
