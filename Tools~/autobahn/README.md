# Autobahn testsuite gate (WebSocket interop, #217)

The WebSocket client is checked against the [Autobahn testsuite](https://github.com/crossbario/autobahn-testsuite)
`fuzzingserver`, the reference RFC 6455 / RFC 7692 peer. Run the gate before every release
candidate, and after any change to `Runtime/WebSocket` framing, close handling, UTF-8 validation or
`permessage-deflate`.

## Run it

```bash
'Tools~/autobahn/run.sh'                           # the full gate (about 30-60 min)
'Tools~/autobahn/run.sh' --cases '12.*,13.*'       # some case groups only
'Tools~/autobahn/run.sh' --no-shadow-build         # reuse .build/shadow (no Runtime change since)
```

Needs docker, the .NET 8 SDK and the Unity managed DLLs the shadow build uses (`run.sh` calls
`Tools~/ci/fetch-unity-dlls.sh` when they are missing, or set `UNITY_DLLS` / `UNITY_PATH`). On
GitHub the same gate is the manual `autobahn` workflow (Actions, "Run workflow"), which uploads the
reports as the `autobahn-reports` artifact.

Run it on an otherwise idle machine. The fuzzing server is a single Python process; on a host
under heavy load the large `permessage-deflate` cases (12.x) back up the server and later cases fail
their handshakes. The harness then reports `handshake-failed`/`timeout` per case.

Exit code 0 means the gate passes. Reports: `.build/autobahn/reports/clients/index.html` (per case,
with wire logs), harness log `.build/autobahn/harness.log`, server log `.build/autobahn/fuzzingserver.log`.

## How it works

1. `Tools~/ci/shadow_build.py` compiles the runtime asmdefs (`.build/shadow/editor`).
2. `TurboHTTP.Autobahn.csproj` (net8.0 console, `Program.cs`) links those DLLs. It registers the
   raw-socket transport (`RawSocketWebSocketTransport.EnsureRegistered`, no Unity bootstrap runs),
   asks the server for the case count, then per case opens `/runCase?case=N&agent=TurboHTTP` with
   `WebSocketClient` and echoes every message back with the same type until the server closes.
   Last, `/updateReports` makes the server write the report.
3. The server runs in docker (`crossbario/autobahn-testsuite`, `wstest -m fuzzingserver`,
   `config/fuzzingserver.json`) on `127.0.0.1:9001`.
4. `check_report.py` reads `index.json`: every case must be OK or INFORMATIONAL, or be listed in
   `expectations.json` with the result it is documented to produce and why (a list of results for
   a timing-dependent case). An unlisted NON-STRICT/UNIMPLEMENTED/FAILED case, a listed case with a
   result not listed for it, or a listed case that did not run fails the gate.

Harness options (`Program.cs`): `AllowCleartext` (the server is `ws://`), `MaxMessageSize` and
`MaxFrameSize` 16 MiB and `ReceiveQueueMaxBytes` 32 MiB (9.1.6/9.2.6 send 16 MiB messages),
`MaxFragmentCount` 70000 (9.3.1/9.4.1 send 4 MiB in 64-byte fragments), `PingInterval` 0 (no
unsolicited pings inside a case), `WithCompression()` (the default offer: `server_no_context_takeover`
and `client_no_context_takeover`, never `client_max_window_bits`). The library defaults (4 MiB, 64
fragments) would close those 9.x cases with 1009 by design.

Case groups: 1 framing, 2 ping/pong, 3 reserved bits, 4 opcodes, 5 fragmentation, 6 UTF-8, 7 close
handling, 9 limits and performance, 10 auto-fragmentation, 12 `permessage-deflate` with the default
offer, 13 `permessage-deflate` with parameter variations. Group 11 does not exist; groups 14+ are
not part of the client suite.

## Documented results

`expectations.json` is the authoritative list (the gate compares against it). Last full run:
517 cases (2026-11-02, groups 1-7, 9, 10, 12, 13), 0 FAILED, gate OK. All cases are OK or
INFORMATIONAL except these:

| Cases | Result | Why |
| --- | --- | --- |
| 3.2-3.4, 4.1.3-4.1.5, 4.2.3-4.2.4, 5.15 | NON-STRICT or OK | Timing-dependent. A valid text message comes just before the protocol error. When the read loop finds the error before the application has echoed that message, the client fails the connection (1002) without the echo (NON-STRICT, which Autobahn accepts; RFC 6455 section 7.1.7 fails the connection as soon as the error is found). When the echo goes out first, the case is OK. |
| 6.4.1-6.4.4 | NON-STRICT | UTF-8 is checked once the whole message is assembled, not per fragment. The client therefore fails (1007) at the end of the message, not at the first bad fragment. RFC 6455 section 8.1 requires failing the connection, not failing fast, and `MaxMessageSize` bounds the message. |
| 13.3.x-13.6.x (72 cases) | UNIMPLEMENTED | Declined by design. In these cases the server accepts `permessage-deflate` only when the offer carries `client_max_window_bits`. TurboHTTP never offers it, because `DeflateStream` always compresses with a 15-bit window (#82). No extension is agreed. |
| 7.1.6, 7.13.1, 7.13.2 | INFORMATIONAL | Behaviour the RFC leaves open (close codes 5000/65535, close before a large echo). |

12.x, 13.1.x, 13.2.x and 13.7.x pass. These are the default offer, a server that requests
`client_no_context_takeover`, and the multi-offer cases. #217 expected the declined 13.x cases to be
the ones "needing `server_no_context_takeover=false`". In fuzzingserver mode the client writes the
offer and always requests `server_no_context_takeover`, and the server honours it. The cases that
are actually declined are the ones that need `client_max_window_bits`.

Defects this gate found and fixed (#217): the Sec-WebSocket-Accept GUID was wrong (no handshake with
a real server succeeded); messages ahead of the peer's close frame were lost; compressed messages
ended with a final DEFLATE block, which a server that keeps context cannot continue past. The
Mono/IL2CPP flush limitation is tracked in #270.
