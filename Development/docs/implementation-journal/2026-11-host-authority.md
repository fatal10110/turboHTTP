# 2026-11 host-authority: ASCII Host / :authority (#46), InvalidRequest for bad H1 fields (#243)

Run w1102, lane host-authority, branch `wf/host-authority`.

## What

- **#46.** One helper, `HttpAuthority` (`Runtime/Transport/Internal/HttpAuthority.cs`), builds the
  wire authority: the A-label form of an IDN host (`Uri.IdnHost`, with an `IdnMapping` fallback if
  a runtime still returns Unicode), an IPv6 literal in exactly one pair of brackets without a zone
  id (both the bracketed and the bare `Uri.Host` spelling are accepted), and the port unless it is
  the scheme default. Used by the HTTP/1.1 `Host` header and absolute-form request target
  (`Http11RequestSerializer`), the HTTP/2 `:authority` (`Http2Connection.Send.BuildAuthorityValue`)
  and the CONNECT authority/Host (`RawSocketTransport.BuildAuthority`).
  - Before: CJK hosts threw from the Latin-1 writer, `münchen.de` went out as a raw 0xFC byte, and
    `:authority` for `https://[::1]/` was `[[::1]]` (`Uri.Host` already carries the brackets on .NET).
- **#46, UnityTls finding.** Unity's Mono SslStream (UnityTls) sends an IP literal as
  `server_name`. The provider cannot suppress it: UnityTls derives SNI from the target host, and the
  target host is also the name the certificate is checked against, so blanking it would weaken
  validation (CLAUDE.md section 3). Recorded as a known limitation in
  `Documentation~/PlatformNotes.md`; `SslStreamServerNameTests.SslStream_ClientHello_IpLiteral_SendsNoServerName`
  reports it as an environment ignore on Mono (with the captured ClientHello in the message) and
  still asserts no server_name on .NET. `SslStreamTlsProvider.cs` is unchanged.
- **#243.** `HttpFieldValidator` throws `HttpFieldValidationException` (internal, sealed,
  derives from `ArgumentException`, so serializer and validator tests keep their contract and HTTP/2's
  `catch (ArgumentException)` still maps it). `RawSocketTransport` catches that type just before
  the generic catch and maps it to `UHttpErrorType.InvalidRequest` with the exception as inner.
  Other `ArgumentException`s stay `Unknown`.

## Files

- `Runtime/Transport/Internal/HttpAuthority.cs` (+ meta), `HttpFieldValidationException.cs` (+ meta)
- `Runtime/Transport/Internal/HttpFieldValidator.cs`, `Http1/Http11RequestSerializer.cs`,
  `Http2/Http2Connection.Send.cs`, `RawSocketTransport.cs`
- `Documentation~/PlatformNotes.md`
- Tests: `Tests/Runtime/Transport/HttpAuthorityTests.cs`,
  `Tests/Runtime/Transport/Http2/Http2ConnectionTests.Authority.cs`,
  `Tests/Runtime/Transport/Http1/RawSocketTransportInvalidFieldTests.cs` (each + meta),
  `Tests/Runtime/Transport/Tls/SslStreamServerNameTests.cs`

## Decisions

- Mapping for #243 by a dedicated exception type (issue option a), not pre-validation: trailers are
  only known after the body, and the H1 header check already runs before any byte is written. The
  connection that was leased for the rejected request is disposed by the existing `finally`
  (`lease?.Dispose()` closes an unreleased connection), so it is never reused after a partial
  message (trailer case).
- The absolute-form target and the CONNECT authority use the same helper (same defect, same file
  or one line); the WebSocket module's Host and CONNECT authority are out of lane: #309.
- IPv6 zone ids are dropped from the authority (RFC 3986 IP-literal has none; .NET `Uri.Host`
  already drops it).
- UnityTls SNI for IP literals: documented limitation, no provider change (conservative option;
  also keeps the `sslstream-provider` lane's file untouched).

## Deferred

- #309 (v1.1): WebSocket handshake Host and WebSocket proxy CONNECT authority.

## Verification

- `./Tools~/ci/check.sh`: pass (invariants, format, shadow build with analyzers).
- Scoped Unity PlayMode run: see the PR's "Local gates" section.
- Not run: the full Unity EditMode/PlayMode suites (host-editor periodic run), IL2CPP player
  builds, devices. The UnityTls SNI behaviour is only observed in the host editor (Mono); players
  are assumed to behave the same (same UnityTls backend) but are not verified.
