# Phase 22.3 Implementation Review

**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Scope:** All new, modified, and deleted files in Phase 22.3 (module interceptor rewrites)
**Review passes:** 1
**Final verdict:** NOT APPROVED

---

## Executive Summary

Phase 22.3 completes most of the intended runtime migration from middleware-era modules to interceptor/handler pairs. The assembly boundaries remain intact, the request clone-on-write rule is mostly followed, and the new redirect/retry/decompression shapes are generally aligned with the Phase 22 architecture.

This slice is not ready for sign-off. Two compile blockers were introduced during the file split, retry observability regresses on successful terminal attempts, and the cache rewrite still diverges from the Phase 22.3 source-of-truth in two material ways: cache-store snapshot work still gates terminal completion, and the documented stale-while-revalidate background path is not implemented.

---

## Pass 1 Findings

### CRITICAL

#### C-1: `AdaptiveInterceptor.cs` is missing `using System.Threading;` after the handler split

**Source:** Infrastructure + Network
**File:** `Runtime/Core/AdaptiveInterceptor.cs`

`AdaptiveInterceptor.InvokeAdaptiveAsync(...)` still uses `CancellationToken`, but the split removed `using System.Threading;`. This repo does not use global usings, and Unity 2021.3 / C# 8 will not resolve the symbol implicitly.

**Impact:** Editor and player compilation fail for the Core assembly.

**Fix:** Restore `using System.Threading;` or fully qualify `CancellationToken`.

#### C-2: `BackgroundNetworkingPolicy.cs` is missing `using System.Threading;` after the interceptor extraction

**Source:** Infrastructure + Network
**File:** `Runtime/Core/BackgroundNetworkingPolicy.cs`

`BackgroundRequestQueuedException` still accepts a `CancellationToken`, but the file no longer imports `System.Threading`. The split from the old combined file removed the namespace import without updating the constructor signature.

**Impact:** Editor and player compilation fail for the Core assembly.

**Fix:** Restore `using System.Threading;` or fully qualify `CancellationToken`.

### HIGH

#### H-1: `RetryInterceptor` records `RetryExhausted` even when the terminal attempt succeeds

**Source:** Infrastructure
**File:** `Runtime/Retry/RetryInterceptor.cs`

When `attempt > _policy.MaxRetries`, the interceptor awaits the real downstream dispatch and then unconditionally records `RetryExhausted` before returning. If the terminal attempt succeeds with a non-retryable success response, the request is still reported as exhausted instead of succeeded.

**Impact:** Retry telemetry becomes incorrect for successful last-attempt recoveries, and any tooling that keys off `RetryExhausted` is misled.

**Fix:** Record exhaustion only when the terminal attempt actually ends in a failure path that would have been retried if more attempts were available.

### MEDIUM

#### M-1: Cache-store snapshot work still blocks `OnResponseEnd` completion

**Source:** Infrastructure
**Files:** `Runtime/Cache/CacheStoringHandler.cs`, `Runtime/Cache/CacheInterceptor.cs`

`CacheStoringHandler.OnResponseEnd(...)` calls `_owner.StoreResponse(...)` before forwarding `_inner.OnResponseEnd(...)`. `StoreResponse(...)` immediately materializes `body?.AsSequence().ToArray()`, constructs a temporary `UHttpResponse`, and runs cacheability preparation synchronously before the async write is fire-and-forget.

The async storage write is not awaited, but the expensive snapshotting work still happens on the hot completion path. That means the logical request does not fully complete, and any outer completion-based resource such as redirect completion or concurrency permits remains held, until cache snapshot preparation finishes.

**Impact:** Cacheable misses still pay synchronous completion latency that the interceptor/handler split was supposed to remove.

**Fix:** Transfer buffer ownership out of the handler first, forward `_inner.OnResponseEnd(...)`, and move response materialization / cache-entry preparation fully onto the background store task.

#### M-2: The documented stale-while-revalidate path is not implemented in `CacheInterceptor`

**Source:** Network
**Files:** `Runtime/Cache/CacheInterceptor.cs`, `Development/docs/phases/phase22/phase-22.3-interceptor-rewrites.md`

The Phase 22.3 source-of-truth explicitly requires a stale-while-revalidate branch that:

1. serves the stale cache entry immediately,
2. clones the request,
3. creates a fresh background `RequestContext` via `RequestContext.CreateForBackground(...)`, and
4. starts revalidation in the background.

The runtime implementation has no `IsStaleWhileRevalidate(...)` branch, no background `CreateForBackground(...)` call, and no fire-and-forget revalidation path. Once an entry is stale, callers still take the foreground revalidation path or cache eviction path instead.

**Impact:** The shipped behavior does not match the phase spec or the 22.3 journal claim that stale-while-revalidate was covered with a cloned background context.

**Fix:** Implement the documented stale-while-revalidate branch or remove the claim from the phase/journal docs until the behavior exists.

---

## Deferred / Remaining Validation

| ID | Severity | Description | Target |
|----|----------|-------------|--------|
| V-1 | HIGH | Unity Editor compile/test execution has not been run from this workspace | Phase 22 validation |
| V-2 | HIGH | IL2CPP/mobile validation for redirect, retry, cache, and decompression interaction remains pending | Phase 22 validation |
| V-3 | MEDIUM | Middleware-era test/file naming cleanup is still deferred | Phase 22.4 |
| V-4 | MEDIUM | Editor monitor replay-builder test remains ignored after the monitor rewrite | Phase 22.4 |

---

## Verified Properties

### Assembly Boundary
- No asmdef dependency changes were introduced for the 22.3 rewrite slice.
- `TurboHTTP.Core` remains free of optional-module references.
- Cache and decompression stay inside optional assemblies without reaching into transport internals.

### Architectural Alignment
- Request-mutating interceptors (`AuthInterceptor`, `DefaultHeadersInterceptor`, `CookieInterceptor`, `AdaptiveInterceptor`) follow clone-on-write instead of mutating the inbound request in place.
- `RedirectInterceptor` keeps the outer dispatch pending via an explicit completion bridge.
- `RetryDetectorHandler` follows the Phase 22 callback error model instead of relying on catching transport exceptions from `await next(...)`.
- `DecompressionHandler` avoids a `MemoryStream` copy by reading from `ReadOnlySequenceStream`.

### Safety / Platform Notes
- The new handler wrappers and `ReadOnlySpan<byte>` callback usage are IL2CPP/AOT-safe patterns.
- Sensitive-header redaction remains enabled by default in logging paths.
- No new Core -> optional module dependency violations were introduced.

---

## Validation Notes

- Reviewed the phase source-of-truth document: `Development/docs/phases/phase22/phase-22.3-interceptor-rewrites.md`
- Reviewed the implementation journal entry: `Development/docs/implementation-journal/2026-03-phase22.3-interceptor-rewrites.md`
- Applied both required specialist rubrics as review checklists:
  - `.claude/agents/unity-infrastructure-architect.md`
  - `.claude/agents/unity-network-architect.md`
- Performed static inspection of the new interceptor/handler runtime files and the migrated tests.
- `git diff --check` passes.
- Not completed: Unity compile/test execution or device validation from this workspace.

---

## Phase Gate

### Verdict: **NOT APPROVED**

Phase 22.3 should not be considered closed until:

1. `AdaptiveInterceptor.cs` compiles again (`using System.Threading;` restored or equivalent)
2. `BackgroundNetworkingPolicy.cs` compiles again (`using System.Threading;` restored or equivalent)
3. `RetryInterceptor` no longer records `RetryExhausted` on successful terminal attempts
4. Cache-store snapshot preparation is removed from the synchronous `OnResponseEnd` path
5. The stale-while-revalidate behavior is either implemented per the phase spec or the phase/journal claims are corrected
