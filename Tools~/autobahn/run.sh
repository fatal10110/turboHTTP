#!/usr/bin/env bash
# Autobahn testsuite interop gate for the WebSocket client (#217). Documented in README.md.
#
#   Tools~/autobahn/run.sh                    full gate: shadow build, harness, fuzzing server, report check
#   Tools~/autobahn/run.sh --cases '12.*,13.*' only these case patterns (the check then covers only them)
#   Tools~/autobahn/run.sh --no-shadow-build  reuse the existing .build/shadow DLLs
#
# Needs docker, dotnet (SDK 8) and the Unity managed DLLs the shadow build uses
# (Tools~/ci/fetch-unity-dlls.sh, or UNITY_DLLS / UNITY_PATH). Reports land in .build/autobahn/reports.
set -euo pipefail
cd "$(dirname "$0")/../.."

cases=""
shadow=1
while [ $# -gt 0 ]; do
  case "$1" in
    --cases) cases="$2"; shift 2 ;;
    --no-shadow-build) shadow=0; shift ;;
    *) echo "usage: $0 [--cases '<pattern>,...'] [--no-shadow-build]" >&2; exit 2 ;;
  esac
done

image="${AUTOBAHN_IMAGE:-crossbario/autobahn-testsuite}"
port="${AUTOBAHN_PORT:-9001}"
out=".build/autobahn"
name="turbohttp-autobahn-$$"

command -v docker >/dev/null || { echo "run.sh: docker not found" >&2; exit 3; }
command -v dotnet >/dev/null || { echo "run.sh: dotnet not found" >&2; exit 3; }

if [ -z "${UNITY_DLLS:-}${UNITY_PATH:-}" ] && [ ! -f ".tools/unity-managed/$(cat Tools~/ci/unity-version)/UnityEngine.dll" ]; then
  "Tools~/ci/fetch-unity-dlls.sh" >/dev/null
fi
if [ "$shadow" = 1 ] || [ ! -f .build/shadow/editor/bin/Debug/netstandard2.1/TurboHTTP.WebSocket.dll ]; then
  python3 "Tools~/ci/shadow_build.py" --variant editor
fi
dotnet build "Tools~/autobahn/TurboHTTP.Autobahn.csproj" -c Release -nologo -v q

rm -rf "$out/reports" "$out/config"
mkdir -p "$out/reports" "$out/config"
python3 - "Tools~/autobahn/config/fuzzingserver.json" "$out/config/fuzzingserver.json" "$cases" <<'PY'
import json, sys
src, dst, cases = sys.argv[1:4]
cfg = json.load(open(src))
if cases:
    cfg["cases"] = [c.strip() for c in cases.split(",") if c.strip()]
json.dump(cfg, open(dst, "w"), indent=2)
PY

docker run -d --rm --name "$name" --user "$(id -u):$(id -g)" -e HOME=/tmp \
  -p "127.0.0.1:$port:9001" \
  -v "$PWD/$out/config:/config:ro" -v "$PWD/$out/reports:/reports" \
  "$image" wstest -m fuzzingserver -s /config/fuzzingserver.json >/dev/null
trap 'docker logs "$name" > "$out/fuzzingserver.log" 2>&1 || true; docker rm -f "$name" >/dev/null 2>&1 || true' EXIT

# docker's port proxy accepts TCP before wstest listens, so probe the server's HTTP status page.
python3 - "$port" <<'PY' || { echo "run.sh: fuzzing server did not start on port $port" >&2; exit 1; }
import sys, time, urllib.request
for _ in range(120):
    try:
        urllib.request.urlopen(f"http://127.0.0.1:{sys.argv[1]}/", timeout=2).read()
        sys.exit(0)
    except OSError:
        time.sleep(1)
sys.exit(1)
PY

dotnet "$out/bin/Release/net8.0/TurboHTTP.Autobahn.dll" --server "ws://127.0.0.1:$port" | tee "$out/harness.log"

check=(python3 "Tools~/autobahn/check_report.py" "$out/reports/clients/index.json" --expectations "Tools~/autobahn/expectations.json")
[ -n "$cases" ] && check+=(--partial)
"${check[@]}"
