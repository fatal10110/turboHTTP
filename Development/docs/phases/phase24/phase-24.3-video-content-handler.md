# Phase 24.3: Video Content Handler

**Depends on:** Phase 15
**Estimated Effort:** 1 week

---

## Step 0: Define Dual-Mode API Contract

Required behavior:

1. Define download mode API (`GetVideoFileAsync`) that returns a local file path.
2. Define URL passthrough mode API (`PrepareVideoUrl`) that configures `VideoPlayer` directly.
3. Define shared options: max size limits, auth header handling, cancellation/progress, lifecycle ownership.

Implementation constraints:

1. Two modes must have clear, non-overlapping responsibilities.
2. API names and behavior should align with existing handler conventions.
3. Mode selection should be explicit at call site.

---

## Step 1: Implement Download Mode

Required behavior:

1. Download video payload to a temp file managed by `UnityTempFileManager`.
2. Enforce max download guard (default 1 GB).
3. Return a local URL/path suitable for `VideoPlayer.url`.
4. Provide cancellation and optional progress reporting.

Implementation constraints:

1. File writing must be streaming/chunked for large media payloads.
2. Temp-file ownership and cleanup rules must be explicit.
3. Failure path must avoid orphaned partial files.

---

## Step 2: Implement URL Passthrough Mode

Required behavior:

1. Configure `VideoPlayer` for remote URL playback.
2. Apply authentication headers where platform APIs support it.
3. Optionally validate URL reachability before playback handoff.

Implementation constraints:

1. Handler should not duplicate streaming logic already owned by `VideoPlayer`.
2. Unsupported per-platform header injection behavior must be documented and surfaced clearly.
3. Validation step should be optional/configurable to avoid extra startup latency.

---

## Step 3: Platform Behavior and Error Mapping

Required behavior:

1. Document platform codec/format variability (iOS, Android, WebGL).
2. Provide recommended mode usage by scenario (streaming vs offline/authenticated playback).
3. Define deterministic error behavior for unsupported formats/platform combinations.

Implementation constraints:

1. Keep behavior predictable even when codec support varies by device.
2. Error messages should include actionable fallback guidance.

---

## Verification Criteria

1. Download mode returns valid local files usable by `VideoPlayer`.
2. URL passthrough mode configures playback correctly on supported platforms.
3. Auth header propagation behaves as documented per platform.
4. Cancellation and max-size guard behavior is correct.
5. Integration tests cover success, cancellation, invalid URL, and cleanup.
