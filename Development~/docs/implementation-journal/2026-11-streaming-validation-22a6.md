# 2026-11 Phase 22a.6 streaming validation runs (#102)

Run record for the code and evidence side of Phase 22a.6. Library: `origin/main` b99977d (the #102 PR adds tests,
the bench baseline and docs only). Host: Windows host editor Unity 2021.3.45f2 driven from WSL2 (4 CPUs), 2026-11-23.
Device confirmation stays with #99.

## What exists (audit)

| Plan item (phase-22a.6) | Evidence |
| --- | --- |
| Step 2 items 1-5, small buffered rows, 5 MB upload, 100 MB download, 10 x 10 MB HTTP/2 | `Tools~/bench` (CoreCLR, Kestrel peer, HTTP/1.1, HTTP/2, TLS, `--heavy`) and, inside Unity, `Tests/Runtime/Performance/StreamingBenchmarks.cs` (HTTP/1.1: items 1-3 and the raw-socket floor) |
| Step 2 items 6-7, Step 6 allocation gates | `StreamingAllocationGateTests` (HTTP/1.1, HTTP/2, decompression, cache response start), `.Pipeline.cs` (retry, redirect, cache replay, chunked read without trailers, streamed upload with known and unknown length, no Expect), `.Variants.cs` (HTTP/2 read with trailers, `deflate`) |
| Step 2 item 8, Step 3 checkpoints | `Tests/Runtime/Mobile/Il2cppStreamingSpikeTests.cs` (7 tests; arm64 device and emulator rows are on #102 and #99) |
| HTTP/2 streamed upload per-chunk gate | not covered: #1263 |

## The 5 % budget

No numeric Phase 22.3 baseline survives (`Tools~/bench/README.md`, "The committed baseline"). Two checks stand in:

- `Tools~/bench` gates managed bytes per request of the six small buffered rows against `baseline.json` (5 % plus
  64 B for HTTP/1.1, 1536 B for HTTP/2) on every run; p50 latency only with `--gate-latency` on a quiet machine.
- `StreamingBenchmarks` compares, in the same run, a buffered GET with a streaming read of the same body into a new
  `byte[]` (since 22a the buffered response is the streaming response collected by `ResponseCollectorHandler`, so
  that handler is what 22a added to the 22.3 buffered path), and the 1 MiB streaming download with a raw-socket
  client. Budgets 1.05 and 0.90 are printed as advisory; `TURBOHTTP_BENCH_GATE_LATENCY=1` makes them fail the test.
  Gross regressions (3x latency, 0.25x throughput), byte counts and connection reuse always gate.

## CoreCLR harness (`Tools~/bench/run.sh`)

Against the first baseline (6452794, 2026-11-08) the three gated HTTP/1.1 rows were over budget on allocation:
`h1-get-1k-buffered` 14054 -> 14843 B, `h1-get-32k-buffered` 15394 -> 16283 B, `h1-post-1k-buffered`
13712 -> 14566 B (+5.6 to +6.3 %); HTTP/2 rows +360 to +435 B, within slack. A `git bisect` of `h1-get-1k-buffered`:

| Commit | B/request |
| --- | --- |
| 6452794 (old baseline) | 14054 |
| 40c144f | 14230 |
| 4dcba98 (#1193, stale-connection retry) | 14602 |
| 101efd6 (#1195) | 14554 |
| 9b30a76 (after #1196, #1198) | 14893 |
| d168779 (#1204) | 14908 |

Several correctness fixes, not one defect, so `baseline.json` was re-recorded (`--record --heavy`, load average 2.9
at the start); reducing the growth is tracked on #120. Throughput of the HTTP/1.1 1 MiB streaming row over the raw
floor: 0.686 and 0.899 in two runs an hour apart (host load), which is why latency stays opt-in.

## Unity runs

Filter `StreamingAllocationGateTests|StreamingBenchmarks|Il2cppStreamingSpikeTests`, `Tools~/unity/run-tests.sh`.

| Run | Total | Passed | Failed | Skipped | Results file |
| --- | --- | --- | --- | --- | --- |
| PlayMode (Editor Mono) | 23 | 23 | 0 | 0 | `results-PlayMode-20261123-111533.xml` |
| PlayMode (Editor Mono), after the 22.3 body model change in the benchmark | 23 | 23 | 0 | 0 | `results-PlayMode-20261123-111744.xml` |
| StandaloneWindows64 IL2CPP (development player) | 23 | 23 | 0 | 0 | `results-StandaloneWindows64-IL2CPP-20261123-112015.xml` |

The first IL2CPP build failed in UnityLinker (`NotImplementedException: Array` in `ReadLocalConstant`): a
`const byte[] x = null;` local in the benchmark wrote a constant of array type to the portable PDB, which Cecil cannot
read. The benchmark has no such local now; worth knowing for any test code that goes into an IL2CPP player.

Allocation gates, bytes per unit (smallest of 5 rounds; budget in brackets):

| Gate | Editor, optimized frame | IL2CPP player, unoptimized frame |
| --- | --- | --- |
| HTTP/1.1 read | 40 (48) | 705 (728) |
| HTTP/1.1 chunked read, no trailers | 32 (48) | 1371 (1392) |
| HTTP/2 read | 1 (32) | 403 (432) |
| HTTP/2 read with trailers (new) | 1 (32) | 403 (432) |
| gzip read | 4248 (4352) | 4121 (4160) |
| deflate read (new) | 4236 (4352) | 4121 (4160) |
| retry / redirect / cache replay read | 0 / 0 / 0 (32) | 2 (32), cache replay 2 |
| HTTP/1.1 streamed upload, Content-Length / chunked | 0 / 0 (32) | 2 / 1 (32) |

Benchmarks (p50 = lowest per-round median; 5 rounds; 100 requests per round for small bodies, 8 for 1 MiB, 4 uploads):

| Row | Editor Mono run 1 | Editor Mono run 2 | IL2CPP player |
| --- | --- | --- | --- |
| h1-get-1k buffered / streaming (us) | 218.8 / 206.6 | 230.6 / 213.6 | 250.4 / 254.3 |
| ratio (budget <= 1.05) | 1.059 | 1.080 | 0.985 |
| h1-get-32k buffered / streaming (us) | 314.9 / 272.3 | 322.8 / 272.5 | 397.3 / 331.5 |
| ratio (budget <= 1.05) | 1.156 | 1.185 | 1.198 |
| h1-post-1k buffered (us) | 217.7 | 210.3 | 254.5 |
| 1 MiB raw / streaming / buffered (MiB/s) | 315.2 / 321.7 / 305.8 | 306.7 / 306.4 / 294.2 | 311.7 / 236.8 / 222.5 |
| streaming / raw (budget >= 0.90) | 1.021 | 0.999 | 0.760 |
| 5 MiB upload byte[] / stream (MiB/s) | 3229 / 531 | 3106 / 498 | 3230 / 443 |

Run 1 read the streaming small body into a reused buffer; run 2 and the player into a new `byte[]` per request, the
model the benchmark keeps.

## Findings and issues filed

- #1265 (v1.0 GA): the buffered collector is over the 5 % budget for 32 KiB in both runtimes (16 KiB reads plus a
  copy into the segmented buffer); the 1 MiB streaming/raw ratio is 0.76 on the IL2CPP development player.
- #1266 (v1.1): a streamed request body uploads 6-7x slower than a `byte[]` body on Mono and IL2CPP, equal on CoreCLR.
- #1263 (v1.1): no allocation gate for the HTTP/2 streamed upload.
- #120 (comment): the HTTP/1.1 per-request allocation growth since the first bench baseline.

## Not run

- Mono player, non-development IL2CPP player, Android and iOS: the device rows stay with #99 (Step 3 checkpoints,
  Step 4 memory on device, Step 5 threshold tuning on a low-memory Android device).
- `StreamingBenchmarks` with `TURBOHTTP_BENCH_GATE_LATENCY=1`: it would fail on #1265 today.

## Run record: buffered collector reads into its segments (#1265), 2026-11-23

The buffered collector now reads into the `SegmentedBuffer` segments instead of a rented 16 KiB buffer plus a copy, and
a declared length between 16 KiB and 64 KiB sizes the first segment (one data read for a 32 KiB body). Gated runs
(`TURBOHTTP_BENCH_GATE_LATENCY=1`, passed to the Windows Unity process through `WSLENV`; every verdict line printed
"gated"), filter `Http11SmallBodies_BufferedAgainstStreaming_WithinLatencyBudget`, `Tools~/unity/run-tests.sh`, Unity
2021.3.45f2. The host was shared with other lanes (load average 2-7 at the starts), so these are not quiet-host numbers.

| Row | Editor Mono run 1 | Editor Mono run 2 | IL2CPP dev player run 1 | IL2CPP dev player run 2 |
| --- | --- | --- | --- | --- |
| h1-get-1k buffered / streaming (us) | 210.4 / 221.4 | 216.4 / 213.4 | 251.0 / 251.3 | 253.2 / 257.1 |
| ratio (budget <= 1.05) | 0.950 | 1.014 | 0.999 | 0.985 |
| h1-get-32k buffered / streaming (us) | 269.9 / 287.5 | 268.2 / 284.3 | 316.4 / 328.0 | 324.4 / 322.0 |
| ratio (budget <= 1.05) | 0.939 | 0.943 | 0.965 | 1.007 |
| h1-post-1k buffered (us) | 219.6 | 210.2 | 239.8 | 258.7 |
| Result | Passed | Passed | Passed | Passed |

Results files: `results-PlayMode-20261123-164817.xml`, `results-PlayMode-20261123-165014.xml`,
`results-StandaloneWindows64-IL2CPP-20261123-170615.xml`, `results-StandaloneWindows64-IL2CPP-20261123-170758.xml`.
One more IL2CPP attempt (16:53) built the player and then timed out waiting for it ("No activity received from the
player in 600 seconds"), with no result; it is not counted.

`Tools~/bench/run.sh` (CoreCLR, default set against `baseline.json`): within budget, allocation per request
h1-get-32k-buffered 16276 -> 15550 B, h1-get-1k-buffered 14895 -> 14932 B (+37 B, within the gate). A first run on the
same head failed with a 120 s request timeout in the `h2-get-1m-streaming` row (the streaming path; the collector is not
on it); the rerun passed.

Not run: the 1 MiB streaming / raw ratio on a non-development IL2CPP player. `run-tests.sh` builds Unity Test
Framework players, which are development builds; `build-player.sh` builds non-development players but runs only the
player-smoke rows. #1265 keeps that criterion open.
