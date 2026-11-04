#!/usr/bin/env bash
# Extracts Unity's managed engine/editor assemblies, plus the NUnit and Test Framework packages the
# editor bundles, from the public unityci/editor image so the shadow build (Tools~/ci/shadow_build.py)
# can compile engine-referencing asmdefs and the Tests/ asmdefs without a Unity install or license.
# Run once per Unity version; an older cache without packages/ is completed in place.
#
#   Tools~/ci/fetch-unity-dlls.sh            # -> .tools/unity-managed/<version> (+ packages/*.tgz)
#   UNITY_VERSION=2021.3.45f2 UNITY_DLLS=/some/dir Tools~/ci/fetch-unity-dlls.sh
#   Tools~/ci/fetch-unity-dlls.sh --prune    # also delete the ~4 GB image afterwards
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
version="${UNITY_VERSION:-$(cat "$root/Tools~/ci/unity-version")}"
out="${UNITY_DLLS:-$root/.tools/unity-managed/$version}"
image="unityci/editor:ubuntu-${version}-base-3.2.2"
# The versions Unity 2021.3 bundles (Editor/Data/Resources/PackageManager/Editor); keep in sync with
# NUNIT_PACKAGE / TEST_FRAMEWORK_PACKAGE in shadow_build.py and the com.unity.test-framework pin in Tools~/unity/*.sh.
packages=(com.unity.ext.nunit-1.0.6.tgz com.unity.test-framework-1.1.33.tgz)

missing=()
for p in "${packages[@]}"; do
  [ -f "$out/packages/$p" ] || missing+=("$p")
done
if [ -f "$out/UnityEngine.dll" ] && [ "${#missing[@]}" = 0 ]; then
  echo "$out"
  exit 0
fi

command -v docker >/dev/null || { echo "fetch-unity-dlls: docker is required" >&2; exit 1; }

docker pull --platform linux/amd64 "$image" >&2
cid="$(docker create --platform linux/amd64 "$image")"
trap 'docker rm -f "$cid" >/dev/null' EXIT
mkdir -p "$(dirname "$out")"
if [ ! -f "$out/UnityEngine.dll" ]; then
  rm -rf "$out.tmp"
  docker cp "$cid:/opt/unity/Editor/Data/Managed/UnityEngine" "$out.tmp"
  if [ -d "$out" ]; then cp -a "$out.tmp/." "$out/" && rm -rf "$out.tmp"; else mv "$out.tmp" "$out"; fi
  missing=("${packages[@]}")
fi
mkdir -p "$out/packages"
for p in "${missing[@]}"; do
  docker cp "$cid:/opt/unity/Editor/Data/Resources/PackageManager/Editor/$p" "$out/packages/$p.tmp"
  mv "$out/packages/$p.tmp" "$out/packages/$p"
done

if [ "${1:-}" = "--prune" ]; then
  docker rmi "$image" >/dev/null
fi
echo "$out"
