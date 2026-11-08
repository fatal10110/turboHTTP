# 2026-11 Android player smoke run (#325) and the iOS script on the WSL helper (#355)

Tooling only: no Runtime/ change.

## What

- **#325, `build-player.sh --target android-il2cpp --run`.** The Android player smoke was built, never
  run: the APK had to be installed and launched by hand and `TURBOHTTP_SMOKE` read from logcat. With
  `--run` the script checks that a device is on adb before the build (`--serial <id>` picks one of
  several, an emulator too), builds for `--arch arm64|x86_64` (default: the device's ABI, so an emulator
  serial gets an x86_64 APK; arm64 without `--run`), then `adb install -r`, `am force-stop`,
  `input keyevent KEYCODE_WAKEUP`, `wm dismiss-keyguard`, fails with exit 2 when the lock screen still
  shows (a OnePlus freezes an app behind it), clears logcat, `am start -W -n
  com.turbohttp.playersmoke/com.unity3d.player.UnityPlayerActivity` and polls `logcat -d -s Unity:V
  AndroidRuntime:E DEBUG:V` for the aggregate marker (`TURBOHTTP_SMOKE_WAIT_S`, default 300 s; the wait
  ends early when the player process is gone on two checks in a row). Logcat is kept as `logcat.txt` in
  the build folder. Exit 1 on a FAIL marker or no marker, 2 for no device, a lock screen or a failed
  install/launch, 5 for a WSL interop outage. Every adb call is bounded (`timeout 120`) and retried like
  `run-device-tests.sh`'s `adbr` (5 tries; from WSL an outage uses the `wsl-interop.sh` back-off).
  `--apk <file>` runs an APK built earlier without starting Unity.
- **Smoke rows.** `TurboHttpPlayerSmoke` is now a MonoBehaviour added at `AfterSceneLoad`; it runs the
  rows of the new `TurboHttpSmokeRows.cs` in a coroutine, logs one
  `TURBOHTTP_SMOKE ROW <id> PASS|FAIL|SKIP <issue> <detail>` line per row and then the aggregate
  `TURBOHTTP_SMOKE PASS ...` (unchanged fields plus `rows=`/`skipped=`) or `TURBOHTTP_SMOKE FAIL rows=<ids>`,
  and quits with 0/1. Rows: `registration` (#1, the old check), `reflection-targets` (#8),
  `ws-json` (#192), `oauth-refresh` (#67), `json-coroutine-class` and `json-coroutine-struct` (#95), and
  `tls-sslstream` / `tls-bouncycastle` (#141, #258 AC2). The HTTP rows use an in-process
  `IHttpTransport` (no socket, host under `.invalid`), so the default run stays offline and deterministic.
  With `--network` the TLS rows send one GET per provider to `https://example.com/` with default
  certificate validation (any HTTP status passes, a rejected certificate fails); the flag reaches the
  player as the `turbohttp_smoke_network=1` intent extra (Android) or `-turbohttpSmokeNetwork`
  (Windows), so one APK serves both runs. The script then requires the PASS row of every provider in
  the build. `check_smoke_log` is shared by the Windows and Android paths.
- **Editor hook.** `TurboHttpPlayerBuild` takes `-smokeArch`, forces the INTERNET permission (the
  network rows open sockets from Assembly-CSharp) and gives x86_64 builds GLES3/GLES2 as
  `run-device-tests.sh` does for emulators.
- **#355 remainder.** `build-ios-test-player.sh` sources `wsl-interop.sh`: `%LOCALAPPDATA%` goes
  through `wsl_resolve_localappdata` (was 3 x 1 s), and both Unity starts (`-createProject` and the
  build) through `wsl_unity_launch` (was one attempt, then "player build failed ... see <log>" for a log
  that did not exist). Exit 5 is in its header; `wsl-interop.sh` and testing.md no longer list it as
  the exception.

## Files

- `Tools~/unity/build-player.sh`, `Tools~/unity/build-ios-test-player.sh`, `Tools~/unity/wsl-interop.sh`
- `Tools~/unity/player-smoke/TurboHttpPlayerSmoke.cs`, `TurboHttpSmokeRows.cs` (new),
  `Editor/TurboHttpPlayerBuild.cs`
- `Tools~/agent-tools/test_android_player_run.py` (new), `Tools~/agent-tools/test_wsl_interop.py`
- `Development~/docs/agents/testing.md` (Host-editor gates: command list, items 3 and 4, WSL paragraph)

## Decisions

- **#8 "full reflection-target list"**: the issue names no source. The row checks every `<type>` of the
  link.xml files whose assembly is in every smoke build (Core, JSON, Transport, Unity, WebSocket,
  WebSocket.Transport; BouncyCastle's in a `--bouncycastle` build): each must resolve by name and keep
  members. Testing and Observability are not referenced by the smoke, so their rules never reach
  UnityLinker. The names are read from an array, not passed as literals, so UnityLinker cannot keep a
  type because of the smoke's own `Type.GetType` call. `test_android_player_run.py` fails when a
  link.xml rule is missing from the list or a new module's link.xml is not classified.
- **#192**: `WebSocketMessage` has no public constructor, so user code cannot call
  `JsonWebSocketSerializer<T>.Deserialize`. The row serializes through `JsonWebSocketSerializer<T>` and
  reads back with the call its `Deserialize` makes (the registered serializer for `typeof(T)`). The
  default serializer TurboHTTP.Complete installs is JsonUtility, which documents dictionary fields as
  not serialized: the DTO's nested list is asserted and its dictionary field is reported
  (`dictionary-field=skipped`); a `Dictionary<string, object>` payload with a nested list and dictionary
  is asserted.
- **Offline HTTP rows** use an in-process transport in Assembly-CSharp: TurboHTTP.Testing is not
  auto-referenced, and a loopback cleartext server would depend on the Android cleartext policy.
- **Arch**: `--arch`, defaulting to the device ABI under `--run` (the brief's open question 1).

## Verification

- Stub tests: `python3 -m unittest discover -s 'Tools~/agent-tools' -p 'test_android_player_run.py'`
  (16: PASS/FAIL/no marker/process gone, outage then recovery, long outage = exit 5, `--serial` on every
  adb call, lock screen incl. the API 33 field, no device, `--network` extra and TLS rows,
  `--bouncycastle`, usage errors, device-ABI build; 2 link.xml drift tests) and
  `test_wsl_interop.py` (iOS lookup outage, short outage at lookup and Unity start, Unity never starts).
  The run tests fail on the old script (no `--apk`/`--run`: usage exit 2).
- shellcheck over every tracked `*.sh`: clean. `./Tools~/ci/check.sh --full`: OK.
- The smoke C# compiled against the Android shadow-build DLLs before the Unity build.
- **Emulator, 2026-11-08** (host WSL2 + Windows Unity 2021.3.45f2, image
  `sdk_phone64_x86_64` Android 13 `TE1A.220922.034`, x86_64 IL2CPP, Managed Stripping Level High,
  `--serial emulator-5590`):
  - `--run` (no BC, offline): exit 0; `device ABI x86_64: building --arch x86_64`.
  - `--apk <that APK> --network`: exit 0.
  - `--bouncycastle --run --network`: exit 0.

  ```
  TURBOHTTP_SMOKE ROW registration PASS #1 http=TurboHTTP.Transport.RawSocketTransport ws=TurboHTTP.WebSocket.Transport.RawSocketWebSocketTransport json=ok bc=present alpn=true
  TURBOHTTP_SMOKE ROW reflection-targets PASS #8 types=33 (link.xml of Core, JSON, Transport, Unity, WebSocket, WebSocket.Transport, BouncyCastle)
  TURBOHTTP_SMOKE ROW ws-json PASS #192 dto=ok nested-list=ok dictionary-field=skipped dictionary-payload=ok
  TURBOHTTP_SMOKE ROW oauth-refresh PASS #67 access_token=parsed refresh_token=rotated token_type=Bearer scope=read expires_in=3600
  TURBOHTTP_SMOKE ROW json-coroutine-class PASS #95 T=class id=1 tags=2
  TURBOHTTP_SMOKE ROW json-coroutine-struct PASS #95 T=struct id=2
  TURBOHTTP_SMOKE ROW tls-sslstream PASS #141 backend=SslStream host=example.com status=200
  TURBOHTTP_SMOKE ROW tls-bouncycastle PASS #258 backend=BouncyCastle host=example.com status=200
  TURBOHTTP_SMOKE PASS http=... bc=present alpn=true rows=8 skipped=0
  ```
  The no-BC builds report `types=31` and `bc=absent`; offline, both TLS rows are SKIP.

## Not run

- Phone (arm64, OnePlus) and any arm64 player: `needs-device-validation` (#99). The lock-screen exit is
  stub-tested only: the emulator showed no keyguard (`KeyguardShowing=false`).
- iOS: `build-ios-test-player.sh` was not run with Unity; only its stub tests ran.
- Windows players with the new rows (`--target win64-mono`): not rebuilt in this lane.
- Unity EditMode/PlayMode suites: nothing under `Tests/` changed (host editor, periodic).
