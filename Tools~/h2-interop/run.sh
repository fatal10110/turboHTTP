#!/usr/bin/env bash
# HTTP/2 adversarial-server and interop job (#111).
#
#   Tools~/h2-interop/run.sh                        full job: shadow build, harness build, every row
#   Tools~/h2-interop/run.sh --only adversarial     only the scripted adversarial-server scenarios (no docker, no TLS)
#   Tools~/h2-interop/run.sh --only interop         only the interop rows (kestrel, nghttpd, nginx, h2o, go, envoy, nghttpx)
#   Tools~/h2-interop/run.sh --row <filter>         only rows whose name contains <filter>
#   Tools~/h2-interop/run.sh --no-shadow-build      reuse the existing .build/shadow DLLs
#
# Adversarial rows: Tests/Runtime/Transport/Http2/Adversarial (the NUnit suite runs the same scenarios in the
# host editor) drive a real Http2Connection in memory with hostile frame sequences. Interop rows: the
# production RawSocketTransport (SslStream, ALPN h2) against Kestrel (in-process), nghttpd, nginx, h2o, Go net/http,
# Envoy and nghttpx (docker, pinned images, Tools~/h2-interop/docker) over TLS on loopback; the per-run root
# (Tools~/tls-interop/TestPki.cs) is trusted only through SSL_CERT_FILE/SSL_CERT_DIR. The bc-* rows repeat this with
# BouncyCastle, directly and through an in-harness CONNECT proxy (#255); BouncyCastle trusts the root through the
# test-anchor hook. Needs dotnet (SDK 8 with the ASP.NET Core runtime), docker for the containerized rows and the
# Unity managed DLLs the shadow build uses (Tools~/ci/fetch-unity-dlls.sh, or UNITY_DLLS / UNITY_PATH). Output lands
# in .build/h2-interop/run (interop.log, docker-<server>.log).
# Exit 0 = every row passed (KNOWN-FAIL rows name a filed defect, N/A rows a server that cannot serve them);
# 1 = a row or a precondition failed (never a skip); 2 = usage; 3 = tool missing.
set -euo pipefail
cd "$(dirname "$0")/../.."

row=()
only=all
shadow=1
while [ $# -gt 0 ]; do
  case "$1" in
    --row) row=(--row "$2"); shift 2 ;;
    --only) only="$2"; shift 2 ;;
    --no-shadow-build) shadow=0; shift ;;
    *) echo "usage: $0 [--only adversarial|interop] [--row <filter>] [--no-shadow-build]" >&2; exit 2 ;;
  esac
done
case "$only" in all|adversarial|interop) ;; *) echo "run.sh: --only takes adversarial or interop" >&2; exit 2 ;; esac

command -v dotnet >/dev/null || { echo "run.sh: dotnet not found" >&2; exit 3; }
if [ "$only" != adversarial ]; then
  command -v docker >/dev/null || { echo "run.sh: docker not found" >&2; exit 3; }
fi

if [ -z "${UNITY_DLLS:-}${UNITY_PATH:-}" ] && [ ! -f ".tools/unity-managed/$(cat Tools~/ci/unity-version)/UnityEngine.dll" ]; then
  "Tools~/ci/fetch-unity-dlls.sh" >/dev/null
fi
if [ "$shadow" = 1 ] || [ ! -f .build/shadow/editor/bin/Debug/netstandard2.1/TurboHTTP.Transport.dll ]; then
  python3 "Tools~/ci/shadow_build.py" --variant editor --no-tests
fi
dotnet build "Tools~/h2-interop/TurboHTTP.H2Interop.csproj" -c Release -nologo -v q

dll="$PWD/.build/h2-interop/bin/Release/net8.0/TurboHTTP.H2Interop.dll"
out="$PWD/.build/h2-interop/run"
rm -rf "$out"
mkdir -p "$out/empty-cert-dir"
status=0
containers=()
# shellcheck disable=SC2317,SC2329  # invoked by the EXIT trap
cleanup() {
  if [ ${#containers[@]} -gt 0 ]; then docker rm -f "${containers[@]}" >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT

if [ "$only" != interop ]; then
  dotnet "$dll" adversarial "${row[@]}" | tee "$out/adversarial.log" || status=1
fi

if [ "$only" != adversarial ]; then
  dotnet "$dll" generate "$out"
  # Every server listens on 8443 inside its container with the per-run dir mounted read-only on /pki
  # (leaf-valid.pem, leaf.key, www/). Images are pinned by digest, directly or through their Dockerfile's FROM.
  docker_dir="$PWD/Tools~/h2-interop/docker"
  nginx_image=nginx:1.27.3-alpine@sha256:814a8e88df978ade80e584cc5b333144b9372a8e3c98872d07137dbf3b44d0e4
  envoy_image=envoyproxy/envoy:v1.32.1@sha256:11c73026f1ba7f92e8a6a093e9902465ad10f6ae7d17dd63a5c075bc09f1c93d
  docker pull -q "$nginx_image" >/dev/null
  docker pull -q "$envoy_image" >/dev/null
  for image in nghttpd nghttpx h2o go; do
    docker build -q -t "turbohttp-h2-interop-$image" "$docker_dir/$image" >/dev/null
  done
  servers=()
  start() {
    local name="$1" id
    shift
    id=$(docker run -d -p 127.0.0.1::8443 -v "$out:/pki:ro" "$@")
    containers+=("$id")
    servers+=("$name")
  }
  start nghttpd turbohttp-h2-interop-nghttpd --echo-upload --htdocs=/pki/www 8443 /pki/leaf.key /pki/leaf-valid.pem
  start nginx -v "$docker_dir/nginx/nginx.conf:/etc/nginx/nginx.conf:ro" \
    -v "$docker_dir/nginx/h2-headers.conf:/etc/nginx/h2-headers.conf:ro" "$nginx_image"
  start h2o turbohttp-h2-interop-h2o
  start go turbohttp-h2-interop-go
  start envoy -e ENVOY_UID=0 -v "$docker_dir/envoy/envoy.yaml:/etc/envoy/envoy.yaml:ro" "$envoy_image"
  start nghttpx turbohttp-h2-interop-nghttpx
  port() { docker port "$1" 8443/tcp | head -n1 | sed 's/.*://'; }

  # The run phase trusts only the per-run root: SSL_CERT_FILE replaces the system bundle and
  # SSL_CERT_DIR points at an empty directory (.NET reads both when it loads the root store).
  interop() {
    SSL_CERT_FILE="$out/root.pem" SSL_CERT_DIR="$out/empty-cert-dir" \
      dotnet "$dll" interop "$out" "$1" "$2" "${row[@]}" | tee -a "$out/interop.log" || status=1
  }
  interop kestrel 0
  for i in "${!servers[@]}"; do
    interop "${servers[$i]}" "$(port "${containers[$i]}")"
  done
  for i in "${!servers[@]}"; do docker logs "${containers[$i]}" >"$out/docker-${servers[$i]}.log" 2>&1 || true; done
fi

exit "$status"
