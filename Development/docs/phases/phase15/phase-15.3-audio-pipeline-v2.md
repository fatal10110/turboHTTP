# Phase 15.3: Audio Pipeline V2 (Temp-File Manager + Concurrency Safety)

**Depends on:** Phase 15.1
**Assembly:** `TurboHTTP.Unity`, `TurboHTTP.Tests.Runtime`
**Files:** 2 new, 1 modified

## Current State

`AudioClipHandler.cs` already provides GUID-based temp-file naming (`GetTempFilePath`), startup sweep (`EnsureStartupCleanup`), retry-based deletion (`TryDeleteTempFile`), and a dedicated temp directory under `Application.temporaryCachePath`. Phase 15.3 extracts these into a dedicated `UnityTempFileManager` and hardens the lifecycle with sharded I/O, bounded concurrency, and structured diagnostics.

---

## Step 1: Centralize Temp-File Lifecycle Management

**Files:**
- `Runtime/Unity/UnityTempFileManager.cs` (new)
- `Runtime/Unity/AudioClipHandler.cs` (modify)

Required behavior:

1. Move all temp-file operations into `UnityTempFileManager` rooted at `Application.temporaryCachePath/TurboHTTP/`.
2. Use collision-resistant naming (`GUID + extension`) and metadata tracking.
3. Add startup sweep for orphaned files from interrupted prior sessions.
4. Add retryable async deletion queue for non-fatal cleanup failures.
5. Emit structured diagnostics for cleanup failures without failing successful request completion.
6. Reduce temp-directory contention under load using sharded subdirectories and bounded file I/O workers.

Implementation constraints:

1. Temp-file registry operations must be thread-safe under concurrent decode flows.
2. Cleanup retries must be bounded and backoff-controlled.
3. Directory creation and sweeps must tolerate permissions and path errors deterministically.
4. Cleanup logic must be idempotent across repeated startup invocations.
5. High-concurrency file writes/deletes should be coordinated with explicit I/O concurrency limits to prevent directory thrash.

---

## Step 2: Add Audio Decode Concurrency and Routing Policy

**Files:**
- `Runtime/Unity/AudioClipHandler.cs` (modify)
- `Runtime/Unity/UnityTempFileManager.cs` (modify)

Required behavior:

1. Add policy for short/long clips (decompress-on-load vs streaming mode).
2. Bound concurrent decode operations and active temp-file count.
3. Add threshold-based routing for optional threaded decode of large clips.
4. Decode compressed assets to PCM on worker threads when decoder support exists.
5. Keep Unity `AudioClip` creation/finalization on main thread.
6. Fallback deterministically to baseline decode path when threaded decoder is unavailable/disabled.
7. Expose temp-file manager contention metrics (queue depth, retry counts, cleanup lag) for diagnostics.

Implementation constraints:

1. No Unity API usage from worker decode threads.
2. Cancellation during decode must clean up temp artifacts and pending registry entries.
3. High-concurrency mode must avoid filename collisions and directory thrash.
4. Streaming-mode decisions must be explicit and testable from policy.

---

## Step 3: Add Audio Pipeline V2 Tests

**File:** `Tests/Runtime/Unity/AudioPipelineV2Tests.cs` (new)

Required behavior:

1. Validate zero filename collisions under high-concurrency decode floods.
2. Validate cancellation and forced-failure paths leave zero orphaned artifacts after cleanup cycle.
3. Validate startup sweep removes stale files from interrupted sessions.
4. Validate streaming-mode policy reduces peak memory for large clips.
5. Validate threaded path fallback behavior and deterministic error reporting.
6. Validate temp-directory contention behavior under parallel decode/delete churn stays within configured I/O limits.

---

## Verification Criteria

1. Temp-file lifecycle remains bounded, deterministic, and leak-free under stress.
2. Audio decode throughput is improved without unbounded I/O or memory growth.
3. Large clip conversion no longer causes avoidable main-thread stalls on supported platforms.
4. Fallback decode path remains fully functional across unsupported platforms.
5. Cleanup diagnostics are actionable without causing request-level false failures.
