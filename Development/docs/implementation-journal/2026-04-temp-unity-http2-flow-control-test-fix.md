# Temp Unity HTTP/2 Flow-Control Test Fix

**Date:** 2026-04-08  
**Scope:** Fix the remaining temp-project PlayMode failure in `Http2FlowControlTests.LargeResponse_CompletesWithStreamWindowUpdates`.

## What was implemented

Updated the failing HTTP/2 flow-control test to model a protocol-compliant peer instead of over-sending past the advertised stream receive window.

1. Added a small `WINDOW_UPDATE` payload parser helper in `Http2FlowControlTests`.
2. Changed `LargeResponse_CompletesWithStreamWindowUpdates` so the test server:
   - tracks the currently advertised per-stream receive window
   - limits each DATA frame to the remaining stream credit
   - waits for a stream-level `WINDOW_UPDATE` before sending more DATA after exhausting the initial `65535`-byte window

## Root cause

The transport now enforces stream-level receive-window accounting correctly. The old test sent `128 KB` of DATA back-to-back without first observing a stream-level `WINDOW_UPDATE`, which is not protocol-compliant once the initial `65535`-byte stream window has been consumed.

That race was more visible in the temp Unity batch workflow because the in-memory duplex test stream completes writes synchronously, so the buffered response collector does not necessarily get scheduled between consecutive server writes.

## Files modified

Tests:

- `Tests/Runtime/Transport/Http2/Http2FlowControlTests.cs`

Documentation:

- `Development/docs/implementation-journal/2026-04-temp-unity-http2-flow-control-test-fix.md`

## Decisions and trade-offs

1. Fixed the test, not the transport.
   - the runtime failure was the correct HTTP/2 behavior for a peer that exceeded the advertised stream receive window
2. Kept the test focused on stream-level flow control.
   - the revised test waits specifically for the stream `WINDOW_UPDATE` that proves consumer-driven credit replenishment is working
3. Avoided asmdef or runtime changes.
   - no module boundaries, public APIs, or transport behavior changed in this pass

## Validation

### Repository checks

- `git diff --check -- Tests/Runtime/Transport/Http2/Http2FlowControlTests.cs`

### Temp Unity project validation

Used the documented workflow from `Development/docs/how-to-run-tests-with-temp-unity-project.md` after syncing the package to `/tmp/turboHTTP-package`.

Focused PlayMode:

- XML: `focused-http2-flowcontrol-20260408-143111.xml`
  - total `1`
  - passed `1`
  - failed `0`
  - skipped `0`
  - duration `0.1729383`

Full PlayMode:

- XML: `test-results-all-playmode-sync-20260408-143130.xml`
  - total `1220`
  - passed `1215`
  - failed `0`
  - skipped `5`
  - duration `40.6827093`

## Specialist rubric pass

Ran both mandatory rubrics explicitly against this fix slice.

- `unity-infrastructure-architect`
  - confirmed the change stays test-only, introduces no asmdef/dependency changes, and does not alter request/response ownership or disposal behavior
- `unity-network-architect`
  - confirmed the revised test now matches RFC-aligned stream-window behavior by waiting for consumer-driven `WINDOW_UPDATE` before sending beyond the initial receive window

No additional findings were identified in this self-review.

## Deferred / remaining work

1. EditMode was not rerun in this slice because the change is runtime-test-only and the failing lane was PlayMode.
2. No `AGENTS.md` or `CLAUDE.md` updates were needed because architecture and workflow rules did not change.
