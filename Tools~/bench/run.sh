#!/usr/bin/env bash
# Streaming benchmark harness (Phase 22a.6, #102). Documented in README.md.
#
#   Tools~/bench/run.sh                     the default set, checked against Tools~/bench/baseline.json
#   Tools~/bench/run.sh --heavy             also the 100 MiB download and 10 x 10 MiB HTTP/2 memory rows
#   Tools~/bench/run.sh --record            write this run as the new baseline (no comparison)
#   Tools~/bench/run.sh --only h1-get-1k-buffered,h2-get-1k-buffered --rounds 3
#
#   Tools~/bench/run.sh --gate-latency      also fail on p50 latency over the budget (quiet machine only)
#
# Harness flags: --iterations N, --warmup N, --rounds N, --only a,b, --budget PERCENT (default 5), --heavy,
# --gate-latency.
# Script flags: --record, --no-compare, --no-shadow-build. Needs dotnet (SDK 8 with the ASP.NET Core shared
# framework) and the Unity managed DLLs the shadow build uses (Tools~/ci/fetch-unity-dlls.sh, or UNITY_DLLS /
# UNITY_PATH). The per-run test root is trusted only through SSL_CERT_FILE/SSL_CERT_DIR. Output:
# .build/bench/run (result.json, bench.log). Exit 0 = pass; 1 = a failure or a regression over the budget;
# 2 = usage; 3 = tool missing.
set -euo pipefail
cd "$(dirname "$0")/../.."

shadow=1
compare=1
record=0
args=()
while [ $# -gt 0 ]; do
  case "$1" in
    --no-shadow-build) shadow=0; shift ;;
    --no-compare) compare=0; shift ;;
    --record) record=1; compare=0; shift ;;
    --heavy|--gate-latency) args+=("$1"); shift ;;
    --out|--compare|--write-baseline) echo "run.sh: $1 is set by the script" >&2; exit 2 ;;
    --*) [ $# -ge 2 ] || { echo "run.sh: $1 needs a value" >&2; exit 2; }; args+=("$1" "$2"); shift 2 ;;
    *) echo "usage: $0 [--record] [--no-compare] [--no-shadow-build] [--heavy] [--iterations N] [--rounds N] [--only a,b] [...]" >&2; exit 2 ;;
  esac
done

command -v dotnet >/dev/null || { echo "run.sh: dotnet not found" >&2; exit 3; }
dotnet --list-runtimes | grep -q '^Microsoft.AspNetCore.App 8\.' || {
  echo "run.sh: the ASP.NET Core 8 shared framework is missing (install the .NET 8 SDK)" >&2; exit 3; }

if [ -z "${UNITY_DLLS:-}${UNITY_PATH:-}" ] && [ ! -f ".tools/unity-managed/$(cat Tools~/ci/unity-version)/UnityEngine.dll" ]; then
  "Tools~/ci/fetch-unity-dlls.sh" >/dev/null
fi
if [ "$shadow" = 1 ] || [ ! -f .build/shadow/editor/bin/Debug/netstandard2.1/TurboHTTP.Transport.dll ]; then
  python3 "Tools~/ci/shadow_build.py" --variant editor --no-tests
fi
dotnet build "Tools~/bench/TurboHTTP.Bench.csproj" -c Release -nologo -v q

baseline="$PWD/Tools~/bench/baseline.json"
if [ "$record" = 1 ]; then args+=(--write-baseline "$baseline"); fi
if [ "$compare" = 1 ] && [ -f "$baseline" ]; then args+=(--compare "$baseline"); fi

dll=".build/bench/bin/Release/net8.0/TurboHTTP.Bench.dll"
out="$PWD/.build/bench/run"
rm -rf "$out"
mkdir -p "$out/pki" "$out/empty-cert-dir"
dotnet "$dll" generate "$out/pki"
# The client trusts only the per-run root: SSL_CERT_FILE replaces the system bundle and SSL_CERT_DIR points
# at an empty directory (.NET reads both when it loads the root store).
BENCH_COMMIT="$(git rev-parse --short HEAD 2>/dev/null || true)" \
SSL_CERT_FILE="$out/pki/root.pem" SSL_CERT_DIR="$out/empty-cert-dir" \
  dotnet "$dll" run "$out/pki" --out "$out" ${args[@]+"${args[@]}"} 2>&1 | tee "$out/bench.log"
exit "${PIPESTATUS[0]}"
