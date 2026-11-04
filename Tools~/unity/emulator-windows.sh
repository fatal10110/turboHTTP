# shellcheck shell=bash
# Windows-side emulator process control for run-emulator-tests.sh on WSL2 with a Windows editor (#353).
# Sourced, not run. From WSL, $! of a backgrounded emulator.exe is the WSL interop pid: the Windows
# emulator.exe and its qemu-system-*.exe child can outlive it, and WSL ps/kill cannot see them. A leftover
# keeps its AVD locked ("Running multiple emulators with the same AVD") and its console port taken, so
# these helpers find the processes by command line (-avd <name>, -port <n>) through powershell.exe and
# stop them with taskkill.exe /T /F. Both are looked up on PATH (the tool tests shadow them with stubs).

# Seconds between two process lookups while waiting for an emulator to exit.
emu_poll_s="${emu_poll_s:-2}"

# Prints "<pid>|<avd>|<port>" for every Windows emulator*/qemu-system* process ("-" when the command line
# names none). Returns 1 when Windows cannot be asked: powershell.exe from WSL fails now and then (empty
# output, "UtilAcceptVsock ... accept4 failed"), so a lookup counts only when its end marker arrived.
win_emulator_procs() {
  local query out="" pid cmd avd port
  query="\$ErrorActionPreference='Stop'; Get-CimInstance -ClassName Win32_Process"
  query+=" -Filter 'Name like ''qemu-system%'' or Name like ''emulator%'''"
  query+=" | ForEach-Object { '{0}|{1}' -f \$_.ProcessId, \$_.CommandLine }; 'end-of-list'"
  for _ in 1 2 3; do
    out="$(powershell.exe -NoProfile -NonInteractive -Command "$query" 2>/dev/null | tr -d '\r' || true)"
    case "$out" in *end-of-list*) break ;; esac
    out=""
    sleep "$emu_poll_s"
  done
  [ -n "$out" ] || return 1
  while IFS='|' read -r pid cmd; do
    case "$pid" in ''|*[!0-9]*) continue ;; esac
    avd="-"; port="-"
    if [[ $cmd =~ [[:space:]]-avd[[:space:]]+\"?([^\"[:space:]]+) ]]; then avd="${BASH_REMATCH[1]}"; fi
    if [[ $cmd =~ [[:space:]]-port[[:space:]]+\"?([0-9]+) ]]; then port="${BASH_REMATCH[1]}"; fi
    printf '%s|%s|%s\n' "$pid" "$avd" "$port"
  done <<< "$out"
}

# Prints the pids of the Windows processes running AVD $1, one per line; returns 1 when Windows cannot be asked.
win_avd_pids() {
  local list pid avd port
  list="$(win_emulator_procs)" || return 1
  while IFS='|' read -r pid avd port; do
    if [ -n "$pid" ] && [ "$avd" = "$1" ]; then printf '%s\n' "$pid"; fi
  done <<< "$list"
}

# Stops AVD $1 on the Windows side: waits up to $2 lookups (emu_poll_s apart) for its processes to exit after
# the console's "emu kill", then force-stops what is left with taskkill /T /F (emulator.exe and its qemu child).
# Returns 1 (with a message naming the AVD) when a process survives or Windows cannot be asked.
win_stop_avd() {
  local avd="$1" polls="$2" pids="" pid i
  for ((i = 0; i < polls; i++)); do
    pids="$(win_avd_pids "$avd")" || pids="?"
    [ -n "$pids" ] || return 0
    sleep "$emu_poll_s"
  done
  if ! pids="$(win_avd_pids "$avd")"; then
    echo "run-emulator-tests: cannot list Windows processes; check for a leftover $avd (Get-Process qemu*)" >&2
    return 1
  fi
  [ -n "$pids" ] || return 0
  for pid in $pids; do
    echo "run-emulator-tests: stopping Windows process $pid ($avd)" >&2
    taskkill.exe /PID "$pid" /T /F >/dev/null 2>&1 || true
  done
  for ((i = 0; i < 5; i++)); do
    pids="$(win_avd_pids "$avd")" || pids="?"
    [ -n "$pids" ] || return 0
    sleep "$emu_poll_s"
  done
  echo "run-emulator-tests: $avd is still running as Windows process(es) $(echo "$pids" | tr '\n' ' ')" >&2
  return 1
}

# Before booting AVD $1 on console port $2: stops leftovers of this script's AVDs (turbohttp-api<N>) that run
# $1 or hold $2, and returns 1 naming the PID when any other emulator holds $2 (never stopped: not ours).
# When Windows cannot be asked it warns and returns 0; the boot then reports the failure.
win_free_avd() {
  local avd="$1" port="$2" list pid a p stale=()
  if ! list="$(win_emulator_procs)"; then
    echo "run-emulator-tests: cannot list Windows processes; not checking for a leftover $avd" >&2
    return 0
  fi
  while IFS='|' read -r pid a p; do
    [ -n "$pid" ] || continue
    [ "$a" = "$avd" ] || [ "$p" = "$port" ] || continue
    if [[ $a =~ ^turbohttp-api[0-9]+$ ]]; then
      case " ${stale[*]-} " in *" $a "*) ;; *) stale+=("$a") ;; esac
    else
      echo "run-emulator-tests: Windows process $pid (AVD $a) holds console port $port needed by $avd;" \
        "it is not one of this script's emulators, so stop it yourself" >&2
      return 1
    fi
  done <<< "$list"
  for a in ${stale[@]+"${stale[@]}"}; do
    echo "run-emulator-tests: a previous run left $a running; stopping it" >&2
    win_stop_avd "$a" 0 || return 1
  done
}
