#!/usr/bin/env bash
set -euo pipefail

# Modes:
#   gate   -> enforce baseline regression checks (default)
#   record -> record current measurements to observed baseline JSON
MODE="${1:-gate}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"

UNITY_BIN="${UNITY_BIN:-/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity}"
PROJECT_PATH="${PROJECT_PATH:-}"
if [[ -z "${PROJECT_PATH}" ]]; then
  echo "usage: PROJECT_PATH=<Unity test project> [UNITY_BIN=<Unity 2021.3 editor binary>] $0 [gate|record]" >&2
  echo "  PROJECT_PATH is the Unity project that consumes this package (see Development~/docs/how-to-run-tests-with-temp-unity-project.md)." >&2
  exit 2
fi
TEST_PLATFORM="${TEST_PLATFORM:-PlayMode}"
THRESHOLD_PERCENT="${TURBOHTTP_ALLOCATION_REGRESSION_THRESHOLD_PERCENT:-10}"

TS="$(date +%Y%m%d-%H%M%S)"
LOG_FILE="${LOG_FILE:-${PROJECT_PATH}/unity-phase19-allocation-${MODE}-${TS}.log}"
TEST_RESULTS="${TEST_RESULTS:-${PROJECT_PATH}/test-results-phase19-allocation-${MODE}-${TS}.xml}"

export TURBOHTTP_ALLOCATION_REGRESSION_THRESHOLD_PERCENT="${THRESHOLD_PERCENT}"

if [[ "${MODE}" == "record" ]]; then
  export TURBOHTTP_ALLOCATION_BASELINE_RECORD=1
  export TURBOHTTP_ALLOCATION_BASELINE_OUTPUT="${TURBOHTTP_ALLOCATION_BASELINE_OUTPUT:-${ROOT}/Tests/Benchmarks/phase19-allocation-baselines.observed.json}"
fi

echo "[phase19] mode=${MODE}"
echo "[phase19] unity=${UNITY_BIN}"
echo "[phase19] project=${PROJECT_PATH}"
echo "[phase19] platform=${TEST_PLATFORM}"
echo "[phase19] threshold=${THRESHOLD_PERCENT}%"
echo "[phase19] log=${LOG_FILE}"
echo "[phase19] results=${TEST_RESULTS}"
if [[ "${MODE}" == "record" ]]; then
  echo "[phase19] observed-baseline-output=${TURBOHTTP_ALLOCATION_BASELINE_OUTPUT}"
fi

"${UNITY_BIN}" -batchmode -nographics \
  -projectPath "${PROJECT_PATH}" \
  -runTests \
  -testPlatform "${TEST_PLATFORM}" \
  -testFilter "TurboHTTP.Tests.Performance.Phase19AllocationGateTests" \
  -testResults "${TEST_RESULTS}" \
  -logFile "${LOG_FILE}"
