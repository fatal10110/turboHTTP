# Phase 22a.4: Buffered Fast Path and Performance Tuning

**Depends on:** 22a.2 and 22a.3 (both complete)
**Assemblies:** `TurboHTTP.Core`, `TurboHTTP.Transport`
**Files to create:** 2 new, several modified

---

## Step 1: `StreamingOptions` Type

**File:** `Runtime/Core/StreamingOptions.cs` (new)

Runtime-configurable thresholds exposed via `UHttpClientOptions`. Avoids compile-time-only constants that require code changes for tuning.

```csharp
public sealed class StreamingOptions
{
    public int SmallBufferedRequestThresholdBytes { get; set; } = 32 * 1024;
    public int DefaultStreamingSendBufferBytes { get; set; } = 32 * 1024;
    public int DefaultStreamingReceiveBufferBytes { get; set; } = 64 * 1024;
    public int DefaultHttp2PerStreamReceiveBufferBytes { get; set; } = 256 * 1024;
    public int BufferedDrainReuseThresholdBytes { get; set; } = 64 * 1024;
    public int MaxConnectionBufferedBytes { get; set; } = 8 * 1024 * 1024;
    public int Http2StallTimeoutSeconds { get; set; } = 60;
}
```

**`SmallBufferedResponseThresholdBytes` removed** — it had no implementable enforcement mechanism. For responses, the code path is determined by calling `SendBufferedAsync` vs `SendStreamingAsync`, not by body size. The actual zero-copy fast path for small responses is `TryDetachBufferedBody` on `IResponseBodySource`, which returns true when all data is already buffered — it does not use a threshold.

**`MaxConnectionBufferedBytes` added** — aggregate HTTP/2 memory bound across all concurrent streams (see 22a.3 Step 3). When the sum of unconsumed bytes exceeds this limit, connection-level WINDOW_UPDATE is deferred.

**`Http2StallTimeoutSeconds` added** — stall detection timeout for HTTP/2 streams (see 22a.3 Step 7). Default 60 seconds.

These are starting targets, not frozen constants. Mobile profiling in 22a.6 may reduce values if per-request working set is too high under concurrent load on low-memory Android devices.

Wire into `UHttpClientOptions`:
```csharp
public StreamingOptions Streaming { get; set; } = new StreamingOptions();
```

---

## Step 2: Direct Buffered Request-Body Send Path

**Files:** `Runtime/Transport/Http1/Http11RequestSerializer.cs`, `Runtime/Transport/Http2/Http2Connection.Send.cs` (modified)

For bodies where `TryGetBufferedData(out var data)` succeeds:

- Write directly from the `ReadOnlyMemory<byte>` to the socket/stream
- **No `Stream` adapter allocation**
- **No transfer buffer rental**
- **No chunked framing** (Content-Length is known from `data.Length`)

This is the hot path for small JSON/form payloads. Must remain zero-overhead relative to the pre-22a buffered path.

### Threshold Check

If body length <= `SmallBufferedRequestThresholdBytes`:
- Use direct memory write (no session open)
- Single `WriteAsync` call for headers + body where possible

If body length > threshold but still buffered (`TryGetBufferedData` succeeds):
- Still use direct memory write (the data is already in memory)
- The threshold only affects whether a streaming adapter is allocated, not whether buffered data is copied

---

## Step 3: Direct Buffered Response Collector Path

**File:** `Runtime/Core/Pipeline/BufferedResponseCollectorHandler.cs` (modified)

Optimized drain path for `SendBufferedAsync`:

### `IResponseBodySource` Fast-Path Contract

Add to `IResponseBodySource`:
```csharp
bool TryDetachBufferedBody(out DetachedBufferedBody body);
```

For body sources that are already fully buffered (e.g., small HTTP/2 responses where all DATA frames fit in the bounded queue before the consumer reads):
- Returns `true` only when the source can **transfer ownership** of the buffered body to the caller with no follow-up reads required
- `DetachedBufferedBody` carries the already-buffered representation in the same ownership shape `UHttpResponse` already supports: single-segment memory or multi-segment `ReadOnlySequence<byte>` plus owner/disposal handle
- The source enters a detached terminal state on success. The collector must not call `ReadAsync`, `DrainAsync`, or `GetTrailersAsync` after detaching
- Enables **zero-copy collector path** — `BufferedResponseCollectorHandler` skips the `ReadAsync` loop and constructs `UHttpResponse` directly from the detached body

For streaming body sources:
- Returns `false`
- Collector falls back to standard `ReadAsync` drain loop into `SegmentedBuffer`

### Optimized Drain into `SegmentedBuffer`

When fast path is not available:
- Drain body source using `ReadAsync` into `SegmentedBuffer`
- Reuse existing pooled segment infrastructure from Phase 19a.3
- No extra full-body copy — `UHttpResponse` retains the `ReadOnlySequence<byte>` plus owner directly

---

## Step 4: Handler/Body-Source Wrapper Pooling Assessment

**Files:** Various (assessment, conditional implementation)

Assess whether pooling of handler wrappers and body source wrappers produces measurable benefit:

### Candidates for Pooling

1. `BufferedResponseCollectorHandler` — allocated per `SendBufferedAsync` call
2. `DecompressionBodySource` (22a.5) — allocated per decompressed response
3. `TeeBodySource` (22a.5) — allocated per cached response

### Decision Criteria

- Measure allocation rate via `GC.GetTotalMemory` in loopback benchmarks
- Only pool if allocation cost is measurable relative to I/O cost
- If pooling is warranted, use existing `ObjectPool<T>` from `Core/Internal` with `PrepareForPool()` reset pattern

### Expected Outcome

Handler wrappers are ~64-128 bytes each. At 3-5 wrappers per request, total is 200-600 bytes — likely within noise for most workloads. Pooling deferred unless benchmarks show otherwise.

---

## Step 5: Allocation and Latency Tuning

**Files:** Various transport and core files

### Allocation Audit

Walk the hot paths and verify:

1. **Small buffered request + small buffered response:** no per-request allocations beyond the request/response objects themselves. No `Stream` adapter, no transfer buffer, no session object.
2. **Streaming paths:** one session allocation, one transfer buffer rental (returned immediately), body source allocation. No per-chunk allocations.

### Latency Audit

Compare against 22.3 baseline:

1. Small JSON GET/POST: must not regress > 5%
2. Small form POST: must not regress > 5%
3. The new code path (`SendBufferedAsync` → `BufferedResponseCollectorHandler` → drain → `UHttpResponse`) must not add measurable latency relative to the old `SendAsync` → `ResponseCollectorHandler` path

### Tuning Actions (if needed)

- Inline small methods on the buffered fast path
- Eliminate unnecessary `ConfigureAwait(false)` on synchronously-completing paths
- Verify `ValueTask` completes synchronously when body source has buffered data (avoids async state machine boxing)

---

## Path-by-Path Memory Targets

| Path | Peak Managed Memory Target | Allocation / Perf Target |
|------|----------------------------|--------------------------|
| Small buffered request + small buffered response | Body bytes only, no duplicate full-body copy | No more than 5% latency regression vs 22.3 baseline; keep hot-path GC within Phase 19 targets |
| Small buffered request + streaming response | Request bytes + <= 64 KB receive buffer | Slight fixed-cost increase allowed; no `O(response size)` growth |
| Streaming request + small buffered response | <= 32 KB send buffer + buffered response bytes | No request-body copy; buffered response still single-copy |
| Streaming request + streaming response | <= 32 KB send buffer + bounded receive buffer | Largest memory win; per-chunk coordination overhead acceptable if throughput within 5-10% of raw socket/file baseline |
| Large buffered request + large buffered response | Exactly one buffered request body + one buffered response body | No extra full-body transport copy; segmented storage allowed |

---

## Planned File Impact

| File | Change |
|------|--------|
| `Runtime/Core/StreamingOptions.cs` | **New:** runtime-configurable threshold type |
| `Runtime/Core/DetachedBufferedBody.cs` | **New:** ownership-carrying detached buffered response representation |
| `Runtime/Core/UHttpClientOptions.cs` | Add `Streaming` property |
| `Runtime/Core/IResponseBodySource.cs` | Add `TryDetachBufferedBody` fast-path method |
| `Runtime/Core/Pipeline/BufferedResponseCollectorHandler.cs` | Zero-copy fast path, optimized drain |
| `Runtime/Transport/Http1/Http11RequestSerializer.cs` | Threshold-based fast-path selection |
| `Runtime/Transport/Http1/Http11ResponseBodySource.cs` | `TryDetachBufferedBody` implementation |
| `Runtime/Transport/Http2/Http2Connection.Send.cs` | Threshold-based fast-path selection |
| `Runtime/Transport/Http2/Http2ResponseBodySource.cs` | `TryDetachBufferedBody` implementation |

---

## Completion Criteria

- Small JSON and form workloads do not regress materially (< 5% latency vs 22.3 baseline)
- Large streaming paths remain bounded and allocation-light
- `StreamingOptions` type is named, documented, and wired into `UHttpClientOptions`
- `TryDetachBufferedBody` fast path produces measurable improvement for small-body responses without violating pooled-buffer ownership
- No per-chunk allocations on streaming paths

## Non-Negotiable Performance Guardrails

1. Buffered small-body path must remain a first-class optimized path, not a streaming wrapper disguised as buffering
2. Streaming path must not allocate per chunk in normal operation
3. HTTP/2 receive buffering must stay bounded per stream
4. No module may reintroduce an accidental whole-body copy on the transport hot path
5. Regression benchmarks use **loopback `HttpListener`-based test server** (not `MockTransport`)

## Post-Step Review

Both specialist agents must review before proceeding to 22a.5:
- `unity-infrastructure-architect`
- `unity-network-architect`
