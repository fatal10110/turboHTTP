#!/usr/bin/env bash
# Runs TurboHTTP's PlayMode suite ON A CONNECTED ANDROID DEVICE (#99): the Unity Test Runner builds an
# IL2CPP player of a throwaway test project that consumes a synced copy of this package, deploys it over
# adb and collects the results through the player connection, or, when that connection never comes up,
# from the NUnit XML the player writes on the device (pulled over adb).
#
#   UNITY_PATH=<Unity 2021.3 editor binary> \
#     Tools~/unity/run-device-tests.sh [--stripping Low|High] [--arch arm64|armv7|x86_64] [--serial <adb serial>]
#       [--skip-build] [--filter <regex>] [--category <name>] [--integration]
#
# Without --serial, one device must be attached (adb devices) and Unity deploys to it. With --serial (a phone
# beside an emulator, or several emulators), Unity only builds the APK and exits, and the script installs and
# launches it on that device; results always come from the device-side file then. --skip-build (with --serial)
# reinstalls the APK the last --serial build of that stripping level and architecture left, so one build
# serves several devices; pass the same --filter/--category, which are baked into the player. x86_64 is for the x86_64
# emulator images (run-emulator-tests.sh). Defaults: Stripping High, arm64. The player settings come from
# Assets/Editor/TurboHttpDeviceTestBuild.cs (written into the test project): IL2CPP, the stripping level,
# the target architecture and INTERNET permission; TURBOHTTP_BOUNCYCASTLE is set for the Android group in
# ProjectSettings.asset. Env: UNITY_DEVICE_TEST_PROJECT (default <cache>/device-testproj),
# UNITY_DEVICE_PACKAGE_COPY (default <cache>/device-package): never run-tests.sh's project, which must not
# get the device-only files; TURBOHTTP_DEVICE_TEST_WAIT_MIN (default 120) bounds the device-side wait.
# A player of an earlier run still running its tests on the device is never replaced: the script exits 2 before
# it touches the device (stop it with adb shell am force-stop com.turbohttp.devicetests). With --serial, a launch
# that starts no process (a parental-control or other lock screen holding the focus) exits 2 naming the
# focused window; a player that dies is reported about a minute after its process is gone (three complete
# process listings without it, device_poll_s apart).
# Exit codes as run-tests.sh: 0 pass, 1 failures, 2 setup error, 3 Unity unavailable, 4 skip gate (an unfiltered
# run is held to the Player-Android entry of Tools~/unity/skip-baseline.txt, #341), 5 WSL interop unavailable
# (from WSL: cmd.exe, adb.exe or Unity.exe did not start through about two minutes of retries, wsl-interop.sh, #355).
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=Tools~/unity/wsl-interop.sh
. "$repo/Tools~/unity/wsl-interop.sh"
# shellcheck source=Tools~/unity/android-adb.sh
. "$repo/Tools~/unity/android-adb.sh"
# Seconds between the device-side wait's checks and between the launch checks (the tool tests set both to 0).
device_poll_s="${device_poll_s:-20}"
launch_poll_s="${launch_poll_s:-2}"
baseline_key=Player-Android; filter=""; category=""; integration=0; stripping=High; arch=arm64; serial=""; skip_build=0
while [ $# -gt 0 ]; do
  case "$1" in
    --stripping) stripping="$2"; shift 2 ;;
    --arch) arch="$2"; shift 2 ;;
    --serial) serial="$2"; shift 2 ;;
    --skip-build) skip_build=1; shift ;;
    --filter) filter="$2"; shift 2 ;;
    --category) category="$2"; shift 2 ;;
    --integration) integration=1; shift ;;
    *) echo "usage: $0 [--stripping Low|High] [--arch arm64|armv7|x86_64] [--serial id [--skip-build]] [--filter regex] [--category name] [--integration]" >&2; exit 2 ;;
  esac
done
case "$stripping" in Low|High) ;; *) echo "run-device-tests: --stripping Low|High" >&2; exit 2 ;; esac
case "$arch" in arm64|armv7|x86_64) ;; *) echo "run-device-tests: --arch arm64|armv7|x86_64" >&2; exit 2 ;; esac
if [ "$category" = ExternalNetwork ]; then integration=1; fi
if [ "$skip_build" = 1 ] && [ -z "$serial" ]; then echo "run-device-tests: --skip-build needs --serial" >&2; exit 2; fi

unity="${UNITY_PATH:-}"
if [ -z "$unity" ] || [ ! -x "$unity" ]; then
  echo "UNITY_UNAVAILABLE: set UNITY_PATH to a Unity $(cat "$repo/Tools~/ci/unity-version") editor binary; CI never runs Unity (no license, #188); Unity gates run in a host editor"
  exit 3
fi

# A Windows Unity.exe driven from WSL needs Windows paths and a cache on a Windows drive.
win=0
case "$unity" in
  *.exe|*.EXE)
    if command -v wslpath >/dev/null 2>&1; then win=1; fi ;;
esac
topath() { if [ "$win" = 1 ]; then wslpath -m "$1"; else printf '%s\n' "$1"; fi; }

if [ "$win" = 1 ]; then
  # A failed lookup is never exit 1 ("test failures"): exit 5 for an interop outage, else 2 (wsl-interop.sh).
  wsl_resolve_localappdata run-device-tests
  cache="$wsl_localappdata/turbohttp"
else
  cache="${XDG_CACHE_HOME:-$HOME/.cache}/turbohttp"
fi
proj="${UNITY_DEVICE_TEST_PROJECT:-$cache/device-testproj}"
pkg="${UNITY_DEVICE_PACKAGE_COPY:-$cache/device-package}"
mkdir -p "$cache"

# adb from the editor's Android SDK (macOS keeps PlaybackEngines beside Unity.app, as in build-player.sh),
# else the one on PATH. A Windows adb.exe from WSL can fail to start, so adbr retries.
engines="$(dirname "$unity")/Data/PlaybackEngines"
case "$(uname -s)" in Darwin) engines="$(cd "$(dirname "$unity")/../../.." && pwd)/PlaybackEngines" ;; esac
adb_exe="$engines/AndroidPlayer/SDK/platform-tools/adb.exe"
[ -x "$adb_exe" ] || adb_exe="$engines/AndroidPlayer/SDK/platform-tools/adb"
[ -x "$adb_exe" ] || adb_exe="$(command -v adb || true)"
[ -n "$adb_exe" ] || { echo "run-device-tests: no adb (editor Android SDK under $engines, or PATH)" >&2; exit 2; }
adb_target=()
if [ -n "$serial" ]; then adb_target=(-s "$serial"); fi
# Each call is bounded (where timeout exists): a frozen device or emulator otherwise blocks adb forever.
adb_timeout=()
if command -v timeout >/dev/null 2>&1; then adb_timeout=(timeout 120); fi
# A failed call is tried 5 times, wsl_poll_s (2 s) apart, then returns 1. From WSL a failure while cmd.exe does not
# answer either is an interop outage (#355): those tries do not count and wait with the wsl-interop.sh back-off,
# and after wsl_tries of them adbr returns wsl_interop_exit (5).
adbr() {
  local out tries=0 outages=0
  while :; do
    if out="$(${adb_timeout[@]+"${adb_timeout[@]}"} "$adb_exe" ${adb_target[@]+"${adb_target[@]}"} "$@" 2>/dev/null)"; then
      printf '%s' "$out" | tr -d '\r'
      return 0
    fi
    if [ "$win" = 1 ] && ! wsl_interop_up; then
      outages=$((outages + 1))
      [ "$outages" -lt "$wsl_tries" ] || return "$wsl_interop_exit"
      wsl_backoff "$outages"
      continue
    fi
    tries=$((tries + 1))
    [ "$tries" -lt 5 ] || return 1
    sleep "$wsl_poll_s"
  done
}
adb_rc=0
adb_state="$(adbr get-state)" || adb_rc=$?
if [ "$adb_state" != device ]; then
  wsl_exit_if_interop "$adb_rc" run-device-tests "adb.exe did not start (adb get-state)"
  echo "run-device-tests: no Android device ${serial:+$serial }ready on adb ($adb_exe devices)" >&2
  exit 2
fi

# The test project and the build are skipped with --skip-build (the APK is reused).
if [ "$skip_build" = 0 ]; then
rsync -a --delete --exclude='.git/' --exclude='.build/' --exclude='.tools/' --exclude='Library/' \
  --exclude='Temp/' --exclude='Logs/' --exclude='.DS_Store' --exclude='.claude/' --exclude='Tools~/' \
  "$repo/" "$pkg/"

if [ ! -d "$proj/ProjectSettings" ]; then
  wsl_unity_launch run-device-tests "$cache/create.log" \
    "$unity" -batchmode -nographics -quit -createProject "$(topath "$proj")" -logFile "$(topath "$cache/create.log")"
  [ "$unity_code" = 0 ] || { echo "run-device-tests: creating $proj failed (Unity exit $unity_code); see $cache/create.log" >&2; exit 2; }
fi
python3 - "$proj/Packages/manifest.json" "$(topath "$pkg")" <<'PY'
import json, sys
path, pkg = sys.argv[1], sys.argv[2]
m = json.load(open(path))
m.setdefault("dependencies", {})["com.turbohttp.complete"] = "file:" + pkg
m["dependencies"].setdefault("com.unity.test-framework", "1.1.33")
m["testables"] = sorted(set(m.get("testables", [])) | {"com.turbohttp.complete"})
json.dump(m, open(path, "w"), indent=2)
PY
# BouncyCastle is opt-in (TURBOHTTP_BOUNCYCASTLE, #196); the TLS suites need it compiled, so the
# test project defines it for the Android group in its Player settings.
# TURBOHTTP_INTEGRATION_TESTS (#263) is defined only for --integration / --category ExternalNetwork runs
# and removed otherwise, so a leftover from an earlier run never compiles the internet-facing tests.
optional_define=""
if [ "$integration" = 1 ]; then optional_define=TURBOHTTP_INTEGRATION_TESTS; fi
python3 - "$proj/ProjectSettings/ProjectSettings.asset" TURBOHTTP_BOUNCYCASTLE "$optional_define" <<'PY'
import re, sys
path, always, optional = sys.argv[1], sys.argv[2], sys.argv[3]
managed = {"TURBOHTTP_BOUNCYCASTLE", "TURBOHTTP_INTEGRATION_TESTS"}
wanted = {always} | ({optional} if optional else set())
text = open(path, encoding="utf-8").read()
m = re.search(r"^  scriptingDefineSymbols:(.*)\n((?:    .*\n)*)", text, re.M)
if not m:
    print("run-device-tests: no scriptingDefineSymbols in " + path, file=sys.stderr)
    sys.exit(2)
entries = [] if m.group(1).strip() == "{}" else m.group(2).splitlines()
for i, line in enumerate(entries):
    key, _, value = line.strip().partition(":")
    if key == "Android":
        symbols = [s for s in value.strip().split(";") if s and s not in managed]
        entries[i] = "    Android: " + ";".join(symbols + sorted(wanted))
        break
else:
    entries.append("    Android: " + ";".join(sorted(wanted)))
block = "  scriptingDefineSymbols:\n" + "".join(e + "\n" for e in entries)
new = text[:m.start()] + block + text[m.end():]
if new != text:
    open(path, "w", encoding="utf-8").write(new)
PY

mkdir -p "$proj/Assets/Editor"
cat > "$proj/Assets/Editor/TurboHttpDeviceTestBuild.cs" <<'CS'
// Written by Tools~/unity/run-device-tests.sh. Device player settings for the Unity Test Runner (#99).
using System;
using System.IO;
using UnityEditor;
using UnityEditor.TestTools;
using UnityEngine;

[assembly: TestPlayerBuildModifier(typeof(TurboHttpDeviceTestBuild))]

public class TurboHttpDeviceTestBuild : ITestPlayerBuildModifier
{
    public BuildPlayerOptions ModifyOptions(BuildPlayerOptions options)
    {
        var stripping = Environment.GetEnvironmentVariable("TURBOHTTP_DEVICE_STRIPPING") == "Low"
            ? ManagedStrippingLevel.Low : ManagedStrippingLevel.High;
        var archName = Environment.GetEnvironmentVariable("TURBOHTTP_DEVICE_ARCH");
        var arch = archName == "armv7" ? AndroidArchitecture.ARMv7
            : archName == "x86_64" ? AndroidArchitecture.X86_64 : AndroidArchitecture.ARM64;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Android, stripping);
        PlayerSettings.Android.targetArchitectures = arch;
        PlayerSettings.Android.forceInternetPermission = true;
        // x86_64 is for the emulators: API 23-25 images offer only GLES 2.0 (a GLES3-only player stops at
        // Unity's "wait for debugger" dialog), and Vulkan froze the headless emulator. Phones keep the default.
        if (arch == AndroidArchitecture.X86_64)
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
                new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3, UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2 });
        }
        else
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, true);
        }
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.turbohttp.devicetests");
        Debug.Log("TURBOHTTP_DEVICE_BUILD stripping=" + stripping + " arch=" + arch);
        // --serial: build the APK only; the script installs it on the chosen device. Without AutoRunPlayer the
        // test runner never starts its player-connection timeout and a batchmode editor would wait forever,
        // so quit on the first editor update after the build (exit 2 when no APK was written).
        var apk = Environment.GetEnvironmentVariable("TURBOHTTP_DEVICE_APK");
        if (!string.IsNullOrEmpty(apk))
        {
            options.options &= ~BuildOptions.AutoRunPlayer;
            options.locationPathName = apk;
            EditorApplication.delayCall += () => EditorApplication.Exit(File.Exists(apk) ? 0 : 2);
        }
        return options;
    }
}
CS

# Test-player only: loopback test servers use http:// and ws://, which Android's default network
# security policy (targetSdk >= 28) refuses; the product follows that policy (#85), so the test
# player opts in through a library manifest. Never shipped: it lives in the throwaway test project.
mkdir -p "$proj/Assets/Plugins/Android/TurboHttpDeviceTests.androidlib"
cat > "$proj/Assets/Plugins/Android/TurboHttpDeviceTests.androidlib/AndroidManifest.xml" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="com.turbohttp.devicetests.cleartext">
    <application android:usesCleartextTraffic="true" />
</manifest>
XML
printf 'android.library=true\n' > "$proj/Assets/Plugins/Android/TurboHttpDeviceTests.androidlib/project.properties"
mkdir -p "$proj/Assets/DeviceResults"
cat > "$proj/Assets/DeviceResults/TurboHttp.DeviceResults.asmdef" <<'JSON'
{
    "name": "TurboHttp.DeviceResults",
    "references": ["UnityEngine.TestRunner"],
    "overrideReferences": true,
    "precompiledReferences": ["nunit.framework.dll"],
    "defineConstraints": ["UNITY_INCLUDE_TESTS"],
    "autoReferenced": false
}
JSON
cat > "$proj/Assets/DeviceResults/TurboHttpDeviceResults.cs" <<'CS'
// Written by Tools~/unity/run-device-tests.sh. The Unity Test Runner reports device results over the
// player connection, which a LAN or adb setup can block; this callback also writes the NUnit result XML
// on the device (Application.persistentDataPath) and logs a marker, so the script can pull it over adb.
using System;
using System.Globalization;
using System.IO;
using NUnit.Framework.Interfaces;
using UnityEngine;
using UnityEngine.TestRunner;

[assembly: TestRunCallback(typeof(TurboHttpDeviceResults))]

public class TurboHttpDeviceResults : ITestRunCallback
{
    // Path.GetTempPath() is /data/local/tmp on Android, which an app cannot write; the tests use it for
    // scratch files, so point TMPDIR at the app's cache directory before any test runs.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void UseAppCacheAsTemp()
    {
        Environment.SetEnvironmentVariable("TMPDIR", Application.temporaryCachePath);
        Debug.Log("TURBOHTTP_DEVICE_RUN tmp=" + System.IO.Path.GetTempPath());
    }

    public const string FileName = "turbohttp-results.xml";
    DateTime _start;

    public void RunStarted(ITest testsToRun)
    {
        _start = DateTime.UtcNow;
        Debug.Log("TURBOHTTP_DEVICE_RUN started tests=" + testsToRun.TestCaseCount);
    }

    public void RunFinished(ITestResult result)
    {
        var path = Path.Combine(Application.persistentDataPath, FileName);
        int total = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount;
        double duration = (DateTime.UtcNow - _start).TotalSeconds;
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<test-run id=\"2\" testcasecount=\"" + total
            + "\" result=\"" + result.ResultState.Status + "\" total=\"" + total + "\" passed=\"" + result.PassCount
            + "\" failed=\"" + result.FailCount + "\" inconclusive=\"" + result.InconclusiveCount + "\" skipped=\""
            + result.SkipCount + "\" duration=\"" + duration.ToString(CultureInfo.InvariantCulture) + "\">\n"
            + result.ToXml(true).OuterXml + "\n</test-run>\n";
        File.WriteAllText(path + ".tmp", xml);
        if (File.Exists(path)) File.Delete(path);
        File.Move(path + ".tmp", path);
        Debug.Log("TURBOHTTP_DEVICE_RUN finished total=" + total + " passed=" + result.PassCount + " failed="
            + result.FailCount + " path=" + path);
    }

    public void TestStarted(ITest test) { }

    public void TestFinished(ITestResult result)
    {
        if (!result.HasChildren && result.ResultState.Status == TestStatus.Failed)
            Debug.Log("TURBOHTTP_DEVICE_FAIL " + result.FullName);
    }
}
CS
fi

ts="$(date +%Y%m%d-%H%M%S)"
sdk="$(adbr shell getprop ro.build.version.sdk || true)"
tag="$stripping-$arch-api${sdk:-unknown}${serial:+-$(printf '%s' "$serial" | tr -c 'A-Za-z0-9._-' _)}"
xml="$cache/results-Android-$tag-$ts.xml"; log="$cache/unity-Android-$tag-$ts.log"
apk=""
if [ -n "$serial" ]; then
  apk="$cache/device-tests-$stripping-$arch.apk"
  if [ "$skip_build" = 0 ]; then rm -f "$apk"
  elif [ ! -s "$apk" ]; then echo "run-device-tests: --skip-build: no $apk from an earlier --serial build" >&2; exit 2; fi
fi
export TURBOHTTP_DEVICE_STRIPPING="$stripping" TURBOHTTP_DEVICE_ARCH="$arch" TURBOHTTP_DEVICE_APK="${apk:+$(topath "$apk")}"
# A Windows Unity.exe started from WSL sees only the variables listed in WSLENV.
export WSLENV="${WSLENV:+$WSLENV:}TURBOHTTP_DEVICE_STRIPPING:TURBOHTTP_DEVICE_ARCH:TURBOHTTP_DEVICE_APK"
args=(-batchmode -projectPath "$(topath "$proj")" -runTests -testPlatform Android -buildTarget Android
      -testResults "$(topath "$xml")" -logFile "$(topath "$log")")
[ -n "$filter" ] && args+=(-testFilter "$filter")
[ -n "$category" ] && args+=(-testCategory "$category")

pkg_id=com.turbohttp.devicetests
device_file=/sdcard/Android/data/$pkg_id/files/turbohttp-results.xml
# y while the player process exists, n on a complete answer without it, empty when unknown (android-adb.sh).
player_running() { android_process_state "$pkg_id" "$sdk"; }
# With --serial: prints y while adb devices lists the serial and n when it does not; fails when adb fails.
serial_listed() {
  local list
  list="$(${adb_timeout[@]+"${adb_timeout[@]}"} "$adb_exe" devices 2>/dev/null | tr -d '\r')" || return 1
  printf '%s\n' "$list" | awk -v s="$serial" '$1 == s { f = 1 } END { print (f ? "y" : "n") }'
}
# The player writes the result file once, when its run ends (written aside and renamed), so it marks the end.
results_on_device() { [ "$(adbr shell "[ -s $device_file ] && echo y || echo n" || true)" = y ]; }
pull_results() { if results_on_device; then adbr exec-out cat "$device_file" > "$xml" || true; fi; }

# A player of an earlier run still in progress (a queued run started too early) must not be killed by the
# install or lose its result file: refuse. One that wrote its result file has finished (a player that did not
# quit) and is replaced. An unknown answer is asked again, then the run goes ahead.
for _ in 1 2 3; do
  state="$(player_running)"
  [ -z "$state" ] || break
  sleep "$launch_poll_s"
done
if [ "$state" = y ] && results_on_device; then
  echo "run-device-tests: the player ($pkg_id) of an earlier run finished (its result file is on the device)" \
    "but still runs; replacing it"
elif [ "$state" = y ]; then
  echo "run-device-tests: the player ($pkg_id) of an earlier run is still running on ${serial:-the device};" \
    "wait for it to finish (its results: adb exec-out cat $device_file), or stop it with" \
    "adb ${serial:+-s $serial }shell am force-stop $pkg_id" >&2
  exit 2
fi
adbr shell rm -f "$device_file" >/dev/null || true
adbr logcat -c >/dev/null || true
adbr logcat -b events -c >/dev/null || true
# Emulator logcat buffers hold 256 KB, far less than a full run logs; a larger one keeps a crash readable.
adbr logcat -G 16M >/dev/null || true

code=0
if [ "$skip_build" = 0 ]; then
  # A Unity.exe that did not start (no log) is retried, then exit 5: never reported as a build error (#355).
  wsl_unity_launch run-device-tests "$log" "$unity" "${args[@]}"
  code="$unity_code"
fi

# --serial: Unity only built the APK; install it on the chosen device and launch it. The test player runs
# the suite on start and quits when it is done.
if [ -n "$serial" ]; then
  if [ "$code" != 0 ] || [ ! -s "$apk" ]; then
    echo "run-device-tests: the player build failed (Unity exit $code); see $log"
    grep -E "error CS[0-9]+" "$log" | head -20 || true
    exit 2
  fi
  adbr install -r "$(topath "$apk")" >/dev/null || {
    rc=$?; wsl_exit_if_interop "$rc" run-device-tests "adb.exe did not start (adb install)"
    echo "run-device-tests: adb install on $serial failed" >&2; exit 2
  }
  # An app behind the lock screen may be frozen (#325): wake the screen and stop on a lock screen that stays.
  adbr shell input keyevent KEYCODE_WAKEUP >/dev/null || true
  adbr shell wm dismiss-keyguard >/dev/null || true
  if android_keyguard_showing; then
    echo "run-device-tests: $serial shows its lock screen: unlock it and keep the screen on" \
      "(Developer options > Stay awake)" >&2
    exit 2
  fi
  adbr shell monkey -p "$pkg_id" -c android.intent.category.LAUNCHER 1 >/dev/null || {
    rc=$?; wsl_exit_if_interop "$rc" run-device-tests "adb.exe did not start (launching $pkg_id)"
    echo "run-device-tests: launching $pkg_id on $serial failed" >&2; exit 2
  }
  # monkey exits 0 even when nothing starts (a parental-control lock screen held the focus): confirm the
  # process, or at least Android's am_proc_start for a player that quit between two checks.
  started=n
  for _ in $(seq 1 30); do
    if [ "$(player_running)" = y ]; then started=y; break; fi
    sleep "$launch_poll_s"
  done
  if [ "$started" = n ] && ! results_on_device && ! android_proc_started "$pkg_id"; then
    focus="$(android_focused_window)"
    echo "run-device-tests: the player did not start on $serial: no $pkg_id process and no am_proc_start" \
      "after the launch (focused window: ${focus:-unknown}); a lock screen or parental control may block it" >&2
    exit 2
  fi
  echo "run-device-tests: installed and launched on $serial"
fi

# The player keeps running when the editor gives up on the player connection: wait for the device-side
# result file and pull it. The wait ends early when the player process is gone (never installed after a
# compile or build error, or crashed); TURBOHTTP_DEVICE_TEST_WAIT_MIN (default 120 minutes, the full suite
# took 70 on a OnePlus 7) only bounds a player that hangs.
if [ ! -f "$xml" ]; then
  echo "run-device-tests: no results over the player connection; waiting for the device-side result file"
  deadline=$(( $(date +%s) + ${TURBOHTTP_DEVICE_TEST_WAIT_MIN:-120} * 60 ))
  left=0; gone=0; unknown=0
  while :; do
    if [ "$(date +%s)" -ge "$deadline" ]; then
      echo "run-device-tests: no result file within ${TURBOHTTP_DEVICE_TEST_WAIT_MIN:-120} minutes; the player" \
        "may still be running (a next run refuses to replace it until it ends or is stopped)"
      break
    fi
    # A --serial device that left adb twice in a row (an emulator that was stopped or died) cannot finish
    # the run; a failed adb call is not counted.
    if [ -n "$serial" ] && [ "$(serial_listed || true)" = n ]; then
      left=$(( left + 1 ))
      if [ "$left" -ge 2 ]; then
        echo "run-device-tests: $serial is no longer on adb"
        break
      fi
      sleep "$device_poll_s"
      continue
    fi
    left=0
    if results_on_device; then break; fi
    # Only complete answers without the process mean the player is gone, three in a row; an unknown answer
    # (adb failed, empty or truncated output) neither counts nor resets, and keeps waiting.
    case "$(player_running)" in
      y) gone=0; unknown=0 ;;
      '')
        unknown=$(( unknown + 1 ))
        if [ "$unknown" = 3 ]; then
          echo "run-device-tests: adb gave no answer to the process check 3 times in a row; still waiting" \
            "(another adb client may have restarted the shared adb server)"
        fi
        ;;
      n)
        unknown=0
        gone=$(( gone + 1 ))
        if [ "$gone" -ge 3 ]; then
          echo "run-device-tests: the player ($pkg_id) is not running (gone on $gone checks in a row)"
          break
        fi
        ;;
    esac
    sleep "$device_poll_s"
  done
  # Also after the deadline: a player that just finished may have written the file since the last check.
  if [ "$left" -lt 2 ]; then pull_results; fi
  # A player that wrote its result file is done, but it does not always quit: stop it so it does not hold
  # the device (and a next run need not replace it).
  if [ -s "$xml" ]; then adbr shell am force-stop "$pkg_id" >/dev/null || true; fi
  if [ "$left" -lt 2 ]; then
    adbr logcat -d -s Unity:V | grep -E "TURBOHTTP_DEVICE_(RUN|FAIL)" | sed 's/^/  device: /' | head -60 || true
    # Keep the device log of every run, taken when it ended: the device (an emulator) may be gone afterwards.
    devlog="$cache/logcat-Android-$tag-$ts.txt"
    adbr logcat -d -b main -b system -b crash -b events > "$devlog" || true
    echo "run-device-tests: device log $devlog"
    if [ ! -s "$xml" ]; then
      grep -E "Fatal signal|FATAL EXCEPTION|DEBUG +: |lowmemorykiller|lmkd|am_proc_died|am_kill|Process $pkg_id .*died" "$devlog" | head -20 || true
    fi
  fi
  if [ -s "$xml" ]; then
    code="$(python3 -c 'import sys, xml.etree.ElementTree as ET; print(0 if ET.parse(sys.argv[1]).getroot().get("failed") == "0" else 1)' "$xml" 2>/dev/null || echo 2)"
  else
    rm -f "$xml"
  fi
fi

if [ ! -f "$xml" ]; then
  echo "run-device-tests: no results (Unity exit $code; compile or build error, player crash, or wait timeout); see $log"
  grep -E "error CS[0-9]+" "$log" | head -20 || true
  exit 2
fi
is_filtered=0
if [ -n "$filter" ] || [ -n "$category" ]; then is_filtered=1; fi
# Failures keep exit 1; the skip gates (an unexplained skip, or more skips than Tools~/unity/skip-baseline.txt
# allows for an unfiltered run, #151) exit 4 so a vanished test cannot pass silently.
set +e
# A player skips more platform-scoped [env] tests than the host editor: its own baseline key (#341).
python3 "$repo/Tools~/unity/summarize_results.py" "$xml" "$baseline_key" "$is_filtered" "$repo/Tools~/unity/skip-baseline.txt"
gate=$?
set -e
echo "run-device-tests: results $xml, log $log"
if [ "$code" != 0 ]; then exit 1; fi
if [ "$gate" != 0 ]; then exit "$gate"; fi
exit 0
