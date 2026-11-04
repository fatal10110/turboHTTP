# 2026-11: host-run-green (#317, #319, #314)

Wave w1104, lane host-run-green. Goal: the unfiltered host-editor PlayMode run is green on its
gates (test failures and the skip gate).

## What

- **#317 / #314 (duplicate).** `StressTests.ConcurrencyMiddleware_OwnedLimiter_DisposedWithClient`
  predates #63/#281: a client disposes configured interceptors only when
  `UHttpClientOptions.DisposeInterceptors` is true (default false). The test now sets it. New
  counterpart `ConcurrencyMiddleware_NotOwnedByDefault_StaysUsableAfterClientDispose`: with the
  default, the real `ConcurrencyInterceptor` still dispatches (200) after `client.Dispose()`.
  No product change.
- **#319, IdnHost tests.** Probed in the host editor (Unity 2021.3.45f2 Mono) and on .NET 8:
  - The old input (`new string('a', 70) + "ü.de"`) parses on Mono as `HostNameType.Basic` with
    `Host == IdnHost == ""`, so the tests skipped. `-ü.de` does the same.
  - Mono's `IdnMapping` accepts bidi, ZWJ, combining-mark and hyphen cases that .NET rejects, so
    those inputs reach ASCII.
  - `ü` + U+FDD0 (permanent noncharacter, IDNA DISALLOWED) parses as a DNS host on both runtimes
    and `Uri.IdnHost` **throws `UriFormatException`** on both (also U+0378, U+FFFD, U+2028,
    `xn--ü`).
  - So the Uri-level tests now use `UnicodeHostRejectedByIdna = "ü\uFDD0.de"`, with no skip.
    Run on Mono they failed: HTTP/1.1 threw `UriFormatException` instead of
    `HttpFieldValidationException`, and HTTP/2 reported `[Unknown]` instead of `InvalidRequest`.
  - Fix: `HttpAuthority.HostFromUri` maps a `UriFormatException` from `Uri.IdnHost` to the same
    `HttpFieldValidationException` that `FormatHost` throws (no host in the message), so both
    transports report a non-retryable InvalidRequest before any byte or stream id.
  - `FormatHost_NoALabelForm_ThrowsWithoutTheHost` keeps the 70-character label (FormatHost only).
- **#319, skip baseline.** `Tools~/unity/skip-baseline.txt` `PlayMode 4`, listing each counted
  skip with its reason: #293 Explicit soak test, 2x UnityTls SNI IP literal (`[env]`, #46),
  Linux-only verifier (`[env]`). testing.md "Host-editor gates" item 1 updated to match.

## Files

- `Runtime/Transport/Internal/HttpAuthority.cs`
- `Tests/Runtime/Performance/StressTests.cs`
- `Tests/Runtime/Transport/HttpAuthorityTests.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.Authority.cs`
- `Tools~/unity/skip-baseline.txt`
- `Development~/docs/agents/testing.md`

## Decisions

- Issue option (a) "an input Mono leaves non-ASCII" does not exist among the probed inputs: Mono
  either converts, returns an empty host, or throws. The chosen input makes both runtimes throw,
  which exercised a real transport gap (wrong error type), so the lane fixes it in Transport
  instead of only changing tests.
- testing.md said "never raise" the baseline; #319 and the lane note ask to raise it by the
  permanent host skips. The rule now says a raise lists each skip with its reason in the file.

## Deferred

- #340 (v1.0-beta): the wire host is not checked as an RFC 3986 host (Mono's empty host for an
  IDNA-invalid name; `ü\u00A0.de` maps to `xn-- -dha.de` with a space on Mono and .NET 8).
- #341 (v1.0 GA): device and emulator runs share the baseline and skip the desktop-verifier
  `[env]` tests, so they exceed `PlayMode 4`.

## Verification

- `'./Tools~/ci/check.sh'`: pass.
- Fail-first, scoped PlayMode in the host editor (`ConcurrencyMiddleware_|HostWithoutALabelForm|FormatHost_NoALabelForm`)
  with the new tests, the old `HttpAuthority` and a mutation that stops the client disposing owned
  interceptors: 3 failed (the owned-limiter test and both IdnHost tests), 2 passed.
- Unfiltered host PlayMode run (Unity 2021.3.45f2, Windows host editor via WSL, `run-tests.sh
  --platform PlayMode`): exit 0, total 2540, passed 2536, failed 0, skipped 4 "within baseline 4"
  (exactly the four listed in skip-baseline.txt; the desktop-verifier tests ran, not skipped).
- Not run: EditMode (baseline unchanged), IL2CPP players, device or emulator runs, the device skip
  baseline (#341).
