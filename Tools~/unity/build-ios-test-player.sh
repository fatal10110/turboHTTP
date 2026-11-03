#!/usr/bin/env bash
# Builds TurboHTTP's PlayMode suite as an iOS SIMULATOR test player (#99): the Unity Test Runner builds an
# IL2CPP player of a throwaway test project that consumes a synced copy of this package and exports the
# Xcode project WITHOUT running it (a Windows editor cannot), so a Mac can build and run it on a simulator.
#
#   UNITY_PATH=/mnt/e/Unity/Hub/6000.3.26f1/Editor/Unity.exe UNITY_IOS_TEST_PROJECT=<cache>/ios-testproj-6000.3 \
#     Tools~/unity/build-ios-test-player.sh [--out <dir>] [--stripping Low|High] [--filter <regex>] [--category <name>] [--integration]
#
# --out (default /mnt/c/turbo-ios/xcode) receives the Xcode project (Unity-iPhone.xcodeproj at its root)
# after a successful build; <out>/../READY is created only after the copy finishes, and stale READY, DONE
# and out/ in that folder are removed before the build. The player runs standalone (no ConnectToHost),
# logs TURBOHTTP_DEVICE_TEST per test, TURBOHTTP_DEVICE_FAIL with message and stack, TURBOHTTP_DEVICE_HANG
# for a test running over 3 minutes, rewrites Application.persistentDataPath/results.xml after every
# fixture (partial="true") and at the end, and quits. --filter '!<full test name>' excludes a test;
# separate several filters with ';'.
# Unity 2021.3 ships the simulator player as x86_64 only; Unity 6 builds the ARM64 simulator player
# (Apple Silicon runtimes such as iOS 26 are arm64-only). Env: UNITY_IOS_TEST_PROJECT (default
# <cache>/ios-testproj; use one per editor version), UNITY_IOS_PACKAGE_COPY (default <cache>/ios-package).
# Exit codes: 0 built, 2 setup or build error, 3 Unity or the iOS build module unavailable.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
out_dir=/mnt/c/turbo-ios/xcode; stripping=High; filter=""; category=""; integration=0
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out_dir="$2"; shift 2 ;;
    --stripping) stripping="$2"; shift 2 ;;
    --filter) filter="$2"; shift 2 ;;
    --category) category="$2"; shift 2 ;;
    --integration) integration=1; shift ;;
    *) echo "usage: $0 [--out dir] [--stripping Low|High] [--filter regex] [--category name] [--integration]" >&2; exit 2 ;;
  esac
done
case "$stripping" in Low|High) ;; *) echo "build-ios-test-player: --stripping Low|High" >&2; exit 2 ;; esac
if [ "$category" = ExternalNetwork ]; then integration=1; fi

unity="${UNITY_PATH:-}"
if [ -z "$unity" ] || [ ! -x "$unity" ]; then
  echo "UNITY_UNAVAILABLE: set UNITY_PATH to a Unity $(cat "$repo/Tools~/ci/unity-version") editor binary"
  exit 3
fi
if [ ! -d "$(dirname "$unity")/Data/PlaybackEngines/iOSSupport" ]; then
  echo "UNITY_UNAVAILABLE: iOS Build Support not installed for $unity"
  exit 3
fi

win=0
case "$unity" in
  *.exe|*.EXE)
    if command -v wslpath >/dev/null 2>&1; then win=1; fi ;;
esac
topath() { if [ "$win" = 1 ]; then wslpath -m "$1"; else printf '%s\n' "$1"; fi; }

if [ "$win" = 1 ]; then
  localappdata=""
  for _ in 1 2 3; do
    localappdata="$( (cd /mnt/c && cmd.exe /c 'echo %LOCALAPPDATA%') 2>/dev/null | tr -d '\r' || true)"
    [ -n "$localappdata" ] && break
    sleep 1
  done
  wincache=""
  if [ -n "$localappdata" ]; then wincache="$(wslpath -u "$localappdata" 2>/dev/null || true)"; fi
  [ -n "$wincache" ] || { echo "build-ios-test-player: cannot resolve %LOCALAPPDATA% from Windows (cmd.exe, wslpath)" >&2; exit 2; }
  cache="$wincache/turbohttp"
else
  cache="${XDG_CACHE_HOME:-$HOME/.cache}/turbohttp"
fi
# Own variables, as run-device-tests.sh: run-tests.sh's UNITY_TEST_PROJECT must never receive the player-only
# results callback and build modifier written below.
proj="${UNITY_IOS_TEST_PROJECT:-$cache/ios-testproj}"
pkg="${UNITY_IOS_PACKAGE_COPY:-$cache/ios-package}"
stage="$cache/ios-build/xcode"
mkdir -p "$cache"

rsync -a --delete --exclude='.git/' --exclude='.build/' --exclude='.tools/' --exclude='Library/' \
  --exclude='Temp/' --exclude='Logs/' --exclude='.DS_Store' --exclude='.claude/' --exclude='Tools~/' \
  "$repo/" "$pkg/"

if [ ! -d "$proj/ProjectSettings" ]; then
  "$unity" -batchmode -nographics -quit -createProject "$(topath "$proj")" -logFile "$(topath "$cache/ios-create.log")"
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
# TURBOHTTP_BOUNCYCASTLE for the iOS group ("iPhone" in ProjectSettings) so the TLS suites compile;
# TURBOHTTP_INTEGRATION_TESTS only for --integration runs (#263).
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
    print("build-ios-test-player: no scriptingDefineSymbols in " + path, file=sys.stderr)
    sys.exit(2)
entries = [] if m.group(1).strip() == "{}" else m.group(2).splitlines()
for i, line in enumerate(entries):
    key, _, value = line.strip().partition(":")
    if key == "iPhone":
        symbols = [s for s in value.strip().split(";") if s and s not in managed]
        entries[i] = "    iPhone: " + ";".join(symbols + sorted(wanted))
        break
else:
    entries.append("    iPhone: " + ";".join(sorted(wanted)))
block = "  scriptingDefineSymbols:\n" + "".join(e + "\n" for e in entries)
new = text[:m.start()] + block + text[m.end():]
if new != text:
    open(path, "w", encoding="utf-8").write(new)
PY

mkdir -p "$proj/Assets/Editor"
cat > "$proj/Assets/Editor/TurboHttpIosTestBuild.cs" <<'CS'
// Written by Tools~/unity/build-ios-test-player.sh. Simulator test-player settings for the Unity Test
// Runner (#99): export the Xcode project to TURBOHTTP_IOS_OUT without running it, then exit the editor.
using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.TestTools;
using UnityEngine;
using UnityEngine.TestTools;

[assembly: TestPlayerBuildModifier(typeof(TurboHttpIosTestBuild))]
[assembly: PostBuildCleanup(typeof(TurboHttpIosTestBuild))]

public class TurboHttpIosTestBuild : ITestPlayerBuildModifier, IPostBuildCleanup
{
    static bool s_Built;

    public BuildPlayerOptions ModifyOptions(BuildPlayerOptions options)
    {
        var stripping = Environment.GetEnvironmentVariable("TURBOHTTP_DEVICE_STRIPPING") == "Low"
            ? ManagedStrippingLevel.Low : ManagedStrippingLevel.High;
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.iOS, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.iOS, stripping);
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, "com.turbohttp.devicetests");
        PlayerSettings.iOS.sdkVersion = iOSSdkVersion.SimulatorSDK;
        PlayerSettings.iOS.targetOSVersionString = "15.0";
        var simArch = "x86_64";
#if UNITY_6000_0_OR_NEWER
        // Apple Silicon simulator runtimes (iOS 26+) are arm64-only; 2021.3 ships an x86_64-only simulator player.
        PlayerSettings.iOS.simulatorSdkArchitecture = AppleMobileArchitectureSimulator.ARM64;
        simArch = PlayerSettings.iOS.simulatorSdkArchitecture.ToString();
#endif
        options.options &= ~(BuildOptions.AutoRunPlayer | BuildOptions.ConnectToHost);
        options.locationPathName = Environment.GetEnvironmentVariable("TURBOHTTP_IOS_OUT");
        s_Built = true;
        Debug.Log("TURBOHTTP_DEVICE_BUILD target=iOS sdk=Simulator arch=" + simArch + " unity=" + Application.unityVersion
            + " stripping=" + stripping + " out=" + options.locationPathName);
        return options;
    }

    public void Cleanup()
    {
        // Nothing runs the player here, so the Test Runner would wait for results forever.
        if (s_Built && Array.IndexOf(Environment.GetCommandLineArgs(), "-runTests") >= 0)
            EditorApplication.Exit(0);
    }
}
CS

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
// Written by Tools~/unity/build-ios-test-player.sh. The player runs without an editor connection: this
// callback logs every test start (TURBOHTTP_DEVICE_TEST) and failure with its message and stack
// (TURBOHTTP_DEVICE_FAIL), rewrites Application.persistentDataPath/results.xml after every fixture
// (partial="true") so a hang still leaves results, flags a test running longer than HangSeconds
// (TURBOHTTP_DEVICE_HANG, from a watchdog thread: a hung test blocks the main thread), writes the full
// NUnit XML when the run ends and quits the player.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using NUnit.Framework.Interfaces;
using UnityEngine;
using UnityEngine.TestRunner;
using Debug = UnityEngine.Debug;

[assembly: TestRunCallback(typeof(TurboHttpDeviceResults))]

public class TurboHttpDeviceResults : ITestRunCallback
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void UseAppCacheAsTemp()
    {
        Environment.SetEnvironmentVariable("TMPDIR", Application.temporaryCachePath);
        Log("TURBOHTTP_DEVICE_RUN tmp=" + Path.GetTempPath());
    }

    public const string FileName = "results.xml";
    const int HangSeconds = 180;
    const int StackLines = 8;

    // _gate owns every field below: the main thread runs the callbacks, the watchdog timer reads them.
    readonly object _gate = new object();
    readonly StringBuilder _leaves = new StringBuilder();
    readonly Stopwatch _clock = new Stopwatch();
    string _path;
    int _total, _passed, _failed, _skipped, _inconclusive;
    string _current;
    TimeSpan _currentStart;
    bool _hangReported, _finished;
    static Timer s_Watchdog;

    static void Log(string message) => Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", message);

    public void RunStarted(ITest testsToRun)
    {
        lock (_gate)
        {
            _path = Path.Combine(Application.persistentDataPath, FileName);
            _clock.Restart();
        }
        if (File.Exists(_path)) File.Delete(_path);
        Log("TURBOHTTP_DEVICE_RUN started tests=" + testsToRun.TestCaseCount + " unity=" + Application.unityVersion);
        s_Watchdog = new Timer(_ => CheckHang(), null, 15000, 15000);
    }

    public void TestStarted(ITest test)
    {
        if (test.IsSuite) return;
        lock (_gate)
        {
            _current = test.FullName;
            _currentStart = _clock.Elapsed;
            _hangReported = false;
        }
        Log("TURBOHTTP_DEVICE_TEST " + test.FullName);
    }

    public void TestFinished(ITestResult result)
    {
        if (!result.Test.IsSuite)
        {
            lock (_gate)
            {
                _current = null;
                _total++;
                switch (result.ResultState.Status)
                {
                    case TestStatus.Passed: _passed++; break;
                    case TestStatus.Failed: _failed++; break;
                    case TestStatus.Skipped: _skipped++; break;
                    default: _inconclusive++; break;
                }
                _leaves.Append(result.ToXml(true).OuterXml).Append('\n');
            }
            if (result.ResultState.Status == TestStatus.Failed)
                Log("TURBOHTTP_DEVICE_FAIL " + result.FullName + " (" + result.ResultState + ", "
                    + result.Duration.ToString("0.###", CultureInfo.InvariantCulture) + "s)\n  message: "
                    + Indent(result.Message, 20) + "\n  stack: " + Indent(result.StackTrace, StackLines));
            return;
        }
        // A fixture is the suite with a type and no method (namespaces have neither, parameterized tests a method).
        if (result.Test.TypeInfo != null && result.Test.Method == null)
            lock (_gate) WritePartial(null);
    }

    public void RunFinished(ITestResult result)
    {
        lock (_gate) _finished = true;
        s_Watchdog?.Dispose();
        int total = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount;
        double duration = _clock.Elapsed.TotalSeconds;
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<test-run id=\"2\" testcasecount=\"" + total
            + "\" result=\"" + result.ResultState.Status + "\" total=\"" + total + "\" passed=\"" + result.PassCount
            + "\" failed=\"" + result.FailCount + "\" inconclusive=\"" + result.InconclusiveCount + "\" skipped=\""
            + result.SkipCount + "\" duration=\"" + duration.ToString(CultureInfo.InvariantCulture) + "\">\n"
            + result.ToXml(true).OuterXml + "\n</test-run>\n";
        lock (_gate) WriteAtomic(xml);
        Log("TURBOHTTP_DEVICE_RUN finished total=" + total + " passed=" + result.PassCount + " failed="
            + result.FailCount + " skipped=" + result.SkipCount + " path=" + _path);
        Application.Quit(result.FailCount == 0 ? 0 : 1);
    }

    void CheckHang()
    {
        try
        {
            lock (_gate)
            {
                if (_finished || _current == null || _hangReported) return;
                var running = _clock.Elapsed - _currentStart;
                if (running.TotalSeconds < HangSeconds) return;
                _hangReported = true;
                WritePartial(_current);
                Log("TURBOHTTP_DEVICE_HANG " + _current + " running=" + (int)running.TotalSeconds
                    + "s; partial results written to " + _path);
            }
        }
        catch (Exception e)
        {
            Log("TURBOHTTP_DEVICE_HANG watchdog error: " + e.Message);
        }
    }

    // Caller holds _gate.
    void WritePartial(string hung)
    {
        var xml = new StringBuilder(_leaves.Length + 512)
            .Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<test-run id=\"2\" partial=\"true\" testcasecount=\"")
            .Append(_total).Append("\" total=\"").Append(_total).Append("\" passed=\"").Append(_passed)
            .Append("\" failed=\"").Append(_failed).Append("\" inconclusive=\"").Append(_inconclusive)
            .Append("\" skipped=\"").Append(_skipped).Append("\" duration=\"")
            .Append(_clock.Elapsed.TotalSeconds.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (hung != null) xml.Append(" hung=\"").Append(SecurityElement.Escape(hung)).Append('"');
        xml.Append(">\n<test-suite type=\"Partial\" name=\"partial\" fullname=\"partial\" total=\"").Append(_total)
            .Append("\" passed=\"").Append(_passed).Append("\" failed=\"").Append(_failed).Append("\">\n")
            .Append(_leaves).Append("</test-suite>\n</test-run>\n");
        WriteAtomic(xml.ToString());
    }

    // Caller holds _gate.
    void WriteAtomic(string xml)
    {
        try
        {
            File.WriteAllText(_path + ".tmp", xml);
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(_path + ".tmp", _path);
        }
        catch (Exception e)
        {
            Log("TURBOHTTP_DEVICE_RUN cannot write " + _path + ": " + e.Message);
        }
    }

    static string Indent(string text, int maxLines)
    {
        if (string.IsNullOrEmpty(text)) return "(none)";
        var lines = text.Replace("\r", "").TrimEnd('\n').Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length && i < maxLines; i++)
        {
            if (i > 0) sb.Append("\n    ");
            sb.Append(lines[i]);
        }
        if (lines.Length > maxLines) sb.Append("\n    ... (").Append(lines.Length - maxLines).Append(" more lines)");
        return sb.ToString();
    }
}
CS

handoff="$(dirname "$out_dir")"
mkdir -p "$handoff"
rm -rf "$handoff/READY" "$handoff/DONE" "$handoff/out" "$stage"
mkdir -p "$(dirname "$stage")"

ts="$(date +%Y%m%d-%H%M%S)"
log="$cache/unity-iOS-sim-$stripping-$ts.log"
stage_path="$(topath "$stage")"
export TURBOHTTP_DEVICE_STRIPPING="$stripping" TURBOHTTP_IOS_OUT="$stage_path"
# A Windows Unity.exe started from WSL sees only the variables listed in WSLENV.
export WSLENV="${WSLENV:+$WSLENV:}TURBOHTTP_DEVICE_STRIPPING:TURBOHTTP_IOS_OUT"
args=(-batchmode -projectPath "$(topath "$proj")" -runTests -testPlatform iOS -buildTarget iOS
      -logFile "$(topath "$log")")
[ -n "$filter" ] && args+=(-testFilter "$filter")
[ -n "$category" ] && args+=(-testCategory "$category")

echo "build-ios-test-player: building -> $stage (log $log)"
set +e
"$unity" "${args[@]}"
code=$?
set -e
grep -E "TURBOHTTP_DEVICE_BUILD|error CS[0-9]+|Build Finished, Result|BuildFailedException" "$log" | head -20 || true
if [ ! -d "$stage/Unity-iPhone.xcodeproj" ]; then
  echo "build-ios-test-player: no Xcode project (Unity exit $code); see $log"
  exit 2
fi

# /mnt/c (drvfs) under memory pressure sometimes fails a write with ENOMEM: retry the copy. READY is only
# created after a complete copy, so a failed copy never hands over a partial project.
copied=0
for _ in 1 2 3 4 5; do
  if rsync -a --delete "$stage/" "$out_dir/"; then copied=1; break; fi
  sleep 10
done
[ "$copied" = 1 ] || { echo "build-ios-test-player: cannot copy the Xcode project to $out_dir; READY not created" >&2; exit 2; }
touch "$handoff/READY"
echo "build-ios-test-player: Xcode project in $out_dir (Unity exit $code); READY created; log $log"
