#!/usr/bin/env bash
# Runs TurboHTTP's Unity Test Runner suites from a throwaway Unity project that consumes
# a synced copy of this package (keeps Library/ and .meta churn out of the repo).
#
#   UNITY_PATH=/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity \
#     Tools~/unity/run-tests.sh --platform EditMode|PlayMode [--filter <regex>] [--category <name>]
#
# WSL with a Windows editor: point UNITY_PATH at Unity.exe under /mnt/<drive>/... The package copy,
# test project, results and log then live under %LOCALAPPDATA%\turbohttp (Unity cannot run a project
# from a \\wsl$ path) and every path handed to Unity is converted with wslpath -m.
#
#   UNITY_PATH=/mnt/e/Unity/Hub/2021.3.45f2/Editor/Unity.exe Tools~/unity/run-tests.sh --platform EditMode
#
# Without --filter/--category the whole suite runs; do that only for the periodic host-editor run
# (testing.md, "Periodic host-editor runs"), never per change.
#
# Exit codes: 0 pass, 1 test failures, 2 usage/setup error, 3 Unity not installed (prints
# UNITY_UNAVAILABLE; PR CI then runs the tests). Env: UNITY_TEST_PROJECT (default
# <cache>/testproj), UNITY_PACKAGE_COPY (default <cache>/package), where <cache> is
# ~/.cache/turbohttp, or %LOCALAPPDATA%\turbohttp for a Windows editor driven from WSL.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
platform=""; filter=""; category=""
while [ $# -gt 0 ]; do
  case "$1" in
    --platform) platform="$2"; shift 2 ;;
    --filter) filter="$2"; shift 2 ;;
    --category) category="$2"; shift 2 ;;
    *) echo "usage: $0 --platform EditMode|PlayMode [--filter regex] [--category name]" >&2; exit 2 ;;
  esac
done
case "$platform" in EditMode|PlayMode) ;; *) echo "run-tests: --platform EditMode|PlayMode required" >&2; exit 2 ;; esac

unity="${UNITY_PATH:-}"
if [ -z "$unity" ] || [ ! -x "$unity" ]; then
  echo "UNITY_UNAVAILABLE: set UNITY_PATH to a Unity $(cat "$repo/Tools~/ci/unity-version") editor binary; PR CI runs the Unity tests"
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
  localappdata="$(cd /mnt/c && cmd.exe /c 'echo %LOCALAPPDATA%' 2>/dev/null | tr -d '\r')"
  [ -n "$localappdata" ] || { echo "run-tests: cannot read %LOCALAPPDATA% from Windows" >&2; exit 2; }
  cache="$(wslpath -u "$localappdata")/turbohttp"
else
  cache="${XDG_CACHE_HOME:-$HOME/.cache}/turbohttp"
fi
proj="${UNITY_TEST_PROJECT:-$cache/testproj}"
pkg="${UNITY_PACKAGE_COPY:-$cache/package}"
mkdir -p "$cache"

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

ts="$(date +%Y%m%d-%H%M%S)"
xml="$cache/results-$platform-$ts.xml"; log="$cache/unity-$platform-$ts.log"
args=(-batchmode -projectPath "$(topath "$proj")" -runTests -testPlatform "$platform"
      -testResults "$(topath "$xml")" -logFile "$(topath "$log")")
# Editor-window tests need graphics in EditMode.
[ "$platform" = PlayMode ] && args+=(-nographics)
[ -n "$filter" ] && args+=(-testFilter "$filter")
[ -n "$category" ] && args+=(-testCategory "$category")

set +e
"$unity" "${args[@]}"
code=$?
set -e

if [ ! -f "$xml" ]; then
  echo "run-tests: no results (Unity exit $code); compile errors? see $log"
  grep -E "error CS[0-9]+" "$log" | head -20 || true
  exit 2
fi
python3 - "$xml" <<'PY'
import sys, xml.etree.ElementTree as ET
run = ET.parse(sys.argv[1]).getroot()
a = run.attrib
print(f"run-tests: total={a.get('total')} passed={a.get('passed')} failed={a.get('failed')} "
      f"skipped={a.get('skipped')} inconclusive={a.get('inconclusive')} duration={a.get('duration')}s")
for case in run.iter("test-case"):
    if case.get("result") == "Failed":
        msg = (case.findtext("failure/message") or "").strip().splitlines()
        print(f"  FAIL {case.get('fullname')}: {msg[0] if msg else ''}")
PY
echo "run-tests: results $xml, log $log"
exit $(( code == 0 ? 0 : 1 ))
