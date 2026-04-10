# Phase 22b Full Review Fixes

**Date:** 2026-04-07  
**Phase:** 22b full review follow-up  
**Status:** Review findings fixed and validated in the local transport harness

## What was implemented

Applied the two protocol fixes from `Development/docs/phases/phase22b/review-22b-full-implementation.md`.

1. Fixed HTTP/1.1 `Expect: 100-continue` handling for non-final informational responses.
   - `RawSocketTransport.SendOnLeaseWithExpectContinueAsync(...)` now keeps reading past non-final `1xx` responses such as `102 Processing` and `103 Early Hints` instead of treating them as final.
   - the expect-continue wait now tracks the already-consumed interim-response count and reuses the parser’s `Max1xxResponses` guard so the response-first path, timeout path, and normal parser stay aligned.
   - final-response handling still preserves the existing body-send failure precedence and reader ownership rules.

2. Tightened HTTP/2 initial response HEADERS validation.
   - `Http2Connection.ReadLoop.HandleHeadersFrame(...)` now rejects unexpected response pseudo-headers in the initial response HEADERS block instead of silently ignoring them.
   - trailing HEADERS behavior remains unchanged: `:status` and any other pseudo-header still fail as before.

3. Added focused regressions for both findings.
   - HTTP/1.1 tests now cover `103 Early Hints` before a final rejection and `103 Early Hints` arriving after the expect timeout while the request body is still streaming.
   - HTTP/2 tests now cover an illegal `:path` pseudo-header in an initial response HEADERS frame and verify the failure stays stream-local by successfully completing a follow-up request on the same connection.

## Files modified

Runtime:

- `Runtime/Transport/Http1/Http11ResponseParser.cs`
- `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs`
- `Runtime/Transport/RawSocketTransport.cs`

Tests:

- `Tests/Runtime/Transport/Http1/RawSocketTransportTests.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.ProtocolAndCleanup.cs`

## Decisions and trade-offs

1. Kept the HTTP/1.1 expect-continue timeout as a single overall wait window.
   - receiving `102`/`103` does not reset the timer, which avoids letting a server extend the wait indefinitely with repeated informational responses.

2. Reused the parser’s interim-response limit instead of introducing a second transport-specific cap.
   - this keeps normal response parsing and expect-continue parsing behaviorally aligned.

3. Rejected illegal HTTP/2 response pseudo-headers at the read-loop boundary.
   - this fails the affected stream as soon as the invalid header block is decoded, which matches the transport’s existing invalid `:status` handling and avoids letting malformed protocol state leak further into response startup.

## Validation

### Repository checks

- `git diff --check -- Runtime/Transport/Http1/Http11ResponseParser.cs Runtime/Transport/Http2/Http2Connection.ReadLoop.cs Runtime/Transport/RawSocketTransport.cs Tests/Runtime/Transport/Http1/RawSocketTransportTests.cs Tests/Runtime/Transport/Http2/Http2ConnectionTests.ProtocolAndCleanup.cs`

### Focused local harness

Created a disposable NUnitLite harness at `/tmp/phase22b-review-fix-check/` that compiles the Core + Transport runtime sources together with the affected HTTP/1.1 and HTTP/2 test files and their helper types.

- `dotnet build /tmp/phase22b-review-fix-check/phase22b-review-fix-check.csproj -v minimal -p:UseAppHost=false`
- `dotnet /tmp/phase22b-review-fix-check/bin/Debug/net8.0/phase22b-review-fix-check.dll`

Harness result: `64/64 passed`

Observed warnings remained limited to pre-existing warnings outside this review-fix scope:

- `Runtime/Transport/Tcp/SaeaSocketChannel.cs` volatile-reference warnings
- `Runtime/Transport/Http2/HpackHuffman.cs` sign-extension warning
- existing async-test warnings in older `RawSocketTransportTests`

## Rubric pass

Ran both mandatory review rubrics explicitly against the final change set.

- `unity-infrastructure-architect`
  - confirmed no asmdef or module-boundary changes were introduced
  - confirmed the HTTP/1.1 fix preserves reader ownership, disposal, and existing send-failure precedence
  - confirmed the new logic stays inside the existing transport/parser boundary and does not expand optional-module coupling

- `unity-network-architect`
  - confirmed HTTP/1.1 expect-continue now matches the parser’s existing non-final `1xx` behavior for `102`/`103`
  - confirmed HTTP/2 response HEADERS now reject illegal pseudo-headers beyond `:status`
  - confirmed the new HTTP/2 test exercises stream-local protocol failure rather than masking the issue behind connection teardown

No additional review findings were identified in this pass.

## Deferred / still required

1. Physical-device TLS / IL2CPP validation for the Phase 22b networking paths remains open.
   - iOS IL2CPP
   - Android IL2CPP

2. Allocation-gate verification for the non-`Expect: 100-continue`, non-trailer fast paths remains open per the full review notes.

3. Unity Test Runner / temp-project validation outside the local harness was not rerun in this follow-up step.
