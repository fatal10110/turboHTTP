# Phase 6: Performance & Hardening — 2026-02-14

## What Was Implemented

Phase 6 (M2 hardening gate) — pooling primitives, concurrency controls, request queue, disposal hardening, timeline optimization, and stress tests.

## Files Created

### Runtime/Performance/ (new files)
- **ObjectPool.cs** — Generic bounded pool using a `lock` based stack strategy. Prioritizes LIFO access for better CPU cache locality (hot items reused first) and O(1) complexity over lock-free complexity. Includes configurable reset callback for cross-request data leakage prevention.
- **ByteArrayPool.cs** — Static facade over `ArrayPool<byte>.Shared`. Rent/Return with optional `clearArray` for security-sensitive buffers. Rejects negative sizes, returns `Array.Empty<byte>()` for zero.
- **ConcurrencyLimiter.cs** — Per-host + global connection limiting using `SemaphoreSlim`. `ConcurrentDictionary<string, SemaphoreSlim>` for per-host semaphores (case-insensitive). Global semaphore acquired first (prevents starvation). Cancellation-safe: if per-host acquire fails after global acquired, global is released in catch block. Idempotent `Dispose()` with `Interlocked.CompareExchange`.
- **ConcurrencyMiddleware.cs** — `IHttpMiddleware` wrapping `ConcurrencyLimiter`. Extracts host from `request.Uri`. Acquire/release in try/finally. Records timeline events (ConcurrencyAcquire/Acquired/Released).
- **RequestQueue.cs** — Priority-based (`High/Normal/Low`) generic queue. Three `ConcurrentQueue<T>` drained in priority order. `SemaphoreSlim` for async dequeue blocking. Graceful shutdown (rejects new enqueue, existing items drainable). `IDisposable`.

### Tests/Runtime/Performance/ (new files)
- **ObjectPoolTests.cs** — Factory creation, reuse, reset callback, capacity enforcement, null handling, thread-safety under contention (100 threads × 100 ops).
- **ConcurrencyLimiterTests.cs** — Per-host and global limit enforcement, cancellation safety (no permit leak), dispose idempotency, thread-safety (50 threads × 20 ops).
- **StressTests.cs** — 1000-request mock transport stress, concurrency middleware enforcement (verifies max concurrent <= limit), multi-host concurrency, pool leak detection, request queue priority ordering, UHttpClient disposal guards, ByteArrayPool edge cases.

## Files Modified

### Runtime/Core/
- **UHttpClient.cs** — `Dispose()` now iterates middlewares and disposes any that implement `IDisposable`. Fixes architect review issue C1 (middleware resource leak).
- **RequestContext.cs** — `TimelineEvent` constructor no longer allocates empty `Dictionary<string, object>` when `data` is null (saves ~80 bytes per event, ~5-7 events per request = ~400-560 bytes saved). Added `Clear()` method for post-request cleanup.

### Runtime/Transport/
- **RawSocketTransport.cs** — Changed `_disposed` from `volatile bool` to `int` with `Interlocked.CompareExchange` for atomic disposal. Fixes architect review issue C2 (double-dispose race).

### Runtime/Transport/Tls/
- **SslStreamTlsProvider.cs** — Cached all ALPN reflection results (options type, properties, protocol field values, auth method) in static fields. `AuthenticateWithAlpnAsync` no longer does per-connection `Assembly.GetType`, `GetProperty`, `GetField` lookups. Fixes architect review issue K4.

### Runtime/Observability/
- **LoggingMiddleware.cs** — Added sensitive header redaction (`Authorization`, `Cookie`, `Set-Cookie`, `Proxy-Authorization`, `WWW-Authenticate`, `X-Api-Key`). Redaction enabled by default, configurable via `redactSensitiveHeaders` constructor param. Fixes security audit finding H-2.

## Decisions Made

1. **ObjectPool uses array + CAS, not ConcurrentBag** — ConcurrentBag has thread-local storage overhead and unbounded growth under IL2CPP. Array-backed with `Interlocked.CompareExchange` gives deterministic capacity and minimal overhead.

2. **ByteArrayPool wraps ArrayPool<byte>.Shared** — Rather than implementing custom bucketing (as architect review suggested), we use the BCL's battle-tested `ArrayPool<byte>.Shared`. Custom bucketing adds complexity with marginal benefit for Phase 6.

3. **ConcurrencyLimiter uses global-first acquisition** — Acquiring global semaphore before per-host prevents starvation where many hosts compete for limited global slots.

4. **Deferred: BouncyCastle cert chain validation** — Security audit finding C-1 (CRITICAL). User will validate SslStream ALPN on physical devices manually. Full cert chain validation deferred to a later phase.

5. **Deferred: Timeline event pooling** — Architect review task 6.4 suggested pooling `TimelineEvent` objects. We opted for the simpler lazy-dict optimization (no empty dict allocation) as the bigger win. Full event pooling requires `RequestContext` to implement `IDisposable` which changes the public API — deferred.

6. **Socket.NoDelay already set** — Network review recommended setting `socket.NoDelay = true`. Confirmed it's already set in `TcpConnectionPool.cs:395`.

## Review Documents Produced

- `docs/architecture-review-phase6.md` — 840 lines, full architecture review + Phase 6 design
- `docs/security-audit.md` — 669 lines, full security audit (1 CRITICAL, 3 HIGH, 5 MEDIUM, 6 LOW)
- `docs/network-review.md` — 275 lines, protocol compliance + performance analysis
- `docs/product-strategy.md` — 355 lines, competitive analysis + go-to-market strategy
