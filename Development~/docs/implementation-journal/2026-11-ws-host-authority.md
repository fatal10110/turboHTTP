# 2026-11 WebSocket Host header and CONNECT authority use the wire host (#309)

## What
The WebSocket handshake `Host` header and the proxy CONNECT authority were built from `Uri.Host`:
raw Unicode for an IDN (`wss://münchen.de/`), and `CONNECT ::1:443` for a bare Mono IPv6 host.
Both now go through the same RFC 3986 wire-host logic as the HTTP stack (#46, #340): A-label IDN,
IPv6 literal in one pair of brackets without a zone id, and the #340 rejections (empty wire host,
`xn-- -dha.de` from U+00A0, labels over 63, names over 253, sub-delims, bracketed non-IP).

## Files
- `Runtime/Core/Internal/WireHost.cs` (new): `TryFromUri` / `TryFormat`, the host logic moved out of
  `HttpAuthority` (the WebSocket core references only Core; same move as `AbandonOnCancel`, #357).
  Try-pattern with an error message, so each caller keeps its own error model.
- `Runtime/Transport/Internal/HttpAuthority.cs`: `HostFromUri` / `FormatHost` forward to `WireHost`
  and still throw `HttpFieldValidationException` with the same messages (HTTP callers and tests
  unchanged).
- `Runtime/WebSocket/WebSocketHandshake.cs`: `BuildHostHeader` uses `WireHost.TryFromUri`; failure is an
  `ArgumentException` (the public `BuildRequest` contract), documented with `<exception>`. Port rule
  unchanged (ws 80, wss 443 omitted).
- `Runtime/WebSocket.Transport/RawSocketWebSocketTransport.cs`: `ValidateUri` checks the wire host and
  throws `WebSocketException(HandshakeFailed)` before the cleartext policy, DNS or any socket. This
  replaces the `IsNullOrWhiteSpace(uri.Host)` `ArgumentException`, so the Mono empty-host case is
  covered too and `WebSocketConnection` keeps the error (an `ArgumentException` became a retryable
  `ConnectionClosed`).
- `Runtime/WebSocket.Transport/ProxyTunnelConnector.cs`: authority = wire host + ":" + port (always
  with the port, RFC 9110 Section 9.3.6); an invalid host throws `HandshakeFailed` and writes nothing.
- `Tests/Runtime/WebSocket/WebSocketHostAuthorityTests.cs` (new, 43 cases).

## Decisions
- No new `WebSocketError` member: `HandshakeFailed` (non-retryable) is the InvalidRequest equivalent the
  lane note asked for; no public enum change.
- `BuildRequest` keeps throwing `ArgumentException` (public contract); the transport throws
  `WebSocketException` because only that survives `WebSocketConnection.ConnectAsync`'s mapping.
- Messages never contain the host (shared constants in `WireHost`).

## Deferred
- #368: `RawSocketWebSocketTransport` still uses the Unicode `Uri.Host` for proxy bypass, the
  cleartext policy (and its message) and DNS. TLS is already normalized by `TlsHostName` (#142/#46).

## Verified
- `./Tools~/ci/check.sh` (invariants, format, shadow build + analyzers): OK.
- Host Editor (Unity 2021.3.45f2, Mono) PlayMode `--filter 'WebSocketHandshakeTests|WebSocketHostAuthority|ProxyTunnelConnectorTests|WebSocketProxyTests|WebSocketCleartextPolicyTests|HttpAuthority'`:
  135 passed, 0 failed, 0 skipped (43 new). The EditMode run with the same filter matched 0 tests (the
  runtime tests run under PlayMode).
- API 29 emulator (x86_64 IL2CPP, High stripping): see the PR body.
- Not run: iOS, devices, macOS, the full Unity suites (host editor, periodic).
