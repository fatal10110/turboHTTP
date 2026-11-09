#!/usr/bin/env bash
# Runs an already built Linux standalone (x86_64, Mono) test player of TurboHTTP's PlayMode suite headless and
# reports its results (#404, #411). The player must come from the Unity Test Runner (-runTests -testPlatform
# StandaloneLinux64) on the test project build-ios-test-player.sh writes, so it carries the device-results callback:
# it logs TURBOHTTP_DEVICE_RUN started tests=<n> before the first test, TURBOHTTP_DEVICE_TEST per test,
# TURBOHTTP_DEVICE_FAIL per failure and TURBOHTTP_DEVICE_RUN finished total=.. passed=.. failed=.. path=<results.xml>
# at the end, then quits.
#
#   Tools~/unity/run-linux-player-tests.sh [--out <dir>] [--attempts <n>] <player folder | player .x86_64 binary>
#
# The Mono runtime of the 2021.3 Linux player sometimes dies with SIGSEGV inside its garbage collector while the
# Test Runner builds the test tree, before any test runs (#423): the log shows "Caught fatal signal" and no
# "TURBOHTTP_DEVICE_RUN started" line. Such a launch carries no result, so it is relaunched, up to --attempts
# launches in all (default 3, env TURBOHTTP_LINUX_PLAYER_ATTEMPTS). Any other launch is final: a player that
# crashes or stops after the run started, or exits without starting it for another reason, is never relaunched.
# Each launch is bounded by TURBOHTTP_LINUX_PLAYER_TIMEOUT seconds (default 3600). --out (default
# <cache>/linux-player-<timestamp>) receives per launch player-<k>.log (the Unity log) and player-<k>.out
# (the player's stdout and stderr), and the player's results.xml.
# Exit codes as run-tests.sh: 0 pass, 1 failures, 2 setup error (no player, every launch crashed before the run,
# a crash, timeout or exit without a finished line after the run started, no results file).
set -euo pipefail

out=""; attempts="${TURBOHTTP_LINUX_PLAYER_ATTEMPTS:-3}"; limit="${TURBOHTTP_LINUX_PLAYER_TIMEOUT:-3600}"; player=""
usage() { echo "usage: $0 [--out dir] [--attempts n] <player folder | .x86_64 binary>" >&2; exit 2; }
while [ $# -gt 0 ]; do
  case "$1" in
    --out) [ $# -ge 2 ] || usage; out="$2"; shift 2 ;;
    --attempts) [ $# -ge 2 ] || usage; attempts="$2"; shift 2 ;;
    -*) usage ;;
    *) [ -z "$player" ] || usage; player="$1"; shift ;;
  esac
done
[ -n "$player" ] || usage
case "$attempts" in ''|*[!0-9]*|0) echo "run-linux-player-tests: --attempts needs a positive integer" >&2; exit 2 ;; esac
case "$limit" in ''|*[!0-9]*|0) echo "run-linux-player-tests: TURBOHTTP_LINUX_PLAYER_TIMEOUT needs a positive integer" >&2; exit 2 ;; esac

if [ -d "$player" ]; then
  shopt -s nullglob
  bins=("$player"/*.x86_64)
  shopt -u nullglob
  [ "${#bins[@]}" = 1 ] || { echo "run-linux-player-tests: expected one *.x86_64 player in $player, found ${#bins[@]}" >&2; exit 2; }
  player="${bins[0]}"
fi
if [ ! -f "$player" ] || [ ! -x "$player" ]; then
  echo "run-linux-player-tests: $player is not an executable player" >&2
  exit 2
fi

if [ -z "$out" ]; then
  out="${XDG_CACHE_HOME:-$HOME/.cache}/turbohttp/linux-player-$(date +%Y%m%d-%H%M%S)"
fi
mkdir -p "$out"
rm -f "$out/results.xml"

started='TURBOHTTP_DEVICE_RUN started'
crashed='Caught fatal signal'
# Prints the first crash report line of this launch (Unity log or console); fails when there is none.
crash_report() {
  local lines
  lines="$(grep -h -s -m1 -F "$crashed" "$log" "$console" || true)"
  [ -n "$lines" ] || return 1
  printf '%s\n' "${lines%%$'\n'*}"
}
for ((k = 1; k <= attempts; k++)); do
  log="$out/player-$k.log"
  console="$out/player-$k.out"
  rm -f "$log" "$console"
  echo "run-linux-player-tests: launch $k/$attempts of $player (log $log)"
  code=0
  timeout -k 10 "$limit" "$player" -batchmode -nographics -logFile "$log" >"$console" 2>&1 || code=$?
  if grep -qF "$started" "$log" 2>/dev/null; then break; fi
  # The crash report is checked before the timeout: after a fatal signal the crash handler may hang (it collects a
  # native backtrace), so a launch that crashed before the run and then timed out is still a pre-run crash.
  if ! crash_report >/dev/null; then
    if [ "$code" = 124 ]; then
      echo "run-linux-player-tests: the player logged no '$started' within ${limit}s; see $log" >&2
      exit 2
    fi
    echo "run-linux-player-tests: the player exited (code $code) without starting the run and without a crash" \
      "report; not relaunched; see $log" >&2
    exit 2
  fi
  how="code $code"
  [ "$code" = 124 ] && how="then hung until the ${limit}s timeout"
  echo "run-linux-player-tests: the player crashed before the run started ($how, $(crash_report));" \
    "a pre-run crash carries no result (the known cause is a Mono GC crash during test discovery, #423)"
  if [ "$k" = "$attempts" ]; then
    echo "run-linux-player-tests: all $attempts launches crashed before the run started; no results; logs in $out" >&2
    exit 2
  fi
done

grep -m1 -F "$started" "$log"
finished="$(grep -m1 -F 'TURBOHTTP_DEVICE_RUN finished' "$log" || true)"
if [ -z "$finished" ]; then
  why="exited with code $code"
  [ "$code" = 124 ] && why="timed out after ${limit}s"
  crash_report >/dev/null && why="crashed ($(crash_report))"
  echo "run-linux-player-tests: the player $why after the run started, without a finished line; not relaunched" \
    "(a mid-run crash is a result); partial results, if any, are at the player's persistentDataPath/results.xml;" \
    "see $log" >&2
  grep -F 'TURBOHTTP_DEVICE_FAIL' "$log" | head -20 >&2 || true
  exit 2
fi
echo "$finished"
grep -F 'TURBOHTTP_DEVICE_FAIL' "$log" | head -40 || true
results="${finished##* path=}"
if [ "$results" = "$finished" ] || [ ! -f "$results" ]; then
  echo "run-linux-player-tests: no results file at '${results}' (from the finished line); see $log" >&2
  exit 2
fi
cp "$results" "$out/results.xml"
echo "run-linux-player-tests: results $out/results.xml, log $log"
failed="$(printf '%s\n' "$finished" | sed -n 's/.* failed=\([0-9][0-9]*\).*/\1/p')"
[ -n "$failed" ] || { echo "run-linux-player-tests: cannot read failed=<n> from: $finished" >&2; exit 2; }
[ "$failed" = 0 ] || exit 1
exit 0
