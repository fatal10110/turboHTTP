# Phase 22a.6: Validation, Benchmarks, Mobile/IL2CPP Pass

**Depends on:** 22a.5 (complete)
**Assemblies:** All (validation pass)
**Files to create:** Test files, benchmark files

---

## Step 1: Functional Test Coverage

### Core Body Model Tests (`Tests/Runtime/Core/`)

1. `RequestBodyReadSession` lifecycle: second `OpenReadSessionAsync` on non-replayable body throws `InvalidOperationException`
2. `RequestBodyReadSession` lifecycle: replayable body allows re-open after previous session disposed
3. `BufferedRequestBody` → `TryGetBufferedData` returns true with correct memory
4. `StreamRequestBody` with seekable stream → `Replayable`, re-open seeks to captured `_startPosition` (not position 0)
5. `StreamRequestBody` with seekable stream at non-zero position → re-open correctly resets to starting position
6. `StreamRequestBody` with non-seekable stream → `NonReplayable`
6. `FactoryRequestBody` → `ReplayableViaFactory`, each `OpenReadSessionAsync` creates fresh stream
7. Detached clone rules: `BufferedRequestBody`, `OwnedMemoryRequestBody`, `FactoryRequestBody`, and `FileRequestBody` clone into independently owned request content
8. Detached clone rules: `StreamRequestBody.Clone()` throws even when the body is sequentially replayable
9. Shared-content copy helper preserves the `Content` reference without opening a second reader
10. `MockResponseBodySource` correctly simulates streaming for unit tests
11. `FileRequestBody` from `TurboHTTP.Files` works with `WithFileBody` extension method

### HTTP/1.1 Transport Tests (`Tests/Runtime/Transport/Http1/`)

9. HTTP/1.1 known-length upload from `FileStream`
10. HTTP/1.1 chunked upload from unknown-length stream (terminal `0\r\n\r\n` verified)
11. HTTP/1.1 large download streamed to file
12. HTTP/1.1 early-dispose response: drain succeeds when remaining <= 64 KB with deterministic framing and no `Connection: close`
13. HTTP/1.1 early-dispose response: closes immediately when `Connection: close` is present
14. HTTP/1.1 early-dispose response: chunked drain succeeds when EOF reached within 64 KB budget
15. HTTP/1.1 early-dispose response: chunked close when EOF not reached within 64 KB budget
16. HTTP/1.1 HEAD response with `Content-Length` produces `EmptyResponseBodySource` (no body read attempted)
17. HTTP/1.1 204/304 responses produce `EmptyResponseBodySource`
18. HTTP/1.1 request serializer: `Content-Length` set and `Transfer-Encoding` stripped when body length is known
19. HTTP/1.1 request serializer: `Transfer-Encoding: chunked` set and `Content-Length` stripped when body length is unknown
20. HTTP/1.1 response parser: `Transfer-Encoding` takes precedence over `Content-Length` when both present (RFC 9112 Section 6.1)
21. HTTP/1.1 response parser: stacked transfer codings beyond bare `chunked` are rejected with clear error
22. HTTP/1.1 `ReadAsync` cancellation transitions body source to faulted state (connection not reusable)
23. HTTP/1.1 drain uses linked cancellation (both caller CT and 2-second timeout honored)

### HTTP/2 Transport Tests (`Tests/Runtime/Transport/Http2/`)

17. HTTP/2 large upload with flow-control stalls
18. HTTP/2 large download with slow consumer (per-stream backpressure, connection-level window stays open)
19. HTTP/2 concurrent mixed-size streams: one slow consumer does not block other streams' DATA delivery
20. HTTP/2 zero-body response (`HEADERS+END_STREAM`): `ReadAsync` returns 0 immediately
21. HTTP/2 early-dispose: `RST_STREAM(CANCEL)` sent, queued buffers released, post-RST DATA frames handled correctly
22. HTTP/2 stall detection: consumer that stops reading triggers stream reset after timeout (coarse-grained check, no per-stream Timer)
23. HTTP/2 aggregate memory bound: `MaxConnectionBufferedBytes` defers connection-level WINDOW_UPDATE when exceeded
24. HTTP/2 `ReadAsync` cancellation on bounded queue does not corrupt state (stronger guarantee than HTTP/1.1)
25. `SingleReaderChannel<T>` version wrapping: exercise 100,000+ read cycles on both Mono and IL2CPP to validate `ManualResetValueTaskSourceCore<int>` short version counter wrapping

### Retry/Redirect Tests (`Tests/Runtime/Retry/`, `Tests/Runtime/Middleware/`)

23. Retry/redirect behavior with replayable vs non-replayable bodies
24. Retry with body where headers were sent but no body bytes committed: retry succeeds for replayable body
25. Non-replayable body failure after partial send: dedicated transport error, no silent buffering

### Request Mutation / Queueing Tests (`Tests/Runtime/Core/`, `Tests/Runtime/Auth/`, `Tests/Runtime/Transport/`)

26. `BackgroundNetworkingInterceptor` queues only detached-cloneable request bodies
27. `AdaptiveInterceptor` uses shared-content copy semantics when only timeout changes
28. `AuthInterceptor` uses shared-content copy semantics when only headers change

### Decompression Tests (`Tests/Runtime/Middleware/`)

29. Decompression while streaming (incremental `GZipStream`)
30. Decompression bomb: compressed response exceeding limit aborts during streaming, not after OOM
31. Decompression with early dispose: CRC32 not validated (acceptable, documented)

### Cache Tests (`Tests/Runtime/Cache/`)

32. Cache commit only after successful full-body completion (natural EOF)
33. Cache tee with consumer abandon mid-stream: no cache entry produced
34. Cache tee with cache write failure: consumer continues receiving body, cache silently detaches
35. Cache tee with body exceeding `MaxCacheableResponseBodyBytes`: silently detaches, consumer continues reading

### Observability Tests (`Tests/Runtime/Pipeline/`, `Tests/Runtime/Observability/`)

35. Logging request preview uses `TryGetBufferedData(...)` for buffered bodies and does not force buffering for streaming bodies
36. Metrics request-byte accounting uses transport-populated sent-byte totals for unknown-length streaming uploads
37. Monitor request snapshot captures buffered request bytes when available and records streaming bodies as unavailable without buffering

### Plugin/Interceptor Tests (`Tests/Runtime/Core/`)

38. `CapabilityEnforcedInterceptor` correctly detects request mutation under new `Content` model
39. `ObservedHandler` correctly tracks body consumption and trailer access

### File Tests (`Tests/Runtime/Files/`)

40. Streaming file download with bounded memory
41. `FileRequestBody` with `WithFileBody` extension method

### Adapter / Helper Migration Checks

42. `TurboHTTP.UniTask` compiles against `SendBufferedAsync(...)` and explicit buffered request helpers
43. `OAuthClient`, `JsonExtensions`, `UnityExtensions`, `AudioClipHandler`, `Texture2DHandler`, and `CoroutineWrapper` use the explicit buffered/streaming APIs with no remaining `SendAsync(...)` call sites

---

## Step 2: Performance Benchmarks

All latency benchmarks use a **loopback `HttpListener`-based test server**, not `MockTransport`.

### Benchmark Suite (`Tests/Runtime/Performance/`)

1. **1 KB JSON GET/POST buffered roundtrip** (loopback) — baseline latency measurement
2. **32 KB JSON buffered roundtrip** (loopback) — threshold boundary test
3. **5 MB upload: buffered vs stream body** (loopback) — memory and throughput comparison
4. **100 MB download to file: buffered vs streaming response** (loopback) — memory peak comparison
5. **10 concurrent 10 MB HTTP/2 downloads with one intentionally slow consumer** — flow control stress test
6. **Allocation-gate tests on small-body buffered paths** (existing Phase 19 gates must still pass)
7. **Streaming-path allocation-gate tests:** zero managed allocations per chunk (measured via `GC.GetTotalMemory` before/after N streaming chunks, divided by N)
8. **Async state machine boxing validation:** hot-path `ReadAsync` chain completes synchronously when data is already buffered (no boxing under IL2CPP)

### Performance Guardrails

- <= 5% latency regression for buffered small-body paths vs 22.3 baseline
- Streaming paths: throughput within 5-10% of raw socket/file baseline
- Zero per-chunk allocations on streaming paths

---

## Step 3: IL2CPP iOS/Android Validation on Physical Devices

### IL2CPP Validation Checkpoints

These were first validated in the **22a.1 Step 0 IL2CPP spike** (blocking prerequisite) and are confirmed here with the full implementation:

1. **`IAsyncDisposable` + `await using`** on `UHttpStreamingResponse` — must work on iOS and Android IL2CPP
2. **`ValueTask<RequestBodyReadSession>`** AOT generic instantiation — verify `link.xml` entries are sufficient
3. **`ValueTask<HttpHeaders>`** on `GetTrailersAsync` — verify AOT generic instantiation
4. **`GZipStream` streaming decompression performance** — verify no IL2CPP-specific performance cliff
5. **`FileStream.WriteAsync` behavior under concurrent load** — measure thread pool pressure from synchronous I/O fallback
6. **Async state machine boxing on hot-path `ReadAsync` chain** — validate synchronous completion when data is buffered (no boxing)

### Platform Matrix

| Platform | Build Type | Validation |
|----------|-----------|------------|
| Unity Editor | Mono | Full test suite |
| Standalone | IL2CPP | Full test suite + allocation gates |
| iOS | IL2CPP | IL2CPP checkpoints + streaming smoke test |
| Android | IL2CPP | IL2CPP checkpoints + streaming smoke test + thread pool monitoring |

---

## Step 4: Memory Profiling

### Large Upload Scenario

- 100 MB file upload via `FileRequestBody`
- Verify peak managed memory stays bounded (send buffer + overhead, not `O(100 MB)`)
- Profile on both Mono and IL2CPP

### Large Download Scenario

- 100 MB streaming download to file via `SendStreamingAsync`
- Verify peak managed memory stays bounded (receive buffer + overhead, not `O(100 MB)`)
- Profile on both Mono and IL2CPP

### Concurrent Download Scenario

- 10 concurrent 10 MB HTTP/2 downloads
- Verify per-stream buffer stays within `DefaultHttp2PerStreamReceiveBufferBytes`
- Verify total memory scales with stream count, not total payload size

---

## Step 5: Mobile-Specific Threshold Tuning

Default thresholds (32 KB send, 64 KB receive, 256 KB HTTP/2 per-stream) may need reduction on low-memory Android devices under concurrent load.

### Tuning Process

1. Run concurrent download benchmark on low-memory Android device (2 GB RAM target)
2. Monitor `GC.GetTotalMemory` and GC pause frequency
3. If per-request working set causes GC pressure under 10+ concurrent streams, reduce `DefaultHttp2PerStreamReceiveBufferBytes` to 128 KB or 64 KB
4. Document recommended thresholds for mobile in `StreamingOptions` XML docs

---

## Step 6: Streaming-Path Allocation-Gate Tests

**File:** `Tests/Runtime/Performance/StreamingAllocationGateTests.cs` (new)

Zero managed allocations per chunk on streaming paths:

```csharp
// Pseudo-test structure
var before = GC.GetTotalMemory(true);
for (int i = 0; i < N; i++)
{
    await bodySource.ReadAsync(buffer, ct);
}
var after = GC.GetTotalMemory(true);
var perChunkBytes = (after - before) / N;
Assert.That(perChunkBytes, Is.LessThanOrEqualTo(threshold));
```

Gates:
- HTTP/1.1 streaming read: 0 bytes per chunk (steady state)
- HTTP/2 streaming read: 0 bytes per chunk (steady state)
- Decompression streaming read: `Task<int>` from `GZipStream` is expected (known allocation point)

---

## Step 7: Documentation Updates

### Files to Update

1. **`CLAUDE.md`** — Development Status section (Phase 22a status)
2. **`AGENTS.md`** — update alongside `CLAUDE.md` if phase status, architecture notes, or workflow expectations change
3. **`Development/docs/implementation-journal/`** — Session file for Phase 22a completion
4. **`Development/docs/phases/phase-22a-end-to-end-streaming.md`** — Mark as implemented, note any deviations
5. **XML docs** on all new public types (`UHttpRequestBody`, `IResponseBodySource`, `UHttpStreamingResponse`, `ResponseBodyStream`, `StreamingOptions`, `DetachedBufferedBody`)

---

## Planned Test File Impact

| File | Description |
|------|-------------|
| `Tests/Runtime/Core/RequestBodyTests.cs` | Body model lifecycle tests |
| `Tests/Runtime/Core/DispatchBridgeTests.cs` | Buffered + streaming bridge tests |
| `Tests/Runtime/Core/BackgroundNetworkingTests.cs` | Detached clone rules for queued/background replay |
| `Tests/Runtime/Transport/Http1/Http11StreamingTests.cs` | HTTP/1.1 streaming send/receive |
| `Tests/Runtime/Transport/Http2/Http2StreamingTests.cs` | HTTP/2 streaming, flow control, abort |
| `Tests/Runtime/Transport/AdaptiveInterceptorTests.cs` | Shared-content mutation helper behavior |
| `Tests/Runtime/Auth/AuthInterceptorTests.cs` | Header-only mutation preserves request content |
| `Tests/Runtime/Auth/OAuthClientTests.cs` | Explicit buffered send migration |
| `Tests/Runtime/Middleware/DecompressionStreamingTests.cs` | Incremental decompression, bomb mitigation |
| `Tests/Runtime/Retry/RetryStreamingTests.cs` | Retry with replayable/non-replayable bodies |
| `Tests/Runtime/Cache/CacheTeeTests.cs` | Tee source, EOF vs abandon |
| `Tests/Runtime/Pipeline/LoggingInterceptorTests.cs` | Buffered-vs-streaming request preview behavior |
| `Tests/Runtime/Observability/MetricsInterceptorTests.cs` | Request-byte accounting for known and unknown lengths |
| `Tests/Runtime/Observability/MonitorInterceptorTests.cs` | Request snapshot behavior for buffered and streaming bodies |
| `Tests/Runtime/Files/StreamingFileDownloadTests.cs` | Streaming file download |
| `Tests/Runtime/Unity/UnityExtensionsTests.cs` | Explicit buffered API migration |
| `Tests/Runtime/Unity/Texture2DHandlerTests.cs` | Explicit buffered API migration |
| `Tests/Runtime/Unity/AudioClipHandlerTests.cs` | Explicit buffered API migration |
| `Tests/Runtime/Unity/CoroutineWrapperTests.cs` | Explicit buffered API migration |
| `Tests/Runtime/Performance/StreamingAllocationGateTests.cs` | Zero-allocation gate tests |
| `Tests/Runtime/Performance/StreamingBenchmarks.cs` | Loopback latency benchmarks |
| `Tests/Runtime/Testing/MockResponseBodySourceTests.cs` | Mock body source tests |

---

## Completion Criteria

- Both specialist rubrics sign off on each sub-phase individually, and on the final integration
- Performance and memory targets are demonstrated, not inferred
- Loopback benchmarks confirm <= 5% latency regression for buffered small-body paths vs 22.3 baseline
- Streaming-path allocation-gate tests pass (zero per-chunk allocations in steady state)
- IL2CPP validation checkpoints all pass on physical iOS and Android devices
- Memory profiling confirms bounded peak memory on large upload/download scenarios

## Phase 22a Success Criteria (Overall)

Phase 22a is successful when all of the following are true:

1. Large uploads and downloads no longer require `O(body size)` managed memory by default
2. Small buffered requests/responses remain competitive with the 22.3 baseline
3. Retry/redirect behavior is explicit and correct for replayable and non-replayable bodies
4. Decompression, file download, and cache store operate incrementally
5. HTTP/2 flow control is tied to bytes actually consumed, not merely received
6. HTTP/2 aggregate buffered memory is bounded by `MaxConnectionBufferedBytes`
7. No optional module quietly reintroduces full-body buffering on the hot path
8. `SingleReaderChannel<T>` version wrapping validated through 100K+ cycles on IL2CPP
9. `Content-Length`/`Transfer-Encoding` conflict resolution enforced in request serializer
10. Response framing checks `Transfer-Encoding` before `Content-Length` per RFC 9112

## Post-Phase Review

Final integration review with both specialist agents:
- `unity-infrastructure-architect`
- `unity-network-architect`

Both reviews must pass. Implementation journal written, and `AGENTS.md` / `CLAUDE.md` updated together when architecture or workflow notes change.
