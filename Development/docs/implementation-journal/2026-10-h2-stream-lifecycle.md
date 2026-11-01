# 2026-10 — HTTP/2 stream lifecycle (#19, #21, #28, #25, #147)

Wave w1030 lane `h2-stream-lifecycle`. Builds on h2-flow-control (#224), h2-headers-in (#210) and
h2-write-path (#198).

## What changed

- **#19 upload after response start.** Upload loops stop on `Http2Stream.IsRequestBodyAborted`
  (peer RST_STREAM, stream failure, cancellation, handler fault, response body abandoned or faulted)
  instead of `IsResponseCompleted` (removed). A started or complete response no longer stops the
  upload (RFC 9113 §8.1). A stream stays in `_activeStreams` until both directions are closed:
  `_closeFlags` (local, remote, abandon-pending) is set with CAS and whoever closes the second
  direction removes it. `IsStreamClosedForReceive` = not active or remote closed. An upload that
  stops without END_STREAM is reset (RST_STREAM CANCEL) unless another path already removed and
  reset it (`RemoveActiveStream(id, stream)` returns true for exactly one caller, so no double
  reset and no RST in answer to a peer RST, RFC 9113 §5.4.2). The request cancellation
  registration is no longer dropped at response start; for streamed responses it is released once
  the response started and the request side is done (both sides check, `ReleaseCancellationIfStreamingHandedOff`).
- **#28 early final / 1xx / malformed.** Expect-continue early final: the body is abandoned and the
  stream ends with RST_STREAM(NO_ERROR) once the response is complete (sent by dispatch or by the
  read loop on END_STREAM, whichever closes the second direction). 1xx + END_STREAM and every
  `ApplyDecodedHeaders` rejection (header list size, :status in trailers, invalid/missing :status,
  #31 field checks, pseudo-headers, trailers without END_STREAM) go through
  `RejectMalformedResponse`: fail, remove, track, RST_STREAM(PROTOCOL_ERROR). The synchronous header
  path queues one reset (`_pendingResetStreamId`, read-loop owned) that `ReadLoopAsync` writes right
  after the frame, so the sync shape and frame order are kept. DATA or a field block after END_STREAM
  on a stream that is still uploading is a STREAM_CLOSED stream error (RFC 9113 §5.1); a response
  that completed before it stands, only the upload stops.
- **#25 content-length.** `TryStartResponse` records the expected length (-1 without content-length
  or for HEAD/204/304, RFC 9110 §6.4.1). DATA beyond it, an END_STREAM (DATA or trailers) short of
  it, or HEADERS+END_STREAM with a non-zero content-length is malformed: the body faults and the
  stream is reset with PROTOCOL_ERROR. The cache only stores bodies that completed naturally
  (`CacheStoringHandler.DisposeAsync`), so a faulted body is never cached.
- **#21 read loop vs pool.** `Http2Stream.TryAcquireLease(streamId, connection)` (CAS on the ref
  count while positive, then identity check, else release). The read loop (DATA, field blocks,
  WINDOW_UPDATE, RST_STREAM, SETTINGS window delta, GOAWAY), `FailAllStreams` and the stall scan
  lease every stream they found through a lookup. The pool-return owners are unchanged (dispatch
  `finally` + body source); a lease only delays the return while it is held. `ReleaseLifetime` at 0
  also removes `(id, this)` from the active map. The stress test exposed a pre-existing deadlock in
  `SingleReaderChannel`: `TryWrite`/`Complete` disposed the pending read's cancellation registration
  inside `_gate`, and `CancellationTokenRegistration.Dispose` waits for a running callback that
  itself takes `_gate` (read loop stalled for good when a request was cancelled while its DATA
  arrived). Registrations are now detached under the lock and disposed outside it.
- **#147 cap only where buffered.** New `UHttpClientOptions.MaxBufferedResponseBodySize` (default
  100 MB, 0 = unlimited) enforced by `BufferedResponseCollectorHandler` (detached, buffered and
  read-loop paths). `Http2Options.MaxResponseBodySize` now defaults to 0 (opt-in transport cap for
  every HTTP/2 response). HTTP/1.1: the head parser no longer rejects Content-Length > 100 MB and
  `Http11ResponseBodySource` no longer caps chunked or read-to-end bodies; the parser's buffered
  (test-only) paths keep their own 100 MB cap.

## Files

Runtime: `Http2/Http2Stream.cs`, `Http2/Http2Connection.cs`, `Http2/Http2Connection.Send.cs`,
`Http2/Http2Connection.ReadLoop.cs`, `Http2/Http2Connection.Lifecycle.cs`, `Http2/Http2Settings.cs`,
`Http2/SingleReaderChannel.cs`, `Http1/Http11ResponseParser.cs`, `Http1/Http11ResponseBodySource.cs`,
Core `UHttpClientOptions.cs`, `Http2Options.cs`, `UHttpClient.cs`, `Pipeline/BufferedDispatchBridge.cs`,
`Pipeline/ResponseCollectorHandler.cs`.
Tests: new `Http2ConnectionTests.StreamLifecycle.cs`, `SingleReaderChannelCancellationRaceTests.cs`,
`Http1/Http11LargeStreamingBodyTests.cs`, `Pipeline/BufferedResponseCapTests.cs`; updated tests that
asserted the old behavior (417 test now expects RST(NO_ERROR); RST-during-expect-wait expects no
RST back; malformed-response tests expect RST(PROTOCOL_ERROR); trailers-skipped test now stops the
upload with a peer RST(NO_ERROR); content-length preallocation test expects the mismatch fault;
H1 read-to-end > 100 MB now succeeds).

## Decisions

- Malformed rejections all use PROTOCOL_ERROR, the header-list-size overrun included (the response
  cannot be processed; RFC 9113 §8.1.1 / §10.5.1).
- Early final for expect-continue: RST_STREAM(NO_ERROR) after the response completes rather than an
  empty END_STREAM DATA frame (that would make a request with Content-Length malformed for the server).
- Streaming responses keep no transport cap by default; the only buffered cap lives in Core.
- A lease released last returns the stream to the pool from the read loop; ownership (who releases
  the owning references) is unchanged from phase 19a.5.

## Deferred

- #21 device validation (IL2CPP ARM64) stays in #99 (`needs-device-validation`).

## Verification

- `'./Tools~/ci/check.sh'`: OK.
- Unity unavailable locally (lanes do not run Unity). The NUnit sources were compiled against the
  shadow-built runtime in a scratch net8.0 project and run there: 288 of 290 tests in
  Transport/Http2, Transport/Http1 (Http11*), Http2BodySizeTests and the buffered-dispatch tests pass;
  the 2 `UHttpClient`-based cap tests cannot run outside Unity (UnityEngine ECall) and are left to the
  host editor. Against `origin/main` the new #19/#25/#28/#147 tests and the channel race test fail.
- Review round 1 added the half-closed (remote) DATA/HEADERS test, HEADERS+END_STREAM with
  content-length, and trailers ending a body shorter/longer than content-length: 293 of 295 pass in
  the scratch project (same 2 Unity-only tests); the late-HEADERS case fails without its fix.
- Not run: Unity EditMode/PlayMode, IL2CPP, devices.
