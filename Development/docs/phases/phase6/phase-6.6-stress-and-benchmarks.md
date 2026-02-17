# Phase 6.6: Stress Tests and Performance Gates

**Depends on:** Phase 6.2, 6.3, 6.4, 6.5
**Assembly:** `TurboHTTP.Tests.Runtime`
**Files:** 1 new

---

## Step 1: Add `StressTests`

**File:** `Tests/Runtime/Performance/StressTests.cs`

Coverage:

1. High-concurrency load (10,000 request scenario with mock transport).
2. Concurrency middleware enforcement under queueing pressure.
3. Byte-array pool allocation reduction checks.

---

## Step 2: Define Performance Gates

Required targets:

1. Throughput: 1000+ requests/sec on mock transport baseline.
2. GC: under 10KB/request primary gate in Phase 6; stretch goal under 1KB after hot-path rewrites.
3. Latency overhead: under 1ms/request middleware + pipeline overhead.
4. No leaks in sustained multi-minute run with explicit measurement method.

Leak-check method (required):

1. Warm-up run (100 requests).
2. Force full GC (`GC.Collect` + `GC.WaitForPendingFinalizers` + `GC.Collect`).
3. Capture baseline memory.
4. Execute long run (>= 10,000 requests).
5. Force full GC again and capture final memory.
6. Compare delta against documented threshold (default < 1MB growth).

Platform note:

1. Editor/Mono leak-check numbers are authoritative for allocation trend gating.
2. IL2CPP leak checks are treated as supportive signals due to different GC/finalizer behavior.
3. Use Unity Profiler allocation capture for authoritative per-request allocation metrics.

---

## Step 3: Hot Path Follow-up Scope

Track and verify:

1. HTTP/1.1 parser line-read path (buffered line reader migration).
2. HPACK encoder/decoder and frame codec hotspots.
3. Timeline-event allocation profile after pooling.
4. Pool-cap tuning and eviction policy behavior under burst loads.

---

## Verification Criteria

1. Stress tests pass reliably on CI-capable runtime test environment.
2. Performance gates are documented and reproducible.
3. Any unmet target is captured as explicit deferred work with owner/phase.
4. Leak-check procedure (iteration count + GC sampling points) is documented in test comments.
5. Benchmark environment is documented (Unity version/backend/build configuration).
