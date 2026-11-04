#!/usr/bin/env bash
# Runs TurboHTTP's Unity Test Runner suites from a throwaway Unity project that consumes
# a synced copy of this package (keeps Library/ and .meta churn out of the repo).
#
#   UNITY_PATH=/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity \
#     Tools~/unity/run-tests.sh --platform EditMode|PlayMode [--filter <regex>] [--category <name>] [--integration] [--with-unitask]
#
# WSL with a Windows editor: point UNITY_PATH at Unity.exe under /mnt/<drive>/... The package copy,
# test project, results and log then live under %LOCALAPPDATA%\turbohttp (Unity cannot run a project
# from a \\wsl$ path) and every path handed to Unity is converted with wslpath -m.
#
#   UNITY_PATH=/mnt/e/Unity/Hub/2021.3.45f2/Editor/Unity.exe Tools~/unity/run-tests.sh --platform EditMode
#
# --integration (implied by --category ExternalNetwork) defines TURBOHTTP_INTEGRATION_TESTS for the run so the
# internet-facing blocks guarded by it compile (TLS integration, BouncyCastle/SslStream providers, pool tests,
# #263). The define is written to the test project for that run and removed again on every other run, so a
# scoped run never compiles them. The ExternalNetwork host run is
#   Tools~/unity/run-tests.sh --platform PlayMode --category ExternalNetwork
# and needs the public internet (testing.md, "Host-editor gates").
#
# --with-unitask (#311) adds com.cysharp.unitask, from the Cysharp git repo at the tag pinned in
# Tools~/ci/unitask_pin.py, to the test project's manifest so TurboHTTP.UniTask, TurboHTTP.UniTask.WebSocket
# and TurboHTTP.Tests.UniTask compile (their versionDefines set TURBOHTTP_UNITASK). After the run the
# package's commit in Packages/packages-lock.json must equal the pinned SHA (else exit 2), and at least one
# UniTask adapter test must have run (else exit 4; they are PlayMode tests). Without the flag the entry is
# removed again, so a later run never compiles them. Needs git and GitHub access for the package fetch:
#   Tools~/unity/run-tests.sh --platform PlayMode --with-unitask --filter 'UniTaskCancellationTests'
#
# Without --filter/--category the whole suite runs; do that only for the periodic host-editor run
# (testing.md, "Host-editor gates"), never per change.
#
# Exit codes: 0 pass, 1 test failures, 2 usage/setup error (incl. a UniTask commit other than the pin), 3 Unity not installed (prints
# UNITY_UNAVAILABLE; CI never runs Unity (no license, #188): this script in a host editor is the Unity gate), 4 skip gate
# (a skipped test without an #issue or [env] reason, or an unfiltered run skipping more tests than
# Tools~/unity/skip-baseline.txt allows; summarize_results.py, #151). Env: UNITY_TEST_PROJECT (default
# <cache>/testproj), UNITY_PACKAGE_COPY (default <cache>/package), where <cache> is
# ~/.cache/turbohttp, or %LOCALAPPDATA%\turbohttp for a Windows editor driven from WSL.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
platform=""; filter=""; category=""; integration=0; unitask=0
while [ $# -gt 0 ]; do
  case "$1" in
    --platform) platform="$2"; shift 2 ;;
    --filter) filter="$2"; shift 2 ;;
    --category) category="$2"; shift 2 ;;
    --integration) integration=1; shift ;;
    --with-unitask) unitask=1; shift ;;
    *) echo "usage: $0 --platform EditMode|PlayMode [--filter regex] [--category name] [--integration] [--with-unitask]" >&2; exit 2 ;;
  esac
done
case "$platform" in EditMode|PlayMode) ;; *) echo "run-tests: --platform EditMode|PlayMode required" >&2; exit 2 ;; esac
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
# com.cysharp.unitask (#311) only for --with-unitask; removed otherwise, like TURBOHTTP_INTEGRATION_TESTS below.
unitask_url=""
if [ "$unitask" = 1 ]; then unitask_url="$(python3 "$repo/Tools~/ci/unitask_pin.py" url)"; fi
python3 - "$proj/Packages/manifest.json" "$(topath "$pkg")" "$unitask_url" <<'PY'
import json, sys
path, pkg, unitask = sys.argv[1], sys.argv[2], sys.argv[3]
m = json.load(open(path))
m.setdefault("dependencies", {})["com.turbohttp.complete"] = "file:" + pkg
m["dependencies"].setdefault("com.unity.test-framework", "1.1.33")
if unitask:
    m["dependencies"]["com.cysharp.unitask"] = unitask
else:
    m["dependencies"].pop("com.cysharp.unitask", None)
m["testables"] = sorted(set(m.get("testables", [])) | {"com.turbohttp.complete"})
json.dump(m, open(path, "w"), indent=2)
PY
# BouncyCastle is opt-in (TURBOHTTP_BOUNCYCASTLE, #196); the TLS suites need it compiled, so the
# test project defines it for the Standalone group (the Editor's group) in its Player settings.
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
    print("run-tests: no scriptingDefineSymbols in " + path, file=sys.stderr)
    sys.exit(2)
entries = [] if m.group(1).strip() == "{}" else m.group(2).splitlines()
for i, line in enumerate(entries):
    key, _, value = line.strip().partition(":")
    if key == "Standalone":
        symbols = [s for s in value.strip().split(";") if s and s not in managed]
        entries[i] = "    Standalone: " + ";".join(symbols + sorted(wanted))
        break
else:
    entries.append("    Standalone: " + ";".join(sorted(wanted)))
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

# Unity wrote the resolved packages before compiling: a moved tag or stale pin voids the run (#311).
if [ "$unitask" = 1 ] && ! python3 "$repo/Tools~/ci/unitask_pin.py" check-lock "$proj/Packages/packages-lock.json"; then
  echo "run-tests: com.cysharp.unitask did not resolve to the pinned commit; see $log" >&2
  exit 2
fi

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
# --with-unitask is the UniTask adapter gate: a run in which none of those tests ran proves nothing.
if [ "$unitask" = 1 ] && ! python3 "$repo/Tools~/ci/unitask_pin.py" check-results "$xml" && [ "$gate" = 0 ]; then
  gate=4
fi
set -e
echo "run-tests: results $xml, log $log"
if [ "$code" != 0 ]; then exit 1; fi
if [ "$gate" != 0 ]; then exit "$gate"; fi
exit 0
