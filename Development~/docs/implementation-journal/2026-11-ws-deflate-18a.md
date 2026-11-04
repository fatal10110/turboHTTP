# 2026-11 ws-deflate-18a (#270, #104)

Wave run w1102, lane ws-deflate-18a. One PR.

## What

- **Compressed client messages on every runtime (#270).** New `Runtime/WebSocket/SyncFlushDeflater.cs`:
  a managed raw DEFLATE encoder (RFC 1951) whose output always ends at a sync flush with the
  `00 00 FF FF` of the empty stored block removed (RFC 7692 section 7.2.1), and never contains a final
  block. It writes one fixed-Huffman block (section 3.2.6) with greedy LZ77 matching over hash chains
  (chain length 8/32/128 for levels 1-3/4-6/7-9), or stored blocks of at most 65535 bytes when those
  are not larger (level 0 always), so a message grows by at most 5 bytes per 65535 plus one. Matches
  stay inside the message and within 32768 bytes (the 15-bit window; `client_max_window_bits` is
  never offered). An empty message is the single byte `0x00`, the empty uncompressed block of RFC 7692 section
  7.2.3.6.
- `PerMessageDeflateExtension.TransformOutbound`: when `client_no_context_takeover` was not agreed and
  `DeflateStream.Flush` is not a sync flush on this runtime (the existing `s_flushIsNotSyncFlush` flag,
  set by the first message on Unity Mono), the message is compressed by `SyncFlushDeflater` instead of
  being sent uncompressed. The first such message, which detects the flag, also falls through to the
  managed encoder. .NET keeps `DeflateStream` (sync flush, dynamic Huffman); Mono with
  `client_no_context_takeover` keeps the final-block form plus the `0x00` stored-block header
  (section 7.2.3.4). Internal test hook `ForceSyncFlushDeflater` selects the managed path on any
  runtime. Stale "v1 no_context_takeover-only mode" doc comment and `Reset` comment updated.
- Tests: `WebSocketDeflateContextTakeoverTests` no longer tolerates an uncompressed message (it
  asserts RSV1 and a payload for every message, on every runtime) and runs both paths
  (`TestCase(false|true)`); new `SyncFlushDeflaterTests` (one retained inflater across seven messages
  at levels 0/1/6/9 incl. empty, 1 byte, more than 65535 random bytes, repeats just inside and just
  outside the 32768-byte window; each message alone; never larger than the stored form; the empty
  message is `0x00`; stored-block headers; BFINAL=0/BTYPE=01 on repetitive data; round trip through
  `TransformInbound`).
- Docs: `Tools~/autobahn/README.md` (the Mono path), `Development/docs/phases/phase18a/overview.md`
  (spike results pointer; future item "full context takeover" now done on the client side).

## Phase 18a closure list (#104)

From `2026-02-phase18a-implementation-status.md` ("Remaining Work Before Marking Phase 18a Fully
Closed"):

| Item | Result | Evidence |
| --- | --- | --- |
| Full WebSocket runtime suites | Unity 2021.3.45f2 Editor Mono PlayMode: 182/182 pass | `run-tests.sh --platform PlayMode --filter 'TurboHTTP\.Tests\.WebSocket\.'` (2026-11-03, this branch plus a temporary spike fixture) |
| Spike 1: `DeflateStream.Flush` sync flush | .NET 8: yes (`00 00 FF FF`). Unity 2021.3.45f2 Editor Mono (Mono 6.13.0): no; `Flush` after writing 67200 bytes wrote 0 bytes, everything came out at `Dispose` (265 bytes, final block) | temporary PlayMode fixture; #217 Autobahn run; #104 comment 2026-11-01 |
| Spike 1 fallback | managed `SyncFlushDeflater` (not native zlib P/Invoke) | this entry, #270 |
| Spike 2: `DeflateStream` memory | Mono Editor, `GC.GetTotalMemory(true)` around 10 open compress+decompress pairs (warm run): +81920 bytes, about 8 KB per pair, 0 residual after dispose. Mono's zlib state is native and not in this number (the phase doc estimates 300-600 KB per pair with context takeover). `SyncFlushDeflater` keeps no state between messages; its scratch arrays are pooled | temporary PlayMode fixture (not committed) |
| Spike 3: `IAsyncEnumerable` | Mono Editor: an `async IAsyncEnumerable<int>` with `await Task.Yield()` consumed by `await foreach` returned all items; netstandard 2.1 type, no package | temporary PlayMode fixture; `WebSocketAsyncEnumerable`, `ReceiveAllAsync` tests in the suite run |
| `wss://` through CONNECT | loopback test passes | `WebSocketBouncyCastleTlsTests.Wss_BouncyCastleBackend_ThroughConnectProxy_EchoesText`, `WebSocketCleartextPolicyTests.WssThroughHttpProxy_AllowCleartextFalse_RefusesProxyBeforeAnySocket` |
| Basic auth over a cleartext proxy | replaced by a refusal unless opt-in (#85, closed) | `2026-11-ws-transport-security.md`, `WebSocketProxyTests`, `ProxyTunnelConnectorTests` |
| 32-bit metrics | collector fields are read and written under one lock (`WebSocketMetricsCollector._gate`), so no torn 64-bit reads by construction; 32-bit ARM IL2CPP run: needs-device-validation | `Runtime/WebSocket/WebSocketMetricsCollector.cs` |
| No `Microsoft.Bcl.AsyncInterfaces` | no asmdef, `package.json`, `.rsp` or DLL references it; `IAsyncEnumerable<T>` comes from netstandard 2.1; the shadow build compiles against Unity's netstandard 2.1 profile | `rg 'Bcl|AsyncInterfaces' -g '*.asmdef' -g package.json -g '*.rsp'`: none; `find -iname 'Microsoft.Bcl*'`: none |
| IL2CPP: flush, memory, `IAsyncEnumerable`, 32-bit ARM | not run | needs-device-validation (#99, owner-deferred) |

## Decisions

- **#270 approach: managed encoder**, not "document the trade-off" and not a native zlib binding.
  RFC 7692 section 7.2.1 expects a sync-flush ending, and the criterion asks for compression on all
  runtimes, which documentation cannot deliver. A zlib P/Invoke would add native plugins per platform
  and could only be validated on devices. The encoder stays in `TurboHTTP.WebSocket` (Core-only
  reference, no unsafe code, no engine references, no reflection), uses `ArrayPool` for scratch and
  output, and the caller still owns and disposes the returned buffer once.
- Fixed Huffman instead of dynamic Huffman: much smaller code; the ratio is lower than
  `DeflateStream` (JSON-like sample: 9600 bytes to 131 managed vs 104 `DeflateStream` on .NET 8), and
  it is only used on the path that previously sent no compression at all.
- Messages that do not compress still go out with RSV1 set, as stored blocks: the same as the
  `DeflateStream` path, which never falls back to uncompressed by size.
- Inbound context takeover is unchanged (the offer always asks for `server_no_context_takeover`).
- **#104 GA vs experimental:** permessage-deflate ships in v1.0 as an opt-in feature
  (`WebSocketConnectionOptions.WithCompression`, off by default), not behind a new `[Experimental]`
  marker: the beta fixes are merged (#82, #201, #218, #237, #257), Autobahn 12.x/13.1/13.2/13.7 pass
  (#217), and the Mono path now compresses with context takeover. What stays open is device-only.
- `CLAUDE.md`/`AGENTS.md`/`Development/docs/agents/project-status.md` (line 27 names #270/#104) are
  edited only by the hot-group lane; the wording is in the PR body.

## Deferred

- #270 criterion 2 (IL2CPP player) and the IL2CPP rows of #104 (flush, memory, `IAsyncEnumerable`,
  32-bit ARM metrics): needs-device-validation, on the owner-deferred device matrix #99. Both issues
  stay open ("Part of").

## Verification

- `'./Tools~/ci/check.sh'`: OK (invariants, format, shadow build with analyzers, 25 assemblies).
- Unity 2021.3.45f2 Editor Mono, PlayMode, host editor:
  - `--filter 'TurboHTTP\.Tests\.WebSocket\.'` (all WebSocket runtime tests, before the final
    buffer-compaction change, with a temporary spike fixture): 182/182 pass.
  - `--filter 'TurboHTTP\.Tests\.WebSocket\.(SyncFlushDeflaterTests|WebSocketDeflateContextTakeoverTests|WebSocketExtensionTests|WebSocketExtensionStrictnessTests|WebSocketFuzzRegressionTests|WebSocketIntegrationTests)'`
    at the final code: 51/51 pass. `WebSocketDeflateContextTakeoverTests(False)` is the regression
    test: on Mono the old code sent the second and third message uncompressed, which the new
    assertion rejects; now Mono compresses them with `SyncFlushDeflater` and one retained inflater
    reads all three.
  - EditMode: not run (the WebSocket tests are PlayMode tests).
- .NET 8 scratch harness (not committed): 300 messages per level 0/1/6/9 (random, low-entropy text,
  sparse random, runs; lengths 0 to 200000) round-trip through one retained `DeflateStream` inflater
  and each alone; output never larger than the stored form. 4 MB of repetitive text at level 9:
  15 ms.
- Not run: Autobahn gate (its harness runs on .NET, where the `DeflateStream` path is used, so it does
  not exercise the managed encoder), IL2CPP players, devices (needs-device-validation, #99).
