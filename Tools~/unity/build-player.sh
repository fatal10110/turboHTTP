#!/usr/bin/env bash
# Builds a TurboHTTP player smoke from a throwaway Unity project that consumes a synced copy of
# this package, inspects which TurboHTTP assemblies survived into the build, and runs the player and
# checks its marker lines (Windows targets always; Android with --run, on an adb device or emulator).
#
#   UNITY_PATH=<Unity 2021.3 editor binary> \
#     Tools~/unity/build-player.sh --target win64-mono|win64-il2cpp|android-il2cpp [--stripping Disabled|Low|Medium|High]
#       [--bouncycastle] [--background] [--network]
#     Tools~/unity/build-player.sh --target android-il2cpp [--bouncycastle] --run [--serial <adb serial>] [--arch arm64|x86_64] [--network] [--suspend]
#     Tools~/unity/build-player.sh --target android-il2cpp [--bouncycastle] --apk <built APK> [--serial <adb serial>] [--network] [--suspend]
#
# The project gets Tools~/unity/player-smoke/ as Assets/TurboHttpSmoke/; the smoke itself has no .asmdef
# (it compiles into Assembly-CSharp) and only H2Rows/ is its own assembly. TurboHttpPlayerSmoke.cs runs the rows of TurboHttpSmokeRows.cs WITHOUT any EnsureRegistered() call
# (#1 AC1, #7 AC1): transport registration, the link.xml reflection targets (#8), WebSocket JSON (#192),
# OAuth refresh (#67) and GetJsonCoroutine<T> (#95) offline, and with --network one HTTPS request per
# TLS provider (#141, #258 AC2) and the HTTP/2-over-TLS rows (#1210, H2Rows/: its own assembly). It logs one "TURBOHTTP_SMOKE ROW <id> PASS|FAIL|SKIP" line per row and
# one aggregate "TURBOHTTP_SMOKE PASS|FAIL" line, which this script requires to be PASS;
# Editor/TurboHttpPlayerBuild.cs sets the backend and the Managed Stripping Level (--stripping; default
# High for IL2CPP targets and Unity's default Disabled for Mono; IL2CPP takes Low|Medium|High) and calls
# BuildPipeline.BuildPlayer. TURBOHTTP_BOUNCYCASTLE is set for the target's group only
# with --bouncycastle and removed otherwise (#196, #258); TURBOHTTP_BACKGROUND_NETWORKING likewise with
# --background, which on Android also checks that the TurboHttpBackground.androidlib classes and the
# WorkManager dependency reached the APK without any Gradle template edit (#89).
#
# Checks: TurboHTTP.Core/Transport/WebSocket.Transport/Unity.WebSocket/Complete/JSON/Retry/Auth are in every
# build (Unity.WebSocket although the smoke never references it: its AlwaysLinkAssembly root, #1280).
# Without --bouncycastle, TurboHTTP.Transport.BouncyCastle and TurboHTTP.SecureProtocol are absent from
# every build at every stripping level (IL2CPP: the linker output, Library/Bee/artifacts/*/ManagedStripped;
# Mono: <name>_Data/Managed; #8 AC2, #196 AC1): both asmdefs are constrained by TURBOHTTP_BOUNCYCASTLE, so
# neither is compiled, which Mono at Disabled (it copies every compiled assembly) depends on (#870); with
# it, both are present. Every build that runs UnityLinker (any level but Disabled) must have received TurboHTTP.link.xml. Mono at
# High with --network is the #450 gate: its tls-sslstream row fails when the linker strips what
# SslStream's configuration init creates by reflection. Windows players are started
# with -batchmode -nographics. Android --run (#325) checks that one device is on adb before the build
# (--serial picks one of several, an emulator too; --arch defaults to the device's ABI, else arm64), then
# installs the APK (adb install -r), force-stops it, wakes the screen (fails on a lock screen it cannot
# dismiss: a OnePlus freezes an app behind it), launches UnityPlayerActivity and polls logcat for the
# aggregate marker (TURBOHTTP_SMOKE_WAIT_S, default 300 s; ends early when the player is gone without one).
# Logcat is kept as <build dir>/logcat.txt. --apk runs an APK built earlier instead of building. Each adb
# call is bounded and retried (an interop outage from WSL as in wsl-interop.sh). --network asks the
# player for the TLS rows (Windows: -turbohttpSmokeNetwork; Android: intent extra
# turbohttp_smoke_network=1) and requires their PASS rows; without it they are SKIP and the run stays
# offline. --network also runs the HTTP/2-over-TLS rows (#1210: 2 MiB download, 1 MiB echoed upload, 100
# concurrent streams after a warm-up, a streaming read cancelled mid-body then a second 2 MiB download) per
# TLS provider against an h2-interop file server: TURBOHTTP_H2_ROWS_URL and TURBOHTTP_H2_ROWS_ROOT (what
# Tools~/h2-interop/run.sh --serve prints; ROOT may be unset for a publicly trusted server), else this script
# starts run.sh --serve 0 itself when --bouncycastle is given (needs dotnet; stopped on exit). Without
# --bouncycastle and without TURBOHTTP_H2_ROWS_URL no provider can run them (SslStream rejects the test root),
# so no server is started and the rows are SKIP and not required. The player gets the URL and the root as
# base64 DER (Windows: -turbohttpSmokeH2Url/-turbohttpSmokeH2Root; Android: intent extras
# turbohttp_smoke_h2_url/_root, and adb reverse for a loopback URL). With --bouncycastle the four
# h2-*-bouncycastle rows must PASS over HTTP/2 (protocol=h2); the h2-*-sslstream rows must PASS when no root
# is given and are SKIP with one (SslStream trusts the platform store only). --suspend (android-il2cpp with --run
# or --apk, #1132) asks the player for the ws-suspend-resume row (intent extra turbohttp_smoke_suspend=1): a WebSocket
# with a 1 s ping interval and a 2 s pong timeout to a loopback echo server inside the player. When the row logs
# "TURBOHTTP_SMOKE SUSPEND ws-suspend-resume ready" (a keep-alive ping in flight), the script backgrounds the app
# once (input keyevent KEYCODE_HOME), waits TURBOHTTP_SMOKE_SUSPEND_S (default 10 s, longer than the pong timeout)
# and brings it back with a launcher intent; the row must then PASS (connection still Open, echo round trip).
# Without --suspend the row is SKIP. CI never builds players (no Unity license, #188, #100): this script, run in a host editor, is
# the player-build gate (testing.md, "Host-editor gates"). TURBOHTTP_SMOKE_WAIT_S (default 300 s, 600 s with
# --suspend, 900 s with --network) also bounds the Windows player run.
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
# shellcheck source=Tools~/unity/android-adb.sh
. "$repo/Tools~/unity/android-adb.sh"
target=""; stripping=""; bouncycastle=0; background=0; run=0; network=0; suspend=0; serial=""; arch=""; apk=""
usage="usage: $0 --target win64-mono|win64-il2cpp|android-il2cpp [--stripping Disabled|Low|Medium|High]
       [--bouncycastle] [--background] [--network]
       [--run] [--serial <adb serial>] [--arch arm64|x86_64] [--apk <built APK>] [--suspend]   (the last five: android-il2cpp)"
while [ $# -gt 0 ]; do
  case "$1" in
    --target|--stripping|--serial|--arch|--apk)
      if [ $# -lt 2 ] || [ -z "$2" ]; then echo "$usage" >&2; exit 2; fi
      case "$1" in
        --target) target="$2" ;;
        --stripping) stripping="$2" ;;
        --serial) serial="$2"; run=1 ;;
        --arch) arch="$2" ;;
        --apk) apk="$2"; run=1 ;;
      esac
      shift 2 ;;
    --bouncycastle) bouncycastle=1; shift ;;
    --background) background=1; shift ;;
    --run) run=1; shift ;;
    --network) network=1; shift ;;
    --suspend) suspend=1; shift ;;
    *) echo "$usage" >&2; exit 2 ;;
  esac
done
# stripped: the UnityLinker output folder (under Library/Bee/artifacts) of the target's build program.
case "$target" in
  win64-mono|win64-il2cpp) group=Standalone; buildtarget=Win64; product=TurboHttpSmoke.exe; stripped=WinPlayerBuildProgram ;;
  android-il2cpp) group=Android; buildtarget=Android; product=TurboHttpSmoke.apk; stripped=Android ;;
  *) echo "build-player: --target win64-mono|win64-il2cpp|android-il2cpp required" >&2; exit 2 ;;
esac
# Default levels: IL2CPP High (#8 AC2, #196 AC1), Mono Unity's default Disabled. IL2CPP has no Disabled.
strip_default=High; [ "$target" = win64-mono ] && strip_default=Disabled
case "${stripping:-$strip_default}" in
  Disabled|Low|Medium|High) ;;
  *) echo "build-player: --stripping Disabled|Low|Medium|High" >&2; exit 2 ;;
esac
if [ "$stripping" = Disabled ] && [ "$target" != win64-mono ]; then
  echo "build-player: --stripping Disabled applies to Mono (IL2CPP always runs UnityLinker: Low|Medium|High)" >&2; exit 2
fi
if [ -n "$stripping" ] && [ -n "$apk" ]; then
  echo "build-player: --stripping does not apply to --apk (the APK is already built)" >&2; exit 2
fi
explicit_stripping="$stripping"
stripping="${stripping:-$strip_default}"
if [ "$target" != android-il2cpp ] && { [ "$run" = 1 ] || [ -n "$arch" ]; }; then
  echo "build-player: --run, --serial, --arch and --apk apply to --target android-il2cpp (Windows players always run)" >&2; exit 2
fi
case "$arch" in ""|arm64|x86_64) ;; *) echo "build-player: --arch arm64|x86_64" >&2; exit 2 ;; esac
if [ "$target" = android-il2cpp ] && [ "$network" = 1 ] && [ "$run" = 0 ]; then
  echo "build-player: --network on android-il2cpp needs --run (the rows run on the device)" >&2; exit 2
fi
if [ "$suspend" = 1 ] && { [ "$target" != android-il2cpp ] || [ "$run" = 0 ]; }; then
  echo "build-player: --suspend applies to --target android-il2cpp with --run or --apk (the script backgrounds the app over adb; iOS: testing.md)" >&2; exit 2
fi
suspend_s="${TURBOHTTP_SMOKE_SUSPEND_S:-10}"
case "$suspend_s" in ''|*[!0-9]*) echo "build-player: TURBOHTTP_SMOKE_SUSPEND_S must be whole seconds, got '$suspend_s'" >&2; exit 2 ;; esac
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
smoke_wait_s="${TURBOHTTP_SMOKE_WAIT_S:-$(if [ "$network" = 1 ]; then echo 900; elif [ "$suspend" = 1 ]; then echo 600; else echo 300; fi)}"
h2_port=""
# Prints the player's row lines and aggregate marker from log $1 and sets fail=1 unless the aggregate is
# PASS, with --bouncycastle reports bc=present, with --network has a PASS row per TLS provider built in, and with
# --suspend has the ws-suspend-resume PASS row.
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
    for row in $h2_rows; do
      [ -z "$h2_url" ] && break
      [ -z "$h2_root_b64" ] && rows="$rows $row-sslstream"
      [ "$bouncycastle" = 1 ] && rows="$rows $row-bouncycastle"
    done
    for row in $rows; do
      grep -qE "TURBOHTTP_SMOKE ROW $row PASS" "$plog" 2>/dev/null ||
        { echo "build-player: FAIL --network: no 'TURBOHTTP_SMOKE ROW $row PASS'"; fail=1; }
      case "$row" in
        h2-*-bouncycastle)
          grep -qE "TURBOHTTP_SMOKE ROW $row PASS .*protocol=h2( |$)" "$plog" 2>/dev/null ||
            { echo "build-player: FAIL --network: $row did not run over HTTP/2"; fail=1; } ;;
      esac
    done
  fi
  if [ "$suspend" = 1 ]; then
    grep -qE "TURBOHTTP_SMOKE ROW ws-suspend-resume PASS" "$plog" 2>/dev/null ||
      { echo "build-player: FAIL --suspend: no 'TURBOHTTP_SMOKE ROW ws-suspend-resume PASS'"; fail=1; }
  fi
}

# #1210: the HTTP/2-over-TLS row server. h2_prepare (before the player starts) waits for a server this
# script started, then sets h2_url and h2_root_b64 (the root as base64 DER, empty without a root).
h2_rows="h2-download h2-upload h2-concurrent h2-stream-cancel"
h2_url="${TURBOHTTP_H2_ROWS_URL:-}"; h2_root="${TURBOHTTP_H2_ROWS_ROOT:-}"; h2_root_b64=""; h2_pid=""; h2_log=""
h2_stop() { if [ -n "$h2_pid" ]; then kill "$h2_pid" 2>/dev/null || true; fi; }
# The rows need a server a built-in provider can trust: a given URL, or the started one (test root) with
# --bouncycastle. Otherwise (the plain --network TLS run, #450) they are not run.
h2_on=0
if [ "$network" = 1 ] && { [ -n "$h2_url" ] || [ "$bouncycastle" = 1 ]; }; then h2_on=1; fi
if [ "$h2_on" = 1 ] && [ -z "$h2_url" ]; then
  command -v dotnet >/dev/null || {
    echo "TOOLCHAIN_UNAVAILABLE: --network starts the HTTP/2 row server (Tools~/h2-interop/run.sh --serve), which needs dotnet;" \
      "or set TURBOHTTP_H2_ROWS_URL/_ROOT to a running one"; exit 3; }
  h2_log="$(mktemp "${TMPDIR:-/tmp}/turbohttp-h2-serve.XXXXXX")"
  "$repo/Tools~/h2-interop/run.sh" --serve 0 >"$h2_log" 2>&1 &
  h2_pid=$!
  trap h2_stop EXIT
fi
h2_prepared=0
h2_prepare() {
  { [ "$network" = 1 ] && [ "$h2_prepared" = 0 ]; } || return 0
  h2_prepared=1
  if [ "$h2_on" = 0 ]; then
    echo "build-player: HTTP/2 rows not run (no --bouncycastle and no TURBOHTTP_H2_ROWS_URL; SslStream cannot trust the test root)"
    return 0
  fi
  if [ -n "$h2_pid" ]; then
    local deadline=$(( $(date +%s) + ${TURBOHTTP_H2_SERVE_WAIT_S:-900} ))
    until grep -q '^serving until' "$h2_log"; do
      if ! kill -0 "$h2_pid" 2>/dev/null; then
        echo "build-player: the HTTP/2 row server (Tools~/h2-interop/run.sh --serve) exited:" >&2; tail -20 "$h2_log" >&2; exit 2
      fi
      if [ "$(date +%s)" -ge "$deadline" ]; then
        echo "build-player: the HTTP/2 row server did not start within ${TURBOHTTP_H2_SERVE_WAIT_S:-900} s; see $h2_log" >&2; exit 2
      fi
      sleep 2
    done
    h2_url="$(sed -n 's/^TURBOHTTP_H2_ROWS_URL=//p' "$h2_log" | head -1)"
    h2_root="$(sed -n 's/^TURBOHTTP_H2_ROWS_ROOT=//p' "$h2_log" | head -1)"
  fi
  case "$h2_url" in https://*) ;; *) echo "build-player: TURBOHTTP_H2_ROWS_URL must be an https URL, got '$h2_url'" >&2; exit 2 ;; esac
  if [ -n "$h2_root" ]; then
    h2_root_b64="$(python3 -I -c '
import base64, sys
data = open(sys.argv[1], "rb").read()
begin, end = b"-----BEGIN CERTIFICATE-----", b"-----END CERTIFICATE-----"
if begin in data:
    body = data.split(begin, 1)[1]
    if end not in body:
        sys.exit(1)
    data = base64.b64decode(b"".join(body.split(end, 1)[0].split()))
if not data.startswith(b"\x30"):
    sys.exit(1)
print(base64.b64encode(data).decode("ascii"))
' "$h2_root")" || { echo "build-player: TURBOHTTP_H2_ROWS_ROOT '$h2_root' is not a PEM or DER certificate" >&2; exit 2; }
  fi
  echo "build-player: HTTP/2 rows against $h2_url (test root: ${h2_root:-none, a publicly trusted server})"
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
  sdk="$(adbr shell getprop ro.build.version.sdk || true)"
  # y while the smoke's process exists, n on a complete answer without it, empty when unknown (android-adb.sh).
  smoke_running() { android_process_state "$pkg_id" "$sdk"; }
  # Installs APK $1, launches it, waits for the aggregate marker, keeps logcat in $2 and checks it (fail=1).
  android_run() {
    local apk_file="$1" logcat="$2" rc out deadline gone=0 suspended=0 extra=()
    adbr install -r "$(topath "$apk_file")" >/dev/null || adb_failed "$?" "install -r $apk_file"
    adbr shell am force-stop "$pkg_id" >/dev/null || adb_failed "$?" "am force-stop"
    adbr shell input keyevent KEYCODE_WAKEUP >/dev/null || adb_failed "$?" "input keyevent KEYCODE_WAKEUP"
    adbr shell wm dismiss-keyguard >/dev/null || true
    if android_keyguard_showing; then
      echo "build-player: ${serial:-the device} shows its lock screen: unlock it and keep the screen on" \
        "(Developer options > Stay awake); an app behind the lock screen may be frozen (#325)" >&2
      exit 2
    fi
    adbr logcat -c >/dev/null || adb_failed "$?" "logcat -c"
    if [ "$network" = 1 ]; then
      h2_prepare
      extra=(--es turbohttp_smoke_network 1)
      [ -n "$h2_url" ] && extra+=(--es turbohttp_smoke_h2_url "$h2_url")
      [ -n "$h2_root_b64" ] && extra+=(--es turbohttp_smoke_h2_root "$h2_root_b64")
      # A loopback row server is the host's: adb reverse maps the device's port to it.
      local hostport="${h2_url#https://}"; hostport="${hostport%%/*}"
      case "$hostport" in
        127.0.0.1:*|localhost:*)
          h2_port="${hostport##*:}"
          adbr reverse "tcp:$h2_port" "tcp:$h2_port" >/dev/null || adb_failed "$?" "reverse tcp:$h2_port" ;;
      esac
    fi
    [ "$suspend" = 1 ] && extra+=(--es turbohttp_smoke_suspend 1)
    out="$(adbr shell am start -W -n "$pkg_id/com.unity3d.player.UnityPlayerActivity" ${extra[@]+"${extra[@]}"})" ||
      adb_failed "$?" "am start"
    case "$out" in *Error*) echo "build-player: am start failed: $out" >&2; exit 2 ;; esac
    echo "build-player: installed and launched $pkg_id on ${serial:-the adb device}; waiting for TURBOHTTP_SMOKE"
    deadline=$(( $(date +%s) + smoke_wait_s ))
    while :; do
      rc=0
      adbr logcat -d -s Unity:V AndroidRuntime:E DEBUG:V > "$logcat" || rc=$?
      wsl_exit_if_interop "$rc" build-player "adb.exe did not start (logcat)"
      # #1132: once the row has a keep-alive ping in flight, background the app past the pong timeout, then resume it.
      if [ "$suspend" = 1 ] && [ "$suspended" = 0 ] && grep -q "TURBOHTTP_SMOKE SUSPEND ws-suspend-resume ready" "$logcat"; then
        suspended=1
        echo "build-player: --suspend: keep-alive ping in flight; backgrounding $pkg_id for $suspend_s s (KEYCODE_HOME)"
        adbr shell input keyevent KEYCODE_HOME >/dev/null || adb_failed "$?" "input keyevent KEYCODE_HOME"
        sleep "$suspend_s"
        # A launcher intent brings the existing task back to the front, as tapping the app icon does.
        out="$(adbr shell am start -a android.intent.action.MAIN -c android.intent.category.LAUNCHER \
          -n "$pkg_id/com.unity3d.player.UnityPlayerActivity")" || adb_failed "$?" "am start (resume)"
        case "$out" in *Error*) echo "build-player: am start (resume) failed: $out" >&2; exit 2 ;; esac
        echo "build-player: --suspend: $pkg_id back in the foreground; waiting for the row"
        gone=0
        continue
      fi
      grep -qE "TURBOHTTP_SMOKE (PASS|FAIL)" "$logcat" && break
      # Gone on two adb checks in a row without a marker: crashed or quit early. A failed check does not count.
      case "$(smoke_running)" in
        n) gone=$((gone + 1)) ;;
        y) gone=0 ;;
      esac
      if [ "$gone" -ge 2 ]; then
        adbr logcat -d -s Unity:V AndroidRuntime:E DEBUG:V > "$logcat" || true
        grep -qE "TURBOHTTP_SMOKE (PASS|FAIL)" "$logcat" || echo "build-player: the player exited without a marker; see $logcat"
        break
      fi
      if [ "$(date +%s)" -ge "$deadline" ]; then
        echo "build-player: no marker within $smoke_wait_s s; see $logcat"
        break
      fi
      sleep "${TURBOHTTP_SMOKE_POLL_S:-3}"
    done
    adbr shell am force-stop "$pkg_id" >/dev/null || true
    if [ -n "$h2_port" ]; then adbr reverse --remove "tcp:$h2_port" >/dev/null || true; fi
    check_smoke_log "$logcat"
    if [ "$suspend" = 1 ] && [ "$suspended" = 0 ]; then
      echo "build-player: FAIL --suspend: no 'TURBOHTTP_SMOKE SUSPEND ws-suspend-resume ready' line, so the app was never backgrounded"
      fail=1
    fi
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
# A non-default level is in the name, so its build directory never passes for the default one.
if [ -n "$explicit_stripping" ] && [ "$stripping" != "$strip_default" ]; then
  variant="$variant-$(printf '%s' "$stripping" | tr '[:upper:]' '[:lower:]')"
fi
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
  -smokeBouncyCastle "$bouncycastle" -smokeArch "${arch:-arm64}" -smokeStripping "$stripping" -logFile "$(topath "$log")"
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
for asm in TurboHTTP.Core TurboHTTP.Transport TurboHTTP.WebSocket.Transport TurboHTTP.Unity.WebSocket TurboHTTP.Complete \
           TurboHTTP.JSON TurboHTTP.Retry TurboHTTP.Auth; do
  has "$asm" || { echo "build-player: FAIL $asm.dll missing"; fail=1; }
done
if [ "$bouncycastle" = 1 ]; then
  for asm in TurboHTTP.Transport.BouncyCastle TurboHTTP.SecureProtocol; do
    has "$asm" || { echo "build-player: FAIL $asm.dll missing with TURBOHTTP_BOUNCYCASTLE"; fail=1; }
  done
else
  for asm in TurboHTTP.Transport.BouncyCastle TurboHTTP.SecureProtocol; do
    has "$asm" && { echo "build-player: FAIL $asm.dll present without TURBOHTTP_BOUNCYCASTLE (stripping $stripping)"; fail=1; }
  done
fi
if [ -f "$out/TurboHTTP.link.xml" ]; then
  echo "build-player: UnityLinker received $out/TurboHTTP.link.xml (assemblies: $(grep -oE '<assembly fullname="[^"]+"' "$out/TurboHTTP.link.xml" | sed 's/.*="//;s/"$//' | sort -u | tr '\n' ' '))"
elif [ "$stripping" != Disabled ]; then
  echo "build-player: FAIL no TurboHTTP.link.xml from TurboHttpLinkerProcessor (stripping $stripping runs UnityLinker)"; fail=1
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
  if [ "$network" = 1 ]; then
    h2_prepare
    player_args+=(-turbohttpSmokeNetwork)
    [ -n "$h2_url" ] && player_args+=(-turbohttpSmokeH2Url "$h2_url")
    [ -n "$h2_root_b64" ] && player_args+=(-turbohttpSmokeH2Root "$h2_root_b64")
  fi
  for _ in 1 2 3; do
    set +e
    (cd "$out" && timeout "$smoke_wait_s" "./$product" "${player_args[@]}" -logFile "$(topath "$plog")" >/dev/null 2>&1)
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
