# 2026-11: WebSocket proxy bypass, cleartext policy and DNS use the wire host (#368)

## What

After #309 the WebSocket handshake `Host` header and the CONNECT authority carried the RFC 3986
wire host, but `RawSocketWebSocketTransport.ConnectAsync` still handed the raw `Uri.Host` (Unicode
for an IDN) to proxy bypass, the cleartext policy and DNS. Now:

- `ValidateUri` returns the wire host it already computed; `GetLookupHost` derives the host the
  three consumers see: the A-label for an IDN (RFC 5890 Section 2.3.2.1; lookups use the A-label,
  RFC 5891 Section 5), an IPv6 literal without brackets, ASCII names and IPv4 literals unchanged.
- `WebSocketProxySettings` normalizes each non-ASCII bypass rule (or the name after `*.`) to its
  A-label once in the constructor, and a non-ASCII `ShouldBypass` argument the same way, so A-label
  and Unicode rules both match `wss://münchen.de/` and `www.münchen.de`.
- The proxy leg uses the proxy URI's lookup host the same way (falls back to `Uri.Host` when the
  proxy URI has no wire host; it is only checked for a non-blank host).
- The two `CleartextNotPermitted` messages, the DNS timeout message and the `ProxyConnectionFailed`
  message no longer contain the host or the proxy URI (the #309 precedent; CLAUDE.md section 3).
- An internal constructor takes a DNS resolver delegate (test seam). The localhost shortcut (#322,
  RFC 6761 Section 6.3) still runs first and never reaches it.

## Files

- `Runtime/WebSocket.Transport/RawSocketWebSocketTransport.cs`
- `Runtime/WebSocket/WebSocketProxySettings.cs` (XML docs on the public `ShouldBypass`)
- `Tests/Runtime/WebSocket/WebSocketWireHostConsumersTests.cs` (+ `.meta`)

## Decisions

- **Bypass compatibility: normalize both sides to A-label** rather than comparing both spellings:
  one comparison form, and it is the DNS/wire form. Only non-ASCII text is rewritten, so ASCII rules,
  ASCII hosts and IP literals compare exactly as before (no change for `[::1]` vs `::1` rules either).
  A rule with no A-label form (for example a 70-character Unicode label) is kept as written and
  matches only its own spelling; it never throws.
- **IPv6 lookup form is bracket-free** (`::1`): what Unity Mono's `Uri.Host` already returned, so on
  Unity nothing changes; .NET's `[::1]` loses its brackets. The zone id handling is whatever
  `Uri.Host` does (unchanged), not `WireHost`'s zone stripping.
- **TLS server name unchanged** (`Uri.Host`): `SslStreamTlsProvider`/`TurboTlsClient` already
  normalize through `TlsHostName`.
- **Messages**: the issue named only the cleartext messages; the DNS timeout and proxy-failure
  messages in the same method also embedded the host or the full proxy URI (which may carry
  userinfo), so they were made host-free too.

## Deferred

- #378 (v1.1): the HTTP transport has the same gap (`RawSocketTransport` cleartext policy/DNS,
  `ProxyBypassMatcher` in `ProxySettings.cs`, host in cleartext messages). Out of #368's scope.
- Android `NetworkSecurityPolicy` receiving the A-label is `needs-device-validation` beyond the
  emulator run below (the policy is replaced by a recording delegate in the tests).

## How verified

- `'./Tools~/ci/check.sh'`: OK.
- Host Editor PlayMode `--filter 'WebSocketWireHostConsumers|WebSocketHostAuthority|WebSocketCleartextPolicy|WebSocketProxy|ProxyTunnelConnector|LocalhostResolution|Nat64AddressSynthesizer|WebSocketConnect'`:
  161/161 passed (31 new cases).
- API 29 emulator (x86_64 IL2CPP, High stripping) `--filter 'WebSocketWireHostConsumers|WebSocketHostAuthority|WebSocketCleartextPolicy|WebSocketProxy|LocalhostResolution'`: EMU_RESULT
- Not run: full EditMode/PlayMode suites (periodic host-editor run), iOS, physical devices.
