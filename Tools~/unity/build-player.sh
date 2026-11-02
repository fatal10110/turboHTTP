#!/usr/bin/env bash
# Builds a TurboHTTP player smoke from a throwaway Unity project that consumes a synced copy of
# this package, inspects which TurboHTTP assemblies survived into the build, and (Windows targets)
# runs the player and checks its marker line.
#
#   UNITY_PATH=/mnt/e/Unity/Hub/2021.3.45f2/Editor/Unity.exe \
#     Tools~/unity/build-player.sh --target win64-mono|win64-il2cpp|android-il2cpp [--bouncycastle] [--background]
#
# The project gets Tools~/unity/player-smoke/ as Assets/TurboHttpSmoke/ with no .asmdef:
# TurboHttpPlayerSmoke.cs creates a UHttpClient (Retry + Auth interceptors), a WebSocket transport
# and a WebSocketClient and round-trips JSON WITHOUT any EnsureRegistered() call (#1 AC1, #7 AC1);
# Editor/TurboHttpPlayerBuild.cs sets the backend (IL2CPP targets: Managed Stripping Level High)
# and calls BuildPipeline.BuildPlayer. TURBOHTTP_BOUNCYCASTLE is set for the target's group only
# with --bouncycastle and removed otherwise (#196, #258); TURBOHTTP_BACKGROUND_NETWORKING likewise with
# --background, which on Android also checks that the TurboHttpBackground.androidlib classes and the
# WorkManager dependency reached the APK without any Gradle template edit (#89).
#
# Checks: TurboHTTP.Core/Transport/WebSocket.Transport/Complete/JSON/Retry/Auth are in every build.
# Without --bouncycastle, TurboHTTP.Transport.BouncyCastle is absent from every build and, on IL2CPP
# (the linker output, Library/Bee/artifacts/*/ManagedStripped), TurboHTTP.SecureProtocol too (#8 AC2,
# #196 AC1); with it, both are present. Mono keeps stripping Disabled and copies every compiled
# assembly, so its SecureProtocol presence is reported, not asserted. Windows players are started
# with -batchmode -nographics and must print "TURBOHTTP_SMOKE PASS"; Android is built, not run
# (device rows: #99). CI player builds need a Unity license (#100).
#
# Exit codes: 0 pass, 1 build or check failure, 2 usage/setup error, 3 Unity, a build module or the
# native toolchain is not installed (prints UNITY_UNAVAILABLE or TOOLCHAIN_UNAVAILABLE). Env:
# UNITY_SMOKE_DIR (default <cache>/player-smoke; holds testproj/, package/ and builds/<run>/ with the
# logs, the TurboHTTP.link.xml UnityLinker received and the build), where <cache> is ~/.cache/turbohttp,
# or %LOCALAPPDATA%\turbohttp for a Windows editor driven from WSL. One Unity process at a time.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
target=""; bouncycastle=0; background=0
while [ $# -gt 0 ]; do
  case "$1" in
    --target) target="${2:-}"; shift 2 ;;
    --bouncycastle) bouncycastle=1; shift ;;
    --background) background=1; shift ;;
    *) echo "usage: $0 --target win64-mono|win64-il2cpp|android-il2cpp [--bouncycastle] [--background]" >&2; exit 2 ;;
  esac
done
# stripped: the UnityLinker output folder (under Library/Bee/artifacts) of the target's build program.
case "$target" in
  win64-mono|win64-il2cpp) group=Standalone; buildtarget=Win64; product=TurboHttpSmoke.exe; stripped=WinPlayerBuildProgram ;;
  android-il2cpp) group=Android; buildtarget=Android; product=TurboHttpSmoke.apk; stripped=Android ;;
  *) echo "build-player: --target win64-mono|win64-il2cpp|android-il2cpp required" >&2; exit 2 ;;
esac

unity="${UNITY_PATH:-}"
if [ -z "$unity" ] || [ ! -x "$unity" ]; then
  echo "UNITY_UNAVAILABLE: set UNITY_PATH to a Unity $(cat "$repo/Tools~/ci/unity-version") editor binary; CI player builds need a license (#100)"
  exit 3
fi
engines="$(dirname "$unity")/Data/PlaybackEngines"
case "$(uname -s)" in Darwin) engines="$(cd "$(dirname "$unity")/../../.." && pwd)/PlaybackEngines" ;; esac
case "$target" in
  win64-mono) module="$engines/windowsstandalonesupport/Variations/win64_player_nondevelopment_mono" ;;
  win64-il2cpp) module="$engines/windowsstandalonesupport/Variations/win64_player_nondevelopment_il2cpp" ;;
  android-il2cpp) module="$engines/AndroidPlayer/Variations/il2cpp" ;;
esac
if [ ! -d "$module" ]; then
  echo "UNITY_UNAVAILABLE: build module for $target not installed ($module)"
  exit 3
fi

win=0
case "$unity" in
  *.exe|*.EXE)
    if command -v wslpath >/dev/null 2>&1; then win=1; fi ;;
esac
topath() { if [ "$win" = 1 ]; then wslpath -m "$1"; else printf '%s\n' "$1"; fi; }

if [ -n "${UNITY_SMOKE_DIR:-}" ]; then
  cache="$(dirname "$UNITY_SMOKE_DIR")"
elif [ "$win" = 1 ]; then
  localappdata=""
  for _ in 1 2 3; do
    localappdata="$( (cd /mnt/c && cmd.exe /c 'echo %LOCALAPPDATA%') 2>/dev/null | tr -d '\r' || true)"
    [ -n "$localappdata" ] && break
    sleep 1
  done
  wincache=""
  if [ -n "$localappdata" ]; then wincache="$(wslpath -u "$localappdata" 2>/dev/null || true)"; fi
  [ -n "$wincache" ] || { echo "build-player: cannot resolve %LOCALAPPDATA% from Windows (cmd.exe, wslpath)" >&2; exit 2; }
  cache="$wincache/turbohttp"
else
  cache="${XDG_CACHE_HOME:-$HOME/.cache}/turbohttp"
fi
work="${UNITY_SMOKE_DIR:-$cache/player-smoke}"
proj="$work/testproj"; pkg="$work/package"
variant="$target-nobc"
[ "$bouncycastle" = 1 ] && variant="$target-bc"
[ "$background" = 1 ] && variant="$variant-bg"
out="$work/builds/$variant-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$out"

# /mnt/c (drvfs) under memory pressure sometimes fails a readdir with ENOMEM: retry the sync.
synced=0
for _ in 1 2 3; do
  if rsync -a --delete --exclude='.git/' --exclude='.build/' --exclude='.tools/' --exclude='Library/' \
       --exclude='Temp/' --exclude='Logs/' --exclude='.DS_Store' --exclude='.claude/' --exclude='Tools~/' \
       --exclude='Development/' "$repo/" "$pkg/"; then
    synced=1; break
  fi
  sleep 5
done
[ "$synced" = 1 ] || { echo "build-player: cannot sync the package to $pkg" >&2; exit 2; }

if [ ! -d "$proj/ProjectSettings" ]; then
  "$unity" -batchmode -nographics -quit -createProject "$(topath "$proj")" -logFile "$(topath "$work/create.log")"
fi
mkdir -p "$proj/Assets/TurboHttpSmoke"
rsync -a --delete --exclude='*.meta' --exclude='Smoke.unity' "$repo/Tools~/unity/player-smoke/" "$proj/Assets/TurboHttpSmoke/"
python3 - "$proj/Packages/manifest.json" "$(topath "$pkg")" <<'PY'
import json, sys
path, pkg = sys.argv[1], sys.argv[2]
m = json.load(open(path))
m.setdefault("dependencies", {})["com.turbohttp.complete"] = "file:" + pkg
json.dump(m, open(path, "w"), indent=2)
PY
# The opt-in defines on or off for the target's group, before Unity starts, so the Editor and the
# player compile see the same defines (TurboHttpPlayerBuild re-checks TURBOHTTP_BOUNCYCASTLE).
python3 - "$proj/ProjectSettings/ProjectSettings.asset" "$group" \
  "TURBOHTTP_BOUNCYCASTLE=$bouncycastle" "TURBOHTTP_BACKGROUND_NETWORKING=$background" <<'PY'
import re, sys
path, group = sys.argv[1], sys.argv[2]
wanted = dict(arg.split("=") for arg in sys.argv[3:])
text = open(path, encoding="utf-8").read()
m = re.search(r"^  scriptingDefineSymbols:(.*)\n((?:    .*\n)*)", text, re.M)
if not m:
    print("build-player: no scriptingDefineSymbols in " + path, file=sys.stderr)
    sys.exit(2)
entries = [] if m.group(1).strip() == "{}" else m.group(2).splitlines()
for i, line in enumerate(entries):
    key, _, value = line.strip().partition(":")
    if key == group:
        symbols = [s for s in value.strip().split(";") if s and s not in wanted]
        symbols += [d for d, on in wanted.items() if on == "1"]
        entries[i] = "    " + group + ": " + ";".join(symbols)
        break
else:
    on = [d for d, v in wanted.items() if v == "1"]
    if on:
        entries.append("    " + group + ": " + ";".join(on))
entries = [e for e in entries if e.strip().partition(":")[2].strip()]
block = "  scriptingDefineSymbols:" + ("\n" if entries else " {}\n") + "".join(e + "\n" for e in entries)
new = text[:m.start()] + block + text[m.end():]
if new != text:
    open(path, "w", encoding="utf-8").write(new)
PY

# Stale linker output from an earlier build of the other variant must not satisfy the checks.
stripped="$proj/Library/Bee/artifacts/$stripped/ManagedStripped"
rm -rf "$stripped"

log="$out/build.log"
echo "build-player: $variant -> $out"
# WSL interop occasionally fails to start a Windows process (no log written): retry those starts.
code=0
for _ in 1 2 3; do
  set +e
  "$unity" -batchmode -nographics -projectPath "$(topath "$proj")" -buildTarget "$buildtarget" \
    -executeMethod TurboHttpPlayerBuild.Build -smokeTarget "$target" -smokeOutput "$(topath "$out/$product")" \
    -smokeBouncyCastle "$bouncycastle" -logFile "$(topath "$log")"
  code=$?
  set -e
  [ -f "$log" ] && break
  sleep 5
done
if [ ! -f "$log" ]; then
  echo "build-player: Unity wrote no log (exit $code): it did not start (WSL interop failure?); rerun" >&2
  exit 2
fi
grep -E "TURBOHTTP_BUILD|error CS[0-9]+" "$log" | head -20 || true

# Assemblies that reached the player: the stripped linker output for IL2CPP, <name>_Data/Managed for Mono.
case "$target" in
  win64-mono) managed="$out/TurboHttpSmoke_Data/Managed" ;;
  *) managed="$stripped" ;;
esac

toolchain=0
if [ "$code" != 0 ] && [ "$target" = win64-il2cpp ] &&
   grep -qiE "Windows 10 SDK|Visual Studio installation|C\+\+ code builder is unable|Unable to detect any compatible Visual Studio|Unable to find .*(SDK|toolchain)" "$log"; then
  toolchain=1
fi

fail=0
if [ -z "$managed" ] || [ ! -d "$managed" ]; then
  echo "build-player: no assembly output found (Unity exit $code); see $log"
  [ "$toolchain" = 1 ] && { echo "TOOLCHAIN_UNAVAILABLE: the IL2CPP native toolchain (Windows 10 SDK + MSVC) is missing; see $log"; exit 3; }
  exit 1
fi
echo "build-player: assemblies in $managed:"
for f in "$managed"/TurboHTTP*.dll; do [ -f "$f" ] && echo "  ${f##*/}"; done
has() { [ -f "$managed/$1.dll" ]; }
for asm in TurboHTTP.Core TurboHTTP.Transport TurboHTTP.WebSocket.Transport TurboHTTP.Complete \
           TurboHTTP.JSON TurboHTTP.Retry TurboHTTP.Auth; do
  has "$asm" || { echo "build-player: FAIL $asm.dll missing"; fail=1; }
done
if [ "$bouncycastle" = 1 ]; then
  for asm in TurboHTTP.Transport.BouncyCastle TurboHTTP.SecureProtocol; do
    has "$asm" || { echo "build-player: FAIL $asm.dll missing with TURBOHTTP_BOUNCYCASTLE"; fail=1; }
  done
else
  has TurboHTTP.Transport.BouncyCastle && { echo "build-player: FAIL TurboHTTP.Transport.BouncyCastle.dll present without TURBOHTTP_BOUNCYCASTLE"; fail=1; }
  if has TurboHTTP.SecureProtocol; then
    if [ "$target" = win64-mono ]; then
      echo "build-player: note TurboHTTP.SecureProtocol.dll present (Mono, stripping Disabled: every compiled assembly is copied)"
    else
      echo "build-player: FAIL TurboHTTP.SecureProtocol.dll survived Stripping High without TURBOHTTP_BOUNCYCASTLE"; fail=1
    fi
  fi
fi
if [ -f "$out/TurboHTTP.link.xml" ]; then
  echo "build-player: UnityLinker received $out/TurboHTTP.link.xml (assemblies: $(grep -oE '<assembly fullname="[^"]+"' "$out/TurboHTTP.link.xml" | sed 's/.*="//;s/"$//' | sort -u | tr '\n' ' '))"
elif [ "$target" != win64-mono ]; then
  echo "build-player: FAIL no TurboHTTP.link.xml from TurboHttpLinkerProcessor"; fail=1
fi

if [ "$toolchain" = 1 ]; then
  echo "TOOLCHAIN_UNAVAILABLE: linker output checked above (fail=$fail); the native IL2CPP compile needs the Windows 10 SDK + MSVC; see $log"
  exit 3
fi
if [ "$code" != 0 ]; then
  echo "build-player: build failed (Unity exit $code); see $log"
  exit 1
fi

if [ "$target" = android-il2cpp ] && [ "$background" = 1 ]; then
  # .dex holds the plugin and WorkManager classes; the binary manifest stores strings as UTF-16LE.
  python3 -I - "$out/$product" <<'PY' || fail=1
import sys, zipfile
apk = zipfile.ZipFile(sys.argv[1])
dex = b"".join(apk.read(n) for n in apk.namelist() if n.endswith(".dex"))
manifest = apk.read("AndroidManifest.xml")
checks = {
    "plugin class com.turbohttp.background.TurboHttpBackgroundWorker": b"Lcom/turbohttp/background/TurboHttpBackgroundWorker;" in dex,
    "WorkManager classes (androidx.work)": b"Landroidx/work/" in dex,
    "WorkManager manifest entries merged": "androidx.work".encode("utf-16-le") in manifest,
}
for name, ok in checks.items():
    print("build-player: " + ("ok " if ok else "FAIL ") + name + " in the APK")
sys.exit(0 if all(checks.values()) else 1)
PY
fi

if [ "$target" != android-il2cpp ]; then
  plog="$out/player.log"
  pcode=0
  for _ in 1 2 3; do
    set +e
    (cd "$out" && timeout 300 "./$product" -batchmode -nographics -logFile "$(topath "$plog")" >/dev/null 2>&1)
    pcode=$?
    set -e
    [ -f "$plog" ] && break
    sleep 5
  done
  marker="$(grep -m1 -E "TURBOHTTP_SMOKE (PASS|FAIL)" "$plog" 2>/dev/null || true)"
  echo "build-player: player exit $pcode: ${marker:-no TURBOHTTP_SMOKE marker}"
  case "$marker" in
    *"TURBOHTTP_SMOKE PASS"*) ;;
    *) fail=1 ;;
  esac
  if [ "$bouncycastle" = 1 ]; then
    case "$marker" in *"bc=present"*) ;; *) echo "build-player: FAIL BouncyCastle provider not found in the player"; fail=1 ;; esac
  fi
fi

if [ "$fail" != 0 ]; then
  echo "build-player: FAIL ($variant); see $out"
  exit 1
fi
echo "build-player: PASS ($variant); see $out"
