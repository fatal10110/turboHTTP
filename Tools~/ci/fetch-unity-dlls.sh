#!/usr/bin/env bash
# Extracts Unity's managed engine/editor assemblies from the public unityci/editor
# image so the shadow build (Tools~/ci/build.sh) can compile engine-referencing
# asmdefs without a Unity install or license. Run once per Unity version.
#
#   Tools~/ci/fetch-unity-dlls.sh            # -> .tools/unity-managed/<version>
#   UNITY_VERSION=2021.3.45f2 UNITY_DLLS=/some/dir Tools~/ci/fetch-unity-dlls.sh
#   Tools~/ci/fetch-unity-dlls.sh --prune    # also delete the ~4 GB image afterwards
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
version="${UNITY_VERSION:-$(cat "$root/Tools~/ci/unity-version")}"
out="${UNITY_DLLS:-$root/.tools/unity-managed/$version}"
image="unityci/editor:ubuntu-${version}-base-3.2.2"

if [ -f "$out/UnityEngine.dll" ]; then
  echo "$out"
  exit 0
fi

command -v docker >/dev/null || { echo "fetch-unity-dlls: docker is required" >&2; exit 1; }

docker pull --platform linux/amd64 "$image" >&2
cid="$(docker create --platform linux/amd64 "$image")"
trap 'docker rm -f "$cid" >/dev/null' EXIT
mkdir -p "$(dirname "$out")"
rm -rf "$out.tmp"
docker cp "$cid:/opt/unity/Editor/Data/Managed/UnityEngine" "$out.tmp"
mv "$out.tmp" "$out"

if [ "${1:-}" = "--prune" ]; then
  docker rmi "$image" >/dev/null
fi
echo "$out"
