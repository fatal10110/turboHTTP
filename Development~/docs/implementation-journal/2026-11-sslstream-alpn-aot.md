# SslStream ALPN options without reflection (#303)

Wave w1110, lane `sslstream-alpn-aot`. Part of #303 (the IL2CPP player log and the device h2 row
are not run here).

## What

`SslStreamTlsProvider` built the ALPN options through reflection: `Expression.Lambda(...).Compile()`
for the options constructor, `typeof(List<>).MakeGenericType(SslApplicationProtocol)` with
`ConstructorInfo.Invoke`/`MethodInfo.Invoke("Add")` for the protocol list, `MethodInfo.Invoke` for
`AuthenticateAsClientAsync(options, ct)` and `PropertyInfo.GetValue` for
`NegotiatedApplicationProtocol`. Under IL2CPP nothing forced AOT code for
`List<SslApplicationProtocol>`, and the `ExecutionEngineException`/`NotSupportedException` such a
gap throws was not in `IsAlpnSetupFailure`, so the connect failed instead of falling back.

Now the provider calls the .NET Standard 2.1 API directly:

- `BuildAlpnOptions(host, protocols)` (internal, `NoInlining`) creates
  `new List<SslApplicationProtocol>` and `new SslClientAuthenticationOptions { TargetHost,
  EnabledSslProtocols = HandshakeProtocols, ApplicationProtocols }`. The static call site is what
  makes IL2CPP generate the instantiation.
- `ResolveAlpnOptions(builder, ...)` (internal) runs the builder before the SslStream exists and maps
  an `IsAlpnSetupFailure` exception to the plain handshake (http/1.1 offered) or
  `PlatformNotSupportedException` (no http/1.1). Other exceptions propagate.
- `IsAlpnSetupFailure` adds `NotSupportedException` (covers `PlatformNotSupportedException`),
  `NotImplementedException` and `ExecutionEngineException`. `InvalidOperationException` stays out
  (#171).
- The handshake and the result read are direct calls (`AuthenticateAsClientAsync(options, ct)`,
  `NegotiatedApplicationProtocol.ToString()`), each in a `NoInlining` helper; `WrapAsync` holds the
  options as `object`.
- `_alpnSupported` is a type-initializer probe of the same builder (any exception means false).

## Files

- `Runtime/Transport/Tls/SslStreamTlsProvider.cs`
- `Runtime/Transport/link.xml` (comments only; rules unchanged)
- `Tools~/ci/analyzer-baseline.txt` (the two RS0030 entries for this file are gone)
- `Tools~/unity/player-smoke/TurboHttpPlayerSmoke.cs` (`alpn=<true|false|n/a>` in the PASS line)
- `Tests/Runtime/Transport/Tls/SslStreamAlpnOptionsTests.cs` (new)

## Decisions

- Direct API instead of "reflection for discovery plus a static `List<>` reference": the shadow build
  (netstandard2.1 against Unity 2021.3's DLLs) compiles it, and it removes the banned
  `MakeGenericType` and `Expression.Compile` instead of baselining them.
- A fresh list per connection, no per-offer cache: the options object is per connection anyway,
  and a shared `List` handed to the runtime is shared mutable state across concurrent connects.
- The fallback stays before the SslStream is created, so one authentication per SslStream (#171)
  holds; a failure inside the handshake call propagates and the stream is disposed.
- Certificate validation is unchanged: the options set no callback, the SslStream constructor
  callback (`ValidateServerCertificate`, zero policy errors) stays the only one. Revocation mode is
  the options default (`NoCheck`), as before.
- `ExecutionEngineException` is obsolete in the target profile (CS0618); the check sits in one
  helper behind a scoped `#pragma`.

## Verification

- `'./Tools~/ci/check.sh'`: OK (invariants, format, shadow build with analyzers; no RS0030 left in
  this file).
- New NUnit tests: the builder yields `[Http2, Http11]` for `AlpnProtocols.H2AndHttp11`, skips
  unknown protocols, returns a fresh list; `ResolveAlpnOptions` falls back on
  `ExecutionEngineException`, throws `PlatformNotSupportedException` for an h2-only offer, lets
  `InvalidOperationException` through.

## Not run

- IL2CPP player smoke with the new `alpn=` probe (AC2 first half): not run in this lane.
- h2 negotiated over SslStream on a device: needs-device-validation (#99).
- Unity PlayMode, scoped (`SslStreamAlpnOptionsTests|SslStreamSingleHandshakeTests|SslStreamTlsProviderFailClosedTests|PlatformConfigNoReflectionTests|SslStreamProviderTests|TlsProviderSelectorTests`): 60/60 passed in the Editor (EditMode matches none). Full suites run in the
  periodic host-editor run.
