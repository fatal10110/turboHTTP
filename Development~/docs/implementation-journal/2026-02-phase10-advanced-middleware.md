# Phase 10: Advanced Middleware — 2026-02-15

## What Was Implemented

Phase 10 active scope was implemented for cache/revalidation, redirect handling, cookie handling, and HTTP/1.1 parser streaming improvements.

Implemented tasks:
- 10.1 Cache entry model
- 10.2 Cache storage interface
- 10.3 Memory cache storage (bounded + LRU + TTL/expiry cleanup)
- 10.4 Cache middleware (URL normalization, `Vary`-aware variants, conditional revalidation, unsafe-method invalidation)
- 10.8 Redirect middleware (`301/302/303/307/308`, loop detection, max redirects, cross-origin sensitive-header stripping)
- 10.9 Cookie middleware + cookie jar (domain/path/expiry/secure/samesite matching, bounded storage)
- 10.10 Streaming transport improvements (buffered parser path replacing byte-by-byte header reads)

Rate limiting tasks (10.5/10.6/10.7) remain deferred per phase plan.

## Files Created

### Runtime/Cache/
- **CacheEntry.cs** — immutable cache snapshot model with header cloning and zero-copy body storage (`ReadOnlyMemory<byte>`), UTC timestamps, expiry/revalidation helpers, variant metadata (`VaryHeaders`, `VaryKey`, `ResponseUrl`).
- **ICacheStorage.cs** — async cache storage abstraction (`Get/Set/Remove/Clear/Count/Size`) with cancellation tokens.
- **MemoryCacheStorage.cs** — thread-safe in-memory backend using a single `SemaphoreSlim` critical section, LRU list + dictionary state, deterministic size accounting, expiry cleanup, bounded eviction.
- **CacheMiddleware.cs** — cache flow + conditional revalidation:
  - method gating (safe methods), non-safe invalidation
  - deterministic key normalization (scheme/host/port/path/query)
  - `Vary`-aware variant selection and storage
  - conservative defaults around sensitive `Vary` dimensions/auth
  - `Cache-Control`/`Expires` handling (`max-age` precedence)
  - `304` merge path for cache metadata headers

### Runtime/Core/
- **RequestMetadataKeys.cs** — reserved metadata keys used by built-in middleware (`follow_redirects`, `max_redirects`, cross-site marker).

### Runtime/Middleware/
- **RedirectMiddleware.cs** — iterative redirect handling with:
  - supported status codes: 301/302/303/307/308
  - method/body rewrite rules for POST-to-GET cases
  - cross-origin stripping for `Authorization`, `Proxy-Authorization`, `Cookie`
  - loop detection and max-redirect enforcement
  - timeline/state redirect-chain diagnostics
- **CookieJar.cs** — RFC 6265-style cookie store:
  - `Set-Cookie` parsing + replacement by `(name, domain, path)` tuple
  - domain/path/secure/samesite/expiry matching
  - bounded storage (per-domain + global) with eviction
  - thread-safe operations via `ReaderWriterLockSlim`
- **CookieMiddleware.cs** — outbound cookie injection and inbound `Set-Cookie` persistence.

### Tests/Runtime/
- **Cache/CacheMiddlewareTests.cs** — hit/miss behavior, no-store behavior, ETag revalidation path, query normalization, `Vary: Accept-Encoding` separation, unsafe-method invalidation.
- **Middleware/RedirectMiddlewareTests.cs** — chain following, max redirects, method rewrite semantics, 307 body preservation, cross-origin header stripping, relative location resolution, loop detection, per-request disable.
- **Middleware/CookieMiddlewareTests.cs** — outbound attach, domain/path filtering, expiry handling, secure-only behavior, bounds enforcement, same-site cross-site behavior.
- **Transport/Http11ResponseParserPerformanceTests.cs** — guard ensuring parser no longer performs single-byte read requests.

## Files Modified

### Runtime/Core/
- **UHttpRequestBuilder.cs** — writes redirect option values into request metadata (`FollowRedirects`, `MaxRedirects`) so middleware can enforce client options deterministically.

### Runtime/Transport/Http1/
- **Http11ResponseParser.cs** — header/status parsing switched to buffered incremental reader; retained existing protocol behavior for status/body/chunked/content-length handling and error semantics.

### Tests/Runtime/
- **Transport/Http11ResponseParserTests.cs** — added fragmented/split-boundary/large-header/multibyte-boundary coverage for buffered parser behavior.
- **Integration/IntegrationTests.cs** — added phase-10 deterministic flow covering redirect + cookie persistence + cache revalidation (`304` reuse path).

### Runtime follow-up (zero-copy body model)
- **Core/UHttpResponse.cs** — response body changed from `byte[]` to `ReadOnlyMemory<byte>`.
- **Cache/CacheEntry.cs** — removed body `Buffer.BlockCopy` path from `Clone()`; cache entries now keep shared body memory.
- **Cache/MemoryCacheStorage.cs** and **Cache/CacheMiddleware.cs** — body handling updated to pass through shared response memory without body duplication.
- **Testing/RecordReplayTransport.cs** — replay and JSON redaction paths updated for `ReadOnlyMemory<byte>` response bodies.
- **Files/FileDownloader.cs**, **JSON/JsonExtensions.cs**, **Observability/** middleware — response body reads moved to `ReadOnlyMemory<byte>`/`Span` APIs.
- **Core/middleware/perf/integration tests** — response construction updated to use explicit empty/default memory (no nullable-body assumptions).

## Decisions Made

1. **Conservative cache defaults:** cache is conservative around privacy-sensitive contexts (`Authorization`, sensitive `Vary`) unless explicitly opted in.
2. **Variant keying over transcoding:** response bytes are stored as received; variants are selected by request dimensions (`Vary`), including encoded payload variants.
3. **Single critical section for memory cache state:** dictionary, LRU list, and size accounting mutate under one async-safe gate to avoid race/corruption.
4. **Iterative redirects (no recursion):** redirect processing is iterative with explicit hop/loop control and cancellation propagation.
5. **Bounded cookie state:** cookie jar enforces both per-domain and global limits to avoid unbounded growth under hostile/high-cardinality sets.
6. **Parser optimization without protocol rewrites:** hot-path buffering was introduced while keeping existing status/body/chunked semantics intact.
7. **Zero-copy cache body reuse:** cached response bodies are now shared via `ReadOnlyMemory<byte>`; cache reads no longer allocate/cloned payload buffers per hit.

## Validation Notes

- Local compile checks for the modified runtime paths were executed successfully.
- A focused .NET-hosted NUnit run for new/updated Phase 10 tests passed (`65/65`) in the isolated harness used for validation.
- Follow-up zero-copy migration compile check passed in an isolated .NET harness (`0` errors) across runtime + runtime tests (Unity/BouncyCastle source excluded, Unity stubs injected for host compilation).
- Follow-up focused NUnit run passed (`127/127`) for cache, redirect, cookie, parser, core, retry, pipeline, stress, and file-downloader suites in the isolated harness.
- Unity Test Runner (full project lane) was not executed in this environment; this should still be run in CI/editor for full package validation.
