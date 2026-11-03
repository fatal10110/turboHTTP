# 2026-11 BouncyCastle client: explicit suites, no OCSP request, handshake deadline, SNI (#142)

Lane bc-client (waves run w1030). Part of #142: AC2/AC3 full handshakes moved to #230. Takes the
SNI part of #46 (#46 keeps the Host header / `:authority` part).

## What changed
- `TurboTlsClient` now makes each security-relevant choice an explicit override, so it holds
  whatever the vendored library's defaults are:
  - `GetSupportedCipherSuites()` returns `OfferedCipherSuites`: the TLS 1.3 AEAD suites, then TLS 1.2
    ECDHE with AEAD only (0x1303, 0x1301, 0x1302, 0xCCA9, 0xC02B, 0xC02C, 0xCCA8, 0xC02F, 0xC030). The list
    is filtered by crypto support, and the engine adds SCSV 0x00FF on the wire.
  - `GetCertificateStatusRequest()` and `GetMultiCertStatusRequest()` return null.
  - `RequiresCloseNotify()` is true. With it false, `TlsProtocol.CloseInput()` would treat EOF as a
    clean close.
  - `GetHandshakeTimeoutMillis()` returns 30 s, or the provider's value.
  - SNI goes through the library hook `GetSniServerNames()`. The hand-built UTF-8 extension is gone.
- New `Runtime/Transport/Tls/TlsHostName` (internal), shared by both providers:
  - Strips IPv6 brackets.
  - Detects IP literals with `Uri.CheckHostName`.
  - Converts the SNI name to its A-label with `IdnMapping`, minus any trailing dot. A literal gets
    no SNI.
  - Transport `AssemblyInfo` adds InternalsVisibleTo for `TurboHTTP.Transport.BouncyCastle`, which
    already references Transport.
- `SslStreamTlsProvider.WrapAsync` passes `TlsHostName.ToTargetHost(host)` as TargetHost on both the
  ALPN and the non-ALPN paths: the A-label, or the bare address. Certificates carry A-labels
  (RFC 5280 section 7.2), so name matching gets more accurate, not weaker. The validation callback
  is unchanged.
- `BouncyCastleTlsProvider.WrapAsync` bounds the whole handshake:
  - A `CancellationTokenSource.CancelAfter(timeout)` disposes the inner stream, the same mechanism
    as caller cancellation.
  - A timeout surfaces as `IOException("TLS handshake timed out after N ms.")` with an inner
    `TimeoutException`, per the `ITlsProvider` contract (retryable network error).
  - Caller cancellation still wins and surfaces as OperationCanceledException.
  - The race where the handshake completes as the deadline fires disposes the result, like the
    existing `ct` race.
  - An internal `[Preserve]` constructor `(int handshakeTimeoutMillis)` lets tests use short bounds,
    reached by reflection because the test asmdef does not reference the glue.

## Decisions
- **Where the deadline lives.** BouncyCastle checks `GetHandshakeTimeoutMillis` only in
  `BlockForHandshake()` (blocking mode). The provider runs the engine non-blocking (#169), so the
  provider enforces the deadline itself. A single overall deadline covers both a silent peer (AC4)
  and a trickling peer (AC5). There is no separate per-read timeout: each read happens inside the
  deadline.
- **Socket ReceiveTimeout** (from the issue text) is not used. The provider only sees a `Stream`,
  and ReceiveTimeout does not bound async, SAEA or poll reads. Disposing the inner stream at the
  deadline is the read bound.
- **Interaction with other timeouts.** The connect timeout (Happy Eyeballs) ends at TCP connect. The
  request timeout (`ct`) covers connect plus handshake. The effective handshake bound is
  min(30 s, remaining request timeout).
- **Error type.** A timeout is an IOException, which `RawSocketTransport` maps to NetworkError. It
  only maps to `UHttpErrorType.Timeout` when the request timeout fired. Changing the transport's
  catch ladder was out of scope.
- **Certificate evaluation bound.** `NotifyServerCertificate` runs synchronously inside
  `OfferInput`, so the deadline only takes effect once it returns. Its own bound belongs to #33
  (comment posted there).
- **Close_notify truncation (AC6)** already worked on main:
  - `ProcessCiphertext` calls `CloseInput()`, which throws `TlsNoCloseNotifyException`, an
    EndOfStreamException and so an IOException.
  - Nothing in Transport/Core catches EndOfStreamException, and the H1 ReadToEnd paths propagate it.
  - It surfaces as `UHttpException(NetworkError)` with that inner exception.
  - The new end-to-end test pins this. `RequiresCloseNotify()` is now explicit.
- **Hostname matching** in `TurboTlsAuthentication` still uses the caller's host (#33 owns it).
- **AC2/AC3.** The netstandard2.1 test assembly cannot pin a server cipher suite, so it cannot run
  the full handshakes these criteria ask for. Scripted ServerHello tests prove that 0xC030 and
  0x1302 are accepted and that a static-RSA selection is rejected. The full handshakes are tracked
  in #230.

## Files
- `Runtime/Transport/BouncyCastle/TurboTlsClient.cs`, `BouncyCastleTlsProvider.cs`
- `Runtime/Transport/Tls/TlsHostName.cs` (+ .meta), `SslStreamTlsProvider.cs`, `Runtime/Transport/AssemblyInfo.cs`
- Tests in `Tests/Runtime/Transport/Tls/`:
  - `ClientHelloCapture.cs` (helper)
  - `BouncyCastleClientHelloTests.cs`
  - `BouncyCastleHandshakeTimeoutTests.cs`
  - `BouncyCastleTruncationTests.cs`
  - `TlsServerNameTests.cs`
  - `SslStreamServerNameTests.cs`

## Verification
- `'./Tools~/ci/check.sh'`: pass.
- Unity is not run in lanes (host editor, periodic).
- As a stand-in, the new tests plus `BouncyCastleHostileCertificateTests` were compiled into a
  scratch net8.0 NUnit project against the shadow-built DLLs.
  - With the change: all pass.
  - Against a shadow build of the base (7ac5c95), 11 fail: the BC SNI tests for literals, the
    trailing dot and IDN; the SslStream IDN test; and both timeout tests and the three suite tests,
    which need the new constructor.
  - These pass on the base too, so they pin existing behavior: the truncation test, the
    status_request test, and the SslStream literal tests (CoreCLR omits SNI for addresses).
- Not run:
  - Unity EditMode/PlayMode.
  - Mono/UnityTls SslStream SNI for literals. This is platform behavior; the host editor run shows
    it.
  - IL2CPP: `IdnMapping`, `Uri.CheckHostName`.
  - iOS/Android devices (#99).
