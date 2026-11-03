# 2026-11 Unity handlers and mobile: object leaks, streamed audio files, adaptive bytes, Android expiry

Lane `unity-handlers-mobile` (run w1102): #94, #96, #261, #267. All code is in `TurboHTTP.Unity`;
no asmdef reference changed.

## What

- **#94 Unity objects dropped on cancellation.** `MainThreadDispatcher` gained an internal
  `ExecuteAsync<T>(func, releaseAbandoned, ct)`: when cancellation completes the task while `func`
  runs, the result goes to `releaseAbandoned` on the main thread. The public `ExecuteAsync<T>` passes
  no release (the dispatcher cannot tell a created object from a looked-up one such as `Camera.main`).
  Texture decode, managed texture/audio creation and sprite creation pass `UnityObjects.Destroy`.
  `GetSpriteAsync` goes through `Texture2DHandler.CreateSpriteOwningTextureAsync`, which destroys the
  texture when the sprite is not delivered. `TextureDecodeScheduler` drops its cancellation
  registration once a decode starts, so a finished texture is never discarded by a late
  `TrySetCanceled`; the decode observes the token itself. The audio coroutine destroys a clip whose
  `TrySetResult` fails.
- **#96 streamed clip temp file.** `DecodeViaUnityTempPathAsync` hands a `TempFileLease` to the loading
  coroutine. For a streamed clip the coroutine claims the file and registers it with
  `StreamedAudioTempFiles`, which checks the tracked clips once a second from a coroutine on the
  dispatcher and releases the file of each destroyed clip. Otherwise the `finally` claims and releases
  it as before. Exactly one side claims (Interlocked). Also fixed while testing: the temp-file root
  read `Application.temporaryCachePath` on every reservation, which throws on the worker thread
  `GetAudioClipAsync` continues on; it is now cached before scene load.
- **#261 adaptive bytes.** `AdaptiveHandler` counts a single-segment buffered body at the head as
  before; any other body is wrapped in `CountingResponseBodySource` (Unity/Mobile), which counts
  reads, drains, detached sequences (no copy) and buffered views, and reports once at end of stream,
  detach, failure or dispose. The sample is then recorded at body completion; one sample per request
  (an error after the head records the failure sample instead). Latency stays time-to-head.
- **#267 Android expiry.** `AndroidBackgroundWorkBridge` owns the scopes' expiration token: one
  `CancellationTokenSource` per foreground episode, cancelled when an `IAndroidBackgroundExpirySignal`
  reports expiry and replaced on resume. The default `AndroidPauseExpirySignal` starts a thread-pool
  `Timer` at `OnApplicationPause(true)` (reported by `AndroidAppPauseListener`, compiled only into
  opted-in Android players) for the new `AndroidBackgroundWorkConfig.BackgroundBudgetMilliseconds`
  (default 5000) and clears on resume, so it fires while the player loop is paused (#88).
  `RemainingBudget` is zero once expired, so `BackgroundNetworkingInterceptor` queues replayable
  requests and calls `TryEnqueueDeferredWork`. `Documentation~/PlatformNotes.md` updated.

## Decisions

- #261 option (a): a small counting source in Unity instead of moving Observability's
  `ObservedResponseBodySource` into Core (no IVT to Unity, and moving it widens the diff into two
  other modules). Bytes are those the adaptive handler sees: decoded when decompression runs inside
  it, which is the canonical order (`MobileNetworkingOptionsExtensions` inserts adaptive first).
- #267: reused no existing option. `GuardGraceMilliseconds` is the Java guard's argument; the
  budget after pause is a separate knob with XML docs. The default 5 s is an app-chosen bound, to
  be tuned by the device run in #266.
- #96: polling (`clip == null` once a second) because `AudioClip` has no destroy callback. Streamed
  clips keep counting toward `MaxActiveTempFiles` while alive (documented on
  `EnableStreamingForLargeClips`).

## Files

Runtime/Unity: `MainThreadDispatcher.cs`, `Texture2DHandler.cs`, `TextureDecodeScheduler.cs`,
`AudioClipHandler.cs`, `UnityTempFileManager.cs`, new `UnityObjects.cs`, `StreamedAudioTempFiles.cs`;
Runtime/Unity/Mobile: `AdaptiveHandler.cs`, `NetworkQualityDetector.cs` (internal
`TryGetLatestSample`), new `CountingResponseBodySource.cs`; Mobile/Android:
`AndroidBackgroundWorkBridge.cs`, `AndroidBackgroundWorkConfig.cs`, new
`AndroidBackgroundExpirySignal.cs`, `AndroidAppPauseListener.cs`. Tests:
`Tests/Runtime/Unity/UnityObjectLeakOnCancelTests.cs`, `StreamedAudioTempFileTests.cs`,
`Tests/Runtime/Transport/AdaptiveByteCountTests.cs`, `Tests/Runtime/Mobile/AndroidBackgroundExpiryTests.cs`.

## Verification

- `'./Tools~/ci/check.sh'` (and `--full` before the PR): pass.
- Unity 2021.3.45f2 host editor, PlayMode, scoped filter
  `AdaptiveByteCountTests|AndroidBackgroundExpiryTests|AdaptiveInterceptorTests|BackgroundBridgeNativeTests|BackgroundNetworkingTests|UnityObjectLeakOnCancelTests|StreamedAudioTempFileTests|MainThreadDispatcherTests|AudioPipelineV2Tests|Texture2DHandlerTests|TexturePipelineV2Tests|AudioClipHandlerTests`: pass.
  With the fixes reverted (AdaptiveHandler from main, no abandoned-result release, no Android
  signal) 9 of the new tests fail.
- The streamed playback test played a 2 s clip to the end in the Windows editor; it ignores itself
  where the audio device does not advance.
- Not run: full Unity suites (periodic host-editor run), IL2CPP builds, Android/iOS devices. The
  Android pause/expiry on a device is added to #266 (`needs-device-validation`).
