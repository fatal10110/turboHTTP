# Runtime And Test Structure Split — 2026-03-15

## What Was Implemented

This change reduced a few oversized runtime and test files without changing behavior, and it tightened the test layout so the `Tests/Runtime` tree is easier to scan.

Implemented in this session:

1. Split the large `WebSocketConnection` implementation into focused partial files.
2. Split the large `UHttpClientTests` fixture into smaller partial test files.
3. Removed a pair of ambiguous duplicate test filenames.
4. Moved non-test support files out of the `Tests/Runtime` root into more explicit folders.

## Files Modified

### Runtime/WebSocket

- `Runtime/WebSocket/WebSocketConnection.cs`
- `Runtime/WebSocket/WebSocketConnection.SendReceive.cs`
- `Runtime/WebSocket/WebSocketConnection.Extensions.cs`
- `Runtime/WebSocket/WebSocketConnection.Lifecycle.cs`

### Tests/Runtime/Core

- `Tests/Runtime/Core/UHttpClientTests.cs`
- `Tests/Runtime/Core/UHttpClientTests.Builders.cs`
- `Tests/Runtime/Core/UHttpClientTests.Execution.cs`

### Tests/Runtime layout cleanup

- Renamed `Tests/Runtime/Extensibility/PluginRegistryTests.cs` to `Tests/Runtime/Extensibility/PluginExtensibilityTests.cs`
- Renamed `Tests/Runtime/Mobile/BackgroundNetworkingTests.cs` to `Tests/Runtime/Mobile/MobileBackgroundNetworkingSmokeTests.cs`
- Moved `Tests/Runtime/TestHelpers.cs` to `Tests/Runtime/Support/TestHelpers.cs`
- Moved `Tests/Runtime/TestHttpClient.cs` to `Tests/Runtime/Manual/TestHttpClient.cs`

## Decisions And Trade-Offs

1. **Partial-class split for runtime code:** `WebSocketConnection` was split mechanically rather than rewritten. This keeps public/private member names and behavior intact while making lifecycle, extension handling, and send/receive paths easier to navigate.
2. **Partial-class split for large tests:** `UHttpClientTests` now separates builder/config coverage from execution/disposal coverage, but keeps shared fixtures and helper doubles in the primary file to avoid scattering test infrastructure.
3. **Test tree naming clarity over historical names:** duplicate basenames such as `PluginRegistryTests.cs` and `BackgroundNetworkingTests.cs` were renamed where they represented different scopes, because identical filenames in different folders slowed navigation and grep-based review.
4. **Dedicated folders for non-test support:** `Support/` is now the intended location for shared test helpers, while `Manual/` is the intended location for non-automated harness scripts.

## Test Layout Rule Captured From This Refactor

Current intended pattern:

- executable runtime tests stay under `Tests/Runtime/<Area>/`
- editor tests stay under `Tests/Editor/`
- shared runtime test helpers go under `Tests/Runtime/Support/`
- manual or ad-hoc harnesses go under `Tests/Runtime/Manual/`
- package runtime support code that ships for consumers remains under `Runtime/Testing/` and should not be treated as NUnit test placement

This matches the existing Phase 7 test-assembly direction while reducing ambiguity inside the test tree itself.

## Review Checklist Pass

The required review rubrics were run as explicit checklists against the final refactor:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

Outcome:

- No module boundary changes were introduced.
- No runtime behavior, allocation strategy, threading model, or disposal semantics were intentionally changed.
- The WebSocket split preserved the same transport/lifecycle code paths and kept all state on the same class.
- Test moves remained inside the same test assembly, so no asmdef edits were required.

## Validation

- `git diff --check` passes.
- Duplicate basenames under `Tests/Runtime` were re-checked after the rename/move pass and now return no duplicates.
- Search confirmed executable tests are still under `Tests/` rather than mixed into `Runtime/`.

Not run in this environment:

- Unity Test Runner / compile validation
- IL2CPP or device validation

Those remain external validation steps because this workspace does not expose a runnable Unity project/test invocation.

## Follow-Up — 2026-03-16

### What Was Implemented

This follow-up continued the oversized-test cleanup by splitting the largest remaining NUnit fixture in the runtime test tree:

1. Split `Tests/Runtime/Transport/Http2/Http2ConnectionTests.cs` into a helper-only partial plus four themed partial files.
2. Kept all HTTP/2 helper methods and nested test doubles on the primary partial to avoid scattering shared setup logic.
3. Re-ran the size scan for `Tests/Runtime` to refresh the next split candidates.

### Files Modified

- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.ConnectionSetup.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.FrameHandling.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.ProtocolAndCleanup.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.AdditionalCoverage.cs`

### Decisions And Trade-Offs

1. **Keep the original filename as the fixture anchor:** `Http2ConnectionTests.cs` now holds shared helpers and nested test doubles, while the new partials carry the actual `[Test]` methods. This preserves discoverability for the fixture entry point.
2. **Split by behavioral theme, not by arbitrary line counts:** connection setup, frame handling, cleanup/protocol validation, and additional coverage now live in separate files, which keeps each file smaller without fragmenting single scenarios across files.
3. **Accept a few mid-sized files instead of over-splitting:** the resulting partials are still substantial, but they are well below the previous 2,546-line outlier and remain readable without turning one fixture into too many tiny files.

### Refreshed Next Candidates

After this split, the largest remaining files under `Tests/Runtime` are:

- `Tests/Runtime/WebSocket/WebSocketTestServer.cs` — helper/support code, 1144 lines
- `Tests/Runtime/Middleware/RedirectInterceptorTests.cs` — 894 lines
- `Tests/Runtime/Transport/Http2/Http2FlowControlTests.cs` — 850 lines
- `Tests/Runtime/Performance/Phase19AllocationGateTests.cs` — 759 lines
- `Tests/Runtime/Transport/TcpConnectionPoolTests.cs` — 738 lines

The next clean NUnit fixture candidates are `RedirectInterceptorTests.cs` and `Http2FlowControlTests.cs`. `WebSocketTestServer.cs` is large too, but it is support infrastructure rather than a direct test fixture.

### Validation

- `git diff --check` passes for the new HTTP/2 split files.
- `Http2ConnectionTests` no longer appears as a single oversized file in the runtime test size scan.
- No asmdef or runtime code changes were required for this follow-up.

Not run in this environment:

- Unity Test Runner / compile validation
