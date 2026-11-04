# 2026-11 Android device run: player smoke and PlayMode suite on a phone (#99)

## What
- New `Tools~/unity/run-device-tests.sh`: runs the PlayMode suite as an IL2CPP player on the one Android
  device on adb. Options: `--stripping Low|High` (default High), `--arch arm64|armv7`, `--filter`,
  `--category`, `--integration`. It writes into its throwaway test project (`device-testproj`, never the
  package):
  - an Editor `ITestPlayerBuildModifier` that sets the player settings (IL2CPP, stripping level,
    architecture, INTERNET permission, application id);
  - a `TestRunCallback` that writes the NUnit result XML to `Application.persistentDataPath` and logs
    `TURBOHTTP_DEVICE_RUN` / `TURBOHTTP_DEVICE_FAIL` markers. When the Editor never receives the results
    over the player connection, the script pulls that file over adb;
  - a test-only `.androidlib` with `usesCleartextTraffic`, and `TMPDIR` pointed at the app cache.
- testing.md: step 4 of the host-editor gates documents the device run.

## Decisions
- **Device-side result file.** The Editor never connected to the player on this LAN: the player tries
  `127.0.0.1:34998`, the LAN IP and the WSL vEthernet IP, and the Editor timed out after 600 s every time,
  even with a Private-profile firewall rule. The tests ran on the device each time, so the script
  collects the XML from the device instead of depending on the connection. The wait ends when the
  `finished` marker appears or the player process is gone (compile/build error, crash), and is capped at
  `TURBOHTTP_DEVICE_TEST_WAIT_MIN` (default 120 min: the full suite took 70). adb comes from the editor's
  Android SDK (macOS layout as in `build-player.sh`) or PATH; no adb or no ready device exits 2 up front.
- **Own project variables.** `UNITY_DEVICE_TEST_PROJECT` / `UNITY_DEVICE_PACKAGE_COPY`, so a lane's
  exported `UNITY_TEST_PROJECT` never receives the device-only files (the `TMPDIR` hook and result callback
  also run in Editor PlayMode).
- **Changes stay in the test project.** Cleartext and `TMPDIR` are test-player settings, not package
  changes. The product's mobile cleartext default (false) is correct; the tests that rely on the Editor
  default are #321.

## Results (OnePlus 7 GM1903, Android 12, arm64-v8a, Unity 2021.3.45f2, IL2CPP High, main 466935d)
- `build-player.sh --target android-il2cpp`, plain and `--bouncycastle --background`: both APKs install,
  launch and log `TURBOHTTP_SMOKE PASS` (`bc=absent` / `bc=present`). #1 and #8 (partial), #89 (ARM64
  launch), #258 AC2: recorded on #99.
- Full PlayMode suite on the device: 2484 total, 2266 passed, 209 failed, 9 skipped (4169 s). Triage:
  #321 (test readiness, ~120), #322 (loopback TLS/TCP fixtures cannot connect, ~50), #320 (IL2CPP
  cancellation/replay classification, P1, ~30), #323 (HTTP/2 flow-control SETTINGS ACK timeouts,
  allocation gates), #317.

## Not run
- Stripping Low, ARMv7, the ExternalNetwork category on the device, and the network rows (Wi-Fi/LTE
  switch, airplane mode, suspend/resume): after #320-#323.
- iOS, a second Android device, NAT64.
