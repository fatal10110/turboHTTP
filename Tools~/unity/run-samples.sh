#!/usr/bin/env bash
# Runs every Samples~ sample in Play mode from a clean throwaway Unity project and prints the
# per-sample console logs (#268). Public network (httpbin.org, jsonplaceholder.typicode.com):
# a manual / host-editor tool, never PR CI, never part of the package's own test suites.
#
#   UNITY_PATH=/mnt/e/Unity/Hub/2021.3.45f2/Editor/Unity.exe Tools~/unity/run-samples.sh
#
# The samples are imported with the Package Manager API behind the "Import" button
# (Sample.Import, Tools~/unity/samples-import/ImportSamples.cs) into
# Assets/Samples/<package displayName>/<version>/<sample displayName>/. Then it runs Tools~/unity/samples-run/SamplesRunTests.cs (one
# [UnityTest] per sample, PlayMode, -nographics). Output: <project>/SamplesRun/<sample>.log, the
# NUnit results XML and the Unity log under <cache>. Exit codes: 0 all five reached their last
# console line, 1 a test failed or timed out, 2 setup/compile error, 3 Unity not installed.
# Env: UNITY_PATH, UNITY_SAMPLES_PROJECT (default <cache>/samples-proj), UNITY_PACKAGE_COPY
# (default <cache>/samples-package), SAMPLES_FRESH=1 recreates the project.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
unity="${UNITY_PATH:-}"
if [ -z "$unity" ] || [ ! -x "$unity" ]; then
  echo "UNITY_UNAVAILABLE: set UNITY_PATH to a Unity $(cat "$repo/Tools~/ci/unity-version") editor binary"
  exit 3
fi

win=0
case "$unity" in *.exe|*.EXE) if command -v wslpath >/dev/null 2>&1; then win=1; fi ;; esac
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
  [ -n "$wincache" ] || { echo "run-samples: cannot resolve %LOCALAPPDATA%" >&2; exit 2; }
  cache="$wincache/turbohttp"
else
  cache="${XDG_CACHE_HOME:-$HOME/.cache}/turbohttp"
fi
proj="${UNITY_SAMPLES_PROJECT:-$cache/samples-proj}"
pkg="${UNITY_PACKAGE_COPY:-$cache/samples-package}"
mkdir -p "$cache"

rsync -a --delete --exclude='.git/' --exclude='.build/' --exclude='.tools/' --exclude='Library/' \
  --exclude='Temp/' --exclude='Logs/' --exclude='.DS_Store' --exclude='.claude/' --exclude='Tools~/' \
  "$repo/" "$pkg/"

[ "${SAMPLES_FRESH:-0}" = 1 ] && rm -rf "$proj"
if [ ! -d "$proj/ProjectSettings" ]; then
  "$unity" -batchmode -nographics -quit -createProject "$(topath "$proj")" -logFile "$(topath "$cache/samples-create.log")"
fi

python3 - "$proj" "$repo" "$(topath "$pkg")" <<'PY'
import json, os, shutil, sys
proj, repo, pkgpath = sys.argv[1:4]
manifest = os.path.join(proj, "Packages", "manifest.json")
m = json.load(open(manifest))
m.setdefault("dependencies", {})["com.turbohttp.complete"] = "file:" + pkgpath
m["dependencies"].setdefault("com.unity.test-framework", "1.1.33")
json.dump(m, open(manifest, "w"), indent=2)
base = os.path.join(proj, "Assets", "Samples")
shutil.rmtree(base, ignore_errors=True)
imp = os.path.join(proj, "Assets", "SamplesImport", "Editor")
shutil.rmtree(os.path.dirname(imp), ignore_errors=True)
os.makedirs(imp)
shutil.copy(os.path.join(repo, "Tools~", "unity", "samples-import", "ImportSamples.cs"), imp)
shutil.rmtree(os.path.join(proj, "Assets", "SamplesRun"), ignore_errors=True)
shutil.rmtree(os.path.join(proj, "SamplesRun"), ignore_errors=True)
PY

ts="$(date +%Y%m%d-%H%M%S)"
# Import through the Package Manager API behind the "Import" button; the exit code is non-zero
# unless all five samples were imported. (The driver tests are copied in after the import.)
set +e
"$unity" -batchmode -nographics -quit -projectPath "$(topath "$proj")" -executeMethod ImportSamples.Run \
  -logFile "$(topath "$cache/samples-import-$ts.log")"
icode=$?
set -e
grep -E "SAMPLES_IMPORT|error CS[0-9]+" "$cache/samples-import-$ts.log" | head -20 || true
if [ "$icode" != 0 ]; then echo "run-samples: sample import failed (Unity exit $icode), see $cache/samples-import-$ts.log"; exit 2; fi
rm -rf "$proj/Assets/SamplesImport" "$proj/Assets/SamplesImport.meta"
cp -r "$repo/Tools~/unity/samples-run" "$proj/Assets/SamplesRun"
# committed as .asmdef.json so the repo asmdef rules (it references the sample assemblies) do not scan it
mv "$proj/Assets/SamplesRun/SamplesRun.asmdef.json" "$proj/Assets/SamplesRun/SamplesRun.asmdef"
xml="$cache/samples-results-$ts.xml"; log="$cache/samples-unity-$ts.log"
set +e
"$unity" -batchmode -nographics -projectPath "$(topath "$proj")" -runTests -testPlatform PlayMode \
  -testResults "$(topath "$xml")" -logFile "$(topath "$log")"
code=$?
set -e
if [ ! -f "$xml" ]; then
  echo "run-samples: no results (Unity exit $code); compile errors? see $log"
  grep -E "error CS[0-9]+" "$log" | head -20 || true
  exit 2
fi
for f in "$proj"/SamplesRun/*.log; do echo "=================== $f"; cat "$f"; done
python3 - "$xml" <<'PY'
import sys, xml.etree.ElementTree as ET
for tc in ET.parse(sys.argv[1]).getroot().iter("test-case"):
    print(tc.get("result"), tc.get("name"), tc.get("duration"))
PY
echo "run-samples: results $xml, log $log"
[ "$code" = 0 ] || exit 1
