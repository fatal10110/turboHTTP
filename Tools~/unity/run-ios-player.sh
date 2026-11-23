#!/usr/bin/env bash
# Mac side of the iOS test-player run (#99): builds the Xcode project that build-ios-test-player.sh
# exported, installs it on a simulator or a connected device, runs it until the player quits, then pulls
# Documents/results.xml and summarises it.
#
#   Tools~/unity/run-ios-player.sh <xcode dir> [--simulator <name|udid>] [--device <udid>] [--team <id>]
#                                  [--launches <n>] [--timeout <s>] [--full] [--baseline-key <key>]
#                                  [--suspend-cycles]
#
# --simulator (default "iPhone 17 Pro") for a simulator build; --device <udid> (xcrun devicectl list devices)
# for a build made with build-ios-test-player.sh --team, signed here with the same --team
# (-allowProvisioningUpdates). --launches runs the installed app that many times (one results file each).
# --timeout bounds one launch (default 1800 s); a launch that does not quit in time is terminated and its
# partial results are kept. --full applies the unfiltered skip and min-total gates (a whole-suite build);
# without it the run counts as filtered. --baseline-key defaults to Player-iOS-2021.3 for a Unity 2021.3
# project and Player-iOS otherwise (Tools~/unity/skip-baseline.txt).
# --suspend-cycles drives DeviceSuspendResumeTests (#1132, #99; build with --filter DeviceSuspendResumeTests): when
# the player logs TURBOHTTP_SUSPEND_READY cycles=<n> it backgrounds the app (opens Settings) for 10 s and brings it
# back, n times, 6 s apart.
# Results: <cache>/results-iOS-<sim|device>-<ts>.xml and the console log beside it
# (<cache>: ${XDG_CACHE_HOME:-~/.cache}/turbohttp). Keep machine paths out of issues and PRs.
# Exit codes: 0 every launch passed its gates, 1 a launch failed a test or a gate, 2 setup/build/install error.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
xcode=""; sim="iPhone 17 Pro"; device=""; team="${TURBOHTTP_IOS_TEAM:-}"; launches=1; timeout_s=1800; filtered=1; key=""; suspend=0
while [ $# -gt 0 ]; do
  case "$1" in
    --simulator) sim="$2"; shift 2 ;;
    --device) device="$2"; shift 2 ;;
    --team) team="$2"; shift 2 ;;
    --launches) launches="$2"; shift 2 ;;
    --timeout) timeout_s="$2"; shift 2 ;;
    --full) filtered=0; shift ;;
    --baseline-key) key="$2"; shift 2 ;;
    --suspend-cycles) suspend=1; shift ;;
    -*) echo "run-ios-player: unknown option $1" >&2; exit 2 ;;
    *) xcode="$1"; shift ;;
  esac
done
case "$launches" in ''|*[!0-9]*|0) echo "run-ios-player: --launches needs a number >= 1" >&2; exit 2 ;; esac
[ -d "$xcode/Unity-iPhone.xcodeproj" ] || { echo "run-ios-player: no Unity-iPhone.xcodeproj in '$xcode'" >&2; exit 2; }
if [ -n "$device" ] && [ -z "$team" ]; then echo "run-ios-player: --device needs --team (the build's signing team)" >&2; exit 2; fi
if [ -z "$key" ]; then
  # The exported project names the editor version in its build settings.
  if grep -qs '2021\.3\.' "$xcode/Unity-iPhone.xcodeproj/project.pbxproj"; then key=Player-iOS-2021.3; else key=Player-iOS; fi
fi

cache="${XDG_CACHE_HOME:-$HOME/.cache}/turbohttp"
dd="$cache/ios-derived"
mkdir -p "$cache"
ts="$(date +%Y%m%d-%H%M%S)"
build_log="$cache/xcodebuild-iOS-$ts.log"

if [ -n "$device" ]; then
  kind=device; sdk=iphoneos
  xb=(-allowProvisioningUpdates DEVELOPMENT_TEAM="$team" CODE_SIGN_STYLE=Automatic -destination "id=$device")
else
  kind=sim; sdk=iphonesimulator
  case "$sim" in
    *-*-*-*-*) udid="$sim" ;;
    *) udid="$(xcrun simctl list devices available | { grep -F "$sim (" || true; } | head -1 | sed -E 's/.*\(([0-9A-F-]{36})\).*/\1/')" ;;
  esac
  [ -n "$udid" ] || { echo "run-ios-player: no available simulator named '$sim'" >&2; exit 2; }
  xb=(-destination "generic/platform=iOS Simulator" CODE_SIGNING_ALLOWED=NO)
fi

echo "run-ios-player: xcodebuild ($sdk) -> $build_log"
if ! xcodebuild -project "$xcode/Unity-iPhone.xcodeproj" -scheme Unity-iPhone -configuration Release -sdk "$sdk" \
     -derivedDataPath "$dd" "${xb[@]}" build >"$build_log" 2>&1; then
  grep -E 'error:|BUILD FAILED' "$build_log" | head -20
  echo "run-ios-player: xcodebuild failed; see $build_log" >&2; exit 2
fi
apps=("$dd/Build/Products/Release-$sdk/"*.app); app="${apps[0]}"
[ -d "$app" ] || { echo "run-ios-player: xcodebuild left no .app in $dd/Build/Products/Release-$sdk" >&2; exit 2; }
bundle="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Info.plist")"
echo "run-ios-player: built $(basename "$app") ($bundle, $(lipo -archs "$app/$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Info.plist")"))"

if [ "$kind" = sim ]; then
  xcrun simctl boot "$udid" 2>/dev/null || true
  xcrun simctl bootstatus "$udid" -b >/dev/null
  xcrun simctl uninstall "$udid" "$bundle" 2>/dev/null || true
  xcrun simctl install "$udid" "$app"
else
  # Never uninstall first: with a free team, removing the developer's last app drops the profile trust.
  xcrun devicectl device install app --device "$device" "$app" >/dev/null
fi

# Runs "$@" in the background and stops it after timeout_s; returns its exit code or 124 on timeout.
bounded() {
  "$@" & local pid=$! waited=0
  while kill -0 "$pid" 2>/dev/null; do
    if [ "$waited" -ge "$timeout_s" ]; then kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null; return 124; fi
    sleep 5; waited=$((waited + 5))
  done
  wait "$pid"
}

# Launches an app by bundle id without terminating it: Settings to background the player, the player to bring it back.
activate() {
  if [ "$kind" = sim ]; then xcrun simctl launch "$udid" "$1" >/dev/null 2>&1
  else xcrun devicectl device process launch --activate --device "$device" "$1" >/dev/null 2>&1; fi
}

# Waits for TURBOHTTP_SUSPEND_READY in the console, then suspends and resumes the player the number of times it asks.
drive_suspend() {
  local console="$1" waited=0 cycles
  until grep -q 'TURBOHTTP_SUSPEND_READY' "$console" 2>/dev/null; do
    sleep 1; waited=$((waited + 1)); [ "$waited" -ge "$timeout_s" ] && return 0
  done
  cycles="$(grep -m1 -o 'TURBOHTTP_SUSPEND_READY cycles=[0-9]*' "$console" | sed 's/.*=//')"
  for c in $(seq 1 "${cycles:-0}"); do
    sleep 6
    # A player that already ended (a failed assertion) must not be started again outside the launch bound.
    grep -qE 'TURBOHTTP_SUSPEND_RESULT|TURBOHTTP_DEVICE_RUN finished' "$console" && return 0
    echo "run-ios-player: suspend $c/$cycles"
    activate com.apple.Preferences || echo "run-ios-player: could not open Settings"
    sleep 10
    grep -qE 'TURBOHTTP_SUSPEND_RESULT|TURBOHTTP_DEVICE_RUN finished' "$console" && return 0
    activate "$bundle" || echo "run-ios-player: could not bring the player back"
  done
}

status=0
for i in $(seq 1 "$launches"); do
  lts="$(date +%Y%m%d-%H%M%S)"
  out="$cache/results-iOS-$kind-$lts.xml"; console="$cache/console-iOS-$kind-$lts.log"
  echo "run-ios-player: launch $i/$launches -> $console"
  rc=0; driver=""
  if [ "$suspend" = 1 ]; then : >"$console"; drive_suspend "$console" & driver=$!; fi
  if [ "$kind" = sim ]; then
    data="$(xcrun simctl get_app_container "$udid" "$bundle" data)"
    rm -f "$data/Documents/results.xml"
    bounded xcrun simctl launch --console-pty --terminate-running-process "$udid" "$bundle" >"$console" 2>&1 || rc=$?
    [ "$rc" = 124 ] && xcrun simctl terminate "$udid" "$bundle" 2>/dev/null || true
    cp "$data/Documents/results.xml" "$out" 2>/dev/null || true
  else
    bounded xcrun devicectl device process launch --console --terminate-existing --device "$device" "$bundle" >"$console" 2>&1 || rc=$?
    xcrun devicectl device copy from --device "$device" --domain-type appDataContainer --domain-identifier "$bundle" \
      --source Documents/results.xml --destination "$out" >/dev/null 2>&1 || true
  fi
  if [ -n "$driver" ]; then kill "$driver" 2>/dev/null || true; wait "$driver" 2>/dev/null || true; fi
  [ "$rc" = 124 ] && echo "run-ios-player: launch $i did not quit within ${timeout_s}s; terminated"
  grep -E 'TURBOHTTP_DEVICE_(HANG|FAIL) |TURBOHTTP_SUSPEND_(RESUMED|RESULT)' "$console" | head -20 || true
  # The player deletes results.xml when its run starts (RunStarted): a launch that never started a run would
  # otherwise pull the previous launch's file, which devicectl keeps across installs.
  if ! grep -q 'TURBOHTTP_DEVICE_RUN started' "$console"; then rm -f "$out"; fi
  if [ ! -s "$out" ]; then echo "run-ios-player: launch $i left no results.xml (console $console)"; status=1; continue; fi
  grep -q 'partial="true"' "$out" && { echo "run-ios-player: launch $i results are partial (the run did not finish)"; status=1; }
  python3 "$repo/Tools~/unity/summarize_results.py" "$out" "$key" "$filtered" "$repo/Tools~/unity/skip-baseline.txt" || status=1
  echo "run-ios-player: results $out"
done
exit "$status"
