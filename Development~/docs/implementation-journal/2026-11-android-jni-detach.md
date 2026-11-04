# 2026-11: Android JNI calls on one attached thread (#328)

## What

Android 6 (API 23) ART aborts the process when a native thread that called `AttachCurrentThread`
exits without `DetachCurrentThread` (`F art: thread.cc:1223] Native thread exited without calling
DetachCurrentThread`, journal `2026-11-android-emulator-matrix.md`). Two paths attached the calling
thread and never detached it:

- `AndroidCleartextTrafficPolicy.QueryPlatform` (the default product path: every cleartext request
  asks it from the transport thread, once per host);
- `AndroidBackgroundPluginJni` (opt-in, `TURBOHTTP_BACKGROUND_NETWORKING`), on every plugin call.

Both now run their JNI work on `AndroidJniThread`: one long-lived background thread that attaches
once, runs each work item in its own local-reference frame (`PushLocalFrame` / `ExceptionClear` /
`PopLocalFrame`) and detaches in a `finally` when it stops. Thread-pool callers block until their
item has run and are never attached. The Unity main thread (`MainThreadDispatcher.IsMainThread`)
runs the work inline, as before, and is never detached.

## Files

- `Runtime/Unity/Mobile/Android/AndroidJniThread.cs` (new): `AndroidJniThread`, the
  `IAndroidJniAttachment` seam and `UnityAndroidJni` (Unity `AndroidJNI` in players; outside Android
  attaching fails).
- `Runtime/Unity/Mobile/Android/AndroidCleartextTrafficPolicy.cs`: no attach; `Resolve(host, cache,
  jni, query)` is the testable core; the cache is re-checked on the JNI thread, so concurrent misses
  for one host ask Java once; a full cache (64 hosts) is dropped instead of no longer caching. Outside
  Android players the answer is `true` without touching the cache.
- `Runtime/Unity/Mobile/Android/AndroidBackgroundPluginJni.cs`: attach calls removed; `Create`
  wraps the JNI plugin in the new `AndroidBackgroundPluginOnJniThread` decorator, which compiles on
  every platform so the marshalling is tested.
- `Runtime/Unity/Mobile/Android/AndroidBackgroundWorkBridge.cs`: remarks only.
- `Tests/Runtime/Mobile/AndroidJniThreadTests.cs` (new, 9 tests, fake JNI).

## Decisions

- **Marshal, do not detach per call.** Unity's `AndroidJNI` has no `GetEnv`, so a caller cannot know
  whether its thread was attached before it. Detaching after each call would also detach threads
  someone else attached: a Java thread calling into C# (an `AndroidJavaProxy` callback that starts a
  request) or a thread another plugin keeps attached. One owned thread never touches a thread it did
  not attach. The main thread runs inline because Unity keeps it attached for the process; before
  the dispatcher has captured its identity it is simply marshalled, which is still correct.
- **Synchronous hand-off.** `CleartextTrafficPolicy.PlatformPolicy` and `IAndroidBackgroundPlugin`
  are synchronous, so `Invoke` blocks the caller on a `Monitor` until the item completes (a blocking
  wait on another thread's work, not on a `Task`). The JNI work never waits on the main thread or on
  another `Invoke`, so there is no cycle. Callers are serialized on the JNI thread; the calls are
  short (one static Java call each) and the cleartext one is cached.
- **Local frames per item.** A native-attached thread that never returns to Java keeps every local
  reference until it detaches, so each item gets its own frame.
- **Attach failure** fails the queued items with `InvalidOperationException` (cleartext: "not
  permitted", fail closed; background bridge: falls back as for any plugin failure) and the next
  call starts a new thread.
- **The JNI thread never exits in a player.** It is a background thread parked on a monitor. `Dispose`
  exists for tests (drains, detaches, joins).

## Deferred

- None filed. The background plugin JNI body runs only in players built with
  `TURBOHTTP_BACKGROUND_NETWORKING`; the device and emulator players below are built without it, so
  the decorator test with a fake JNI is the evidence for that path. #266 keeps its other device rows.

## How verified

- `'./Tools~/ci/check.sh' --full`: OK (the android shadow variant compiles the `UNITY_ANDROID`
  bodies, with `TURBOHTTP_BACKGROUND_NETWORKING`).
- Unity 2021.3.45f2 PlayMode `--filter 'AndroidJniThreadTests|AndroidBackgroundExpiryTests|BackgroundBridgeNativeTests|CleartextTrafficPolicyTests'`:
  52 passed, 0 failed.
- Unity EditMode `--filter 'BackgroundPluginPackagingTests'`: 14 passed, 0 failed.
- **API 23 emulator, full PlayMode suite, once** (`run-emulator-tests.sh --api 23 --jobs 1 --stripping High`,
  x86_64, IL2CPP, Unity 2021.3.45f2, commit 70cd691): the suite **completed**: 2509 tests, 2367 passed,
  133 failed, 9 skipped, 87 minutes, and the player wrote its result file. Before this change the
  same emulator aborted about a minute into the suite (`Native thread exited without calling
  DetachCurrentThread`, signal 11). A logcat check late in the run found no such line. All lane
  fixtures passed on the emulator: `AndroidJniThreadTests` 9/9, `CleartextTrafficPolicyTests` 17/17,
  `RawSocketTransportCleartextTests` 6/6, `WebSocketCleartextPolicyTests` 6/6,
  `AndroidBackgroundExpiryTests` 11/11, `BackgroundBridgeNativeTests` 15/15,
  `RawSocketTransportConnectionReuseTests` 10/11. The failures are outside this lane: HTTP/2, TLS
  negative suite, BouncyCastle and cancellation classification (#320 lists
  `BackgroundNetworkingTests.ClientSendAsync_PreservesQueuedBackgroundException`), and
  `RawSocketTransportConnectionReuseTests.StaleConnection_BodilessPost_ResetAfterRequestWritten_NotReplayed`
  (`Expected NetworkError but was Unknown`, an error-classification result, not a cleartext or JNI
  one). The run's skip gate also reports 9 skips against a baseline of 0 (#319).
- **Phone (OnePlus 7, Android 12), filtered run**: no results. The player never started, because
  the phone was under a Google Family Link lock (`com.google.android.gms/.kids.LockscreenActivityV2`
  resumed, `com.turbohttp.devicetests` suspended by `com.google.android.gms`, logcat "Attempted Lock
  Task Mode violation ... SuspendedAppActivity"). The lock was left alone; the run is still open.

## Not run

- Full EditMode/PlayMode suites in the editor (host editor, periodic).
- A player built with `TURBOHTTP_BACKGROUND_NETWORKING` on a device.
- iOS (not affected: no JNI).
- The filtered phone run (blocked by the phone's parental lock, above).
