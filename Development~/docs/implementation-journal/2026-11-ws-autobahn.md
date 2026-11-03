# 2026-11 ws-autobahn (#217, #82)

Wave run w1101, lane ws-autobahn. One PR.

## What

- **Autobahn gate (#217, last criterion of #82).** `Tools~/autobahn/`: a net8.0 console harness
  (`TurboHTTP.Autobahn.csproj`, `Program.cs`) that links the shadow-build DLLs and drives
  `WebSocketClient` against the `crossbario/autobahn-testsuite` fuzzing server in docker; `run.sh`
  (shadow build, harness build, server, run, check); `check_report.py` (gate verdict from
  `index.json` against `expectations.json`); `config/fuzzingserver.json` (groups 1-7, 9, 10, 12,
  13); `README.md` (how to run, harness options, the documented per-case results). Manual
  `workflow_dispatch` job `.github/workflows/autobahn.yml` uploads the reports. `.gitignore`
  un-ignores `Tools~/autobahn/*.csproj`.
- The first runs found three client defects, fixed here with regression tests:
  1. **Wrong Sec-WebSocket-Accept GUID** (`WebSocketConstants.WebSocketGuid`, since phase 18):
     `258EAFA5-E914-47DA-95CA-5AB53DC52D51` instead of RFC 6455 section 1.3's
     `258EAFA5-E914-47DA-95CA-C5AB0DC85B11`. Every handshake with a real server failed with
     "Sec-WebSocket-Accept mismatch"; the in-house fixtures (`WebSocketTestServer`,
     `InMemoryWebSocketPeer`, handshake tests) computed the accept with the same helper and agreed.
     Also corrected in `Development/docs/phases/phase18/phase-18.1-websocket-framing.md`.
     Test: `WebSocketAcceptKeyTests` (RFC 6455 section 1.3 sample nonce and accept).
  2. **Messages ahead of the peer's close frame were lost.** `WebSocketClient.ReceiveAsync` threw
     ConnectionClosed as soon as the state was Closed, before looking at the queue;
     `WebSocketConnection.ReceiveAsync` threw InvalidOperation on Closed; and
     `HandleConnectionStateChanged` completed the client queue while the listener could still hold
     a message, which `EnqueueAsync` then refused and disposed. Autobahn's `/getCaseCount` (a text
     frame and the close frame in one write) exposed it. Now both receive paths drain the completed
     queue on Closed and then report ConnectionClosed with the peer's close code; only the listener
     completes the client queue (in its `finally`, after the connection's queue is drained).
     Test: `WebSocketReceiveAfterCloseTests`.
  3. **permessage-deflate messages ended with a final DEFLATE block.** `Compress` disposed the
     `DeflateStream` (which writes a BFINAL=1 block after the sync flush) and kept those bytes, so
     the 00 00 FF FF tail was never removed. A server that keeps its inflater across messages
     (allowed and, per RFC 7692 section 7.2.2, required when `client_no_context_takeover` is not in
     the response) stopped after the first message: Autobahn 12.x and 13.1.x FAILED (2 of 1000
     messages echoed). Messages now end at the sync flush with the tail removed (section 7.2.1).
     Unity Mono's `DeflateStream.Flush` is not a sync flush. The first Unity PlayMode run of the
     new test showed it: the retained inflater returned only message 1. On such a runtime,
     `Compress` keeps the final block and appends the empty stored block header (0x00, section
     7.2.3.4), but only when `client_no_context_takeover` was agreed. Otherwise the message is sent
     uncompressed (RSV1 unset, section 6). A static `Volatile` flag records the runtime's flush
     behaviour, so the client does not compress a message only to throw the result away. Tests:
     `WebSocketDeflateContextTakeoverTests` (one retained inflater over three messages; every
     message compressed and round-tripped when `client_no_context_takeover` is agreed). Restoring
     compression on Mono/IL2CPP: #270. Spike evidence posted on #104.

## Decisions

- Manual gate plus dispatch-only workflow, not a PR check: the run takes 30-60 minutes. #100 (CI
  pipeline) is untouched.
- The harness raises the receive limits only as far as group 9 needs (16 MiB, 70000 fragments);
  defaults are unchanged.
- NON-STRICT results are accepted where Autobahn marks them as allowed and the RFC does not require
  the strict behaviour; each is listed with its reason in `expectations.json` and the README:
  - 3.2-3.4, 4.1.3-4.1.5, 4.2.3-4.2.4, 5.15 (timing-dependent, NON-STRICT or OK): when the read
    loop detects the protocol error behind a valid text message before the application echoes that
    message, the connection fails without the echo. RFC 6455 section 7.1.7 allows failing at once.
    The idle-host run produced OK for 3.4 and 4.1.5, so `expectations.json` accepts a list of
    results per case and `check_report.py` checks membership.
  - 6.4.1-6.4.4: UTF-8 is validated once the whole message is assembled
    (`WebSocketConnection.SendReceive.cs`), not per fragment. RFC 6455 section 8.1 requires failing
    the connection on invalid UTF-8, not failing on the first bad fragment. No follow-up: fail-fast
    only saves reading the rest of a message already bounded by `MaxMessageSize`.
- 13.3.x-13.6.x (72 cases) are UNIMPLEMENTED by design. In these cases the server accepts
  `permessage-deflate` only if the offer carries `client_max_window_bits`, which the client never
  offers (#82). #217's example ("needing `server_no_context_takeover=false`") does not occur in
  fuzzingserver mode: the client writes the offer and always requests `server_no_context_takeover`,
  and the server honours it. 12.x and 13.1/13.2/13.7 pass.

## Deferred

- #270 (v1.1): compressed client messages on Mono/IL2CPP when the server keeps context.

## Verification

- Autobahn gate (`'./Tools~/autobahn/run.sh' --no-shadow-build` after the gate's shadow build, idle
  host, 2026-11-02): 517 cases (groups 1-7, 9, 10, 12, 13), 0 FAILED, `check_report: OK`
  (NON-STRICT 3.2, 3.3, 4.1.3, 4.1.4, 4.2.3, 4.2.4, 5.15, 6.4.1-6.4.4; UNIMPLEMENTED 13.3-13.6;
  INFORMATIONAL 7.1.6, 7.13.1, 7.13.2). An earlier run on a host at load ~30 wedged the server
  from 12.x on (handshake timeouts): run the gate on an idle machine.
- `Tools~/ci/check.sh --full`: OK
- Unity PlayMode, scoped (`TurboHTTP\.Tests\.WebSocket\.(WebSocketAcceptKeyTests|WebSocketReceiveAfterCloseTests|WebSocketDeflateContextTakeoverTests|WebSocketExtensionStrictnessTests|WebSocketExtensionTests|WebSocketFramingTests|WebSocketHandshakeTests|WebSocketClientReconnectTests|WebSocketReceiveBackpressureTests|WebSocketMessageLimitTests)`):
  74/74 pass. EditMode: no tests match (runtime tests run in PlayMode).
- Not run: IL2CPP players, devices. The Unity Mono and IL2CPP `DeflateStream.Flush` sync-flush
  behaviour is covered by `WebSocketDeflateContextTakeoverTests` in the periodic host-editor run.
