#!/usr/bin/env bash
# Local quality gate. CI runs the same steps (.github/workflows/ci.yml).
#
#   Tools~/ci/check.sh            fast: repo invariants, format, shadow build incl. Tests/ (editor variant)
#   Tools~/ci/check.sh --full     also iOS/Android shadow builds, workflow/shell/python linters
#   Tools~/ci/check.sh --fix      apply whitespace formatting instead of verifying it
#   Tools~/ci/check.sh --no-unitask  offline: skip the pinned UniTask fetch (the UniTask adapters are not built)
#
# Needs: python3, dotnet 8 SDK, and Unity managed DLLs (Tools~/ci/fetch-unity-dlls.sh, once).
# Optional (--full): actionlint, shellcheck. See Development~/docs/agents/tooling.md.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2

full=0; fix=0; shadow_args=()
for a in "$@"; do
  case "$a" in
    --full) full=1 ;;
    --fix) fix=1 ;;
    --no-unitask) shadow_args+=(--no-unitask) ;;
    *) echo "usage: $0 [--full] [--fix] [--no-unitask]" >&2; exit 2 ;;
  esac
done

status=0
step() { echo "== $*"; }
fail() { echo "FAIL: $*"; status=1; }

step "repo invariants"
python3 "Tools~/ci/check_repo.py" || fail "repo invariants"

step "format (dotnet format whitespace)"
fmt=(dotnet format whitespace --folder --exclude Runtime/Transport/BouncyCastle/Lib --exclude .build --exclude .tools -v q)
if [ "$fix" = 1 ]; then "${fmt[@]}" || fail "format"
else "${fmt[@]}" --verify-no-changes || fail "format (run: Tools~/ci/check.sh --fix)"; fi

step "shadow build + analyzers"
if [ "$full" = 1 ]; then variant=all; else variant=editor; fi
# The fetch script returns at once when the DLLs and the test packages (#297) are already there.
if [ -z "${UNITY_DLLS:-}${UNITY_PATH:-}" ]; then
  "Tools~/ci/fetch-unity-dlls.sh" >/dev/null || fail "fetch Unity DLLs"
fi
python3 "Tools~/ci/shadow_build.py" --variant "$variant" ${shadow_args[@]+"${shadow_args[@]}"} || fail "shadow build"

if [ "$full" = 1 ]; then
  step "workflow lint (actionlint)"
  if command -v actionlint >/dev/null; then actionlint || fail "actionlint"; else echo "skip: actionlint not installed"; fi
  step "shell lint (shellcheck)"
  if command -v shellcheck >/dev/null; then
    git ls-files -z '*.sh' '.githooks/*' | xargs -0 shellcheck || fail "shellcheck"
  else echo "skip: shellcheck not installed"; fi
  step "python tool tests"
  if [ -d "Tools~/agent-tools" ]; then
    python3 -m unittest discover -s "Tools~/agent-tools" -p 'test_*.py' -q || fail "agent-tools tests"
  fi
  python3 "Tools~/ci/test_doc_snippets.py" -q || fail "doc snippet harness tests"
  python3 "Tools~/ci/test_banned_symbols.py" -q || fail "banned-symbol (sync-over-async) gate test"
  python3 "Tools~/ci/test_shadow_build_tests.py" -q || fail "shadow build of Tests/ (Unity NUnit) gate test"
  python3 "Tools~/ci/test_il2cpp_filter_check.py" -q || fail "il2cppfilter invariant test"
  python3 "Tools~/ci/test_unitask_pin.py" -q || fail "UniTask pin gate test"
  python3 "Tools~/unity/test_platform_matrix.py" -q || fail "Unity platform matrix tool test"
  python3 .github/issue-seed/create_issues.py --dry-run >/dev/null || fail "issue seed validation"
  step "waves orchestration simulation"
  if command -v node >/dev/null; then node .claude/skills/turbohttp-waves/waves_sim.mjs >/dev/null || fail "waves_sim"
  else echo "skip: node not installed"; fi
fi

if [ "$status" = 0 ]; then echo "check.sh: OK"; else echo "check.sh: FAILED"; fi
exit "$status"
