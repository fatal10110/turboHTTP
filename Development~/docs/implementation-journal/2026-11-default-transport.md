# 2026-11 default transport sharing and bootstrap attribute tests (#275, #276)

## What

- #275: a `UHttpClient` built with default options did not share `HttpTransportFactory.Default`.
  Root cause (host editor, Unity 2021.3.45f2 PlayMode, temporary stack logging in
  `HttpTransportFactory.Register`/`DetachCreatedDefault`/`Reset`, the RawSocket `Lazy` factory and the
  `UHttpClient` constructor, not committed): nothing re-registered after the singleton was created
  (one `Lazy` creation, every `Register` ran with `lazyCreated=False`). The constructor took the
  `CreateWithOptions` branch because `Http2Options.IsDefault()` returned false for a fresh instance:
  #234/#147 (73710a8) changed the `MaxResponseBodySize` default to 0 (no transport cap) but
  `IsDefault()` still compared it with 100 MiB. Every default-options client therefore built and
  owned its own `RawSocketTransport` (own pool, own HTTP/2 connections). The issue's re-registration
  hypothesis was wrong for this failure; the re-registration drop it describes is still real for a
  second `EnsureRegistered()` call after `Default` was read, so both are fixed.
- `Http2Options`: `DefaultMaxResponseBodySize = 0`, used by the field initializer and `IsDefault()`.
- `RawSocketTransport.EnsureRegistered()` is idempotent: the factories are cached in static readonly
  fields and passed to the new internal `HttpTransportFactory.RegisterUnlessCurrent`, which (under
  `_lock`) keeps the current `Lazy` when exactly these factories are registered and the default is not
  a `SetForTesting` override, and only re-sets the capabilities probe. Any other registration,
  `SetForTesting` or `Reset` in between makes the call register again.
- #276: the two bootstrap attribute tests match `PreserveAttribute` by exact type
  (`RuntimeInitializeOnLoadMethodAttribute` derives from it, so `GetCustomAttribute<PreserveAttribute>()`
  threw `AmbiguousMatchException`).

## Files

- `Runtime/Core/Http2Options.cs`, `Runtime/Core/HttpTransportFactory.cs`, `Runtime/Transport/RawSocketTransport.cs`
- `Tests/Runtime/Transport/DefaultTransportRegistrationTests.cs` (new)
- `Tests/Runtime/Transport/TransportBootstrapTests.cs`, `Tests/Runtime/Core/JsonBridgeRegistrationTests.cs`

## Decisions

- Cached delegates plus reference comparison instead of a "registered once" flag: a flag would also
  skip registration after `Reset()` or after a user `Register(...)`, which must still be replaced.
- The skip applies whenever the RawSocket registration is current and a `Lazy` exists, created or
  not. Skipping only a created value would still race with a `Default` read that is creating it.
  Consequence: a `Lazy` whose factory threw keeps its cached exception until the bootstrap reset, a
  user `Register` or `Reset` (the `RawSocketTransport()` constructor opens nothing, so this is not
  expected in practice).
- Replacing a different registration (user factory, `SetForTesting`) still does not dispose the default
  that registration created: it belongs to whoever registered it, as with `Register`. The bootstrap's
  per-session reset (`DetachCreatedDefault` + dispose) is unchanged and still the only path that
  replaces and disposes a created RawSocket default.
- Behavior change: default-options clients now share the process-wide default transport (the
  documented design) instead of each owning one; disposing such a client no longer closes its pool.

## Deferrals

None.

## Verification

- `'./Tools~/ci/check.sh'`: OK.
- Host editor PlayMode (Unity 2021.3.45f2), before the fix:
  `--filter 'ConnectionOptionsTests|TransportBootstrapTests|JsonBridgeRegistrationTests|DefaultTransportRegistrationTests'`
  25 total, 6 failed (ConnectionOptionsTests.DefaultOptions_ShouldReuseDefaultTransport and five new tests).
  After the fix: 25/25 passed. `--filter 'PlatformConfigNoReflectionTests|UHttpClientTests|ConnectionPoolOptions|Http2Options'`: 56/56 passed.
- Mutation check (not committed): without the method-level `[Preserve]` both #276 tests fail.
- Not run: the full EditMode/PlayMode suites (periodic host-editor run), IL2CPP player builds, devices.
