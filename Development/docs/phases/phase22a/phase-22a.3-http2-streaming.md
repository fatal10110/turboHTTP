# Phase 22a.3: HTTP/2 Streaming Send/Receive

**Depends on:** 22a.1 (complete)
**Assembly:** `TurboHTTP.Transport`
**Files to create:** 1 new, 3 modified

---

## Step 1: Producer-Fed DATA Send Path

**File:** `Runtime/Transport/Http2/Http2Connection.Send.cs` (modified)

`Http2Connection.SendDataAsync(...)` must read from the request body session incrementally instead of slicing one pre-buffered body.

### Buffered Fast Path

- If `TryGetBufferedData(out var data)` succeeds, write DATA frames directly from the stored memory
- No per-stream send buffer allocation
- End-stream flag set on the final DATA frame

### Streaming Path

- Request DATA production is paced by stream window + connection window
- A per-stream send buffer is rented lazily on first non-buffered read
- Read from `RequestBodyReadSession.ReadAsync` into send buffer
- Write DATA frames as window permits
- End-stream sent only when body session reaches EOF

### Replay Semantics

HTTP/2 retry rules match HTTP/1.1 replay rules:
- Replayable bodies may be resent on eligible retries
- Non-replayable bodies may not
- A stream reset after DATA frames were sent is not retryable unless the body is replayable and policy permits
- "No bytes committed" = "no DATA frames sent" (HEADERS frame may have been sent). Clearer boundary than HTTP/1.1 because HEADERS and DATA are separate frame types.

---

## Step 2: Bounded Per-Stream Receive Queue

**File:** `Runtime/Transport/Http2/Http2Stream.cs` (modified)

`Http2Stream` already receives DATA frames incrementally, but today forwards them through callbacks with no pull contract. 22a replaces that with a bounded per-stream body source.

### Queue Design

DATA frames are appended into a pooled bounded queue owned by the `Http2Stream`. The queue is the `SingleReaderChannel<T>` from 22a.1 (in `TurboHTTP.Transport`, `Runtime/Transport/Http2/SingleReaderChannel.cs`).

Properties:
- **SPSC:** producer = `ReadLoopAsync` thread, consumer = caller's async continuation thread
- **Non-blocking enqueue:** the read loop must NEVER block on a full per-stream buffer. If a DATA frame would cause the buffer to exceed capacity, the stream is reset with `RST_STREAM(FLOW_CONTROL_ERROR)`. This should be rare because per-stream backpressure (deferred WINDOW_UPDATE) prevents the server from overshooting.
- **Async dequeue:** `ReadAsync` blocks asynchronously when queue is empty, using `ManualResetValueTaskSourceCore` for zero-allocation notification
- **Error slot:** atomic error field that surfaces on the next `ReadAsync` (set via internal `IFaultableResponseBodySource.Fault` — not on the public interface)
- **Cancellation-aware:** consumer cancellation wakes the pending reader with `OperationCanceledException`

### Buffer Size and Flow Control Window Reconciliation

Initial target: `DefaultHttp2PerStreamReceiveBufferBytes = 256 KB`

**Critical invariant:** the per-stream buffer capacity MUST be >= the initial stream-level receive window advertised in SETTINGS. If the buffer is smaller than the window, the server can legally send more DATA than the buffer can hold, forcing the read loop to reset the stream.

With the default RFC 9113 initial window of 65,535 bytes, the 256 KB buffer provides comfortable headroom.

**Recommendation:** advertise a larger initial window size in SETTINGS (e.g., 256 KB to match the buffer) to reduce WINDOW_UPDATE frame overhead.

---

## Step 3: Decoupled Flow Control (WINDOW_UPDATE Model)

**File:** `Runtime/Transport/Http2/Http2Connection.cs` (modified)

**This is the single most important correctness decision for HTTP/2 streaming.**

### Connection-Level WINDOW_UPDATE

Sent as part of DATA frame processing using the **existing half-window threshold batching** (send WINDOW_UPDATE when window drops below half the initial value). "Immediately on receipt" means "within the same DATA frame processing iteration using the existing threshold logic," NOT "one WINDOW_UPDATE per DATA frame" — that would create significant frame overhead for small DATA frames.

This prevents a slow consumer on one stream from starving all other streams on the connection.

### Per-Stream WINDOW_UPDATE

Sent **when bytes are consumed by the reader** (deferred). This provides true per-stream backpressure — the server cannot overshoot the per-stream buffer.

Without this decoupled model, one slow consumer blocks DATA delivery for all multiplexed streams.

### Aggregate Memory Bound

**Critical constraint:** Under this decoupled model, the connection-level window is replenished before data is consumed. The total unconsumed data across all streams can reach `N_active_streams * per_stream_buffer_capacity`. With 100 concurrent streams at 256 KB each, this is 25 MB.

**Mitigation:** Add `MaxConnectionBufferedBytes` to `StreamingOptions` (default: e.g., 8 MB). When the sum of unconsumed bytes across all active streams exceeds this limit, **defer connection-level WINDOW_UPDATE** until consumption catches up. This caps aggregate memory independently of stream count.

Additionally, `SETTINGS_MAX_CONCURRENT_STREAMS` must be part of the memory calculation. Document the relationship: worst-case memory = `min(MaxConnectionBufferedBytes, max_concurrent_streams * per_stream_buffer)`. The `StreamingOptions` XML docs must include guidance on this.

### `SETTINGS_INITIAL_WINDOW_SIZE` Mid-Connection Changes

Per RFC 9113 Section 6.9.2, when a `SETTINGS` frame changes `SETTINGS_INITIAL_WINDOW_SIZE`, the per-stream receive window for all active streams must be adjusted by the difference. The decoupled WINDOW_UPDATE model (deferred per-stream updates) must be compatible with mid-connection `SETTINGS_INITIAL_WINDOW_SIZE` changes — the deferred accounting must adjust accordingly.

---

## Step 4: `Http2ResponseBodySource`

**File:** `Runtime/Transport/Http2/Http2ResponseBodySource.cs` (new)

Implements `IResponseBodySource`, wrapping the per-stream bounded queue.

### `ReadAsync`

- Pulls from the bounded queue
- Returns 0 on END_STREAM
- Sends per-stream WINDOW_UPDATE after consuming bytes
- Throws `UHttpException` on transport errors (from error slot)

### Zero-Body Responses

When HEADERS arrives with END_STREAM set (e.g., status 200 with no body), `Http2ResponseBodySource` is created in a **pre-completed state**:
- `ReadAsync` returns 0 immediately
- `GetTrailersAsync` returns `HttpHeaders.Empty` immediately. A later trailing HEADERS frame is not legal once the initial response HEADERS has already carried END_STREAM

### `Length`

- Returns Content-Length value from response headers if present
- Returns `null` otherwise

### `GetTrailersAsync`

- Blocks until END_STREAM or error
- Returns trailers from trailing HEADERS frame
- Returns `HttpHeaders.Empty` if no trailers. The zero-body pre-completed path above always resolves empty

---

## Step 5: Post-RST_STREAM DATA Frame Handling

**File:** `Runtime/Transport/Http2/Http2Connection.cs` (modified, continued)

After sending `RST_STREAM`, the peer may still send DATA frames that were in-flight. Per RFC 9113 Section 5.1, the endpoint must be prepared to receive frames for a short period.

The read loop handles post-RST DATA frames by:

1. **Decrementing the connection-level receive window** (mandatory — these bytes count against the connection window)
2. **Sending connection-level WINDOW_UPDATE if needed** (to keep the connection flowing for other streams)
3. **NOT delivering bytes to the body source** (the stream is dead)
4. **Suppressing redundant RST_STREAM** for recently-reset streams (optimization — sending a second RST_STREAM is wasteful but harmless)

---

## Step 6: Abort / Early-Dispose Protocol

**File:** `Runtime/Transport/Http2/Http2Stream.cs` (modified, continued)

When the consumer calls `DisposeAsync()` on the streaming response:

1. **Set aborted flag atomically** — `Volatile.Write(ref _aborted, 1)`. This must happen first.
2. **Wake pending reader** — if `ReadAsync` is blocked waiting for data, it is woken and throws `ObjectDisposedException`.
3. **Read loop discards** — on the next `AppendResponseData` call, the read loop checks the aborted flag. If set, it discards the DATA payload and does NOT enqueue. Prevents writing to released pool buffers.
4. **Release queued buffers** — all pooled segments in the bounded queue are returned to the pool.
5. **Write RST_STREAM(CANCEL)** — queued under `_writeLock`. Between setting the aborted flag and writing RST_STREAM, the server may send additional DATA frames — handled by step 3.
6. **Release stream resources** — the `Http2Stream` is returned to the pool.

**Race condition resolution:** the aborted flag is the single source of truth. Both the reader (consumer thread) and the producer (read loop thread) check it atomically before accessing the queue. The read loop never blocks — it either enqueues successfully or discards on abort.

---

## Step 7: Stall Detection

**File:** `Runtime/Transport/Http2/Http2Stream.cs` (modified, continued)

If a consumer doesn't read for a configurable timeout, the stream is reset with `RST_STREAM(CANCEL)` to prevent connection-level degradation.

### Timer Mechanism

**Do NOT allocate a per-stream `Timer` object.** Instead, use a coarse-grained check in the HTTP/2 `ReadLoopAsync`:
- Each `Http2Stream` tracks `_lastConsumptionTick` (updated on each successful `ReadAsync` return)
- The read loop periodically scans active streams (e.g., every 5 seconds) for stalled consumers
- Streams where `Environment.TickCount64 - _lastConsumptionTick > StallTimeoutMs` trigger the abort protocol (Step 6)
- This avoids per-stream timer allocation overhead at scale

### Default Timeout

`StreamingOptions.Http2StallTimeoutSeconds` = **60 seconds** (default). This must be generous enough to accommodate:
- Legitimate pauses (e.g., consumer writing to disk between reads)
- Decompression batching: when `DecompressionBodySource` wraps the HTTP/2 source, `GZipStream` may internally buffer and not issue inner reads for extended periods. The stall timer applies to the raw HTTP/2 body source reads, not application-level reads.
- Mobile game scenarios with 20+ concurrent HTTP/2 streams for asset downloads at varying rates

The timeout is configurable for workloads that need tighter detection.

---

## Step 8: Streaming Trailers Completion

**File:** `Runtime/Transport/Http2/Http2ResponseBodySource.cs` (continued)

Trailers are delivered via a trailing HEADERS frame with END_STREAM. The body source captures trailers and makes them available via `GetTrailersAsync`:

- If trailers arrive before consumer calls `GetTrailersAsync`, return immediately
- If consumer calls before trailers arrive, block asynchronously using `ManualResetValueTaskSourceCore`
- If no trailers (END_STREAM on DATA frame), return `HttpHeaders.Empty`

---

## Planned File Impact

| File | Change |
|------|--------|
| `Runtime/Transport/Http2/Http2Connection.Send.cs` | Incremental `RequestBodyReadSession` reads, buffered fast path |
| `Runtime/Transport/Http2/Http2Connection.cs` | Decoupled WINDOW_UPDATE, post-RST DATA handling, read loop changes |
| `Runtime/Transport/Http2/Http2Stream.cs` | Bounded queue, abort protocol, zero-body pre-completion, stall detection |
| `Runtime/Transport/Http2/Http2ResponseBodySource.cs` | **New:** bounded queue consumer implementing `IResponseBodySource` |

---

## Completion Criteria

- One slow HTTP/2 consumer does not force unbounded per-stream buffering
- One slow consumer does not block DATA delivery for other streams (connection-level window stays open)
- **Aggregate memory across all streams bounded by `MaxConnectionBufferedBytes`**
- Flow control remains correct under concurrent streams
- **Decoupled WINDOW_UPDATE model is compatible with mid-connection `SETTINGS_INITIAL_WINDOW_SIZE` changes**
- Read loop never blocks on per-stream buffer operations
- Zero-body responses (`HEADERS+END_STREAM`) produce pre-completed body source where `ReadAsync` returns 0 immediately; `GetTrailersAsync` returns `HttpHeaders.Empty`
- Post-RST_STREAM DATA frames are handled correctly (connection window accounting without delivery)
- Abort protocol is race-free (atomic aborted flag as single source of truth)
- **Stall detection uses coarse-grained read loop scan, not per-stream `Timer` objects**

## Post-Step Review

Both specialist agents must review before proceeding to 22a.4:
- `unity-infrastructure-architect`
- `unity-network-architect`
