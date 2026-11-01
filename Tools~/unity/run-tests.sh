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
# UNITY_UNAVAILABLE; PR CI does not run Unity tests, the periodic host-editor run does), 4 skip gate
# (a skipped test without an #issue or [env] reason, or an unfiltered run skipping more tests than
# Tools~/unity/skip-baseline.txt allows; summarize_results.py, #151). Env: UNITY_TEST_PROJECT (default
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
  echo "UNITY_UNAVAILABLE: set UNITY_PATH to a Unity $(cat "$repo/Tools~/ci/unity-version") editor binary; PR CI does not run Unity tests (no license, #188)"
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
  [ -n "$wincache" ] || { echo "run-tests: cannot resolve %LOCALAPPDATA% from Windows (cmd.exe, wslpath)" >&2; exit 2; }
  cache="$wincache/turbohttp"
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
# BouncyCastle is opt-in (TURBOHTTP_BOUNCYCASTLE, #196); the TLS suites need it compiled, so the
# test project defines it for the Standalone group (the Editor's group) in its Player settings.
python3 - "$proj/ProjectSettings/ProjectSettings.asset" TURBOHTTP_BOUNCYCASTLE <<'PY'
import re, sys
path, define = sys.argv[1], sys.argv[2]
text = open(path, encoding="utf-8").read()
m = re.search(r"^  scriptingDefineSymbols:(.*)\n((?:    .*\n)*)", text, re.M)
if not m:
    print("run-tests: no scriptingDefineSymbols in " + path, file=sys.stderr)
    sys.exit(2)
entries = [] if m.group(1).strip() == "{}" else m.group(2).splitlines()
for i, line in enumerate(entries):
    key, _, value = line.strip().partition(":")
    if key == "Standalone":
        symbols = [s for s in value.strip().split(";") if s]
        if define not in symbols:
            entries[i] = "    Standalone: " + ";".join(symbols + [define])
        break
else:
    entries.append("    Standalone: " + define)
block = "  scriptingDefineSymbols:\n" + "".join(e + "\n" for e in entries)
new = text[:m.start()] + block + text[m.end():]
if new != text:
    open(path, "w", encoding="utf-8").write(new)
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
is_filtered=0
if [ -n "$filter" ] || [ -n "$category" ]; then is_filtered=1; fi
# Failures keep exit 1; the skip gates (an unexplained skip, or more skips than Tools~/unity/skip-baseline.txt
# allows for an unfiltered run, #151) exit 4 so a vanished test cannot pass silently.
set +e
python3 "$repo/Tools~/unity/summarize_results.py" "$xml" "$platform" "$is_filtered" "$repo/Tools~/unity/skip-baseline.txt"
gate=$?
set -e
echo "run-tests: results $xml, log $log"
if [ "$code" != 0 ]; then exit 1; fi
if [ "$gate" != 0 ]; then exit "$gate"; fi
exit 0
