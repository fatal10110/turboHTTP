# Unity host-editor runs

Full EditMode + PlayMode runs in a local Unity 2021.3.45f2 editor (testing.md, "Periodic host-editor
runs"). PR CI skips Unity tests because the repository has no `UNITY_LICENSE` secret (#188), so
these runs are the only real Unity check. Append one dated section per run.

## 2026-10-31: first run (main 5dfa76e, after waves run w1030 wave 1)

Host: Windows Editor driven from WSL (`UNITY_PATH=/mnt/e/Unity/Hub/2021.3.45f2/Editor/Unity.exe`).

### As found on main

- Compile: **failed**. `Tests/Runtime/Transport/Tls/BouncyCastleHandshakeThreadPoolTests.cs:20` used
  `[NonParallelizable]` (merged in #199). That attribute is missing from `com.unity.ext.nunit` 1.0.6,
  so the whole `TurboHTTP.Tests.Runtime` assembly did not compile in Unity.
- After removing the attribute:
  - EditMode: 25 total, 24 passed, 0 failed, 1 skipped (#152).
  - PlayMode: 1329 total, 1316 passed, 2 failed, 11 skipped.
    - Failed: `ProxySettingsTests.EnvProxyPrecedence_ResolvesCorrectly` and
      `HttpsProxy_FallbackToHttpProxy_WhenEnabled`. Windows environment names are case-insensitive:
      the tests set `HTTPS_PROXY` and then cleared `https_proxy`, which is the same variable on
      Windows. Test bug only.
    - Skipped (11): every loopback real-TLS test, because Unity's Mono cannot run
      `CertificateRequest`. That includes the acceptance tests of #195 and #199, which had therefore
      never run in Unity (#151).

### Fixed in this PR

- `[NonParallelizable]` removed. New `check_repo.py unitynunit` invariant rejects NUnit APIs Unity
  lacks (`[NonParallelizable]`, `Assert.Multiple`, `Assert.*Async`) and `async Task` test methods.
  The list was checked against Unity's `nunit.framework.dll`.
- `ProxySettingsTests`: clear the lowercase name before setting the uppercase one. PlayMode filter
  `ProxySettingsTests`: 7/7.
- `TestLocalhostCertificate`: a committed test-only localhost PKCS#12 (SHA1/3DES so Mono reads it)
  replaces runtime generation in `LocalTlsTestServer` and `RawSocketTransportProxyTunnelTests`.
  PlayMode filter over the six TLS fixtures: 35 total, 33 passed, **2 failed, 0 skipped**.
- `Tools~/unity/run-tests.sh` drives a Windows `Unity.exe` from WSL.

### Left open

- #206: `BouncyCastleHandshakeThreadPoolTests.ConcurrentHandshakes_OverSaea_WithClampedThreadPool_CompleteWithoutBlockingIo`
  (WSAECONNABORTED during the SAEA handshake) and
  `BouncyCastleOutputBacklogTests.Read_Fails_WhenQueuedOutputExceedsBound_WhileWriterIsStalled`
  (close_notify error instead of the backlog error) fail on Unity Mono.
- #151 stays open for its CI criterion (skip-count gate), which needs a Unity license in CI.

Not run: IL2CPP player builds, devices (#99).

## 2026-11-01: after waves run w1030 (main 45f0a56)

- EditMode: 52 total, 51 passed, 0 failed, 1 skipped (#152).
- PlayMode: **did not complete.** The run hung right after the `[Phase19][Throughput]` output and was
  killed after about 1 h 40 min without log progress (a healthy full run takes about 40 s).
  - `StreamingAllocationGateTests.CacheRevalidation200Streaming_ResponseStart_DoesNotAllocateFullBody`
    (added in #220) hangs on its own. Cause: background cache work starts with `await Task.Yield()`,
    which resumes on Unity's main thread, and a foreground write for the same URL waits on it while the
    test blocks that thread. Filed as #245 (library fix).
  - With that test excluded, the suite still hangs when the `Performance` fixtures run together; each
    passes alone. Filed as #246 (bisection stopped at the namespace level).
- `run-tests.sh` has no per-test timeout, so a synchronous deadlock stalls the whole run silently.
  Watch the log's modification time; a run with no log progress for a few minutes has hung.

Not run: IL2CPP player builds, devices (#99).
