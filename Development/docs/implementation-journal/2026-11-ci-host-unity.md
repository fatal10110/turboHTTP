# 2026-11 CI has no Unity: host-editor gates (#188, #100, #115, #263)

Wave run w1102, lane ci-host-unity. Base `origin/main` 5f98945.

## What

Owner decision 2026-11-02 (comments on #188 and #100): no Unity license in CI, ever. Unity runs only in the local host editor.

- `.github/workflows/ci.yml`: the `unity-tests` job (game-ci, license-guarded, passed vacuously without secrets) is deleted; the `ci` aggregate (the one required check) now needs `lint`, `tls-interop`, `h2-interop`. No ruleset change (it requires only the context `ci`).
- `.github/workflows/nightly-external.yml` deleted. It was a no-op without the license (it ran 17 s and only printed the license warning), so the nightly never ran anything.
- `Tools~/unity/run-tests.sh`: new `--integration` flag, implied by `--category ExternalNetwork`. It adds `TURBOHTTP_INTEGRATION_TESTS` to the test project's Standalone defines so the gated internet blocks compile (#263); every other run removes it, so a scoped run never compiles them (#263 AC b). The define helper manages both `TURBOHTTP_BOUNCYCASTLE` and `TURBOHTTP_INTEGRATION_TESTS`.
- `Tools~/unity/build-player.sh` header: player builds are a host-editor gate, not a CI job.
- `Development/docs/agents/testing.md` ("Host-editor gates", replaces "Periodic host-editor runs") and `tooling.md`: the full EditMode/PlayMode run with the skip baseline, the ExternalNetwork run and `build-player.sh` are the Unity gates; the `unity-tests` secrets section is replaced by "Unity license: none".
- Follow-up #297 (v1.1): compile the `Tests/` asmdefs in the shadow build (#100 scope addition from #140).

## Decisions

- The two tests behind the same define that are not internet tests (`Parse_ReadToEnd_ExceedsMaxBodySize_ThrowsIOException`, 100 MB body; `RawSocketTransport_FreshConnection_IOException_NoRetry`, loopback RST) stay gated (the 100 MB allocation is deliberate) and are run with `--integration --filter`, documented in testing.md. They are not `ExternalNetwork`, so #263's AC does not cover them.
- #100 items withdrawn by the owner decision, not delivered: 2021.3 + latest-LTS matrix, CI IL2CPP player builds, CI coverage artifacts, Unity license secrets. Delivered elsewhere: fuzz job (#112), samples compile check in CI and host run (#98, #268), player builds (`build-player.sh`, #273), skip gate (#151).
- `.claude/skills/turbohttp-waves/waves.js:102` still says "its unity-tests jobs skip" and quotes the old heading; it is a shared hot file and was left alone (the instruction stays correct: CI does not run Unity tests).

## Host ExternalNetwork run (Unity 2021.3.45f2, Windows host editor driven from WSL, PlayMode, 2026-11-03, base 5f98945 + this branch)

`run-tests.sh --platform PlayMode --category ExternalNetwork`: total 25, passed 25, failed 0, skipped 0, inconclusive 0 (13.1 s), no compile errors. By class: TlsIntegrationTests 4, TlsPerformanceBenchmarkTests 2, BouncyCastleProviderTests 7, SslStreamProviderTests 6, TcpConnectionPoolTests 3 (all formerly compiled out), IntegrationTests 2, PlatformTests 1. No fix, no Ignore, no new issue was needed (#263 AC c: nothing failed). Project defines during the run: `TURBOHTTP_BOUNCYCASTLE;TURBOHTTP_INTEGRATION_TESTS`.

Define lifecycle checks: `--platform PlayMode --integration --filter 'ExceedsMaxBodySize|FreshConnection_IOException_NoRetry'` passed 5/5 (define present); the following `--filter 'Http11ResponseParser'` run passed 99/99 and the project defines went back to `TURBOHTTP_BOUNCYCASTLE` only.

## Not run

Full EditMode/PlayMode suites (periodic host run), `build-player.sh` (script header text only changed), IL2CPP, devices (#99). The workflow edit is observable only on the PR: `ci` must be green with three jobs.
