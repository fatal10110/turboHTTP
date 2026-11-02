#!/usr/bin/env bash
# Parser fuzz harness (#112). Documented in README.md.
#
#   Tools~/fuzz/run.sh                                   replay the committed corpus for every target
#   Tools~/fuzz/run.sh --driver mutate --execs 1000000   seeded mutation loop (NOT coverage guided)
#   Tools~/fuzz/run.sh --driver libfuzzer --runs 1000000 coverage guided: SharpFuzz + libfuzzer-dotnet
#
# Options: --target <name> (repeatable, default: all), --runs N / --time S (libfuzzer budget per
# target), --execs N (mutate budget), --seed N, --max-len N, --no-shadow-build.
# The libfuzzer driver needs `sharpfuzz` (dotnet tool SharpFuzz.CommandLine) and `libfuzzer-dotnet`
# on PATH, or LIBFUZZER_DOTNET=<path>; .github/workflows/fuzz.yml installs both. Exit 3: a tool is
# missing. Exit 1: a finding (crash, hang or memory blow-up); inputs land in .build/fuzz/artifacts.
set -euo pipefail
cd "$(dirname "$0")/../.."

driver=replay
targets=()
runs=""
time_budget=""
execs=1000000
seed=1
max_len=4096
shadow=1
while [ $# -gt 0 ]; do
  case "$1" in
    --driver) driver="$2"; shift 2 ;;
    --target) targets+=("$2"); shift 2 ;;
    --runs) runs="$2"; shift 2 ;;
    --time) time_budget="$2"; shift 2 ;;
    --execs) execs="$2"; shift 2 ;;
    --seed) seed="$2"; shift 2 ;;
    --max-len) max_len="$2"; shift 2 ;;
    --no-shadow-build) shadow=0; shift ;;
    *) echo "usage: $0 [--driver replay|mutate|libfuzzer] [--target <name>]... [--runs N] [--time S] [--execs N] [--seed N] [--max-len N] [--no-shadow-build]" >&2; exit 2 ;;
  esac
done
case "$driver" in replay|mutate|libfuzzer) ;; *) echo "run.sh: unknown driver '$driver'" >&2; exit 2 ;; esac

command -v dotnet >/dev/null || { echo "run.sh: dotnet not found" >&2; exit 3; }
libfuzzer_dotnet=""
if [ "$driver" = libfuzzer ]; then
  command -v sharpfuzz >/dev/null || { echo "run.sh: sharpfuzz not found (dotnet tool install --global SharpFuzz.CommandLine)" >&2; exit 3; }
  libfuzzer_dotnet="${LIBFUZZER_DOTNET:-$(command -v libfuzzer-dotnet || true)}"
  [ -x "$libfuzzer_dotnet" ] || { echo "run.sh: libfuzzer-dotnet not found (set LIBFUZZER_DOTNET)" >&2; exit 3; }
fi

if [ -z "${UNITY_DLLS:-}${UNITY_PATH:-}" ] && [ ! -f ".tools/unity-managed/$(cat Tools~/ci/unity-version)/UnityEngine.dll" ]; then
  "Tools~/ci/fetch-unity-dlls.sh" >/dev/null
fi
if [ "$shadow" = 1 ] || [ ! -f .build/shadow/editor/bin/Debug/netstandard2.1/TurboHTTP.Transport.dll ]; then
  python3 "Tools~/ci/shadow_build.py" --variant editor
fi

out=".build/fuzz"
bin="$out/bin/Release/net8.0"
build=(dotnet build "Tools~/fuzz/TurboHTTP.Fuzz.csproj" -c Release -nologo -v q)
[ "$driver" = libfuzzer ] && build+=(-p:FuzzDriver=libfuzzer)
# SharpFuzz instruments the DLL copies in the build output, so start from a clean output every time
# (an instrumented DLL cannot be instrumented again; .build/shadow is never touched).
rm -rf "${out:?}/bin" "${out:?}/obj"
"${build[@]}"
if [ "$driver" = libfuzzer ]; then
  for dll in TurboHTTP.Core TurboHTTP.Transport TurboHTTP.WebSocket TurboHTTP.JSON; do
    sharpfuzz "$bin/$dll.dll"
  done
fi

harness=(dotnet "$bin/TurboHTTP.Fuzz.dll")
if [ ${#targets[@]} -eq 0 ]; then
  mapfile -t targets < <("${harness[@]}" list)
fi

status=0
for target in "${targets[@]}"; do
  corpus="Tools~/fuzz/corpus/$target"
  artifacts="$out/artifacts/$target"
  mkdir -p "$artifacts"
  echo "== $target ($driver)"
  "${harness[@]}" replay "$target" "$corpus" || status=1

  case "$driver" in
    mutate)
      "${harness[@]}" mutate "$target" --corpus "$corpus" --execs "$execs" --seed "$seed" \
        --max-len "$max_len" --artifacts "$artifacts" || status=1
      ;;
    libfuzzer)
      # New inputs go to the work corpus (first directory); the committed corpus stays read-only.
      work="$out/work/$target"
      mkdir -p "$work"
      flags=(-timeout=30 -rss_limit_mb=2560 -max_len="$max_len" -seed="$seed" -print_final_stats=1
        -artifact_prefix="$artifacts/")
      [ -n "$runs" ] && flags+=(-runs="$runs")
      [ -n "$time_budget" ] && flags+=(-max_total_time="$time_budget")
      TURBOHTTP_FUZZ_TARGET="$target" "$libfuzzer_dotnet" --target_path=dotnet --target_arg="$bin/TurboHTTP.Fuzz.dll" \
        "${flags[@]}" "$work" "$corpus" 2>&1 | tee "$out/$target.libfuzzer.log" || status=1
      ;;
  esac
done
exit "$status"
