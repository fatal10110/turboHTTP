# Phase 22b Specialist Review Fixes

**Date:** 2026-04-08  
**Phase:** 22b full implementation specialist review follow-up  
**Status:** Code/test findings fixed and validated in a focused local harness

## What was implemented

Closed the remaining actionable issues from
`Development/docs/phases/phase22b/review-22b-full-implementation.md`.

1. Hardened proxy CONNECT body draining for 407 auth retry reuse.
   - `RawSocketTransport.DrainProxyConnectBodyAsync(...)` now gives
     `Transfer-Encoding: chunked` precedence over `Content-Length`, drains chunk
     data plus the terminal trailer section, and treats partial drains / invalid
     chunk framing as network errors instead of silently reusing a corrupt
     connection.
   - unsupported transfer-codings without `Content-Length` now fail fast so the
     proxy socket is not reused in an undefined parser state.

2. Enforced RFC 9113 trailing HEADERS termination rules in HTTP/2.
   - `Http2Connection.ReadLoop.DecodeAndSetHeaders(...)` now fails the affected
     stream when trailing HEADERS arrive without `END_STREAM`.
   - the failure remains stream-local: the active stream is removed, the body is
     faulted, and the connection stays reusable for later requests.

3. Hardened the timing-sensitive early-dispose regression test.
   - `RawSocketTransportTests` now uses `Stopwatch` instead of
     `DateTime.UtcNow` for the connection-close early-dispose assertion.
   - the tolerance was widened to `1000ms` to avoid CI/clock-resolution
     flakiness.

4. Added focused regressions for the new protocol cases.
   - HTTP/1.1 proxy CONNECT tests now verify that a chunked `407 Proxy
     Authentication Required` body is fully drained before the next CONNECT
     response is parsed.
   - HTTP/2 tests now verify that trailing HEADERS without `END_STREAM` fault
     only the affected stream and do not poison the connection.

5. Closed the low-risk cleanup items from the same review pass.
   - extracted the CONNECT ALPN list to a static field to avoid a per-CONNECT
     array allocation
   - added comments clarifying the expect-continue timeout semantics
   - updated the pool-key semaphore test assertion to express the blocking
     intent directly
   - documented the intentionally permissive invalid/prohibited response-trailer
     skipping behavior in both buffered and streaming HTTP/1.1 parser paths

## Files modified

Runtime:

- `Runtime/Transport/RawSocketTransport.cs`
- `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs`
- `Runtime/Transport/Http1/Http11ResponseParser.cs`
- `Runtime/Transport/Http1/Http11ResponseBodySource.cs`

Tests:

- `Tests/Runtime/Transport/Http1/RawSocketTransportTests.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.ProtocolAndCleanup.cs`
- `Tests/Runtime/Transport/TcpConnectionPoolTests.cs`

Documentation:

- `Development/docs/implementation-journal/2026-04-phase22b-specialist-review-fixes.md`

## Decisions and trade-offs

1. Malformed CONNECT response bodies now fail reuse instead of attempting a
   best-effort retry on the same socket.
   - this is stricter than the previous behavior, but it avoids hiding parser
     corruption behind a later, misleading retry failure

2. Kept response-trailer parsing permissive for invalid/prohibited fields.
   - framing and size violations still fail the response
   - individual invalid trailer lines are skipped for interoperability and that
     behavior is now documented inline

3. Re-reviewed the request-trailer validator scope during this pass.
   - no code change was needed for the review note about integrity trailers:
     the current `TrailerFieldValidator.ProhibitedRequestTrailers` already
     permits `Digest`, `Content-MD5`, and custom integrity-style trailer fields

4. Left the CONNECT cold-path byte-at-a-time line reader unchanged.
   - the specialist review marked this as a future optimization opportunity, not
     a correctness issue

## Validation

### Repository checks

- `git diff --check -- Runtime/Transport/RawSocketTransport.cs Runtime/Transport/Http2/Http2Connection.ReadLoop.cs Runtime/Transport/Http1/Http11ResponseParser.cs Runtime/Transport/Http1/Http11ResponseBodySource.cs Tests/Runtime/Transport/Http1/RawSocketTransportTests.cs Tests/Runtime/Transport/Http2/Http2ConnectionTests.ProtocolAndCleanup.cs Tests/Runtime/Transport/TcpConnectionPoolTests.cs`

### Focused local harness

Created a temporary NUnitLite harness at:

- `/tmp/phase22b-review-fixes-20260408/`

Build:

- `dotnet build /tmp/phase22b-review-fixes-20260408/phase22b-review-fixes-20260408.csproj -v minimal -p:UseAppHost=false`

Focused execution:

- `dotnet /tmp/phase22b-review-fixes-20260408/bin/Debug/net8.0/TurboHTTP.Phase22bReviewFixes.dll --test=TurboHTTP.Tests.Transport.Http1.RawSocketTransportTests.Connect407_ChunkedBodyDrain_PreservesNextConnectResponse --test=TurboHTTP.Tests.Transport.Http1.RawSocketTransportTests.RawSocketTransport_StreamingConnectionClose_EarlyDispose_SkipsDrainAndCloses --test=TurboHTTP.Tests.Transport.Http2.Http2ConnectionTests.TrailingHeaders_WithoutEndStream_FailsOnlyAffectedStream --test=TurboHTTP.Tests.Transport.TcpConnectionPoolTests.GetConnection_WithPoolKeyOverride_SemaphoreUsesPhysicalEndpoint --test=TurboHTTP.Tests.Transport.Http1.RawSocketTransportTests.Connect407_RetryWithAuthOnce --test=TurboHTTP.Tests.Transport.Http2.Http2ConnectionTests.TrailingHeaders_WithoutStatus_AreAccepted --test=TurboHTTP.Tests.Transport.Http2.Http2ConnectionTests.UnexpectedResponsePseudoHeader_FailsOnlyAffectedStream --test=TurboHTTP.Tests.Transport.Http1.RawSocketTransportTests.HttpViaProxy_BufferedChunkedResponse_PreservesTrailers`

Harness result:

- `8/8 passed`

Observed warnings stayed limited to pre-existing warnings outside this review-fix
scope:

- `Runtime/Transport/Tcp/SaeaSocketChannel.cs` volatile-reference warnings
- `Runtime/Transport/Http2/HpackHuffman.cs` sign-extension warning
- pre-existing async-test warnings in older transport tests

## Specialist rubric pass

Ran both mandatory rubrics explicitly against the final change set.

- `unity-infrastructure-architect`
  - confirmed all changes stay inside existing Core/Transport/Test boundaries
  - confirmed the new proxy drain path does not introduce shared-state races and
    now fails malformed reuse cases deterministically
  - confirmed the new comments/tests improve maintainability without expanding
    the public API surface

- `unity-network-architect`
  - confirmed CONNECT retry handling now preserves inbound stream alignment for
    chunked `407` responses
  - confirmed HTTP/2 trailing HEADERS now enforce RFC 9113 §8.1 with a
    stream-local failure
  - confirmed the timing-test hardening removes clock-resolution sensitivity
    without weakening the behavioral guarantee

No additional code findings were identified in this self-review pass.

## Deferred / still required

1. Physical-device TLS validation from the specialist review remains open.
   - iOS IL2CPP, especially BouncyCastle concurrent read/write in the
     expect-continue path
   - Android IL2CPP

2. Allocation-gate verification for non-`Expect: 100-continue`,
   non-trailer fast paths remains open.

3. The CONNECT cold-path line reader optimization remains deferred.
