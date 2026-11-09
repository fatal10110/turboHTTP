# Soak and lossy-network harness (#114)

An hour of mixed traffic through TurboHTTP, with the leak checks the 1.0 criterion asks for: less than
10 MB managed-heap growth, no pooled object or pooled byte buffer left outstanding, no socket left open.

```bash
'Tools~/soak/run.sh'                                  # 1 h, every profile (the release gate)
'Tools~/soak/run.sh' --duration 120s                  # a short local run
'Tools~/soak/run.sh' --duration 10m --profiles clean,lossy --workers 4
```

Needs the .NET 8 SDK (with its ASP.NET Core shared framework) and the Unity managed DLLs the shadow build
links (`Tools~/ci/fetch-unity-dlls.sh`, run automatically). No Unity license, no root, no network beyond
loopback. The manual workflow `.github/workflows/soak.yml` runs the same script and uploads the results.
Exit codes: 0 pass, 1 fail, 2 usage, 3 tool missing.

## What runs

`run` (the client under test) starts `serve` as a second process, so the client's heap and socket count
hold nothing but TurboHTTP:

- **Server**: Kestrel (ASP.NET Core 8) on loopback, an independent peer: HTTP/1.1 cleartext with
  WebSocket, and HTTP/1.1 + HTTP/2 over TLS with ALPN. It advertises the RFC 9113 default 65,535-byte
  windows, so every large upload is flow controlled. The TLS leaf is issued by a per-run root that run.sh
  trusts through `SSL_CERT_FILE`/`SSL_CERT_DIR` only; the client's certificate validation is unchanged.
  Every response carries `x-soak-protocol`, so an ALPN fallback to HTTP/1.1 fails the HTTP/2 rows.
- **Fault proxies**: a TCP forwarder per profile in front of each server port. It forwards ciphertext
  and never terminates TLS.

  | Profile | Faults |
  | --- | --- |
  | `clean` | none (direct to Kestrel) |
  | `latency` | 40 ms one-way delay per chunk, 10 ms jitter |
  | `bandwidth` | 2 MiB/s per direction per connection, 5 ms delay |
  | `lossy` | 10 ms delay; 2 % of chunks stall 100 to 600 ms (what loss and retransmission look like to TCP's user) |
  | `reset` | every connection is reset (RST) once it has forwarded a random 16 KiB to 4 MiB |

  A user-space proxy cannot drop single packets: the kernel retransmits below it. "Loss" here is the
  stall that retransmission causes, or a reset. This is not a packet-loss validation; kernel `tc netem`
  (root or CAP_NET_ADMIN) is not used.

  Requests time out after 30 s, except the HTTP/2 rows of `lossy`, which get 90 s. The proxy delivers each
  direction in order and stalls it as a whole, so the server's WINDOW_UPDATEs wait behind download DATA,
  and the test server pins its flow-control windows at 65,535 bytes. A 1 MiB HTTP/2 echo can therefore
  legitimately need more than 30 s there (#409). Both limits are below the 120 s operation watchdog.
- **Client workers** (8 by default) draw operations from a seeded mix, each against a random profile:
  buffered GETs and echo POSTs on both protocols, streamed chunked downloads, 512 KiB to 3 MiB HTTP/2
  downloads and 1 MiB uploads (flow control), bursts of 16 multiplexed HTTP/2 requests, cancellation
  before the response and in the middle of a body, bodies abandoned mid-stream, WebSocket echo sessions
  closed normally or dropped without a close. Every body byte is verified.

## Verdict

Every `--checkpoint` seconds (default 600, the first after the warm-up, min(60 s, duration / 5)), and at
the end, the workers pause until nothing is in flight; the harness then forces a full GC and records the
managed heap, the client's sockets (`/proc/self/fd`), the server's open connections and the outstanding
count (`Rent - Return - Discard`) of the HTTP/2 stream, parsed-response and header-scratch pools, plus
the library's pooled byte buffers held (`ByteArrayPool` rents minus returns since before the client was
created, #292). All checkpoints compare the same idle load, with the pooled connections still open, so the
byte-buffer count there includes the socket and read buffers idle connections keep. The run fails when:

- any operation fails unexpectedly (a non-`UHttpException`/`WebSocketException` error anywhere, any
  error outside the `reset` profile, a wrong status or body byte, an intended cancellation that does not
  cancel), hangs past 120 s, or a faulted task is never observed (`TaskScheduler.UnobservedTaskException`);
- the heap at the final checkpoint exceeds the baseline checkpoint by more than `--max-growth-mb` (10);
- a pool has an outstanding item at a checkpoint, or a checkpoint has more than `--max-sockets` (256)
  client sockets;
- after the client and transport are disposed, a pooled byte buffer is still outstanding (a rent with no
  return: some path broke the one-owner, return-once rule), the client still holds a socket, or a fault proxy still
  holds a connection (the proxy closes as soon as either side does). Kestrel's own open-connection count
  is reported; it is the oracle only where the client socket count is unavailable, because Kestrel may
  finish tearing down HTTP/2 connections later than the client.

`.build/soak/run/result.json` has the numbers, `samples.csv` one row per `--sample` (10 s) and
`harness.log` the console output.

## Limits

- `ArrayPool<byte>.Shared` rents are not counted (no production counter); retained byte arrays show up
  only as heap growth (#292).
- Workstation, non-concurrent GC on .NET 8, not Unity Mono or IL2CPP: the leak checks hold for the managed
  code paths, but a Mono or device heap profile is not measured here. The device run of a shortened soak
  belongs to the device matrix (#99).
- Linux only for the socket count (`/proc/self/fd`); elsewhere it reports -1 and that check is skipped.
