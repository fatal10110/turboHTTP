#!/usr/bin/env bash
# Builds a TurboHTTP player smoke from a throwaway Unity project that consumes a synced copy of
# this package, inspects which TurboHTTP assemblies survived into the build, and runs the player and
# checks its marker lines (Windows targets always; Android with --run, on an adb device or emulator).
#
#   UNITY_PATH=<Unity 2021.3 editor binary> \
#     Tools~/unity/build-player.sh --target win64-mono|win64-il2cpp|android-il2cpp [--bouncycastle] [--background] [--network]
#     Tools~/unity/build-player.sh --target android-il2cpp [--bouncycastle] --run [--serial <adb serial>] [--arch arm64|x86_64] [--network]
#     Tools~/unity/build-player.sh --target android-il2cpp [--bouncycastle] --apk <built APK> [--serial <adb serial>] [--network]
#
# The project gets Tools~/unity/player-smoke/ as Assets/TurboHttpSmoke/ with no .asmdef:
# TurboHttpPlayerSmoke.cs runs the rows of TurboHttpSmokeRows.cs WITHOUT any EnsureRegistered() call
# (#1 AC1, #7 AC1): transport registration, the link.xml reflection targets (#8), WebSocket JSON (#192),
# OAuth refresh (#67) and GetJsonCoroutine<T> (#95) offline, and with --network one HTTPS request per
# TLS provider (#141, #258 AC2). It logs one "TURBOHTTP_SMOKE ROW <id> PASS|FAIL|SKIP" line per row and
# one aggregate "TURBOHTTP_SMOKE PASS|FAIL" line, which this script requires to be PASS;
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
# with -batchmode -nographics. Android --run (#325) checks that one device is on adb before the build
# (--serial picks one of several, an emulator too; --arch defaults to the device's ABI, else arm64), then
# installs the APK (adb install -r), force-stops it, wakes the screen (fails on a lock screen it cannot
# dismiss: a OnePlus freezes an app behind it), launches UnityPlayerActivity and polls logcat for the
# aggregate marker (TURBOHTTP_SMOKE_WAIT_S, default 300 s; ends early when the player is gone without one).
# Logcat is kept as <build dir>/logcat.txt. --apk runs an APK built earlier instead of building. Each adb
# call is bounded and retried (an interop outage from WSL as in wsl-interop.sh). --network asks the
# player for the TLS rows (Windows: -turbohttpSmokeNetwork; Android: intent extra
# turbohttp_smoke_network=1) and requires their PASS rows; without it they are SKIP and the run stays
# offline. CI never builds players (no Unity license, #188, #100): this script, run in a host editor, is
# the player-build gate (testing.md, "Host-editor gates").
#
# Exit codes: 0 pass, 1 build, check or smoke failure (FAIL row, or no marker), 2 usage/setup error (no
# device, lock screen, install or launch failed), 3 Unity, a build module or the
# native toolchain is not installed (prints UNITY_UNAVAILABLE or TOOLCHAIN_UNAVAILABLE), 5 WSL interop unavailable
# (a Windows editor driven from WSL: cmd.exe or Unity.exe did not start through about two minutes of retries,
# wsl-interop.sh, #355). Env:
# UNITY_SMOKE_DIR (default <cache>/player-smoke; holds testproj/, package/ and builds/<run>/ with the
# logs, the TurboHTTP.link.xml UnityLinker received and the build), where <cache> is ~/.cache/turbohttp,
# or %LOCALAPPDATA%\turbohttp for a Windows editor driven from WSL. One Unity process at a time.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=Tools~/unity/wsl-interop.sh
. "$repo/Tools~/unity/wsl-interop.sh"
target=""; bouncycastle=0; background=0; run=0; network=0; serial=""; arch=""; apk=""
usage="usage: $0 --target win64-mono|win64-il2cpp|android-il2cpp [--bouncycastle] [--background] [--network]
       [--run] [--serial <adb serial>] [--arch arm64|x86_64] [--apk <built APK>]   (the last four: android-il2cpp)"
while [ $# -gt 0 ]; do
  case "$1" in
    --target|--serial|--arch|--apk)
      if [ $# -lt 2 ] || [ -z "$2" ]; then echo "$usage" >&2; exit 2; fi
      case "$1" in
        --target) target="$2" ;;
        --serial) serial="$2"; run=1 ;;
        --arch) arch="$2" ;;
        --apk) apk="$2"; run=1 ;;
      esac
      shift 2 ;;
    --bouncycastle) bouncycastle=1; shift ;;
    --background) background=1; shift ;;
    --run) run=1; shift ;;
    --network) network=1; shift ;;
    *) echo "$usage" >&2; exit 2 ;;
  esac
done
# stripped: the UnityLinker output folder (under Library/Bee/artifacts) of the target's build program.
case "$target" in
  win64-mono|win64-il2cpp) group=Standalone; buildtarget=Win64; product=TurboHttpSmoke.exe; stripped=WinPlayerBuildProgram ;;
  android-il2cpp) group=Android; buildtarget=Android; product=TurboHttpSmoke.apk; stripped=Android ;;
  *) echo "build-player: --target win64-mono|win64-il2cpp|android-il2cpp required" >&2; exit 2 ;;
esac
if [ "$target" != android-il2cpp ] && { [ "$run" = 1 ] || [ -n "$arch" ]; }; then
  echo "build-player: --run, --serial, --arch and --apk apply to --target android-il2cpp (Windows players always run)" >&2; exit 2
fi
case "$arch" in ""|arm64|x86_64) ;; *) echo "build-player: --arch arm64|x86_64" >&2; exit 2 ;; esac
if [ "$target" = android-il2cpp ] && [ "$network" = 1 ] && [ "$run" = 0 ]; then
  echo "build-player: --network on android-il2cpp needs --run (the rows run on the device)" >&2; exit 2
fi
if [ -n "$apk" ] && [ ! -s "$apk" ]; then echo "build-player: --apk $apk: no such APK" >&2; exit 2; fi

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
if [ -z "$apk" ] && [ ! -d "$module" ]; then
  echo "UNITY_UNAVAILABLE: build module for $target not installed ($module)"
  exit 3
fi

win=0
case "$unity" in
  *.exe|*.EXE)
    if command -v wslpath >/dev/null 2>&1; then win=1; fi ;;
esac
topath() { if [ "$win" = 1 ]; then wslpath -m "$1"; else printf '%s\n' "$1"; fi; }

fail=0
# Prints the player's row lines and aggregate marker from log $1 and sets fail=1 unless the aggregate is
# PASS, with --bouncycastle reports bc=present, and with --network has a PASS row per TLS provider built in.
check_smoke_log() {
  local plog="$1" marker row rows=tls-sslstream
  grep -E "TURBOHTTP_SMOKE ROW " "$plog" 2>/dev/null | sed 's/.*TURBOHTTP_SMOKE ROW/  TURBOHTTP_SMOKE ROW/' || true
  marker="$(grep -m1 -E "TURBOHTTP_SMOKE (PASS|FAIL)" "$plog" 2>/dev/null | sed 's/.*TURBOHTTP_SMOKE/TURBOHTTP_SMOKE/' || true)"
  echo "build-player: ${marker:-no TURBOHTTP_SMOKE marker}"
  case "$marker" in
    "TURBOHTTP_SMOKE PASS"*) ;;
    *) fail=1 ;;
  esac
  if [ "$bouncycastle" = 1 ]; then
    rows="$rows tls-bouncycastle"
    case "$marker" in *"bc=present"*) ;; *) echo "build-player: FAIL BouncyCastle provider not found in the player"; fail=1 ;; esac
  fi
  if [ "$network" = 1 ]; then
    for row in $rows; do
      grep -qE "TURBOHTTP_SMOKE ROW $row PASS" "$plog" 2>/dev/null ||
        { echo "build-player: FAIL --network: no 'TURBOHTTP_SMOKE ROW $row PASS'"; fail=1; }
    done
  fi
}

if [ "$run" = 1 ]; then
  # adb from the editor's Android SDK, else PATH (as run-device-tests.sh).
  adb_exe="$engines/AndroidPlayer/SDK/platform-tools/adb.exe"
  [ -x "$adb_exe" ] || adb_exe="$engines/AndroidPlayer/SDK/platform-tools/adb"
  [ -x "$adb_exe" ] || adb_exe="$(command -v adb || true)"
  [ -n "$adb_exe" ] || { echo "build-player: no adb (editor Android SDK under $engines, or PATH)" >&2; exit 2; }
  adb_target=()
  if [ -n "$serial" ]; then adb_target=(-s "$serial"); fi
  adb_timeout=()
  if command -v timeout >/dev/null 2>&1; then adb_timeout=(timeout 120); fi
  # As run-device-tests.sh: a failed call is tried 5 times, wsl_poll_s apart, then returns 1; from WSL a failure
  # while cmd.exe does not answer either is an interop outage, retried with the back-off, then wsl_interop_exit.
  adbr() {
    local out tries=0 outages=0
    while :; do
      if out="$(${adb_timeout[@]+"${adb_timeout[@]}"} "$adb_exe" ${adb_target[@]+"${adb_target[@]}"} "$@" 2>/dev/null)"; then
        printf '%s' "$out" | tr -d '\r'
        return 0
      fi
      if [ "$win" = 1 ] && ! wsl_interop_up; then
        outages=$((outages + 1))
        [ "$outages" -lt "$wsl_tries" ] || return "$wsl_interop_exit"
        wsl_backoff "$outages"
        continue
      fi
      tries=$((tries + 1))
      [ "$tries" -lt 5 ] || return 1
      sleep "$wsl_poll_s"
    done
  }
  # Exits 5 for an interop outage (status $1), else 2, naming the adb step $2.
  adb_failed() {
    wsl_exit_if_interop "$1" build-player "adb.exe did not start ($2)"
    echo "build-player: adb $2 failed on ${serial:-the adb device}" >&2
    exit 2
  }
  rc=0
  state="$(adbr get-state)" || rc=$?
  if [ "$state" != device ]; then
    wsl_exit_if_interop "$rc" build-player "adb.exe did not start (adb get-state)"
    echo "build-player: no Android device ${serial:+$serial }ready on adb (get-state: ${state:-no answer}); with several, pass --serial" >&2
    exit 2
  fi
  if [ -z "$arch" ] && [ -z "$apk" ]; then
    abi="$(adbr shell getprop ro.product.cpu.abi)" || adb_failed "$?" "getprop ro.product.cpu.abi"
    abi="${abi%%[[:space:]]*}"
    case "$abi" in
      arm64-v8a) arch=arm64 ;;
      x86_64) arch=x86_64 ;;
      *) echo "build-player: device ABI '$abi' is neither arm64-v8a nor x86_64; pass --arch" >&2; exit 2 ;;
    esac
    echo "build-player: device ABI $abi: building --arch $arch"
  fi

  pkg_id=com.turbohttp.playersmoke
  # y while the smoke's process exists, n when it does not; fails when adb fails (ps as in run-device-tests.sh).
  smoke_running() {
    local ps_out
    ps_out="$(adbr shell "ps -A 2>/dev/null; ps 2>/dev/null")" || return 1
    if printf '%s\n' "$ps_out" | grep -q "[[:space:]]$pkg_id\$"; then echo y; else echo n; fi
  }
  # True while the lock screen shows (field names differ across Android versions); false when unknown.
  keyguard_showing() {
    local windows
    windows="$(adbr shell dumpsys window)" || return 1
    printf '%s\n' "$windows" | grep -qE '(mShowingLockscreen|mDreamingLockscreen|isStatusBarKeyguard|mKeyguardShowing)=true'
  }
  # Installs APK $1, launches it, waits for the aggregate marker, keeps logcat in $2 and checks it (fail=1).
  android_run() {
    local apk_file="$1" logcat="$2" rc out deadline gone=0 extra=()
    adbr install -r "$(topath "$apk_file")" >/dev/null || adb_failed "$?" "install -r $apk_file"
    adbr shell am force-stop "$pkg_id" >/dev/null || adb_failed "$?" "am force-stop"
    adbr shell input keyevent KEYCODE_WAKEUP >/dev/null || adb_failed "$?" "input keyevent KEYCODE_WAKEUP"
    adbr shell wm dismiss-keyguard >/dev/null || true
    if keyguard_showing; then
      echo "build-player: ${serial:-the device} shows its lock screen: unlock it and keep the screen on" \
        "(Developer options > Stay awake); an app behind the lock screen may be frozen (#325)" >&2
      exit 2
    fi
    adbr logcat -c >/dev/null || adb_failed "$?" "logcat -c"
    if [ "$network" = 1 ]; then extra=(--es turbohttp_smoke_network 1); fi
    out="$(adbr shell am start -W -n "$pkg_id/com.unity3d.player.UnityPlayerActivity" ${extra[@]+"${extra[@]}"})" ||
      adb_failed "$?" "am start"
    case "$out" in *Error*) echo "build-player: am start failed: $out" >&2; exit 2 ;; esac
    echo "build-player: installed and launched $pkg_id on ${serial:-the adb device}; waiting for TURBOHTTP_SMOKE"
    deadline=$(( $(date +%s) + ${TURBOHTTP_SMOKE_WAIT_S:-300} ))
    while :; do
      rc=0
      adbr logcat -d -s Unity:V AndroidRuntime:E DEBUG:V > "$logcat" || rc=$?
      wsl_exit_if_interop "$rc" build-player "adb.exe did not start (logcat)"
      grep -qE "TURBOHTTP_SMOKE (PASS|FAIL)" "$logcat" && break
      # Gone on two adb checks in a row without a marker: crashed or quit early. A failed check does not count.
      case "$(smoke_running || true)" in
        n) gone=$((gone + 1)) ;;
        y) gone=0 ;;
      esac
      if [ "$gone" -ge 2 ]; then
        adbr logcat -d -s Unity:V AndroidRuntime:E DEBUG:V > "$logcat" || true
        grep -qE "TURBOHTTP_SMOKE (PASS|FAIL)" "$logcat" || echo "build-player: the player exited without a marker; see $logcat"
        break
      fi
      if [ "$(date +%s)" -ge "$deadline" ]; then
        echo "build-player: no marker within ${TURBOHTTP_SMOKE_WAIT_S:-300} s; see $logcat"
        break
      fi
      sleep "${TURBOHTTP_SMOKE_POLL_S:-3}"
    done
    adbr shell am force-stop "$pkg_id" >/dev/null || true
    check_smoke_log "$logcat"
  }

  if [ -n "$apk" ]; then
    android_run "$apk" "$(dirname "$apk")/logcat-$(date +%Y%m%d-%H%M%S).txt"
    if [ "$fail" != 0 ]; then echo "build-player: FAIL (run of $apk)"; exit 1; fi
    echo "build-player: PASS (run of $apk)"
    exit 0
  fi
fi

if [ -n "${UNITY_SMOKE_DIR:-}" ]; then
  cache="$(dirname "$UNITY_SMOKE_DIR")"
elif [ "$win" = 1 ]; then
  wsl_resolve_localappdata build-player
  cache="$wsl_localappdata/turbohttp"
else
  cache="${XDG_CACHE_HOME:-$HOME/.cache}/turbohttp"
fi
work="${UNITY_SMOKE_DIR:-$cache/player-smoke}"
proj="$work/testproj"; pkg="$work/package"
variant="$target"
[ "$arch" = x86_64 ] && variant="$target-x86_64"
if [ "$bouncycastle" = 1 ]; then variant="$variant-bc"; else variant="$variant-nobc"; fi
[ "$background" = 1 ] && variant="$variant-bg"
out="$work/builds/$variant-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$out"

# /mnt/c (drvfs) under memory pressure sometimes fails a readdir with ENOMEM: retry the sync.
synced=0
for _ in 1 2 3; do
  if rsync -a --delete --exclude='.git/' --exclude='.build/' --exclude='.tools/' --exclude='Library/' \
       --exclude='Temp/' --exclude='Logs/' --exclude='.DS_Store' --exclude='.claude/' --exclude='Tools~/' \
       --exclude='Development~/' "$repo/" "$pkg/"; then
    synced=1; break
  fi
  sleep 5
done
[ "$synced" = 1 ] || { echo "build-player: cannot sync the package to $pkg" >&2; exit 2; }

if [ ! -d "$proj/ProjectSettings" ]; then
  wsl_unity_launch build-player "$work/create.log" \
    "$unity" -batchmode -nographics -quit -createProject "$(topath "$proj")" -logFile "$(topath "$work/create.log")"
  [ "$unity_code" = 0 ] || { echo "build-player: creating $proj failed (Unity exit $unity_code); see $work/create.log" >&2; exit 2; }
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
# A Unity.exe that did not start from WSL (no log written) is retried, then exit 5 (wsl-interop.sh, #355).
wsl_unity_launch build-player "$log" \
  "$unity" -batchmode -nographics -projectPath "$(topath "$proj")" -buildTarget "$buildtarget" \
  -executeMethod TurboHttpPlayerBuild.Build -smokeTarget "$target" -smokeOutput "$(topath "$out/$product")" \
  -smokeBouncyCastle "$bouncycastle" -smokeArch "${arch:-arm64}" -logFile "$(topath "$log")"
code="$unity_code"
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
  # A linker-output failure above is a real regression, not a missing toolchain: report it first.
  if [ "$fail" != 0 ]; then
    echo "build-player: FAIL ($target): linker output check failed before the missing native toolchain; see $log"
    exit 1
  fi
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
  player_args=(-batchmode -nographics)
  if [ "$network" = 1 ]; then player_args+=(-turbohttpSmokeNetwork); fi
  for _ in 1 2 3; do
    set +e
    (cd "$out" && timeout 300 "./$product" "${player_args[@]}" -logFile "$(topath "$plog")" >/dev/null 2>&1)
    pcode=$?
    set -e
    [ -f "$plog" ] && break
    sleep 5
  done
  echo "build-player: player exit $pcode"
  check_smoke_log "$plog"
elif [ "$run" = 1 ]; then
  android_run "$out/$product" "$out/logcat.txt"
fi

if [ "$fail" != 0 ]; then
  echo "build-player: FAIL ($variant); see $out"
  exit 1
fi
echo "build-player: PASS ($variant); see $out"
