# Phase 11.3: AudioClip Handler

**Depends on:** Phase 11.1
**Assembly:** `TurboHTTP.Unity`
**Files:** 1 new

---

## Step 1: Implement AudioClip Conversion APIs

**File:** `Runtime/Unity/AudioClipHandler.cs`

Required behavior:

1. Provide `AsAudioClipAsync` conversion from response bytes.
2. Provide `GetAudioClipAsync` for download + conversion flow.
3. Support explicit audio format mapping (`WAV`, `MP3`, `OGG`, `AIFF`).
4. Name resulting clips for easier debugging and scene wiring.

Implementation constraints:

1. If temporary files are used for decode interop, use explicit `try/finally` cleanup so deletion runs on success, cancellation, and exception paths.
2. All Unity decode operations must execute on main thread.
3. Cancellation and decode failures must not leak files or request resources.
4. Unknown audio format mappings must fail clearly (no silent fallback).
5. Document platform format caveats and expected behavior for unsupported combinations.
6. Add startup fallback cleanup for orphaned temp files in case of crash/abrupt termination.
7. Temp-file deletion failures must be handled as non-fatal and retried by fallback cleanup policy.
8. Generate collision-resistant temp filenames (for example GUID-based names in a dedicated TurboHTTP temp subdirectory) so parallel decode flows cannot overwrite each other.
9. Integration tests use `[UnityTest]` attribute to properly simulate runtime frame behavior.

---

## Verification Criteria

1. Supported audio formats load into non-null `AudioClip` objects.
2. Temporary decode artifacts are deleted after success and failure.
3. Main-thread-only APIs are never called from worker threads.
4. Invalid/corrupt audio content produces deterministic failures.
5. Cancellation during decode and exception during decode both leave zero temporary files.
6. Startup fallback cleanup removes stale temporary audio artifacts from previous failed sessions.
7. High-concurrency decode requests use unique temp files with zero filename collisions.
