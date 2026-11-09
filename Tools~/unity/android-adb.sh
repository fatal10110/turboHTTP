# shellcheck shell=bash
# Android device checks shared by run-device-tests.sh and build-player.sh (#412). Sourced, not run: the caller
# defines adbr (adb with retries; prints the answer without \r and fails when adb fails).
#
# A large adb answer is never piped into grep -q: under pipefail grep -q exits at its first match, the writer
# dies of SIGPIPE and the pipeline reads as "no match". On a phone with several user profiles `ps -A` prints
# about 1900 lines (150 KB), so a live player in the middle of the list read as gone (#412). Answers are
# matched in bash instead, and only an answer known to be complete can say "not running".

# Prints y while process $1 runs, n when a complete answer lacks it, and nothing when the answer is empty,
# truncated or adb failed (unknown: never "gone"; a failed adb call returns adbr's code, 5 for a WSL interop outage). From API $2 = 26 it asks pidof (one short line; "none"
# marks a complete answer without the process). Older levels read ps (pidof is missing before API 24 and lists
# every pid on Android 6; toybox ps needs -A from API 26, toolbox lists all), ended by a marker line so a
# truncated listing stays unknown. A process still named <pre-initialized> right after launch reads n.
android_process_state() {
  local pkg="$1" sdk="${2:-}" out line rc=0
  case "$sdk" in '' | *[!0-9]*) sdk=0 ;; esac
  if [ "$sdk" -ge 26 ]; then
    out="$(adbr shell "pidof $pkg || echo none")" || rc=$?
    [ "$rc" = 0 ] || return "$rc"
    out="${out//$'\n'/ }"
    out="${out%"${out##*[![:space:]]}"}"
    if [[ $out =~ ^[0-9]+([[:space:]]+[0-9]+)*$ ]]; then echo y; elif [ "$out" = none ]; then echo n; fi
    return 0
  fi
  out="$(adbr shell "ps -A 2>/dev/null; ps 2>/dev/null; echo turbohttp-ps-end")" || rc=$?
  [ "$rc" = 0 ] || return "$rc"
  case "$out" in *turbohttp-ps-end) ;; *) return 0 ;; esac
  while IFS= read -r line; do
    case "$line" in *[[:space:]]"$pkg") echo y; return 0 ;; esac
  done <<<"$out"
  echo n
}

# True while the lock screen shows; false when it does not or adb failed. Field names differ across Android
# versions (API 33 prints KeyguardShowing= and mDreamingLockscreen=; older ones mShowingLockscreen= or
# isStatusBarKeyguard=).
android_keyguard_showing() {
  local windows
  windows="$(adbr shell dumpsys window)" || return 1
  [[ $windows =~ (mShowingLockscreen|mDreamingLockscreen|isStatusBarKeyguard|KeyguardShowing)=true ]]
}

# Prints the focused window ("u0 com.google.android.gms/.kids.LockscreenActivityV2" for a parental-control
# lock screen), or nothing when adb failed or none is focused.
android_focused_window() {
  local windows line
  windows="$(adbr shell dumpsys window)" || return 0
  while IFS= read -r line; do
    case "$line" in
      *mCurrentFocus=*)
        line="${line#*mCurrentFocus=}"
        line="${line#Window\{}"
        line="${line%\}}"
        # "Window{1a2b3c u0 pkg/.Activity}": drop the window hash.
        printf '%s\n' "${line#* }"
        return 0
        ;;
    esac
  done <<<"$windows"
}

# Prints y when the events log shows Android starting a process for package $1 (am_proc_start) since it was
# cleared, n when a log answer lacks it, and nothing when adb failed (returning adbr's code: 5 is a WSL interop
# outage). Tells a player that started and quit between two checks from a launch that never happened.
android_proc_started() {
  local events line rc=0
  events="$(adbr logcat -d -b events)" || rc=$?
  [ "$rc" = 0 ] || return "$rc"
  while IFS= read -r line; do
    case "$line" in *am_proc_start*"$1"*) echo y; return 0 ;; esac
  done <<<"$events"
  echo n
}

# Prints y when the Unity log holds marker $1 (bash match), n when a log answer lacks it, and nothing when adb
# failed (returning adbr's code).
android_unity_logged() {
  local log rc=0
  log="$(adbr logcat -d -s Unity:V)" || rc=$?
  [ "$rc" = 0 ] || return "$rc"
  case "$log" in *"$1"*) echo y ;; *) echo n ;; esac
}
