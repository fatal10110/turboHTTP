# 2026-11: count every byte-buffer rent so the soak can prove none leak (#292)

## What

- Every `ArrayPool<byte>.Shared.Rent/Return` in the runtime modules now goes through the existing
  Interlocked-counting `ByteArrayPool` (`Runtime/Core/Internal/ByteArrayPool.cs`). That covers Core, Transport,
  WebSocket, Cache, Middleware, Observability, Files, JSON and Testing, 135 call sites. `ArrayPoolMemoryOwner<T>`
  takes the same path when `T` is `byte`.
- Its `ActiveCount` is now a leak oracle: rents minus returns minus abandons.
- New `ByteArrayPool.Abandon(byte[])` and `ByteArrayPoolDiagnostics.AbandonCount`. They count a rented array
  the code deliberately leaves to the GC because abandoned I/O may still write into it. Nothing goes back to
  the pool. Five sites already did this without counting it:
  - `Http11ResponseParser.BufferedStreamReader.Dispose`: a cancelled head read (#293) or body read (#345)
    still pending.
  - `SaeaSocketChannel.Dispose`: drain timeout (#168).
  - `PollSelectSocketChannel.Dispose`: the poll thread did not exit within 500 ms.
  - `WebSocketHandshakeValidator`: the abandoned handshake read, and the prefetch reader's `Dispose`.
- Soak (`Tools~/soak`):
  - Records `byte_buffers_held` at every checkpoint (JSON, console, and a new `samples.csv` column).
  - After the client and transport are disposed, waits up to 10 s and fails the run when any library byte
    buffer is still outstanding. The value is in `result.json` as `byte_buffers_after_dispose`.
  - Updated `README.md` and the `Program.cs` header.
- Updated the `PoolHealthReporter` doc comment: the counters now cover all runtime raw byte rents.

## Decisions

- **Scope: every runtime module, not only Transport.**
  - `ArrayPoolMemoryOwner<byte>` is shared by Core (`PooledArrayBufferWriter.DetachAsOwner`), Transport
    (`Http2ResponseBodySource` detached chunks) and WebSocket. A counted rent returned raw, or a raw rent
    returned counted, makes the count drift for good.
  - Routing every friend assembly mechanically removes every such mixed flow.
  - Left raw on purpose:
    - `Runtime/Bootstrap/JsonUtilitySerializer.cs` has no `InternalsVisibleTo` from Core, and its rent and
      return sit in one try/finally.
    - The soak harness's own rents in `Tools~/soak/Ops.cs`.
    - Test fixtures.
- **Soak oracle: checked after dispose, not at idle checkpoints.**
  - Idle pooled connections legitimately hold buffers: the socket-channel recv/send buffers, the HTTP/2
    read buffer and the HPACK output buffer. So a periodic checkpoint cannot require 0. A 120 s run showed 5
    held at both the baseline and the final checkpoint.
  - Once `UHttpClient` and `RawSocketTransport` are disposed nothing is in flight and nothing may be held.
    The harness checks against the count taken before the client was created.
  - Periodic checkpoints record the value so growth stays visible.
- **No behaviour change.** `ByteArrayPool` wraps the same `ArrayPool<byte>.Shared` instance.
  - `Rent(0)` returns `Array.Empty<byte>()`, as the shared pool does.
  - Negative lengths throw `ArgumentOutOfRangeException` in both.
  - The only difference: `Return(null)` is ignored where the raw pool throws `ArgumentNullException`. No call
    site can pass null on a non-bug path; the scout audit found none.
- **Rent and return pairs.** A cs-scout audit of every return site found no array returned that was not
  rented from the pool, and no double return.
- **Hot-path cost.** One `Interlocked.Increment` per rent and per return on a static long, plus one more for a
  `clearArray: true` return. No allocation: `GetDiagnostics()` returns a struct, and the `ArrayPoolMemoryOwner`
  `typeof(T) == typeof(byte)` check is a JIT/IL2CPP constant per instantiation with reference casts. Cross-core
  contention on the counter's cache line is small next to `ArrayPool.Rent` itself, which already does
  per-core work.

## Verification

- `'./Tools~/ci/check.sh' --full` passes (shadow build with analyzers, including `Tools~/soak`).
- `Tools~/soak/run.sh --duration 120s` (8 workers, all profiles, .NET 8):
  - Before `Abandon` existed, it failed with "12 pooled byte buffer(s) not returned after the client and
    transport were disposed". A rerun with temporary per-rent stack capture (never committed) showed that all
    the outstanding buffers were `BufferedStreamReader` buffers abandoned by cancelled reads. That proves the
    check fires.
  - After `Abandon`, it passes with `byte_buffers_after_dispose: 0`.
- New tests in `Tests/Runtime/Performance/ByteArrayPoolLeakDetectionTests.cs`:
  - An unreturned rent is outstanding until it is returned.
  - `Abandon` is not outstanding and is not a return.
  - An unreleased HTTP/1.1 fixed body counts as 1 until `ReleaseBodyBuffers`. This test fails on the old code,
    where the parser rented raw.
  - A detached `PooledArrayBufferWriter` owner counts as 1 until it is disposed, and disposing it twice
    returns the buffer once. Fails on the old code.
  - `ArrayPoolMemoryOwner<byte>.Rent` is counted. Fails on the old code.
  - A `BufferedStreamReader` with an abandoned read is counted as abandoned, not outstanding. Fails without
    `Abandon`.
  - Zero-length rents and returns are not counted.
- Unity PlayMode, scoped run, filter
  `ByteArrayPool|ObjectPoolDiscardAccounting|Phase19AllocationGate|StreamingAllocationGate|CoreSurface|Http11ResponseParser|HeadReadCancellation|BodyReadCancellation|StressTests`:
  - 177 passed, 0 failed.
  - This includes `Phase19AllocationGateTests` and `StreamingAllocationGateTests`. Their results XML records no
    byte counts.
  - The full suites run in the periodic host-editor run.
- Not run:
  - IL2CPP player and device.
  - Before/after allocation numbers. The gates only assert their limits (and pass); they print no numbers.

## Deferred

- #114 (the hour-long soak itself) stays open; this only adds the byte-buffer oracle to it.
