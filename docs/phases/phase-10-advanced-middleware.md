# Phase 10: Advanced Middleware

**Milestone:** M3 (v1.0 "feature-complete")
**Dependencies:** Phase 9 (Platform Compatibility)
**Estimated Complexity:** High
**Critical:** Yes - Key differentiators

## Overview

Implement advanced middleware that sets TurboHTTP apart: HTTP caching with ETag support. Rate limiting is deferred to [Phase 14: Post-v1.0 Roadmap](phase-14-future.md).

Detailed sub-phase breakdown: [Phase 10 Implementation Plan - Overview](phase10/overview.md)

## Goals

1. Create `CacheMiddleware` with ETag/Last-Modified support
2. Create `CacheStorage` abstraction (memory and disk implementations)
3. Implement cache eviction policies (LRU, TTL)
4. Support cache validation and revalidation

## Tasks

### Task 10.1: Cache Entry Model

**File:** `Runtime/Cache/CacheEntry.cs`

```csharp
using System;
using System.Collections.Generic;

namespace TurboHTTP.Cache
{
    /// <summary>
    /// Represents a cached HTTP response.
    /// </summary>
    public class CacheEntry
    {
        public string Key { get; set; }
        public byte[] Body { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public int StatusCode { get; set; }
        public DateTime CachedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string ETag { get; set; }
        public string LastModified { get; set; }

        /// <summary>
        /// Check if this cache entry is expired.
        /// </summary>
        public bool IsExpired()
        {
            if (ExpiresAt.HasValue)
            {
                return DateTime.UtcNow >= ExpiresAt.Value;
            }
            return false;
        }

        /// <summary>
        /// Check if this cache entry can be revalidated.
        /// </summary>
        public bool CanRevalidate()
        {
            return !string.IsNullOrEmpty(ETag) || !string.IsNullOrEmpty(LastModified);
        }
    }
}
```

### Task 10.2: Cache Storage Interface

**File:** `Runtime/Cache/ICacheStorage.cs`

```csharp
using System.Threading.Tasks;

namespace TurboHTTP.Cache
{
    /// <summary>
    /// Abstraction for cache storage backends.
    /// </summary>
    public interface ICacheStorage
    {
        /// <summary>
        /// Get a cache entry by key.
        /// Returns null if not found or expired.
        /// </summary>
        Task<CacheEntry> GetAsync(string key);

        /// <summary>
        /// Store a cache entry.
        /// </summary>
        Task SetAsync(string key, CacheEntry entry);

        /// <summary>
        /// Remove a cache entry.
        /// </summary>
        Task RemoveAsync(string key);

        /// <summary>
        /// Clear all cache entries.
        /// </summary>
        Task ClearAsync();

        /// <summary>
        /// Get the number of cached entries.
        /// </summary>
        Task<int> GetCountAsync();

        /// <summary>
        /// Get total size of cached data in bytes.
        /// </summary>
        Task<long> GetSizeAsync();
    }
}
```

### Task 10.3: Memory Cache Storage

**File:** `Runtime/Cache/MemoryCacheStorage.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TurboHTTP.Cache
{
    /// <summary>
    /// In-memory cache storage with LRU eviction.
    /// </summary>
    public class MemoryCacheStorage : ICacheStorage
    {
        private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>();
        private readonly LinkedList<string> _lruList = new LinkedList<string>();
        private readonly int _maxEntries;
        private readonly long _maxSizeBytes;
        private long _currentSizeBytes;

        public MemoryCacheStorage(int maxEntries = 100, long maxSizeBytes = 10 * 1024 * 1024)
        {
            _maxEntries = maxEntries;
            _maxSizeBytes = maxSizeBytes;
        }

        public Task<CacheEntry> GetAsync(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                // Check if expired
                if (entry.IsExpired())
                {
                    RemoveAsync(key).Wait();
                    return Task.FromResult<CacheEntry>(null);
                }

                // Update LRU
                _lruList.Remove(key);
                _lruList.AddFirst(key);

                return Task.FromResult(entry);
            }

            return Task.FromResult<CacheEntry>(null);
        }

        public Task SetAsync(string key, CacheEntry entry)
        {
            // Calculate entry size
            long entrySize = entry.Body?.Length ?? 0;

            // Remove existing entry if present
            if (_cache.ContainsKey(key))
            {
                RemoveAsync(key).Wait();
            }

            // Evict entries if necessary
            while (_cache.Count >= _maxEntries || _currentSizeBytes + entrySize > _maxSizeBytes)
            {
                if (_lruList.Last == null)
                    break;

                var oldestKey = _lruList.Last.Value;
                RemoveAsync(oldestKey).Wait();
            }

            // Add new entry
            _cache[key] = entry;
            _lruList.AddFirst(key);
            _currentSizeBytes += entrySize;

            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                _cache.Remove(key);
                _lruList.Remove(key);
                _currentSizeBytes -= entry.Body?.Length ?? 0;
            }

            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            _cache.Clear();
            _lruList.Clear();
            _currentSizeBytes = 0;
            return Task.CompletedTask;
        }

        public Task<int> GetCountAsync()
        {
            return Task.FromResult(_cache.Count);
        }

        public Task<long> GetSizeAsync()
        {
            return Task.FromResult(_currentSizeBytes);
        }
    }
}
```

### Task 10.4: Cache Middleware

**File:** `Runtime/Cache/CacheMiddleware.cs`

```csharp
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.Cache
{
    /// <summary>
    /// Cache policy configuration.
    /// </summary>
    public class CachePolicy
    {
        /// <summary>
        /// Enable caching for GET requests.
        /// </summary>
        public bool EnableCache { get; set; } = true;

        /// <summary>
        /// Default TTL for cached responses without explicit expiration.
        /// </summary>
        public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Enable ETag/Last-Modified revalidation.
        /// </summary>
        public bool EnableRevalidation { get; set; } = true;

        /// <summary>
        /// Cache storage backend.
        /// </summary>
        public ICacheStorage Storage { get; set; } = new MemoryCacheStorage();
    }

    /// <summary>
    /// Middleware that caches HTTP responses with ETag support.
    /// </summary>
    public class CacheMiddleware : IHttpMiddleware
    {
        private readonly CachePolicy _policy;

        public CacheMiddleware(CachePolicy policy = null)
        {
            _policy = policy ?? new CachePolicy();
        }

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            // Only cache GET requests
            if (!_policy.EnableCache || request.Method != HttpMethod.GET)
            {
                return await next(request, context, cancellationToken);
            }

            var cacheKey = GetCacheKey(request);
            var cachedEntry = await _policy.Storage.GetAsync(cacheKey);

            // Cache hit
            if (cachedEntry != null && !cachedEntry.IsExpired())
            {
                // Check if we should revalidate
                if (_policy.EnableRevalidation && cachedEntry.CanRevalidate())
                {
                    return await RevalidateAsync(request, context, next, cachedEntry, cacheKey, cancellationToken);
                }

                context.RecordEvent("CacheHit", new System.Collections.Generic.Dictionary<string, object>
                {
                    { "key", cacheKey }
                });

                Debug.Log($"[CacheMiddleware] Cache HIT: {request.Uri}");

                return CreateResponseFromCache(cachedEntry, request, context);
            }

            // Cache miss - fetch from server
            context.RecordEvent("CacheMiss");
            Debug.Log($"[CacheMiddleware] Cache MISS: {request.Uri}");

            var response = await next(request, context, cancellationToken);

            // Cache successful responses
            if (response.IsSuccessStatusCode)
            {
                await CacheResponseAsync(cacheKey, response);
            }

            return response;
        }

        private async Task<UHttpResponse> RevalidateAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CacheEntry cachedEntry,
            string cacheKey,
            CancellationToken cancellationToken)
        {
            context.RecordEvent("CacheRevalidation");

            // Add conditional headers
            var headers = request.Headers.Clone();
            if (!string.IsNullOrEmpty(cachedEntry.ETag))
            {
                headers.Set("If-None-Match", cachedEntry.ETag);
            }
            if (!string.IsNullOrEmpty(cachedEntry.LastModified))
            {
                headers.Set("If-Modified-Since", cachedEntry.LastModified);
            }

            var revalidationRequest = request.WithHeaders(headers);
            var response = await next(revalidationRequest, context, cancellationToken);

            // 304 Not Modified - use cached version
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                context.RecordEvent("CacheRevalidationNotModified");
                Debug.Log($"[CacheMiddleware] 304 Not Modified: {request.Uri}");

                // Update cache entry timestamp
                cachedEntry.CachedAt = DateTime.UtcNow;
                await _policy.Storage.SetAsync(cacheKey, cachedEntry);

                return CreateResponseFromCache(cachedEntry, request, context);
            }

            // Content modified - cache new response
            context.RecordEvent("CacheRevalidationModified");
            if (response.IsSuccessStatusCode)
            {
                await CacheResponseAsync(cacheKey, response);
            }

            return response;
        }

        private async Task CacheResponseAsync(string key, UHttpResponse response)
        {
            var entry = new CacheEntry
            {
                Key = key,
                Body = response.Body,
                Headers = response.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                StatusCode = (int)response.StatusCode,
                CachedAt = DateTime.UtcNow,
                ETag = response.Headers.Get("ETag"),
                LastModified = response.Headers.Get("Last-Modified")
            };

            // Parse Cache-Control header
            var cacheControl = response.Headers.Get("Cache-Control");
            if (!string.IsNullOrEmpty(cacheControl))
            {
                if (cacheControl.Contains("no-store") || cacheControl.Contains("no-cache"))
                {
                    return; // Don't cache
                }

                var maxAge = ExtractMaxAge(cacheControl);
                if (maxAge.HasValue)
                {
                    entry.ExpiresAt = DateTime.UtcNow.Add(maxAge.Value);
                }
            }

            // Parse Expires header
            if (!entry.ExpiresAt.HasValue)
            {
                var expires = response.Headers.Get("Expires");
                if (DateTime.TryParse(expires, out var expiresDate))
                {
                    entry.ExpiresAt = expiresDate.ToUniversalTime();
                }
            }

            // Use default TTL if no expiration specified
            if (!entry.ExpiresAt.HasValue)
            {
                entry.ExpiresAt = DateTime.UtcNow.Add(_policy.DefaultTtl);
            }

            await _policy.Storage.SetAsync(key, entry);
            Debug.Log($"[CacheMiddleware] Cached response for {key} (expires: {entry.ExpiresAt})");
        }

        private UHttpResponse CreateResponseFromCache(CacheEntry entry, UHttpRequest request, RequestContext context)
        {
            var headers = new HttpHeaders();
            foreach (var kvp in entry.Headers)
            {
                headers.Set(kvp.Key, kvp.Value);
            }
            headers.Set("X-Cache", "HIT");

            return new UHttpResponse(
                (HttpStatusCode)entry.StatusCode,
                headers,
                entry.Body,
                context.Elapsed,
                request
            );
        }

        private string GetCacheKey(UHttpRequest request)
        {
            // Use URL as cache key
            // TODO: Include relevant headers in key (Accept, Accept-Language, etc.)
            return request.Uri.ToString();
        }

        private TimeSpan? ExtractMaxAge(string cacheControl)
        {
            var parts = cacheControl.Split(',');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("max-age="))
                {
                    var value = trimmed.Substring("max-age=".Length);
                    if (int.TryParse(value, out var seconds))
                    {
                        return TimeSpan.FromSeconds(seconds);
                    }
                }
            }
            return null;
        }
    }
}
```

### Deferred Tasks 10.5-10.7: Rate Limiting (Moved to Phase 14)

Rate limit policy/config, token bucket implementation, and rate-limit middleware (former tasks 10.5-10.7) are deferred to [Phase 16: Extended Capabilities and Resilience](phase-16-advanced-capabilities.md).

## Validation Criteria

### Success Criteria

- [ ] Cache middleware caches GET responses
- [ ] ETag revalidation works (returns 304 Not Modified)
- [ ] Last-Modified revalidation works
- [ ] Cache respects Cache-Control headers
- [ ] Cache eviction works (LRU policy)

### Unit Tests

Create test file: `Tests/Runtime/Cache/CacheMiddlewareTests.cs`

```csharp
using NUnit.Framework;
using System;
using System.Net;
using System.Threading.Tasks;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Cache
{
    public class CacheMiddlewareTests
    {
        [Test]
        public async Task CacheMiddleware_CachesSuccessfulGetRequests()
        {
            var storage = new MemoryCacheStorage();
            var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });
            var transport = new MockTransport();

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/data"));
            var context = new RequestContext(request);

            // First request - cache miss
            var response1 = await middleware.InvokeAsync(
                request,
                context,
                (req, ctx, ct) => transport.SendAsync(req, ctx, ct),
                default
            );

            Assert.AreEqual(1, await storage.GetCountAsync());

            // Second request - cache hit
            var response2 = await middleware.InvokeAsync(
                request,
                context,
                (req, ctx, ct) => transport.SendAsync(req, ctx, ct),
                default
            );

            Assert.IsNotNull(response2);
            Assert.AreEqual("HIT", response2.Headers.Get("X-Cache"));
        }
    }
}
```

### Task 10.8: Redirect Middleware

Detailed plan: [Phase 10.8 Redirect Middleware](phase10/phase-10.8-redirect-middleware.md)

**Carried over from Phase 3/4 backlog.** `UHttpClientOptions.FollowRedirects` and `MaxRedirects` are defined but NOT enforced. Implement a `RedirectMiddleware` that:
- Follows 301, 302, 303, 307, 308 status codes
- Converts POST to GET on 301/302/303 (RFC 9110 Section 15.4)
- Preserves method on 307/308
- Enforces `MaxRedirects` limit (default 10)
- Strips `Authorization` header on cross-origin redirects
- Handles relative `Location` headers
- Records redirect chain in `RequestContext`

### Task 10.9: Cookie Middleware

Detailed plan: [Phase 10.9 Cookie Middleware](phase10/phase-10.9-cookie-middleware.md)

**Carried over from Phase 4 backlog.** Implement a `CookieMiddleware` with:
- RFC 6265-compliant cookie jar (domain, path, expiry, secure, httponly)
- Automatic `Cookie` header injection on matching requests
- Automatic `Set-Cookie` response parsing and storage
- Per-domain cookie limits (RFC 6265 Section 6.1: at least 50 per domain, 3000 total)
- Optional persistent storage (in-memory default, disk-backed option)
- `SameSite` attribute support (Lax/Strict/None)
- Thread-safe for HTTP/2 concurrent streams

### Task 10.10: Streaming Transport Improvements

Detailed plan: [Phase 10.10 Streaming Transport Improvements](phase10/phase-10.10-streaming-transport-improvements.md)

Buffered I/O rewrite for HTTP/1.1 response parser to eliminate byte-by-byte ReadAsync (documented GC hotspot: ~400 Task allocations per response). See Phase 6 notes.

## Next Steps

Once Phase 10 is complete and validated:

1. Move to [Phase 11: Unity Integration](phase-11-unity-integration.md)
2. Implement Unity-specific content handlers (Texture2D, AudioClip)
3. Add main thread synchronization
4. M3 milestone progress

## Notes

- Cache middleware dramatically reduces bandwidth and latency
- ETag revalidation ensures cache freshness
- Rate limiting is deferred to [Phase 14: Post-v1.0 Roadmap](phase-14-future.md)
- Redirect and cookie support are essential for real-world HTTP client usage
- These features are rare in Unity HTTP libraries

## Review Notes

> **TODO: Phase 10 Complexity** - This phase has high implementation complexity that may warrant splitting:
> - **Cache storage backends**: Memory cache with LRU is straightforward, but disk-based caching (not yet specified) adds significant complexity (serialization, file locking, corruption handling, platform-specific paths)
> - **Cache key generation**: Current implementation uses URL only; production needs to include `Vary` headers, authentication context, and potentially request body hashes for POST caching
> - **Cache invalidation**: No explicit invalidation API or support for cache tags/groups
>
> Consider splitting into sub-phases:
> 1. Phase 10a: Memory cache with basic ETag/Last-Modified support
> 2. Phase 10b: Disk cache storage backend (defer to post-M3 if needed)
> 3. Phase 10c: Advanced cache features (Vary headers, invalidation API, cache tags)

## Deferred Items from Phase 2

1. **HTTP 429 retryability** — `UHttpError.IsRetryable()` currently treats all 4xx as non-retryable. HTTP 429 (Too Many Requests, RFC 6585) is a 4xx code that is explicitly retryable. Handling for 429 + `Retry-After` should be addressed when rate limiting is implemented in Phase 14.

### Security & Privacy Notes

- Be conservative by default: do not cache responses with sensitive headers (e.g., `Authorization`) unless explicitly opted in
- Ensure cache keys partition correctly (at minimum: method + URL + relevant `Vary` headers; consider auth/session context)
- Respect `Cache-Control`, `Pragma`, and `Set-Cookie` semantics to avoid leaking user-specific data across sessions
