# 2026-10 cookies-auth: credential scoping across redirects, CookieJar scoping, OAuth refresh

Issues: #56, #58, #59, #53 (v1.0-beta), #69 (v1.1, same file as #53). Follow-up: #212.

## What changed

- **#56 Auth vs. redirects.** `AuthInterceptor` binds its credential to the origin (scheme, host,
  port) of the first request it sees for a `RequestContext`. The origin is kept in context state
  under a key unique to each interceptor instance. A hop to another origin gets no `Authorization`.
  This holds when Auth runs inside Redirect (once per hop) and in the A -> B -> B case, where the
  per-hop `IsCrossSiteRequest` flag is false on the last hop. With Auth outside Redirect the
  existing strip in `RedirectInterceptor.BuildRedirectRequest` already applies. A redirect back to
  the original origin carries the token again.
- **#69 refresh deadlock.** `OAuthClient` marks its token-endpoint and discovery requests with
  `AuthInterceptor.SkipAuthMetadataKey` (`"turbohttp.auth.skip"`, internal). `AuthInterceptor`
  then neither calls the token provider (which would re-enter the per-key refresh semaphore) nor
  sends the old access token to the token endpoint.
- **#53 refresh_token carry-over.** When a refresh response has no `refresh_token`,
  `RefreshTokenAsync` stores a new `OAuthToken` that keeps the presented refresh token
  (RFC 6749 section 6).
- **#58 cookies per hop.** `CookieHandler` takes the request-uri from each `OnRequestStart`, which
  a wrapping `RedirectHandler` forwards once per hop, so the final response is stored for the final
  host. For 3xx responses consumed by `RedirectHandler`, there is a new internal
  `IRedirectResponseObserver` (Middleware). `CookieInterceptor` registers itself in context state
  before dispatch. `RedirectHandler` snapshots the observers at construction and notifies them for
  every redirect response it consumes. Because of the snapshot, only interceptors outside the
  redirect interceptor are notified; interceptors inside it already see each hop, so nothing is
  stored twice. Registration is idempotent per instance, so a retry re-dispatch does not store
  twice either.
- **#59 CookieJar.**
  - Private-PSL shared-hosting suffixes added (github.io, blogspot.com, herokuapp.com,
    amazonaws.com, appspot.com, ...). co.za and com.ua were already covered by the
    2-letter-ccTLD heuristic and now have tests.
  - New optional `isPublicSuffix` constructor predicate (public API), for example backed by the
    full PSL.
  - A public-suffix Domain equal to the request host now becomes host-only (RFC 6265 5.3
    step 5). Before, it was rejected.
  - IP-literal hosts: the Domain attribute must equal the host, and the cookie is host-only
    (5.1.3).
  - "Leave secure cookies alone" (6265bis): a non-https response cannot create, overwrite or
    delete a cookie that shadows a Secure cookie. The check is in `StoreFromSetCookieHeaders`
    under the write lock.
  - `__Secure-` and `__Host-` prefixes are enforced, case-insensitively.
  - A name+value over 4096 is rejected. An attribute value over 1024 is ignored.
  - A Path attribute not starting with `/` now falls back to the default path, as RFC 6265 5.2.4
    requires. Before, it was ignored.
  - `GetCookieHeader` takes the read lock. The last-access stamp is a `long` ticks field written
    with `Interlocked.Exchange` (no torn writes on 32-bit IL2CPP). Expired cookies are swept after
    the read section, under the write lock, only when one was seen.

## Files

Runtime: `Runtime/Auth/AuthInterceptor.cs`, `Runtime/Auth/OAuthClient.cs`,
`Runtime/Middleware/CookieHandler.cs`, `Runtime/Middleware/CookieInterceptor.cs`,
`Runtime/Middleware/CookieJar.cs`, `Runtime/Middleware/RedirectHandler.cs`.
Tests: `Tests/Runtime/Auth/AuthRedirectOrderTests.cs`, `Tests/Runtime/Auth/OAuthRefreshTokenTests.cs`,
`Tests/Runtime/Middleware/CookieRedirectHopTests.cs`, `Tests/Runtime/Middleware/CookieJarScopingTests.cs`.
Docs: hazard line in `Development/docs/agents/architecture.md`.

## Decisions

- #56: origin binding in Auth (issue option 1, default origin = first request's origin) instead of
  trusting the Redirect metadata flag, which is per hop. No public API change. Option 3 (never
  inject over plain http) is not adopted: it would change existing behaviour and is not required
  by RFC 9110 15.4.
- #69: an internal skip key in Auth (only Auth reads and writes it) rather than a Core
  `RequestMetadataKeys` entry, to avoid conflicts with the shared file. Discovery is skipped too.
- #58: the Cookie header for later hops when Cookie is outside Redirect is out of the AC (which
  only requires storage) and is tracked as #212 (v1.1).
- #59: compact built-in list plus a pluggable predicate. No embedded full PSL (size, update
  cadence).

## Verification

- `'./Tools~/ci/check.sh'`: pass (invariants, format, shadow build with analyzers).
- Unity 2021.3.45f2 host editor (`run-tests.sh` from WSL, Windows `Unity.exe`), PlayMode
  `--filter 'AuthRedirectOrderTests|OAuthRefreshTokenTests|OAuthClientTests|AuthInterceptorTests|CookieRedirectHopTests|CookieJarScopingTests|CookieInterceptorTests|RedirectInterceptorTests'`:
  105/105 passed. EditMode with the same filter selects 0 tests: these fixtures live in
  `TurboHTTP.Tests.Runtime`, which runs under PlayMode.
- The same files also pass (105/105) under the .NET 8 NUnit runner in a scratch harness outside
  the repo. The harness compiles Core (with a stub for the three UnityEngine members it uses),
  JSON, Auth, Middleware and Testing.
- With `Runtime/Auth` and `Runtime/Middleware` reverted to `origin/main`, 27 of the new cases
  fail (the #69 case fails through its 10 s cancellation bound instead of hanging). The cases that
  already held on main pass: Auth outside Redirect, same-origin redirects, and co.za/com.ua.
- Not run: IL2CPP builds and devices.
