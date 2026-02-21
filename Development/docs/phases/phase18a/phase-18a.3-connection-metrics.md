# Phase 18a.3: Connection Metrics & Observability

**Depends on:** Phase 18
**Assembly:** `TurboHTTP.WebSocket`
**Files:** 2 new, 3 modified
**Estimated Effort:** 3-4 days

---

## Motivation

Production WebSocket deployments need visibility into connection health without external monitoring tools. Game developers need frame-level metrics for adaptive quality-of-service. The current implementation tracks `_lastActivityTimestamp` but exposes nothing to consumers.

---

## Step 1: Define Metrics Snapshot

**File:** `Runtime/WebSocket/WebSocketMetrics.cs` (new)

Required behavior:

1. Define `WebSocketMetrics` as a readonly struct:
   - `BytesSent` (long) — total bytes written to the wire (including frame overhead).
   - `BytesReceived` (long) — total bytes read from the wire.
   - `MessagesSent` (long) — total application messages sent.
   - `MessagesReceived` (long) — total application messages received.
   - `FramesSent` (long) — total frames sent (including control frames, fragments).
   - `FramesReceived` (long) — total frames received.
   - `PingsSent` (long) — keep-alive pings sent.
   - `PongsReceived` (long) — keep-alive pongs received.
   - `UncompressedBytesSent` (long) — pre-compression application payload size (sum of original message lengths before compression). 0 if compression inactive.
   - `CompressedBytesSent` (long) — bytes sent after compression. 0 if compression inactive.
   - `CompressedBytesReceived` (long) — compressed bytes received before decompression. 0 if compression inactive.
   - `CompressionRatio` (double) — computed: `CompressedBytesSent > 0 ? (double)UncompressedBytesSent / CompressedBytesSent : 1.0`.
   - `ConnectionUptime` (TimeSpan) — time since connection opened.
   - `LastActivityAge` (TimeSpan) — time since last frame was sent or received.

Implementation constraints:

1. **32-bit IL2CPP safety.** `Interlocked.Add` on `long` fields is not truly atomic on 32-bit ARM (IL2CPP). Follow the established Phase 6 `HttpMetrics` pattern: use `public long` fields for `Interlocked` operations with the documented caveat that 32-bit reads of these counters may tear. For `CompressionRatio` (double), store as `long` bits via `BitConverter.DoubleToInt64Bits` and reconvert on read. Add 32-bit Android IL2CPP to the validation matrix.
2. `GetSnapshot()` returns a frozen `WebSocketMetrics` value at a consistent point in time (single pass read of all fields).

---

## Step 2: Implement Metrics Collector

**File:** `Runtime/WebSocket/WebSocketMetricsCollector.cs` (new)

Required behavior:

1. Internal class owned by `WebSocketConnection`.
2. Increment methods called by frame reader/writer: `RecordFrameSent(int byteCount)`, `RecordFrameReceived(int byteCount)`, `RecordMessageSent()`, `RecordMessageReceived()`, `RecordCompression(int originalSize, int compressedSize)`.
3. Thread-safe via `Interlocked.Add` — no locks.
4. Exposes `GetSnapshot()` for external consumption.

---

## Step 3: Expose Metrics on Client API

**Files:** `Runtime/WebSocket/IWebSocketClient.cs` (modify), `Runtime/WebSocket/WebSocketClient.cs` (modify), `Runtime/WebSocket/WebSocketConnection.cs` (modify)

Required behavior:

1. Add `Metrics` property to `IWebSocketClient` returning `WebSocketMetrics`.
2. Wire counters into frame reader/writer call sites.
3. Optionally expose an `OnMetricsUpdated` event fired at configurable intervals (e.g., every 100 messages or every 5s). **Threading model:** event fires on the network thread (the receive loop thread or the send caller's thread). Unity consumers must marshal to main thread via `MainThreadDispatcher` — document this explicitly.

---

## Verification Criteria

1. Counter accuracy after N sends/receives.
2. Thread-safety: concurrent counter increments from send + receive threads.
3. `UncompressedBytesSent` vs `CompressedBytesSent` ratio computation.
4. `CompressionRatio` is `1.0` when compression is inactive.
5. `CompressionRatio` division-by-zero guard (`CompressedBytesSent == 0`).
6. Snapshot immutability (values don't change after creation).
7. `OnMetricsUpdated` event fires at configured interval on network thread.
