# 2026-11 — HTTP/2 idle-stream frames and server preface (#30)

Wave w1101 lane `h2-conformance`. The last two bullets of #30; the stream WINDOW_UPDATE overflow bullet
was done by #224 (`FlowControl_StreamWindowUpdateOverflow_ResetsOnlyThatStream`).

## What changed

- `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs`:
  - New read-loop-owned `_serverPrefaceReceived`. The first frame must be a non-ACK SETTINGS (RFC 9113
    Section 3.4); anything else (PING, SETTINGS ACK, HEADERS, WINDOW_UPDATE, an unknown type) throws
    `Http2ProtocolException(ProtocolError)`, so the existing catch sends GOAWAY(PROTOCOL_ERROR) and fails
    the connection.
  - New `ThrowIfFrameOnIdleStream` before the frame switch: DATA, HEADERS, RST_STREAM, WINDOW_UPDATE and
    PUSH_PROMISE on an idle stream id are a connection PROTOCOL_ERROR (Sections 5.1, 5.1.1, 6.4).
    `IsIdleStreamId`: odd ids `>= Volatile.Read(ref _nextStreamId)`, even ids above the new read-loop
    owned `_highestPromisedStreamId`. PRIORITY stays allowed on idle ids; stream-0 frames,
    CONTINUATION and unknown types are untouched.
  - `RejectPushPromiseAsync`: a promised id that is zero, odd or not above the previous reservation is a
    connection PROTOCOL_ERROR (Sections 5.1.1, 6.6); a legal one is recorded in `_highestPromisedStreamId`
    so frames on the refused (now closed) pushed stream are still tolerated.
- `Runtime/Transport/Http2/Http2Connection.cs`: `TryCompleteSettingsAck(Exception)` with an Interlocked
  once-gate `_settingsAckCompleted` (reset in `InitializeAsync` before the read loop starts). The ACK,
  the timeout and cancellation registrations go through it instead of try/catch around
  `ManualResetValueTaskSourceCore` completions (which are not safe against concurrent completers).
- `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`: `FailAllStreams` also completes the ACK wait
  with the mapped error, so a connection that dies before the ACK (invalid preface, peer close,
  dispose) fails `InitializeAsync` at once with that error instead of after the 5 s ACK timeout.
- Tests: `Tests/Runtime/Transport/Http2/Http2ConnectionTests.IdleStreamAndPreface.cs` (new).

## Decisions

- **One check before the switch, not per handler.** It runs before any HPACK decode or window
  accounting, so an idle DATA frame no longer touches the connection receive window and an idle HEADERS
  block is never decoded (HPACK sync does not matter on a connection error).
- **HEADERS on an idle id is a connection error for a client.** Section 5.1 lets HEADERS open an idle
  stream, but a server never opens odd ids and may only use an even id after PUSH_PROMISE reserved it
  (5.1.1: "an unexpected stream identifier" is a connection PROTOCOL_ERROR).
- **Even ids with push enabled.** `EnablePush` defaults to true and pushes are refused with
  RST_STREAM(REFUSED_STREAM); the promised stream is then closed, not idle, so the highest promised id is
  tracked instead of treating every even id as idle.
- **Odd ids below `_nextStreamId` are closed.** An allocated id whose HEADERS were not written yet is
  treated as closed (existing per-frame handling), the lenient reading; a server cannot legitimately
  reference it.
- **InitializeAsync observes connection death through the ACK source.** Waiting on `_readLoopTask`
  would need a stored failure and an extra `Task.WhenAny`; completing the source from `FailAllStreams`
  covers the read loop, dispose and any other connection failure with one call.
- **Fake servers.** Every fake server in Tests already sends a non-ACK SETTINGS first (survey in the lane
  brief); the PlayMode run of all `TurboHTTP.Tests.Transport.*` confirms no H2 test needed a change.

## Deferred

None.

## Verification

- `'./Tools~/ci/check.sh'`: OK.
- Unity PlayMode `--filter 'Http2ConnectionTests\.(IdleStream|ServerPreface|ClosedStream|PromisedStream|PushPromise_Illegal)'`
  on the old runtime code: 15 of 19 fail (the 4 tolerance tests pass on both), all 19 pass with the fix.
- Unity PlayMode `--filter 'Http2ConnectionTests|Http2FlowControlTests|Http2ConnectionManagerTests|RawSocketTransportHttp2ConnectionManagementTests|BouncyCastleDuplexStreamTests|RawSocketTransportProxyTunnelTests|RawSocketTransportCancellationTests'`:
  242/242 pass.
- Unity PlayMode `--filter 'TurboHTTP\.Tests\.Transport\.'`: 934/947; the 13 failures are TLS/X509 and
  bootstrap tests (`ServerCertificateVerifierTests`, `SslStreamServerNameTests`,
  `BouncyCastleOutputBacklogTests`, `TransportBootstrapTests`) in code this lane does not touch.
- Not run: EditMode (the runtime tests are PlayMode), IL2CPP player, device.
