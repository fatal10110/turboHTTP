#!/usr/bin/env bash
# Runs TurboHTTP's PlayMode suite ON A CONNECTED ANDROID DEVICE (#99): the Unity Test Runner builds an
# IL2CPP player of a throwaway test project that consumes a synced copy of this package, deploys it over
# adb and collects the results through the player connection, or, when that connection never comes up,
# from the NUnit XML the player writes on the device (pulled over adb).
#
#   UNITY_PATH=/mnt/e/Unity/Hub/2021.3.45f2/Editor/Unity.exe \
#     Tools~/unity/run-device-tests.sh [--stripping Low|High] [--arch arm64|armv7] [--filter <regex>] [--category <name>] [--integration]
#
# One device must be attached (adb devices). Defaults: Stripping High, arm64. The player settings come from
# Assets/Editor/TurboHttpDeviceTestBuild.cs (written into the test project): IL2CPP, the stripping level,
# the target architecture and INTERNET permission; TURBOHTTP_BOUNCYCASTLE is set for the Android group in
# ProjectSettings.asset. Env: UNITY_DEVICE_TEST_PROJECT (default <cache>/device-testproj),
# UNITY_DEVICE_PACKAGE_COPY (default <cache>/device-package): never run-tests.sh's project, which must not
# get the device-only files; TURBOHTTP_DEVICE_TEST_WAIT_MIN (default 120) bounds the device-side wait.
# Exit codes as run-tests.sh: 0 pass, 1 failures, 2 setup error, 3 Unity unavailable, 4 skip gate.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
platform=PlayMode; filter=""; category=""; integration=0; stripping=High; arch=arm64
while [ $# -gt 0 ]; do
  case "$1" in
    --stripping) stripping="$2"; shift 2 ;;
    --arch) arch="$2"; shift 2 ;;
    --filter) filter="$2"; shift 2 ;;
    --category) category="$2"; shift 2 ;;
    --integration) integration=1; shift ;;
    *) echo "usage: $0 [--stripping Low|High] [--arch arm64|armv7] [--filter regex] [--category name] [--integration]" >&2; exit 2 ;;
  esac
done
case "$stripping" in Low|High) ;; *) echo "run-device-tests: --stripping Low|High" >&2; exit 2 ;; esac
case "$arch" in arm64|armv7) ;; *) echo "run-device-tests: --arch arm64|armv7" >&2; exit 2 ;; esac
if [ "$category" = ExternalNetwork ]; then integration=1; fi

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
  # set -e must not turn a failed probe into exit 1 ("test failures"): every failure here is exit 2.
  # WSL interop occasionally fails to start cmd.exe; try a few times before giving up.
  localappdata=""
  for _ in 1 2 3; do
    localappdata="$( (cd /mnt/c && cmd.exe /c 'echo %LOCALAPPDATA%') 2>/dev/null | tr -d '\r' || true)"
    [ -n "$localappdata" ] && break
    sleep 1
  done
  wincache=""
  if [ -n "$localappdata" ]; then wincache="$(wslpath -u "$localappdata" 2>/dev/null || true)"; fi
  [ -n "$wincache" ] || { echo "run-device-tests: cannot resolve %LOCALAPPDATA% from Windows (cmd.exe, wslpath)" >&2; exit 2; }
  cache="$wincache/turbohttp"
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
adbr() { local out; for _ in 1 2 3 4 5; do if out="$("$adb_exe" "$@" 2>/dev/null)"; then printf '%s' "$out" | tr -d '\r'; return 0; fi; sleep 2; done; return 1; }
[ "$(adbr get-state || true)" = device ] || { echo "run-device-tests: no Android device ready on adb ($adb_exe devices)" >&2; exit 2; }

rsync -a --delete --exclude='.git/' --exclude='.build/' --exclude='.tools/' --exclude='Library/' \
  --exclude='Temp/' --exclude='Logs/' --exclude='.DS_Store' --exclude='.claude/' --exclude='Tools~/' \
  "$repo/" "$pkg/"

if [ ! -d "$proj/ProjectSettings" ]; then
  "$unity" -batchmode -nographics -quit -createProject "$(topath "$proj")" -logFile "$(topath "$cache/create.log")"
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
        var arch = Environment.GetEnvironmentVariable("TURBOHTTP_DEVICE_ARCH") == "armv7"
            ? AndroidArchitecture.ARMv7 : AndroidArchitecture.ARM64;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Android, stripping);
        PlayerSettings.Android.targetArchitectures = arch;
        PlayerSettings.Android.forceInternetPermission = true;
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.turbohttp.devicetests");
        Debug.Log("TURBOHTTP_DEVICE_BUILD stripping=" + stripping + " arch=" + arch);
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

ts="$(date +%Y%m%d-%H%M%S)"
xml="$cache/results-Android-$stripping-$arch-$ts.xml"; log="$cache/unity-Android-$stripping-$arch-$ts.log"
export TURBOHTTP_DEVICE_STRIPPING="$stripping" TURBOHTTP_DEVICE_ARCH="$arch"
# A Windows Unity.exe started from WSL sees only the variables listed in WSLENV.
export WSLENV="${WSLENV:+$WSLENV:}TURBOHTTP_DEVICE_STRIPPING:TURBOHTTP_DEVICE_ARCH"
args=(-batchmode -projectPath "$(topath "$proj")" -runTests -testPlatform Android -buildTarget Android
      -testResults "$(topath "$xml")" -logFile "$(topath "$log")")
[ -n "$filter" ] && args+=(-testFilter "$filter")
[ -n "$category" ] && args+=(-testCategory "$category")

pkg_id=com.turbohttp.devicetests
device_file=/sdcard/Android/data/$pkg_id/files/turbohttp-results.xml
pull_results() { if [ "$(adbr shell "[ -s $device_file ] && echo y || true")" = y ]; then adbr exec-out cat "$device_file" > "$xml" || true; fi; }
adbr shell rm -f "$device_file" >/dev/null || true
adbr logcat -c >/dev/null || true

set +e
"$unity" "${args[@]}"
code=$?
set -e

# The player keeps running when the editor gives up on the player connection: wait for the device-side
# marker and pull the XML it wrote. The wait ends early when the player process is gone (never installed
# after a compile or build error, or crashed); TURBOHTTP_DEVICE_TEST_WAIT_MIN (default 120 minutes, the
# full suite took 70 on a OnePlus 7) only bounds a player that hangs.
if [ ! -f "$xml" ]; then
  echo "run-device-tests: no results over the player connection; waiting for the device-side result file"
  deadline=$(( $(date +%s) + ${TURBOHTTP_DEVICE_TEST_WAIT_MIN:-120} * 60 ))
  while [ "$(date +%s)" -lt "$deadline" ]; do
    if adbr logcat -d -s Unity:V | grep -q "TURBOHTTP_DEVICE_RUN finished"; then
      pull_results
      break
    fi
    # Only an adb call that succeeded with no pid means the player is gone; a failed call, or a device
    # without pidof, keeps waiting until the deadline.
    if pid="$(adbr shell "if command -v pidof >/dev/null; then pidof $pkg_id || true; else echo unknown; fi")" \
      && [ -z "$pid" ]; then
      echo "run-device-tests: the player ($pkg_id) is not running"
      pull_results
      break
    fi
    sleep 20
  done
  adbr logcat -d -s Unity:V | grep -E "TURBOHTTP_DEVICE_(RUN|FAIL)" | sed 's/^/  device: /' | head -60 || true
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
python3 "$repo/Tools~/unity/summarize_results.py" "$xml" "$platform" "$is_filtered" "$repo/Tools~/unity/skip-baseline.txt"
gate=$?
set -e
echo "run-device-tests: results $xml, log $log"
if [ "$code" != 0 ]; then exit 1; fi
if [ "$gate" != 0 ]; then exit "$gate"; fi
exit 0
