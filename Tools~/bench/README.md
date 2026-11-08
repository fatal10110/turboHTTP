# Streaming benchmark harness (Phase 22a.6, #102)

Per-request latency, managed allocation, throughput and peak heap of TurboHTTP on HTTP/1.1 and HTTP/2,
buffered and streaming, against a loopback Kestrel peer, with a regression check against a committed
baseline (`baseline.json`). It replaces the temporary harness the Phase 22a journal numbers came from.

```bash
'Tools~/bench/run.sh'                         # default set, compared with Tools~/bench/baseline.json
'Tools~/bench/run.sh' --heavy                 # also the 100 MiB and 10 x 10 MiB memory rows (~10 s more)
'Tools~/bench/run.sh' --only h1-get-1k-buffered,h2-get-1k-buffered --rounds 3
'Tools~/bench/run.sh' --record --heavy        # write this run as the new baseline.json
'Tools~/bench/run.sh' --gate-latency         # also gate p50 latency (a quiet machine with its own baseline)
```

Needs the .NET 8 SDK (with its ASP.NET Core shared framework) and the Unity managed DLLs the shadow build
links (`Tools~/ci/fetch-unity-dlls.sh`, run automatically). No Unity, no root, no network beyond loopback.
Output: `.build/bench/run/result.json` and `bench.log`. Exit codes: 0 pass, 1 a failure or a regression
over the budget, 2 usage, 3 tool missing. The default set takes about a minute.

## What runs

`run` (the client under test) starts `serve` as a second process, so the process-wide allocation counter
and the heap hold nothing but the client:

- **Server**: Kestrel (ASP.NET Core 8) on loopback: HTTP/1.1 cleartext, and HTTP/1.1 + HTTP/2 over TLS with
  ALPN, using the soak harness's per-run PKI (`Tools~/soak/SoakPki.cs`, trusted only through
  `SSL_CERT_FILE`/`SSL_CERT_DIR`; certificate validation is unchanged). Routes: `GET /bytes/N`,
  `POST /echo`, `POST /sink`. Every response carries `x-bench-protocol`, so an HTTP/2 row that fell back
  to HTTP/1.1 fails.
- **Client**: one `UHttpClient` over `RawSocketTransport`, public API only. Scenarios run one request at a
  time (latency is the round trip, not queueing): a verified warm-up request, up to `--warmup` unmeasured
  requests, then `--rounds` rounds of `--iterations` requests (fewer for the 1 MiB and 5 MiB rows), each
  round after a full GC.

| Scenario | Plan item (phase-22a.6 Step 2) | Gated |
| --- | --- | --- |
| `h1-`/`h2-get-1k-buffered`, `-get-32k-buffered`, `-post-1k-buffered` | 1, 2: small buffered round trips | yes |
| `h1-`/`h2-get-1k-streaming`, `-get-1m-streaming` | streaming read path, throughput | no |
| `h1-`/`h2-post-5m-buffered`, `-post-5m-stream` | 3: 5 MB upload, byte[] vs stream body | no |
| `raw-h1-get-1m` | the raw-socket floor for the streaming guardrail | no |
| `h1-get-100m-file-streaming`, `-file-buffered` (`--heavy`) | 4, Step 4: 100 MB download to file, peak heap | no |
| `h1-filedownloader-100m` (`--heavy`) | Step 1 Files item 40: `FileDownloader` to disk in bounded memory | no |
| `h2-10x10m-one-slow-consumer` (`--heavy`) | 5, Step 4: 10 concurrent 10 MB HTTP/2 downloads, one slow reader | fails if a fast stream ends after the slow one |

Reported per scenario: p50 (the lowest per-round median: background load only ever adds latency, so the
best round is the most repeatable estimate of the client's own cost), p95 and mean latency over all rounds; managed bytes
allocated per request (`GC.GetTotalAllocatedBytes(precise: true)`, median over rounds; it includes the
caller's own async state machine, as any caller's would); MiB/s for bodies of 1 MiB or more; peak managed
heap growth over the post-GC baseline for the memory rows (sampled every millisecond). The run also prints
the HTTP/1.1 1 MiB streaming throughput as a ratio of the raw-socket floor (the "within 5-10 % of raw"
guardrail).

## Regression check

With `baseline.json` present, `run.sh` compares every scenario. A **gated** row (the small buffered round
trips: the plan's "<= 5 % latency regression for buffered small-body paths") fails the run when:

- its managed bytes per request exceed the baseline by more than `--budget` percent (default 5) plus 64
  bytes. Allocation transfers between machines and barely moves between runs (under 2 % on the gated rows),
  so this is always gated;
- with `--gate-latency` only: its p50 is more than the budget above the baseline, and the baseline was
  recorded on the same machine shape (OS, architecture, processor count). Without the flag, or on another
  machine, latency deltas are printed as advisory.

Latency is opt-in because loopback latency on a shared host is not repeatable to 5 %: on the 4-CPU WSL2
machine that recorded the baseline, identical back-to-back runs moved the gated p50s by -43 % to +8 % while
other jobs ran (load average 6 to 10). Use `--gate-latency` on a quiet machine (the run warns when the load
average exceeds half the processors), with a baseline recorded there (`--record --heavy`), and rerun before
reading one over-budget row as a regression. The other rows print their deltas for information.

## The committed baseline

No numeric Phase 22.3 baseline survives in the repository (the 22.3 journal has no numbers and the earlier
22a figures came from a temporary harness), so `baseline.json` records the first run of this harness: the
library at the commit in its `commit` field, on the machine in its `machine` field. It is the reference for
later regressions, not a comparison with 22.3.

What this harness does not cover: IL2CPP or Mono numbers (it runs on CoreCLR; the NUnit allocation gates in
`Tests/Runtime/Performance/` are the in-Unity check), device memory and thread-pool behavior (Phase 22a.6
Steps 3 and 5, tracked in #99), and real packet loss (`Tools~/soak` covers stalls and resets).
