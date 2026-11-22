#!/usr/bin/env bash
# Unity-free NUnit harness (#879): every Tests/Runtime file that mentions none of UnityEngine, UnityEditor or
# UnityTest, compiled into a plain .NET 8 test assembly against the shadow-built DLLs and run with dotnet test.
#
#   Tools~/unit-tests/run.sh                         shadow build, harness build, every selected test
#   Tools~/unit-tests/run.sh --filter <expr>         only tests matching a dotnet test filter, e.g. 'FullyQualifiedName~CookieJar'
#   Tools~/unit-tests/run.sh --no-shadow-build       reuse the existing .build/shadow DLLs
#
# Selection: TurboHTTP.UnitTests.csproj (files: automatic, plus the commented UnitExclude/UnitHelper lists) and
# excluded-tests.txt (single tests, applied here as filter clauses). ExternalNetwork tests never run here. A green run proves logic on .NET 8 only;
# the Unity host-editor runs stay the authority (Development~/docs/agents/testing.md). Needs dotnet (SDK 8) and
# the Unity managed DLLs the shadow build uses (Tools~/ci/fetch-unity-dlls.sh, or UNITY_DLLS / UNITY_PATH).
# Results (TRX) land in .build/unit/results.
# Exit 0 = every selected test passed; 1 = a test or a build failed; 2 = usage; 3 = tool missing.
set -euo pipefail
cd "$(dirname "$0")/../.."

filter="TestCategory!=ExternalNetwork"
shadow=1
while [ $# -gt 0 ]; do
  case "$1" in
    --filter) filter="($2)&$filter"; shift 2 || { echo "run.sh: --filter takes an expression" >&2; exit 2; } ;;
    --no-shadow-build) shadow=0; shift ;;
    *) echo "usage: $0 [--filter <expr>] [--no-shadow-build]" >&2; exit 2 ;;
  esac
done

while IFS= read -r line; do
  case "$line" in ''|'#'*) continue ;; esac
  filter="$filter&FullyQualifiedName!=$line"
done < "Tools~/unit-tests/excluded-tests.txt"

command -v dotnet >/dev/null || { echo "run.sh: dotnet not found" >&2; exit 3; }

if [ -z "${UNITY_DLLS:-}${UNITY_PATH:-}" ] && [ ! -f ".tools/unity-managed/$(cat Tools~/ci/unity-version)/UnityEngine.dll" ]; then
  "Tools~/ci/fetch-unity-dlls.sh" >/dev/null
fi
if [ "$shadow" = 1 ] || [ ! -f .build/shadow/editor/bin/Debug/netstandard2.1/TurboHTTP.Transport.dll ]; then
  python3 "Tools~/ci/shadow_build.py" --variant editor --no-tests
fi

csproj="Tools~/unit-tests/TurboHTTP.UnitTests.csproj"
results="$PWD/.build/unit/results"
rm -rf "$results"
dotnet build "$csproj" -c Release -nologo -v m -clp:NoSummary
# --blame-hang-timeout turns a hung test into a failure with a dump instead of a job timeout.
dotnet test "$csproj" -c Release --no-build -nologo --filter "$filter" \
  --blame-hang-timeout 5m --logger "trx;LogFileName=unit.trx" --logger "console;verbosity=minimal" \
  --results-directory "$results"
