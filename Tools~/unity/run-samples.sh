#!/usr/bin/env bash
# Runs every Samples~ sample in Play mode from a clean throwaway Unity project and prints the
# per-sample console logs (#268). Public network (httpbin.org, jsonplaceholder.typicode.com):
# a manual / host-editor tool, never PR CI, never part of the package's own test suites.
#
#   UNITY_PATH=<Unity 2021.3 editor binary> Tools~/unity/run-samples.sh
#
# The samples are imported with the Package Manager API behind the "Import" button
# (Sample.Import, Tools~/unity/samples-import/ImportSamples.cs) into
# Assets/Samples/<package displayName>/<version>/<sample displayName>/. Then it runs Tools~/unity/samples-run/SamplesRunTests.cs (one
# [UnityTest] per sample, PlayMode, -nographics). Output: <project>/SamplesRun/<sample>.log, the
# NUnit results XML and the Unity log under <cache>. Exit codes: 0 all five reached their last
# console line, 1 a test failed or timed out, 2 setup/compile error, 3 Unity not installed, 5 WSL interop
# unavailable (cmd.exe or Unity.exe did not start from WSL through about two minutes of retries, wsl-interop.sh, #355).
# Env: UNITY_PATH, UNITY_SAMPLES_PROJECT (default <cache>/samples-proj), UNITY_PACKAGE_COPY
# (default <cache>/samples-package), SAMPLES_FRESH=1 recreates the project.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=Tools~/unity/wsl-interop.sh
. "$repo/Tools~/unity/wsl-interop.sh"
unity="${UNITY_PATH:-}"
if [ -z "$unity" ] || [ ! -x "$unity" ]; then
  echo "UNITY_UNAVAILABLE: set UNITY_PATH to a Unity $(cat "$repo/Tools~/ci/unity-version") editor binary"
  exit 3
fi

win=0
case "$unity" in *.exe|*.EXE) if command -v wslpath >/dev/null 2>&1; then win=1; fi ;; esac
topath() { if [ "$win" = 1 ]; then wslpath -m "$1"; else printf '%s\n' "$1"; fi; }

if [ "$win" = 1 ]; then
  wsl_resolve_localappdata run-samples
  cache="$wsl_localappdata/turbohttp"
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
  wsl_unity_launch run-samples "$cache/samples-create.log" \
    "$unity" -batchmode -nographics -quit -createProject "$(topath "$proj")" -logFile "$(topath "$cache/samples-create.log")"
  [ "$unity_code" = 0 ] || { echo "run-samples: creating $proj failed (Unity exit $unity_code); see $cache/samples-create.log" >&2; exit 2; }
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
wsl_unity_launch run-samples "$cache/samples-import-$ts.log" \
  "$unity" -batchmode -nographics -quit -projectPath "$(topath "$proj")" -executeMethod ImportSamples.Run \
  -logFile "$(topath "$cache/samples-import-$ts.log")"
icode="$unity_code"
grep -E "SAMPLES_IMPORT|error CS[0-9]+" "$cache/samples-import-$ts.log" | head -20 || true
if [ "$icode" != 0 ]; then echo "run-samples: sample import failed (Unity exit $icode), see $cache/samples-import-$ts.log"; exit 2; fi
rm -rf "$proj/Assets/SamplesImport" "$proj/Assets/SamplesImport.meta"
cp -r "$repo/Tools~/unity/samples-run" "$proj/Assets/SamplesRun"
# committed as .asmdef.json so the repo asmdef rules (it references the sample assemblies) do not scan it
mv "$proj/Assets/SamplesRun/SamplesRun.asmdef.json" "$proj/Assets/SamplesRun/SamplesRun.asmdef"
xml="$cache/samples-results-$ts.xml"; log="$cache/samples-unity-$ts.log"
wsl_unity_launch run-samples "$log" \
  "$unity" -batchmode -nographics -projectPath "$(topath "$proj")" -runTests -testPlatform PlayMode \
  -testResults "$(topath "$xml")" -logFile "$(topath "$log")"
code="$unity_code"
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
