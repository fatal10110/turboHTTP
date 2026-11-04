# External TLS rows and the Happy Eyeballs family interleave (#361, #362)

Run w1108, lane external-tls-rows. Date 2026-11-04.

## What

- **#362 root cause: ours, not UnityTLS.** The 10 s "timeout" of `HttpClient_WithSslStream_CanFetchGoogle`
  on the API 23 emulator is the TCP connect deadline (`ConnectionPoolOptions.DefaultConnectTimeout`).
  It is not specific to SslStream: the same pool request over BouncyCastle and Auto timed out too.
  BouncyCastle only looked healthy because `BouncyCastleProviderTests` dials with its own `Socket.BeginConnect`.
  - Phases, measured with a temporary triage test on the API 23 emulator (run `run-20261104-165454`):
    - DNS answers at once: 8 IPv4 and 8 IPv6 addresses for www.google.com.
    - A raw IPv6 connect hangs (no IPv6 route; the SYN is dropped). A raw IPv4 connect takes 4 ms.
    - SslStream over IPv4 handshakes in about 90 ms (TLS 1.2), with or without ALPN. BouncyCastle over
      IPv4 takes about 85 ms (TLS 1.3, h2). SNI, ALPN, cipher choice and the GlobalSign cross-signed
      chain were all fine.
    - `TcpConnectionPool.ConnectWithDeadlineAsync` failed with `TCP connect timed out after 10000ms`.
      For docs.google.com (1 IPv6 and 1 IPv4 address) it connected in 255 ms.
  - Cause: `HappyEyeballsConnector.BuildSchedule` listed every preferred-family (IPv6) address before
    the first IPv4 one: v6 at 0 ms, v6 at 125 ms, v4 at 250 ms. With `MaxConcurrentAttempts = 2`, the
    two blackholed IPv6 attempts held both slots, so the IPv4 attempt never started.
- **Fix:** the schedule interleaves the families as RFC 8305 section 4 describes (First Address Family
  Count 1): preferred[0] at 0, alternate[0] at `FamilyStaggerDelay`, then preferred[1], alternate[1], …
  Each later attempt starts `AttemptSpacingDelay` after the previous one (section 5). When one
  family runs out, the rest of the other family follows.
- **#361:** new `TlsPublicHostTests` (ExternalNetwork, `TURBOHTTP_INTEGRATION_TESTS`) sends
  `UHttpClient` requests on SslStream, BouncyCastle and Auto:
  - to self-signed, untrusted-root, expired and wrong.host badssl.com, asserting `CertificateError`;
  - to a valid chain (www.google.com), asserting 200.
  - Unreachable hosts are `[env]` skips, never passes:
    - which failures count: `OperationCanceledException`, `UHttpException(Timeout)`, or `NetworkError`
      with a `SocketException` in the chain;
    - a bad-certificate host that answers fails the test;
    - any other failure fails the test.
- The existing valid-chain tests move off ISRG/Amazon-rooted hosts:
  - `TlsIntegrationTests` (sha256.badssl.com, httpbin.org) now use www.google.com;
  - `BouncyCastleProviderTests.WrapAsync_WildcardCert_Succeeds` (sha256.badssl.com) now uses
    docs.google.com, a `*.google.com` wildcard.
  - `SendWithGuardedTimeoutAsync` now reports a timeout or cancel as an `[env]` skip that carries the
    transport message, instead of an `Assert.Fail` that dropped it.

## Files

- `Runtime/Transport/Connection/HappyEyeballsConnector.cs`: `BuildSchedule` interleaves, `NextDue`;
  summary doc updated.
- `Tests/Runtime/Transport/HappyEyeballsInterleaveTests.cs` (new): 8 unroutable IPv6 + 8 IPv4 with
  default options connects over IPv4; the dial order alternates families. Both fail on the old code
  (`TimeoutException ... 5000ms`; wrong order).
- `Tests/Runtime/Transport/Tls/TlsPublicHostTests.cs` (new).
- `Tests/Runtime/Transport/Tls/TlsIntegrationTests.cs`, `BouncyCastleProviderTests.cs`: hosts repointed,
  env-skip helper.

## Decisions

- Host choice (chains read with `openssl s_client` on 2026-11-04):
  - Android 6 has no ISRG Root X1/X2 and no Amazon Root CA 1.
  - Valid rows use www.google.com and docs.google.com: WE2, then GTS Root R4, cross-signed by
    GlobalSign Root CA, which is in the API 23 store.
  - www.digicert.com (DigiCert Global Root G2) also passed SslStream on API 23 in triage. It is not
    used in tests.
  - wrong.host.badssl.com chains to ISRG Root X1, so API 23 rejects it on two grounds (name and root).
    Both are certificate errors, so the assertion holds either way.
- `MaxConcurrentAttempts` keeps its meaning, as decided in `2026-11-transport-connect.md`. A third
  attempt still waits while two attempts hang, but those two are now one per family, so a dead
  family no longer blocks the other one. The connect deadline bounds the remaining case, where both
  families are blackholed at the first address.
- No platform-limitation note and no SslStream fail-fast: the bug is in our connect schedule, not in
  UnityTLS on Android 6.

## Deferrals

None.

## Verification

- `./Tools~/ci/check.sh`: pass.
- Host Editor (Windows, Unity 2021.3.45f2):
  - PlayMode `--filter HappyEyeballs`: 15/15. The 2 new tests fail on the old connector, 13/15 there.
  - PlayMode `--category ExternalNetwork --integration --filter 'TlsIntegrationTests|BouncyCastleProviderTests|TlsPublicHostTests'`:
    26/26.
- Emulators (x86_64 IL2CPP, Stripping High), run `run-20261104-170732`, same filter plus the temporary
  triage fixture:
  - API 23: 26/26 non-triage pass.
  - API 33: 26/26 non-triage pass.
  - Triage on both: the pool connect to www.google.com finishes in 256 ms over IPv4; the
    SslStream, BouncyCastle and Auto requests return 200.
  - The run at the pushed head is posted on #99, #361 and #362.
- Not run:
  - iOS (simulator or device) and a physical Android device;
  - real NAT64 or IPv6-only networks. With one address family the schedule is unchanged.
  - Every emulator row stays `needs-device-validation`.
