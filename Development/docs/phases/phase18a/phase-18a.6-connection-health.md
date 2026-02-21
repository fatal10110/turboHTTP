# Phase 18a.6: Connection Health & Diagnostics

**Depends on:** Phase 18, 18a.3
**Assembly:** `TurboHTTP.WebSocket`
**Files:** 1 new, 2 modified
**Estimated Effort:** 2-3 days

---

## Motivation

Game networking and real-time applications need more than binary "connected/disconnected" — they need latency measurement, bandwidth estimation, and connection quality scoring to make adaptive decisions (e.g., reduce update frequency on degraded connections).

---

## Step 1: Implement Health Monitor

**File:** `Runtime/WebSocket/WebSocketHealthMonitor.cs` (new)

Required behavior:

1. **Latency measurement — event-driven, not polling.**
   - Add internal `OnPongReceived(TimeSpan rtt)` callback on `WebSocketConnection` (fired when a pong is received, with the RTT computed from the matching ping send timestamp). The health monitor **subscribes** to this event — no separate polling timer.
   - Maintain a rolling window of RTT samples (last 10 measurements) in a lock-protected circular buffer.
   - Expose `CurrentRtt` (TimeSpan — latest sample), `AverageRtt` (TimeSpan — mean of window), `RttJitter` (TimeSpan — standard deviation of window).
2. **Bandwidth estimation:** using metrics from 18a.3 (`BytesSent`, `BytesReceived`, `ConnectionUptime`), compute `RecentThroughput` (bytes/second) over a sliding window.
3. **Connection quality scoring:** composite score (0.0-1.0) based on:
   - RTT relative to baseline (mean of first 3 measurements): weight 0.6.
   - Pong loss rate (pings sent vs pongs received from metrics): weight 0.4.
   - ~~Message delivery success rate~~ — **removed** (TCP guarantees delivery; meaningless for TCP-based WebSocket).
4. **Quality change event:** `OnQualityChanged(ConnectionQuality)` fires when quality transitions between bands:
   - `Excellent` (score ≥ 0.9), `Good` (≥ 0.7), `Fair` (≥ 0.5), `Poor` (≥ 0.3), `Critical` (< 0.3)
5. **Baseline establishment:** first 3 RTT samples establish baseline. Quality is `Unknown` before baseline.

Implementation constraints:

1. **Thread safety:** use `lock` on the circular buffer (10 entries, small critical section, infrequent access). Do NOT use lock-free structures — complexity not warranted.
2. Health monitoring is opt-in via `WebSocketConnectionOptions.EnableHealthMonitoring` (default false).

---

## Step 2: Expose on Client API

**Files:** `Runtime/WebSocket/IWebSocketClient.cs` (modify), `Runtime/WebSocket/WebSocketClient.cs` (modify)

Required behavior:

1. Add `Health` property returning `WebSocketHealthSnapshot` (RTT, quality, throughput).
2. Add `OnConnectionQualityChanged` event (fires on network thread — Unity consumers must marshal via `MainThreadDispatcher`).

---

## Verification Criteria

1. RTT measurement from pong receipt event (event-driven, not polling).
2. Rolling window statistics (mean, jitter) over 10 samples.
3. Quality scoring transitions between bands with explicit threshold values.
4. Baseline establishment from first 3 samples; quality is `Unknown` before baseline.
5. Quality change event fires on transition only, not on every sample.
6. Scoring uses RTT (weight 0.6) and pong loss rate (weight 0.4) only.
