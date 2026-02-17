# Phase 11: Unity Integration — 2026-02-16

## What Was Implemented

Phase 11 runtime functionality was implemented in the `TurboHTTP.Unity` module:

1. Main-thread dispatch infrastructure for Unity API access from background threads.
2. Texture and sprite conversion helpers for image responses.
3. Audio clip conversion helpers with temp-file decode interop and cleanup policy.
4. Unity-specific convenience extensions for path-safe file downloads and client defaults.
5. Coroutine wrappers for request send + JSON flows.

A dedicated Unity runtime test suite was also added for dispatcher behavior, texture handling, audio failure paths, Unity helper path safety, and coroutine wrapper callback semantics.

## Files Created / Modified

### Runtime

- `Runtime/Unity/MainThreadDispatcher.cs`
  - Singleton dispatcher with queue + `TaskCompletionSource` completion.
  - Captured main-thread managed thread ID and `IsMainThread()` helper.
  - Lifecycle state tracking (`Uninitialized`, `Initializing`, `Ready`, `Disposing`).
  - Editor domain-reload/play-mode shutdown hooks and deterministic pending-work failure.

- `Runtime/Unity/Texture2DHandler.cs`
  - `TextureOptions` (readability, mipmaps, linear, format, content-type validation, max-bytes guard).
  - `AsTexture2D`, `GetTextureAsync`, `AsSprite`, `GetSpriteAsync`.
  - Main-thread decode enforcement and response-body copy before decode.

- `Runtime/Unity/AudioClipHandler.cs`
  - `AudioClipType` enum (`WAV`, `MP3`, `OGG`, `AIFF`).
  - `AsAudioClipAsync`, `GetAudioClipAsync`.
  - Temp-file decode pipeline with GUID filenames in dedicated directory.
  - `try/finally` temp cleanup + startup fallback cleanup for orphaned artifacts.
  - Main-thread coroutine decode execution and cancellation propagation.

- `Runtime/Unity/UnityExtensions.cs`
  - `DownloadToPersistentDataAsync`, `DownloadToTempCacheAsync`.
  - Path canonicalization + root-boundary validation with traversal rejection.
  - Directory creation and cancellation-aware file writes.
  - `CreateUnityClient` with Unity-oriented default `User-Agent`.

- `Runtime/Unity/CoroutineWrapper.cs`
  - Coroutine wrappers: `SendCoroutine`, `GetJsonCoroutine<T>`.
  - Root exception unwrapping for faulted tasks.
  - Cancellation-safe callback suppression and optional owner-lifecycle callback guard.
  - JSON wrapper kept optional via reflection (no Unity module compile-time dependency on `TurboHTTP.JSON`).

### Tests

- `Tests/Runtime/Unity/MainThreadDispatcherTests.cs`
  - Worker-thread dispatch to main thread, exception propagation, `IsMainThread()` behavior.

- `Tests/Runtime/Unity/Texture2DHandlerTests.cs`
  - Valid PNG decode, content-type validation, opt-out path, max-byte guard, sprite creation.

- `Tests/Runtime/Unity/AudioClipHandlerTests.cs`
  - Empty-body and unsupported-format deterministic failure cases.

- `Tests/Runtime/Unity/UnityExtensionsTests.cs`
  - Path-safe persistent data download and traversal rejection.
  - Default/override `User-Agent` behavior for `CreateUnityClient`.

- `Tests/Runtime/Unity/CoroutineWrapperTests.cs`
  - Success callback path, error callback root exception path, cancellation callback suppression, typed JSON callback path.

### Project Metadata

- `CLAUDE.md`
  - Development status updated to mark Phase 11 complete and reference this journal entry.

## Decisions and Trade-offs

1. **Module boundary preserved:** `TurboHTTP.Unity` continues to reference only `TurboHTTP.Core`; JSON coroutine support uses reflection to avoid cross-module compile-time coupling.
2. **Compatibility-first decode path:** Texture decode remains synchronous (`Texture2D.LoadImage`) on main thread, consistent with Phase 11 baseline and deferred advanced scheduling work in Phase 15.
3. **Deterministic lifecycle failure:** Dispatcher rejects/enqueues work based on explicit lifecycle state and fails pending work during shutdown/reload windows instead of allowing silent hangs.
4. **Temp-file hygiene:** Audio decode temp artifacts use collision-resistant names and best-effort deletion with startup fallback cleanup for crash/reload residue.
5. **Path safety in helpers:** Unity file-download helpers canonicalize and enforce root-bound paths before writing to disk.
6. **IL2CPP stripping guard for JSON coroutine wrapper:** Added `Runtime/JSON/link.xml` to preserve `TurboHTTP.JSON.JsonExtensions` because Unity coroutine JSON helper resolves it via reflection.

## Validation Notes

Implementation added targeted Unity runtime tests for Phase 11 behaviors. Unity Test Runner execution was not run in this environment.
