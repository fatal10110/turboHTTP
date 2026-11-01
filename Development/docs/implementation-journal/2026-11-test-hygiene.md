# 2026-11 test hygiene (#222, #115, #190, #151, #152, #157)

Tests and test tooling only; no product code changed.

## What

- **#222** `Tools~/unity/run-tests.sh`: the `[ -n x ] && wslpath || true` one-liner (shellcheck SC2015) is an
  `if`; `shellcheck 'Tools~/unity/run-tests.sh'` is clean.
- **#115** Every test that reaches the public internet now carries `[Category("ExternalNetwork")]` (SslStream,
  BouncyCastle, TlsIntegration, TlsPerformanceBenchmark, TcpConnectionPool integration tests; the others already
  had it). Most sit inside `#if TURBOHTTP_INTEGRATION_TESTS`, a define nothing sets, so they are compiled out
  by default; tagged anyway. `ci.yml` already excludes the category; new `.github/workflows/nightly-external.yml`
  (cron + `workflow_dispatch`) runs only that category, and, like `ci.yml`, warns and passes without
  `UNITY_LICENSE` (#188).
- **#190** New `ignores` check in `Tools~/ci/check_repo.py` (default list, so `check.sh` and the ci lint job run
  it): `Assert.Ignore`, `Assert.Inconclusive`, `[Ignore(...)]` and `Ignore = "..."` in `Tests/**/*.cs` need `#<n>`,
  `[env]` or the `EnvironmentIgnorePrefix` constant in the message; a bare `[Ignore]` fails. Calls to
  `TestRequirements.IgnoreEnvironment(` are accepted (the helper adds `[env] `). Audit result: `Inconclusive` and
  `[Ignore]` have no hits; the "missing reflected type" class was turned into failures by #140; the #152 test is
  re-enabled (below); the two `DecompressionLimitAndAllocationTests` counter-availability skips now use
  `IgnoreEnvironment`. Every remaining skip is an `IgnoreEnvironment` caller. Tests:
  `Tools~/agent-tools/test_check_repo_ignores.py` (runs in `check.sh --full`).
- **#151** The four CONNECT tests already run with `TestLocalhostCertificate` (journal 2026-10-unity-host-run; a
  filtered PlayMode run of `RawSocketTransportProxyTunnel` this round skipped 0). What was left is the gate:
  `Tools~/unity/summarize_results.py` (called by `run-tests.sh`) lists every skipped case with its reason and
  exits 4 when a skip has no `#n`/`[env]` reason (any run) or when an unfiltered run skips more than
  `Tools~/unity/skip-baseline.txt` (`EditMode 0`, `PlayMode 0`). Filtered runs only list skips.
  Tests: `Tools~/agent-tools/test_unity_results_summary.py`.
- **#152** `ReplayBuilder_BuildsReplayRequestFromCapturedEvent` is replaced by two EditMode tests that capture a
  real request through `UHttpClient` + `MonitorInterceptor` + `MockTransport`: with default redaction the replay
  omits `Authorization` (the `[REDACTED]` placeholder is never sent) and warns "captured with redaction",
  truncation and transport-managed headers; with `RedactionPolicy.None` the header replays. The Editor test
  asmdef gains the `TurboHTTP.Testing` reference (test-only; `check_repo.py` leaves it unrestricted). SetUp/TearDown
  also restore `CaptureWithoutViewer` and `RedactionPolicy`.
- **#157** `Http2BodySizeTests` re-implemented the guard inline. It is deleted; `Http2ConnectionTests.ResponseBodyCap.cs`
  drives a real `Http2Connection`: DATA over `MaxResponseBodySize` fails the response (NetworkError) and the server
  receives RST_STREAM(CANCEL) (RFC 9113 6.4); two frames crossing the cap cumulatively fail on the second; a body
  exactly at the cap completes.

## Decisions

- #151 baselines are 0/0: every skip left is an environment skip that the periodic host-editor run is expected
  to satisfy, so a skip there means a test stopped executing. The unfiltered PlayMode suite hangs (#246), so the
  PlayMode number is unconfirmed; raise it with a justification if the first full run shows a legitimate skip.
- The CI half of #151 ("fails CI when the skipped count rises") cannot be met while the unity-tests job passes
  vacuously without a license (#188); it is bound to #188.
- `testing.md` should mention `skip-baseline.txt`, exit code 4 and the nightly workflow; docs-agents owns that
  file, so it is not edited here.

## Verified

- `./Tools~/ci/check.sh` (repo invariants incl. `ignores`, format, editor shadow build): OK.
- `python3 -m unittest discover -s Tools~/agent-tools -p 'test_*.py'`: pass; `shellcheck Tools~/unity/run-tests.sh`: clean.
- Unity 2021.3.45f2 (host editor, scoped): EditMode `HttpMonitorEditorTests|HttpMonitorRedactionSettingsTests` 9/9, 0 skipped;
  PlayMode `Http2ConnectionTests|RawSocketTransportProxyTunnel|MonitorInterceptorTests|DecompressionLimitAndAllocation|ResponseBodyCap`
  224/224, 0 skipped. With the read-loop guard neutralised locally (reverted, not committed) the two new cap tests
  and `PostHeaderBufferedFailure_ReportsNetworkError` fail.
- Not run: the full Unity suites (periodic host run), IL2CPP, device, the nightly workflow itself.
