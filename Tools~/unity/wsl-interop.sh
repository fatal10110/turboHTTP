# shellcheck shell=bash
# WSL interop retries for the Unity scripts that drive a Windows editor from WSL2 (#355). Sourced, not run.
#
# WSL-to-Windows interop (Unity.exe, cmd.exe, adb.exe, powershell.exe) fails now and then, sometimes for a
# minute or more: a call returns nothing, often with "<3>WSL ... UtilAcceptVsock:281: accept4 failed 110" on
# stderr, and a Unity.exe that never started writes no log. These helpers retry such calls with a growing pause
# (wsl_poll_s*try*2 seconds, at most 30; wsl_tries tries, about two minutes with the defaults), the back-off of
# win_emulator_procs in emulator-windows.sh. When an outage outlasts it they print "WSL interop unavailable" and
# exit wsl_interop_exit (5), so an outage is never reported as a compile error, a missing device or a setup error.
# The scripts set win=1 when UNITY_PATH is a Windows Unity.exe and wslpath exists; only then do they retry.
# Any script can source this file (build-ios-test-player.sh still has its own single-attempt Unity start).

# Exit code of every script that sources this file when WSL interop stays unavailable (documented in each header).
wsl_interop_exit=5
# Back-off unit in seconds and the number of tries per interop call (the tool tests set wsl_poll_s=0).
wsl_poll_s="${wsl_poll_s:-2}"
wsl_tries="${wsl_tries:-8}"

# Sleeps before retry number $1 (1-based): wsl_poll_s*$1*2 seconds, at most 30.
wsl_backoff() {
  local pause=$((wsl_poll_s * $1 * 2))
  [ "$pause" -le 30 ] || pause=30
  sleep "$pause"
}

# Prints the outage message for script $1 ($2: what did not answer) and exits wsl_interop_exit.
wsl_interop_unavailable() {
  echo "$1: WSL interop unavailable: $2 (Windows programs do not start from WSL; stderr may show" \
    "'UtilAcceptVsock ... accept4 failed'). Not a test, compile or device failure: rerun once" \
    "'cmd.exe /c ver' answers (exit $wsl_interop_exit, #355)" >&2
  exit "$wsl_interop_exit"
}

# Exits through wsl_interop_unavailable for script $2 ($3: what did not answer) when status $1 is wsl_interop_exit.
wsl_exit_if_interop() {
  if [ "$1" = "$wsl_interop_exit" ]; then wsl_interop_unavailable "$2" "$3"; fi
}

# Runs cmd.exe /c "$1" once from a Windows directory (no UNC warning), bounded by timeout where it exists, and
# prints its output without \r. Fails when cmd.exe fails.
wsl_cmd() {
  local bound=()
  if command -v timeout >/dev/null 2>&1; then bound=(timeout 60); fi
  (cd /mnt/c 2>/dev/null || :; ${bound[@]+"${bound[@]}"} cmd.exe /c "$1") 2>/dev/null | tr -d '\r'
}

# True when Windows answers one cmd.exe call now: tells an interop outage from a Windows tool's own error.
wsl_interop_up() {
  case "$(wsl_cmd 'echo wsl-interop-ok' || true)" in *wsl-interop-ok*) return 0 ;; esac
  return 1
}

# Sets wsl_localappdata to the WSL path of %LOCALAPPDATA%. A cmd.exe call counts only when it answered: empty
# output is an interop failure and is retried with the back-off; after wsl_tries tries it exits through
# wsl_interop_unavailable for script $1. An answer that is not a drive path, or one wslpath cannot convert, exits 2.
wsl_resolve_localappdata() {
  local try answer=""
  wsl_localappdata=""
  for ((try = 1; try <= wsl_tries; try++)); do
    answer="$(wsl_cmd 'echo %LOCALAPPDATA%' || true)"
    answer="${answer%%$'\n'*}"
    [ -z "$answer" ] || break
    if [ "$try" -lt "$wsl_tries" ]; then wsl_backoff "$try"; fi
  done
  [ -n "$answer" ] || wsl_interop_unavailable "$1" "cmd.exe did not answer the %LOCALAPPDATA% lookup ($wsl_tries tries)"
  if ! [[ $answer =~ ^[A-Za-z]:\\ ]]; then
    echo "$1: cmd.exe printed '$answer' for %LOCALAPPDATA%, not a Windows path" >&2
    exit 2
  fi
  wsl_localappdata="$(wslpath -u "$answer" 2>/dev/null || true)"
  [ -n "$wsl_localappdata" ] || { echo "$1: wslpath cannot convert %LOCALAPPDATA% ($answer)" >&2; exit 2; }
}

# Starts Unity ("$@" after $1 = script name, $2 = the -logFile path in WSL form) and sets unity_code to its exit
# code. Unity writes its log as soon as it starts, so an attempt that left no log never started. From WSL (win=1)
# that is an interop failure: it is retried with the back-off, and after wsl_tries tries the script exits through
# wsl_interop_unavailable. Elsewhere it is one attempt and exits 2. The log is removed first: a stale one would
# hide a start that failed.
wsl_unity_launch() {
  local script="$1" log="$2" tries=1 try
  shift 2
  if [ "${win:-0}" = 1 ]; then tries="$wsl_tries"; fi
  rm -f "$log"
  for ((try = 1; try <= tries; try++)); do
    unity_code=0
    "$@" || unity_code=$?
    [ ! -f "$log" ] || return 0
    echo "$script: Unity wrote no log (exit $unity_code): it did not start (try $try of $tries)" >&2
    if [ "$try" -lt "$tries" ]; then wsl_backoff "$try"; fi
  done
  if [ "${win:-0}" = 1 ]; then
    wsl_interop_unavailable "$script" "Unity.exe did not start ($tries tries, last exit $unity_code, no log)"
  fi
  echo "$script: Unity did not start (exit $unity_code, no log at $log)" >&2
  exit 2
}
