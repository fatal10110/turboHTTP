# Phase 15.2: Texture Pipeline V2 (Scheduling + Memory Guards)

**Depends on:** Phase 15.1
**Assembly:** `TurboHTTP.Unity`, `TurboHTTP.Tests.Runtime`
**Files:** 2 new, 1 modified

---

## Step 1: Introduce Decode Scheduler and Policy Guards

**Files:**
- `Runtime/Unity/TextureDecodeScheduler.cs` (new)
- `Runtime/Unity/Texture2DHandler.cs` (modify)

Required behavior:

1. Preserve existing synchronous baseline decode path (`Texture2D.LoadImage`) as compatibility fallback.
2. Add decode scheduling with per-frame budget so bursts are spread across frames.
3. Introduce policy limits:
   - `MaxSourceBytes`
   - `MaxPixels`
   - `MaxConcurrentDecodes`
4. Perform pre-decode validation using `Content-Length` (when present) and runtime byte-length checks.
5. Reject oversized payloads before decode starts.
6. Emit decode duration and queue latency into request timeline diagnostics.
7. Subscribe to `Application.lowMemory` to aggressively trim queued decode work and return pooled buffers.

Implementation constraints:

1. Scheduler queue must be bounded and cancellation-aware.
2. Main-thread texture creation/upload remains mandatory.
3. No hidden global cache of decoded texture payloads in this phase.
4. Preserve existing handler option defaults unless policy explicitly overrides.
5. Keep decode scheduling opt-in defaults conservative for mobile targets.
6. Use explicit buffer ownership and pooling (`ArrayPool<byte>` or equivalent) for large intermediate decode buffers to reduce LOH churn.

---

## Step 2: Add Optional Threaded Large-Asset Decode Path

**Files:**
- `Runtime/Unity/Texture2DHandler.cs` (modify)
- `Runtime/Unity/TextureDecodeScheduler.cs` (new)

Required behavior:

1. Add threshold-based routing (`ThreadedDecodeMinBytes`, `ThreadedDecodeMinPixels`).
2. For routed assets, decode compressed bytes to raw RGBA on worker threads through decoder abstraction.
3. Keep Unity object creation and `LoadRawTextureData`/`Apply` on main thread.
4. If decoder is unavailable, unsupported, or policy-disabled, fallback deterministically to baseline path.
5. Keep optional experimental async decode path feature-gated by Unity version/capability checks.
6. Add optional decoder warmup/pre-initialization API so first-use JIT/init cost can be paid during controlled startup.

Implementation constraints:

1. Threaded path must never call Unity APIs off main thread.
2. Worker decode concurrency must be bounded by policy and platform profile.
3. Fallback and error surfaces must include format, size, and policy context.
4. Large-payload routing must be deterministic for equivalent inputs.
5. Warmup must be opt-in and non-blocking for gameplay-critical startup paths.

---

## Step 3: Add Texture Pipeline V2 Tests

**File:** `Tests/Runtime/Unity/TexturePipelineV2Tests.cs` (new)

Required behavior:

1. Validate burst smoothing across frames under scheduler budget.
2. Validate oversized payload rejection before decode.
3. Validate bounded concurrency and queue growth under flood conditions.
4. Validate threaded path fallback when decoder/plugin is missing.
5. Compare worst-frame stall between baseline sync path and threaded path for large images.
6. Validate low-memory callback path trims queue/pool usage without corrupting in-flight decode state.
7. Validate warmup path reduces first large-decode spike versus cold-start baseline.

---

## Verification Criteria

1. Large texture bursts no longer decode entirely in a single frame by default.
2. Memory guard policy prevents unbounded queued decode pressure.
3. Baseline path remains correct and deterministic when threaded path is unavailable.
4. Threaded decode improves worst-frame stalls for large assets on supported targets.
5. Diagnostics expose scheduler queue depth and decode timing for tuning.
6. Memory-pressure handling and buffer pooling reduce peak allocation spikes under burst workloads.
