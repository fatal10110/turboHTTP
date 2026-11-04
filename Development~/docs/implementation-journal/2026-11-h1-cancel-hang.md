# 2026-11 — HTTP/1.1 head read and CONNECT reply read race the token (#293)

## What

A cancelled in-flight HTTP/1.1 request on Unity 2021.3 Mono (Windows host editor) could hang after
dispose-on-cancel had closed its socket: about one run in 25 of
`SoakShortTests.Http11_MixedWorkloadWithInFlightCancel_EveryCancelledRequestEnds`, and the same shape
on the iOS IL2CPP simulator (`ReusedConnectionFirstByteTimeoutTests.InfiniteFirstByteTimeout_DisablesTheRetry`,
#293 comment; #322 lists the IL2CPP family). The transport relied on the platform completing the
pending socket read once the socket was disposed. It no longer does for the response head and the
CONNECT reply: those reads race the token and return the cancellation at once.

## Files

- `Runtime/Transport/Internal/AbandonableTask.cs`: `WaitAsync<T>(Task<T>, ct, abandonedResource)`
  (the generic form of the #172 helper) and `Observe(Task, IDisposable)`, which observes the abandoned
  task and disposes the resource once it ends.
- `Runtime/Transport/Http1/Http11ResponseParser.cs` (`BufferedStreamReader`): in the head phase a
  pending `FillBufferAsync` read with a cancellable token goes through `AbandonableTask.WaitAsync`.
  The abandoned read keeps `_buffer` (`Dispose` leaves it to the GC while the read is pending, as
  `WaitForAbortedReadAsync` already did for expect-continue) and its end disposes the stream.
  `ParsedResponseHead`'s constructor calls `MarkHeadComplete()`, so body reads keep the old path
  (no race, no allocation per read).
- `Runtime/Transport/Http1/ConnectReplyReader.cs`: `ReadAsync` and `DrainBodyAsync` race their whole
  core (the `LineReader` buffers go back to the pool only after the abandoned read ended). Covers the
  HTTP CONNECT tunnel and the WebSocket proxy tunnel (`ProxyTunnelConnector`).
- `Tests/Runtime/Transport/Http1/HeadReadCancellationTests.cs` (new): a stream whose read ignores the
  token and `Dispose`; parse, `EnsureBufferedAsync` and the CONNECT reply end with
  `OperationCanceledException`, the buffer stays out of the pool, the late fault is observed, the
  stream is disposed when the read ends, and an uncancelled parse still completes.
- `Tests/Runtime/Performance/SoakShortTests.cs`: the in-flight-cancel soak is no longer `[Explicit]`.
- `Tools~/unity/skip-baseline.txt`: PlayMode 4 -> 3.

## Decisions

- **Race at the read, not around `SendOnLeaseAsync`.** Abandoning the whole send would leave the
  request serializer and `RequestContext` in use after the request ended (pooled request bodies, the
  context's event list). Abandoning only the platform read leaves nothing but the read and its buffer.
- **Head phase only.** `FillBufferAsync` also serves body reads; racing them would allocate per read
  on the zero-allocation body path (`StreamingAllocationGateTests`). Body reads keep
  `Http11ResponseBodySource`'s close-on-cancel handling.
- **Allocation.** Only a head read that is still pending with a cancellable token pays for the race
  (completion source, registration, `WhenAny`): about one per request, never per body read.
  Non-cancellable tokens and synchronously completed reads take the old path unchanged.
- **Connection never pooled.** The race throws `OperationCanceledException`; the dispatch catch
  ladder maps it to Cancelled/Timeout and `lease.Dispose()` destroys the connection
  (`RegisterDisposeOnCancel` already disposed stream and socket).
- **HTTP/2 preface.** `Http2Connection.InitializeAsync` already waits on a completion source that a
  token registration completes; no read is awaited under the request token there. No change.

## Root cause

Mono socket behavior, not a TurboHTTP wait
([#293 comment](https://github.com/fatal10110/turboHTTP/issues/293#issuecomment-6625851176)).

- **Repro.** Host-editor PlayMode (2021.3.45f2, Mono) on origin/main 69135eb, with temporary
  head-read tracing and `[Repeat(80)]` on the soak test. The watchdog fired after 62 s.
- **What the hung stream did.** It was a fresh plain-HTTP connection (`NetworkStream` over `Socket`).
  The `NetworkStream.ReadAsync` of `FillBufferAsync` was being issued when dispose-on-cancel disposed
  the stream on another thread. `ReadAsync` returned a pending task, the cancel callback returned, and
  the read never completed or faulted.
- **Not a TurboHTTP wait.** No TurboHTTP wait was involved: the only pending await was the platform
  read.
- **Intermittent.** Another stream in the same run had the identical interleaving and faulted correctly
  with "The I/O operation has been aborted". So this is an intermittent race in Mono's socket async
  path between a pending receive and `Socket.Dispose`. Hence the rule: never rely on platform read
  completion after a cancel.

## Verification

- `'./Tools~/ci/check.sh'`: pass.
- **Host-editor PlayMode (Unity 2021.3.45f2, Mono), at 89a53c5:**
  - `--filter 'SoakShortTests.Http11_MixedWorkloadWithInFlightCancel'` with a temporary
    `[Repeat(100)]`: 1/1 passed. The results show `Repeat=100`, 42.9 s.
  - Scoped
    `RawSocketTransportCancellationTests|ReusedConnectionFirstByteTimeoutTests|HeadReadCancellationTests|Http11ResponseParser|ConnectReply|AbandonableTask|SoakShortTests.Http11_Mixed|SoakShortTests.Http11_Abandoned`
    (the soak still at `Repeat(100)`): 150/150 passed.
  - The same soak at `[Repeat(80)]` on origin/main hung, which was the root-cause run.
- The new tests and `AbandonableTaskTests`, `RawSocketTransportCancellationTests`,
  `ReusedConnectionFirstByteTimeoutTests`, `ConnectReplyReaderTests`, the proxy tunnel/407 tests and
  `SoakShortTests` were run on .NET 8 through the shadow-built test assembly (not a Unity run): all
  pass except three BouncyCastle-dependent proxy tests that need the BC module loaded. Against
  origin/main the four cancellation tests of the new file time out (the read never ends).

## Not run

- The full Unity suites: they run in the periodic host editor.
- Unity EditMode: no Editor tests are affected.
- IL2CPP, devices: iOS and Android IL2CPP behavior stays `needs-device-validation` (#322).
