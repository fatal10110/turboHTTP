# 2026-11 samples host run (#268, part of #98) and the TLS fallback rule check (#110)

Wave run w1102, lane test-infra-gaps. Base `origin/main` 52da510.

## #268: each `Samples~` sample imported and run in a clean 2021.3 project

### What

`Tools~/unity/run-samples.sh` (new; Unity ignores `Tools~`, PR CI never runs it) builds a clean throwaway project, adds the package as a `file:` dependency (no test-framework changes beyond the default, no `TURBOHTTP_BOUNCYCASTLE` define, so the customer default path), imports the samples and runs them in Play mode:

1. `Tools~/unity/samples-import/ImportSamples.cs` (run with `-executeMethod`) imports the five samples with `UnityEditor.PackageManager.UI.Sample.Import(OverridePreviousImports)`, the API behind the Package Manager "Import" button. The files land in `Assets/Samples/TurboHTTP - Complete HTTP Client/1.0.0-pre.1/<sample>/`. The helper is deleted from the project afterwards.
2. `Tools~/unity/samples-run/SamplesRunTests.cs` (+ `SamplesRun.asmdef.json`, renamed to `.asmdef` on copy so the repo asmdef rules do not scan it) is copied in. One PlayMode `[UnityTest]` per sample creates a GameObject, adds the sample MonoBehaviour (same as adding it to a scene and pressing Play), records every console message to `<project>/SamplesRun/<sample>.log` and waits for the sample's last console line (90 s timeout). `LogAssert.ignoreFailingMessages` is set because sample 01 logs an error on purpose. The verdict below comes from reading the recorded logs, the test only fails on a timeout.

The samples call public services, so nothing here is an NUnit test under `Tests/` or part of PR CI; run it by hand: `UNITY_PATH=/mnt/e/Unity/Hub/2021.3.45f2/Editor/Unity.exe 'Tools~/unity/run-samples.sh'` (exit 3 = Unity unavailable).

### Result (2026-11-03, Unity 2021.3.45f2, Windows host editor, Mono, Editor Play mode, `-nographics`)

Two runs from a fresh project (`SAMPLES_FRESH=1`), the first with a plain folder copy as the import, the second with the `Sample.Import` API: identical outcome.

| Sample | Compiles after import | Play mode | Network | Console output vs the sample's comments |
| --- | --- | --- | --- | --- |
| 01 BasicUsage | yes, no errors | PASS (1.6 s) | httpbin.org | Examples 1-5 print status/body as documented; example 6 (`/status/404`) logs `Error: HttpError - HTTP request failed with status 404 NotFound` through `Debug.LogError`, which is what the sample does. |
| 02 JsonApi | yes | PASS (1.3 s) | jsonplaceholder.typicode.com | 100 posts, post 1, created id 101, updated title, delete status `OK`. |
| 03 FileDownload | yes | PASS (1.1 s) | httpbin.org `/bytes/1048576` | 64 progress lines to 100 %, `Downloaded 1048576 bytes`, saved to `%USERPROFILE%\AppData\LocalLow\DefaultCompany\<project>\downloaded.bin` (`Application.persistentDataPath`; the runner deletes a stale file first). |
| 04 Authentication | yes | PASS (1.3 s) | httpbin.org `/bearer` | Token fetched once, `/bearer` answers `authenticated: true` with the token, `InvalidateToken()` triggers a second fetch, `Token was refreshed automatically!`. |
| 05 AdvancedFeatures | yes | PASS (7.7-8.0 s) | httpbin.org `/cache/60`, `/status/503`, `/delay/1` | Second `/cache/60` served from cache (5 ms vs 183 ms); `/status/503` is retried with backoff (about 3.5 s of the run) and returns `Final status: ServiceUnavailable` as a response, not an exception (error model: 4xx/5xx are normal responses); six `/delay/1` requests all `OK`; metrics total 9, successful 8, failed 1 (the 503). |

No compiler warning or error comes from the sample assemblies. The only non-sample console output is the one-time `UHttpClientOptions.FollowRedirects is true but no RedirectInterceptor is configured` warning on the first `new UHttpClient()` of the process (sample 01 when run first). That is the documented diagnostic from #64 (`Documentation~/Modules/Core.md`), not a sample defect. No deviation found, so no follow-up issue was filed.

### Not run

- The Package Manager window click itself (GUI). The API the button calls was used; the result is the same files in the same place.
- Mono or IL2CPP player builds of the samples, any device, iOS/Android: needs-device-validation, tracked by #99/#103.
- Repeat runs against httpbin.org flakiness: both runs passed; a 503/429 from httpbin would need a rerun before filing a defect.

## #110: TLS fallback rule

Re-verified on `origin/main` (code and docs were reconciled by #195, see `2026-10-tls-auto-failclosed.md`):

- The one-sentence "TLS fallback rule (#34, #110)" is quoted identically in `phase22c/overview.md:33`, `phase-22c-proxy-http2-alpn.md:530`, `phase22c/phase-22c.4-validation.md:132`, plus `phase-22c.2` and `phase-22c.3` and `agents/platform.md:17`.
- `rg -n "no runtime catch-and-retry" Development/docs/phases` returns only those rule sentences.
- `Runtime/Transport/Tcp/TcpConnectionPool.cs` (`CreateConnectionAsync` TLS block) has no provider retry any more.
- The AC3 tests exist in `Tests/Runtime/Transport/TcpConnectionPoolTlsFallbackTests.cs` (`Auto_CertificateFailure_PropagatesWithoutProviderSwitch`, `Auto_PlatformTlsException_PropagatesWithoutProviderSwitch`), not in `TcpConnectionPoolTests` (a shared hot file). Both ran on the host editor (PlayMode `--filter TcpConnectionPoolTlsFallbackTests`): 2 passed, 0 skipped.

This lane adds a "Superseded (#34, #110)" note under the title of four older docs that still described the Auto-falls-back-to-BouncyCastle design: `phase3c/step-3c.8-provider-selector.md`, `phase3c/step-3c.10-config-api.md`, `phase-03c-bouncy-castle-tls.md`, `phase19a/phase-19a.6-system-tls-priority.md`.

### Deferred

- The fourth quoted spot, "22d.2 Step 4.5": `Development/docs/phases/phase22d/` does not exist in the repo. #125 (v2.0, "Correct the Phase 22d plan before implementation") carries the amendment that whoever corrects 22d.2 must quote the sentence verbatim. #110 therefore stays open ("Part of #110"); closing it is the owner's call once #125 lands.
