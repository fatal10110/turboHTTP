#!/usr/bin/env bash
# Runs TurboHTTP's PlayMode suite on Android emulators across API levels: boots one x86_64 AVD per level,
# builds the IL2CPP x86_64 test player once (run-device-tests.sh --serial) and installs that APK on the
# other emulators (--skip-build). Emulators complement the phone run (#99); they are not ARM, not a real
# radio and not real vendor TLS/network stacks, so device rows stay needs-device-validation.
#
#   UNITY_PATH=<Unity 2021.3 editor binary> \
#     Tools~/unity/run-emulator-tests.sh [--api 23,26,29,33,36] [--jobs 2] [--stripping Low|High]
#       [--filter <regex>] [--category <name>] [--integration]
#
# Needs an Android SDK with the emulator and system-images;android-<N>;default;x86_64 for each level
# (TURBOHTTP_EMULATOR_SDK, default <cache>/android-sdk; set it up with the SDK's sdkmanager) and hardware
# acceleration (WHPX on Windows, KVM on Linux: emulator -accel-check). AVDs turbohttp-api<N> are written under
# <cache>/emulators/avd and wiped on every boot. --jobs bounds the emulators running at once (3 GB RAM each).
# From WSL with a Windows editor the emulators are Windows processes: each level's emulator.exe/qemu is found
# by its AVD name and stopped with taskkill when its console does not stop it, after the level and on exit
# (emulator-windows.sh, #353). Before a boot, a leftover turbohttp-api<N> from an earlier run is stopped; another
# emulator holding the console port fails the level (exit 2) naming its PID.
# Exit: the highest run-device-tests.sh exit code over the levels (0 pass, 1 failures, 2 setup, 4 skip gate,
# 5 WSL interop unavailable), 3 when Unity is unavailable. Exit 5 also when, from WSL, the %LOCALAPPDATA% lookup or
# a level's boot failed while Windows programs did not start (wsl-interop.sh, #355): an outage, not a result.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=Tools~/unity/emulator-windows.sh
. "$repo/Tools~/unity/emulator-windows.sh"
# shellcheck source=Tools~/unity/wsl-interop.sh
. "$repo/Tools~/unity/wsl-interop.sh"
apis="23,26,29,33,36"; jobs=2; pass=()
while [ $# -gt 0 ]; do
  case "$1" in
    --api) apis="$2"; shift 2 ;;
    --jobs) jobs="$2"; shift 2 ;;
    --stripping|--filter|--category) pass+=("$1" "$2"); shift 2 ;;
    --integration) pass+=("$1"); shift ;;
    *) echo "usage: $0 [--api 23,26,...] [--jobs N] [--stripping Low|High] [--filter regex] [--category name] [--integration]" >&2; exit 2 ;;
  esac
done
case "$jobs" in ''|*[!0-9]*|0) echo "run-emulator-tests: --jobs takes a positive number" >&2; exit 2 ;; esac
IFS=, read -r -a levels <<< "$apis"
for api in "${levels[@]}"; do
  case "$api" in ''|*[!0-9]*) echo "run-emulator-tests: --api takes comma-separated API levels" >&2; exit 2 ;; esac
done

unity="${UNITY_PATH:-}"
if [ -z "$unity" ] || [ ! -x "$unity" ]; then
  echo "UNITY_UNAVAILABLE: set UNITY_PATH to a Unity $(cat "$repo/Tools~/ci/unity-version") editor binary"
  exit 3
fi

# Same cache and adb as run-device-tests.sh: a Windows Unity.exe from WSL drives Windows tools.
win=0
case "$unity" in *.exe|*.EXE) if command -v wslpath >/dev/null 2>&1; then win=1; fi ;; esac
topath() { if [ "$win" = 1 ]; then wslpath -w "$1"; else printf '%s\n' "$1"; fi; }
if [ "$win" = 1 ]; then
  wsl_resolve_localappdata run-emulator-tests
  cache="$wsl_localappdata/turbohttp"
else
  cache="${XDG_CACHE_HOME:-$HOME/.cache}/turbohttp"
fi
sdk="${TURBOHTTP_EMULATOR_SDK:-$cache/android-sdk}"
emu="$sdk/emulator/emulator.exe"
[ -x "$emu" ] || emu="$sdk/emulator/emulator"
[ -x "$emu" ] || { echo "run-emulator-tests: no emulator under $sdk (TURBOHTTP_EMULATOR_SDK)" >&2; exit 2; }
# The emulator accepts an SDK root only with a platform-tools/ directory; adb stays the editor's (one
# adb version per daemon), so an empty one is enough.
mkdir -p "$sdk/platform-tools"
engines="$(dirname "$unity")/Data/PlaybackEngines"
case "$(uname -s)" in Darwin) engines="$(cd "$(dirname "$unity")/../../.." && pwd)/PlaybackEngines" ;; esac
adb_exe="$engines/AndroidPlayer/SDK/platform-tools/adb.exe"
[ -x "$adb_exe" ] || adb_exe="$engines/AndroidPlayer/SDK/platform-tools/adb"
[ -x "$adb_exe" ] || adb_exe="$(command -v adb || true)"
[ -n "$adb_exe" ] || { echo "run-emulator-tests: no adb (editor Android SDK under $engines, or PATH)" >&2; exit 2; }
# Every adb call is bounded (where timeout exists): a frozen emulator stops answering its console and adbd.
adb_timeout=()
if command -v timeout >/dev/null 2>&1; then adb_timeout=(timeout 30); fi
adbs() { local serial="$1"; shift; ${adb_timeout[@]+"${adb_timeout[@]}"} "$adb_exe" -s "$serial" "$@" 2>/dev/null | tr -d '\r'; }

avd_home="$cache/emulators/avd"
ts="$(date +%Y%m%d-%H%M%S)"
out="$cache/emulators/run-$ts"
mkdir -p "$avd_home" "$out"
for api in "${levels[@]}"; do
  [ -d "$sdk/system-images/android-$api/default/x86_64" ] \
    || { echo "run-emulator-tests: missing system-images;android-$api;default;x86_64 in $sdk" >&2; exit 2; }
  name="turbohttp-api$api"
  mkdir -p "$avd_home/$name.avd"
  printf 'avd.ini.encoding=UTF-8\npath=%s\npath.rel=avd/%s.avd\ntarget=android-%s\n' \
    "$(topath "$avd_home/$name.avd")" "$name" "$api" > "$avd_home/$name.ini"
  cat > "$avd_home/$name.avd/config.ini" <<INI
AvdId=$name
avd.ini.displayname=$name
avd.ini.encoding=UTF-8
abi.type=x86_64
hw.cpu.arch=x86_64
hw.cpu.ncore=4
hw.ramSize=3072
disk.dataPartition.size=6G
image.sysdir.1=system-images/android-$api/default/x86_64/
tag.id=default
target=android-$api
hw.gpu.enabled=yes
hw.gpu.mode=swiftshader_indirect
hw.keyboard=yes
hw.lcd.width=1080
hw.lcd.height=1920
hw.lcd.density=420
PlayStore.enabled=false
INI
done

ANDROID_SDK_ROOT="$(topath "$sdk")"
ANDROID_AVD_HOME="$(topath "$avd_home")"
export ANDROID_SDK_ROOT ANDROID_AVD_HOME
export WSLENV="${WSLENV:+$WSLENV:}ANDROID_SDK_ROOT:ANDROID_AVD_HOME"

started=(); started_avds=(); lane_pids=()
# Interrupted or failed: stop the lanes first, then their emulators. Lanes run as their own process groups
# (set -m below): without job control bash starts background jobs with SIGINT ignored, so a Ctrl-C would
# leave run-device-tests.sh and Unity running against emulators that are gone. The group of a lane also
# holds its emulator process, which the TERM stops when the console no longer answers.
# shellcheck disable=SC2317,SC2329  # invoked by the traps
cleanup() {
  trap - INT TERM
  for p in ${lane_pids[@]+"${lane_pids[@]}"}; do kill -TERM -- "-$p" 2>/dev/null || true; done
  # Console kill only where this run launched an emulator: a serial whose pre-boot check refused a foreign
  # emulator on its port must not reach that emulator's console (#353).
  for s in ${started[@]+"${started[@]}"}; do
    [ -f "$out/$s.launched" ] || continue
    adbs "$s" emu kill >/dev/null || true
  done
  # From WSL the TERM reaches only the interop pid of emulator.exe: find the Windows processes by AVD name.
  if [ "$win" = 1 ]; then
    for a in ${started_avds[@]+"${started_avds[@]}"}; do win_stop_avd "$a" 5 || true; done
  fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# From WSL: returns 5 (with the outage message) when Windows programs do not start now, else 1. A boot that failed
# during a WSL interop outage (emulator.exe or adb.exe did not start) is reported as exit 5, not a setup error (#355).
boot_failed() {
  if [ "$win" = 1 ] && ! wsl_interop_up; then
    echo "run-emulator-tests: WSL interop unavailable: API $1 boot failed while cmd.exe does not answer (#355)" >&2
    return "$wsl_interop_exit"
  fi
  return 1
}

# Boots the AVD for one level on its own console port and waits for boot_completed (5 minutes).
# Returns 1 when it fails, 5 when it failed during a WSL interop outage.
boot() {
  local api="$1" port="$2" serial="emulator-$2"
  if [ "$win" = 1 ]; then win_free_avd "turbohttp-api$api" "$port" || return 1; fi
  # From here on the serial is ours: the EXIT trap may send it a console kill.
  : > "$out/$serial.launched"
  # Vulkan off: the Unity player picked Vulkan on the headless gfxstream/SwiftShader backend and froze the
  # whole emulator (adbd and console); GLES works.
  "$emu" -avd "turbohttp-api$api" -port "$port" -no-window -no-audio -no-boot-anim -no-snapshot -wipe-data \
    -gpu swiftshader_indirect -feature -Vulkan > "$out/emulator-api$api.log" 2>&1 &
  emu_pid=$!
  local deadline=$(( $(date +%s) + 300 ))
  while [ "$(date +%s)" -lt "$deadline" ]; do
    if ! kill -0 "$emu_pid" 2>/dev/null; then
      echo "run-emulator-tests: the API $api emulator exited; see $out/emulator-api$api.log" >&2
      boot_failed "$api"
      return
    fi
    if [ "$(adbs "$serial" shell getprop sys.boot_completed)" = 1 ]; then
      # Keep the screen on and unlocked: the test player must stay in the foreground.
      adbs "$serial" shell svc power stayon true >/dev/null || true
      adbs "$serial" shell input keyevent 82 >/dev/null || true
      return 0
    fi
    sleep 5
  done
  echo "run-emulator-tests: API $api did not boot in 5 minutes; see $out/emulator-api$api.log" >&2
  boot_failed "$api"
}

# Stops the lane's emulator: the console first, the process when the console does not answer (frozen).
# Does nothing when the lane launched no emulator (the pre-boot check refused): the serial may then be
# another emulator's console, which this script must not stop.
stop_emulator() {
  local serial="$1" avd="$2"
  [ -n "$emu_pid" ] || return 0
  adbs "$serial" emu kill >/dev/null || true
  if [ "$win" = 1 ]; then
    # $emu_pid is the WSL interop pid of emulator.exe: it can be gone while the Windows qemu runs on (#353).
    win_stop_avd "$avd" 15 || true
    [ -z "$emu_pid" ] || kill -KILL "$emu_pid" 2>/dev/null || true
    return 0
  fi
  for _ in $(seq 1 15); do kill -0 "$emu_pid" 2>/dev/null || return 0; sleep 2; done
  kill -KILL "$emu_pid" 2>/dev/null || true
}

# One level: boot, run the suite (build or reuse the APK), stop the emulator, record the exit code.
# run-device-tests.sh ends its wait when the serial leaves adb, so a lane whose emulator died finishes.
lane() {
  local api="$1" port="$2" build="$3" serial="emulator-$2" code=0
  emu_pid=""
  if boot "$api" "$port"; then
    local extra=()
    [ "$build" = 1 ] || extra=(--skip-build)
    "$repo/Tools~/unity/run-device-tests.sh" --arch x86_64 --serial "$serial" ${extra[@]+"${extra[@]}"} ${pass[@]+"${pass[@]}"} \
      > "$out/api$api.log" 2>&1 || code=$?
  else
    code=$?
    [ "$code" = "$wsl_interop_exit" ] || code=2
  fi
  stop_emulator "$serial" "turbohttp-api$api"
  echo "$code" > "$out/api$api.code"
}

echo "run-emulator-tests: API levels ${levels[*]}, $jobs at a time; logs in $out"
# The first level builds the APK; the others start once its run reports the install.
port=5560
first="${levels[0]}"
started+=("emulator-$port"); started_avds+=("turbohttp-api$first")
set -m; lane "$first" "$port" 1 & lane_pids+=("$!"); set +m
until grep -q "installed and launched" "$out/api$first.log" 2>/dev/null || [ -f "$out/api$first.code" ]; do sleep 10; done
if grep -q "installed and launched" "$out/api$first.log" 2>/dev/null; then
  for api in "${levels[@]:1}"; do
    while [ "$(jobs -rp | wc -l)" -ge "$jobs" ]; do sleep 10; done
    port=$(( port + 2 ))
    started+=("emulator-$port"); started_avds+=("turbohttp-api$api")
    set -m; lane "$api" "$port" 0 & lane_pids+=("$!"); set +m
  done
else
  echo "run-emulator-tests: API $first never installed a player (boot or build failed); the other levels are skipped"
fi
wait || true
# Every lane finished and stopped its emulator: nothing is left for the EXIT trap.
lane_pids=(); started=(); started_avds=()

worst=0
for api in "${levels[@]}"; do
  code="$(cat "$out/api$api.code" 2>/dev/null || echo 2)"
  summary="$(grep -E '^run-tests: total=' "$out/api$api.log" 2>/dev/null | tail -1 || true)"
  [ -n "$summary" ] || summary="$(grep -E '^run-device-tests: ' "$out/api$api.log" 2>/dev/null | tail -1 || true)"
  echo "  API $api: exit $code ${summary:-(no log)}"
  if [ "$code" -gt "$worst" ]; then worst="$code"; fi
done
echo "run-emulator-tests: logs and per-level output in $out"
exit "$worst"
