# 2026-11 mobile-background (#89 #106 #176 #87 #88, part of #181)

Wave run w1101, lane mobile-background. Background networking becomes one experimental, opt-in
feature whose native plugins build cleanly, stay out of projects that do not opt in, and never wait
on the main-thread dispatcher.

## What

- **Opt-in define `TURBOHTTP_BACKGROUND_NETWORKING` (#106).** Same shape as `TURBOHTTP_BOUNCYCASTLE` (#196).
  - Managed native paths compile only under `UNITY_IOS|UNITY_ANDROID && !UNITY_EDITOR && TURBOHTTP_BACKGROUND_NETWORKING`
    (`IosBackgroundTaskBindings`, `AndroidBackgroundPluginJni`, `BackgroundExecutionBridgeFactory`). Without it the
    default bridge is `null` and `UseBackgroundNetworking()` runs with foreground semantics.
  - `Editor/TurboHttpBackgroundPluginGate.cs` installs `PluginImporter.SetIncludeInBuildDelegate` on the
    `.androidlib` and the iOS `TurboHttpBackgroundTaskBridge.mm` ([InitializeOnLoad] and again in
    `IPreprocessBuildWithReport`): they are built in only when the active target's Player Settings define the symbol.
  - Host finding: Unity 2021.3.45f2 ignores an importer's `defineConstraints` for native plugins (an Android export
    without the define still contained the .androidlib). The metas keep the constraint as the declarative marker the
    `native` repo check reads; the delegate does the gating.
  - `shadow_build.py` defines the symbol so the ios/android variants compile and analyze the native paths.
- **Android packaging (#89).** `Plugins/Android/src/**` (Kotlin) and the ignored package-local `mainTemplate.gradle` are
  gone. `Plugins/Android/TurboHttpBackground.androidlib/` holds `AndroidManifest.xml` (package only), `build.gradle`
  (`com.android.library`, compile/min SDK taken from the parent `unityLibrary`, `androidx.work:work-runtime:2.7.1`,
  consumer ProGuard keep rules) and the plugin rewritten in Java (no Kotlin Gradle plugin needed). The `.java` metas are
  PluginImporters disabled on every platform: Unity copies the .androidlib folder wholesale, and must not also treat
  them as loose Java sources. `queryDeferredWorkStatus` no longer blocks on a WorkManager future; the status map only
  tracks keys this process enqueued and drops them on cancel.
- **No dispatcher on the begin/end path (#88, #181 Android criterion).**
  - iOS: begin/end call UIKit directly (both documented thread-safe). The managed side allocates a cookie and registers
    the scope's CTS before calling begin; the native table maps cookie -> task id, and whichever of end/expiration
    removes the entry ends the task (exactly once; an expiry that beats the table insert is recorded and begin then
    ends the task and reports failure). Begin marks its cookie pending before the UIKit call, and only a pending
    cookie can be recorded as expired-before-begin, so an expiry that races a completed end leaves no entry behind
    (review r1). No lock is held across a UIKit call.
  - Android: `IAndroidBackgroundPlugin` over JNI (`AndroidBackgroundPluginJni`), called on the calling thread after
    `AndroidJNI.AttachCurrentThread()`. Init is a lock-guarded one-time JNI call; failure leaves foreground semantics.
  - The three RS0030 baseline lines (+ comment), both CS0169 lines and both CS1998 lines for the two bridges are gone.
- **iOS expiry (#87).** No polling loop and no 25 s fallback budget: a scope expires only when UIKit's expiration
  handler runs (main thread, app in background), through a `[MonoPInvokeCallback]` registered lazily under a lock
  before the first begin. `IosBackgroundTaskBindings.ToRemainingBudget`: DBL_MAX/inf/NaN/negative/> 1e6 s ->
  `TimeSpan.MaxValue` (fail open); native returns -1 for "unlimited". `RemainingBudget` is `Zero` once expired, so
  the interceptor never calls `backgroundTimeRemaining` (still main-thread only) on the request path.
  `IosBackgroundTaskBridge(TimeSpan? fallbackBudget)` became a parameterless constructor (CHANGELOG Breaking).
- **tvOS (#176).** tvOS disabled on `TurboHttpBackgroundTaskBridge.mm.meta` and `TurboHttpSecTrustVerifier.mm.meta`
  (same defect: callers compile for `UNITY_IOS` only). New `check_repo.py native`: each `DllImport("__Internal")`
  must resolve to an `extern "C"` under `Plugins/`, and per platform (iPhone/tvOS/Android) x custom define the
  importer includes the plugin exactly when the `#if` guard compiles its callers. It fails on origin/main (both
  tvOS findings) and passes here.
- `Runtime/Unity/AssemblyInfo.cs`: IVT to `TurboHTTP.Tests.Runtime` (fakes for the native layers).
- Docs: PlatformNotes (iOS/Android background sections, opt-in steps, tvOS not supported), APIReference,
  Troubleshooting, `UseBackgroundNetworking` remarks, CHANGELOG (Fixed, Breaking).

## Tests

- `Tests/Runtime/Mobile/BackgroundBridgeNativeTests.cs`: DBL_MAX/inf/NaN/negative/1e7 -> unlimited, exact budgets;
  iOS and Android begin/end/enqueue complete synchronously on a plain worker thread (no dispatcher); foreground scope
  never expires; native expiration cancels, queues the in-flight request, ends the task once; begin/plugin failures
  fall back to foreground and release the dedupe key.
- `Tests/Editor/BackgroundPluginPackagingTests.cs`: .androidlib declares `androidx.work:work-runtime`, no loose
  sources/templates, importer platforms (no tvOS), gate covers both plugins and requires the exact define,
  `PluginImporter.GetImporters(Android)` contains the .androidlib only when the active target defines it, bridge sources never call
  `MainThreadDispatcher.Execute*` or `GetAwaiter().GetResult()`.
- `check_repo.py native` (runs in check.sh/CI).

## Verified (host, Windows Unity 2021.3.45f2, AGP 7.4.2 / Gradle 7.5.1)

Probe project with the package as a `file:` dependency and a scene calling `UseBackgroundNetworking()`:

| Build | Result |
| --- | --- |
| Android Gradle export, define, before the gate | .androidlib included as `unityLibrary:TurboHttpBackground.androidlib`; `gradle.properties` already has `android.useAndroidX=true` |
| Android Gradle export, no define, before the gate | .androidlib STILL included (importer `defineConstraints` ignored for native plugins), managed JNI code correctly absent |
| Android Gradle export, no define, with the gate | no .androidlib in `settings.gradle` / `unityLibrary/build.gradle` |
| Android APK, IL2CPP ARM64, define | Succeeded; merged manifest has WorkManager `InitializationProvider`, `SystemJobService`, `SystemAlarmService`, receivers; dex has `com.turbohttp.background.*` |
| Android APK, Mono, no define | Succeeded; no WorkManager manifest entries, no plugin classes |
| Android APK, Mono, define, `minifyRelease` (R8) | Succeeded; plugin classes and JNI method names kept (consumer rules) |
| iOS Xcode export (IL2CPP), define | Succeeded; `TurboHttpBackgroundTaskBridge.mm` in the Xcode project; the four `turbohttp_*` P/Invokes and the reverse P/Invoke wrapper for `OnExpired` generated |
| iOS Xcode export (IL2CPP), no define | Succeeded; bridge .mm not in the project (SecTrust .mm still is), no `turbohttp_*background*` P/Invokes |

Scoped Unity runs: EditMode `BackgroundPluginPackagingTests|WebGlBuildGuardTests` 21/21; PlayMode
`BackgroundBridgeNativeTests|BackgroundNetworkingTests|MobileNetworkingOptionsExtensionsTests|MobileBackgroundNetworkingSmokeTests|CoreSurfaceTests` 57/57.
`check.sh --full` passes. `check_repo.py native` fails on origin/main (both tvOS findings) and passes here.

## Not run / deferred

- Device behavior (begin/end with the player loop paused, foreground request > 25 s, the iOS expiration handler
  ending the task once, JNI from pool threads including an attached pool thread that exits without Detach, class
  lookup off the main thread, Xcode compile of the .mm): `needs-device-validation`, follow-up #266 (v1.0 GA).
  The Android build line stays in matrix #99.
- Android scopes have no expiry signal, so the WorkManager deferred-work path is unreachable on Android (same as
  before this PR); PlatformNotes now says so. Follow-up #267 (v1.1).
- Xcode compile/link of the .mm (no macOS host); tvOS build (module not installed).
- CI Android/iOS player build job: #100 (blocked by the Unity license, #188).
- #181 other sync-over-async sites (UHttpStreamingResponse, dispatch bridges, Http2Connection.Lifecycle,
  PollSelectStream, SaeaStream): not this lane; #181 stays open.
