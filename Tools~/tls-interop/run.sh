#!/usr/bin/env bash
# TLS interop job (#230, #113): the production TLS providers against OpenSSL 3 s_server.
#
#   Tools~/tls-interop/run.sh                    full job: shadow build, harness build, PKI, every row
#   Tools~/tls-interop/run.sh --row <filter>     only rows whose name contains <filter>
#   Tools~/tls-interop/run.sh --no-shadow-build  reuse the existing .build/shadow DLLs
#
# Rows: BouncyCastle handshakes pinned to 0xC030 (TLS 1.2) and 0x1302 (TLS 1.3) plus one HTTP/1.1
# request through BouncyCastleTlsStream; the negative certificate suite and a TLS 1.1 server for the
# SslStream, BouncyCastle and Auto backends, with positive controls. The per-run test root is trusted
# only through SSL_CERT_FILE/SSL_CERT_DIR (the system roots are not), so the SslStream verifier runs
# unchanged; BouncyCastle has no platform verifier on Linux (#248), so the harness also registers the
# root as a test trust anchor and checks the production rejection in one row. Needs dotnet (SDK 8), OpenSSL 3 and the Unity managed DLLs the shadow build uses
# (Tools~/ci/fetch-unity-dlls.sh, or UNITY_DLLS / UNITY_PATH). Output lands in .build/tls-interop/run.
# Exit 0 = every row passed; 1 = a row or a precondition failed (never a skip); 2 = usage; 3 = tool missing.
set -euo pipefail
cd "$(dirname "$0")/../.."

row=()
shadow=1
while [ $# -gt 0 ]; do
  case "$1" in
    --row) row=(--row "$2"); shift 2 ;;
    --no-shadow-build) shadow=0; shift ;;
    *) echo "usage: $0 [--row <filter>] [--no-shadow-build]" >&2; exit 2 ;;
  esac
done

command -v dotnet >/dev/null || { echo "run.sh: dotnet not found" >&2; exit 3; }
command -v "${TURBO_TLS_INTEROP_OPENSSL:-openssl}" >/dev/null || { echo "run.sh: openssl not found" >&2; exit 3; }

if [ -z "${UNITY_DLLS:-}${UNITY_PATH:-}" ] && [ ! -f ".tools/unity-managed/$(cat Tools~/ci/unity-version)/UnityEngine.dll" ]; then
  "Tools~/ci/fetch-unity-dlls.sh" >/dev/null
fi
if [ "$shadow" = 1 ] || [ ! -f .build/shadow/editor/bin/Debug/netstandard2.1/TurboHTTP.Transport.BouncyCastle.dll ]; then
  python3 "Tools~/ci/shadow_build.py" --variant editor
fi
dotnet build "Tools~/tls-interop/TurboHTTP.TlsInterop.csproj" -c Release -nologo -v q

dll=".build/tls-interop/bin/Release/net8.0/TurboHTTP.TlsInterop.dll"
out="$PWD/.build/tls-interop/run"
rm -rf "$out"
mkdir -p "$out/empty-cert-dir"
dotnet "$dll" generate "$out"
# The run phase trusts only the per-run root: SSL_CERT_FILE replaces the system bundle and
# SSL_CERT_DIR points at an empty directory (.NET reads both when it loads the root store).
SSL_CERT_FILE="$out/root.pem" SSL_CERT_DIR="$out/empty-cert-dir" dotnet "$dll" run "$out" "${row[@]}" | tee "$out/harness.log"
exit "${PIPESTATUS[0]}"
