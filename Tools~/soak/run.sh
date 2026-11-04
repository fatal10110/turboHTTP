#!/usr/bin/env bash
# Soak and lossy-network harness (#114). Documented in README.md.
#
#   Tools~/soak/run.sh                          the 1 h soak, every profile (clean, latency, bandwidth, lossy, reset)
#   Tools~/soak/run.sh --duration 120s          a short run (the harness flags pass through)
#   Tools~/soak/run.sh --profiles clean,lossy   only some profiles
#
# Harness flags: --duration S|Nm|Nh, --workers N, --seed N, --warmup S, --sample S, --checkpoint S,
# --max-growth-mb X (default 10), --max-sockets N, --profiles a,b. Script flag: --no-shadow-build.
# Needs dotnet (SDK 8 with the ASP.NET Core shared framework) and the Unity managed DLLs the shadow build
# uses (Tools~/ci/fetch-unity-dlls.sh, or UNITY_DLLS / UNITY_PATH). The per-run test root is trusted only
# through SSL_CERT_FILE/SSL_CERT_DIR (the system roots are not). Output: .build/soak/run (result.json,
# samples.csv, harness.log). Exit 0 = pass; 1 = a failure, growth over the limit, a leaked pooled object or
# socket; 2 = usage; 3 = tool missing.
set -euo pipefail
cd "$(dirname "$0")/../.."

shadow=1
args=()
while [ $# -gt 0 ]; do
  case "$1" in
    --no-shadow-build) shadow=0; shift ;;
    --out) echo "run.sh: --out is fixed to .build/soak/run" >&2; exit 2 ;;
    --*) [ $# -ge 2 ] || { echo "run.sh: $1 needs a value" >&2; exit 2; }; args+=("$1" "$2"); shift 2 ;;
    *) echo "usage: $0 [--no-shadow-build] [--duration S] [--workers N] [--seed N] [--profiles a,b] [...]" >&2; exit 2 ;;
  esac
done

command -v dotnet >/dev/null || { echo "run.sh: dotnet not found" >&2; exit 3; }
dotnet --list-runtimes | grep -q '^Microsoft.AspNetCore.App 8\.' || {
  echo "run.sh: the ASP.NET Core 8 shared framework is missing (install the .NET 8 SDK)" >&2; exit 3; }

if [ -z "${UNITY_DLLS:-}${UNITY_PATH:-}" ] && [ ! -f ".tools/unity-managed/$(cat Tools~/ci/unity-version)/UnityEngine.dll" ]; then
  "Tools~/ci/fetch-unity-dlls.sh" >/dev/null
fi
if [ "$shadow" = 1 ] || [ ! -f .build/shadow/editor/bin/Debug/netstandard2.1/TurboHTTP.WebSocket.Transport.dll ]; then
  python3 "Tools~/ci/shadow_build.py" --variant editor
fi
dotnet build "Tools~/soak/TurboHTTP.Soak.csproj" -c Release -nologo -v q

dll=".build/soak/bin/Release/net8.0/TurboHTTP.Soak.dll"
out="$PWD/.build/soak/run"
rm -rf "$out"
mkdir -p "$out/pki" "$out/empty-cert-dir"
dotnet "$dll" generate "$out/pki"
# The client trusts only the per-run root: SSL_CERT_FILE replaces the system bundle and SSL_CERT_DIR points
# at an empty directory (.NET reads both when it loads the root store).
SSL_CERT_FILE="$out/pki/root.pem" SSL_CERT_DIR="$out/empty-cert-dir" \
  dotnet "$dll" run "$out/pki" --out "$out" "${args[@]}" 2>&1 | tee "$out/harness.log"
exit "${PIPESTATUS[0]}"
