# 2026-11: HTTP proxy bypass, cleartext policy, pool and DNS use the wire host (#378)

## What

HTTP counterpart of #368 (WebSocket, PR #379). Since #46/#340 the HTTP `Host`, `:authority`,
absolute-form and CONNECT authority carried the RFC 3986 wire host, but `RawSocketTransport` still
handed the raw `Uri.Host` (Unicode for an IDN; IPv6 bracketed on .NET, bare on Unity Mono) to the
cleartext policy, the connection pool and HTTP/2 keys, DNS and TLS, and
`ProxyEnvironmentResolver` matched bypass rules against it. Now:

- `WireHost.LookupHost(Uri[, wireHost])` (Core, internal) derives the lookup host: the A-label for an
  IDN (RFC 5890 Section 2.3.2.1; lookups use the A-label, RFC 5891 Section 5), an IPv6 literal
  without brackets (RFC 3986 Section 3.2.2: brackets are URI syntax), ASCII names and IPv4 literals
  unchanged. A host with no wire form (an unvalidated proxy address) falls back to `Uri.Host`.
- `RawSocketTransport.SendAsync` keeps the validated wire host from `HttpAuthority.HostFromUri` and
  uses its lookup host for the cleartext check, the pool and HTTP/2 keys, DNS and TLS. The proxy leg
  (cleartext check and `DispatchViaProxyAsync`'s `proxyHost`, which feeds pool keys and DNS) uses
  the proxy address's lookup host.
- `ProxyBypassMatcher.IsBypassed` compares in A-label form: a non-ASCII host and a non-ASCII rule
  (including the name after `*.` or `.`) are converted with `WireHost.ToALabel`; IPv6 brackets are
  ignored on both sides. `ProxyEnvironmentResolver.Resolve` passes the target's lookup host for the
  configured list and for `NO_PROXY`.
- The bypass rule port split no longer reads a bare IPv6 rule as `host:port` (`::1` used to become
  host `:` with port 1, so a bare IPv6 rule never matched); `[v6]:port` is recognized.
- The two cleartext refusal messages and the pool's DNS-timeout message no longer contain the host
  or the proxy host (CLAUDE.md section 3; #309/#368 precedent).
- `TcpConnectionPool` gained an internal constructor with a DNS resolver delegate (test seam, like
  `RawSocketWebSocketTransport`). The localhost shortcut (#322, RFC 6761 Section 6.3) still runs
  first and the DNS timeout still applies.

## Files

- `Runtime/Core/Internal/WireHost.cs` (`LookupHost`, `ToALabel`, `StripBrackets`, `IsAscii` internal)
- `Runtime/Core/ProxySettings.cs` (A-label matching, IPv6 port split, XML docs on the public
  `ProxyBypassMatcher.IsBypassed` and `BypassList`)
- `Runtime/Transport/RawSocketTransport.cs`
- `Runtime/Transport/Tcp/TcpConnectionPool.cs`
- `Tests/Runtime/Proxy/HttpWireHostConsumersTests.cs` (+ `.meta`)

## Decisions

- **Normalize both sides to A-label** (as #368): one comparison form, the DNS/wire form. Only
  non-ASCII text is rewritten, so ASCII rules and hosts compare exactly as before and allocate
  nothing extra. A rule with no A-label form is kept as written and matches only its own spelling.
- **Rules are normalized per call**, not cached: `BypassList` is a mutable public property and the
  `NO_PROXY` list is parsed per call, and ASCII rules (the common case) cost nothing.
- **IPv6 lookup form is bracket-free**, the form Unity Mono's `Uri.Host` already returned, so on
  Unity pool keys, DNS and the policy see the same string as before; on .NET `[::1]` loses its
  brackets. Bracketed rules (`[::1]`, `[::1]:8080`) keep matching because brackets are stripped on
  both sides.
- **TLS unchanged**: both providers pass the host through `TlsHostName.ToTargetHost`/`ToSniHostName`,
  which already turned a Unicode name into its A-label and stripped IPv6 brackets; on an A-label or a
  bare literal they are the identity. The certificate reference identity stays the A-label; nothing
  is accepted that was rejected before. One nuance: the A-label now comes from `Uri.IdnHost` (the same
  one the `Host` header carries) instead of `IdnMapping` on the Unicode name; for names where the two
  IDNA mappings differ, TLS now names the host the request is addressed to.
- **Android cleartext policy gets the A-label**, the form Android's own stacks pass to
  `NetworkSecurityPolicy.isCleartextTrafficPermitted`. Device behavior with an IDN domain-config is
  needs-device-validation.

## Deferred

None. `WebSocketProxySettings` keeps its own `ToALabel` copy (#368); it could call
`WireHost.ToALabel` now, left alone to stay within this issue's scope.

## Verified

- `'./Tools~/ci/check.sh'`: OK (invariants, format, shadow build of 33 assemblies with analyzers).
- Unity PlayMode `--filter 'HttpWireHostConsumersTests|ProxySettingsTests|CleartextTrafficPolicyTests|ProxySupportTests|RawSocketTransportCleartextTests|WireHost|TcpConnectionPool'`:
  183/183 passed (60 `HttpWireHostConsumersTests` cases). `Tests/Runtime` tests run on PlayMode; the
  EditMode filter selects none.
- Not run: the full Unity suites (periodic host-editor run), IL2CPP players, Android emulator or
  device (the Android policy with an IDN host is needs-device-validation), iOS.
