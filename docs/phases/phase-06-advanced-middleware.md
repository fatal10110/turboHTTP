# Phase 6: Advanced Middleware

**Milestone:** M2 (v0.5 "feature-complete core")
**Dependencies:** Phase 5 (Content Handlers)
**Estimated Complexity:** High
**Critical:** Yes - Key differentiators

## Overview

Implement advanced middleware that sets TurboHTTP apart: HTTP caching with ETag support and rate limiting with token bucket algorithm. These features are essential for production applications but rarely found in Unity HTTP clients.

## Goals

1. Create `CacheMiddleware` with ETag/Last-Modified support
2. Create `CacheStorage` abstraction (memory and disk implementations)
3. Create `RateLimitMiddleware` with token bucket algorithm
4. Create per-host rate limiting
5. Implement cache eviction policies (LRU, TTL)
6. Support cache validation and revalidation

## Tasks

### Task 6.1: Cache Entry Model

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

### Task 6.2: Cache Storage Interface

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

### Task 6.3: Memory Cache Storage

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

### Task 6.4: Cache Middleware

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

### Task 6.5: Rate Limit Configuration

**File:** `Runtime/RateLimit/RateLimitConfig.cs`

```csharp
using System;
using System.Collections.Generic;

namespace TurboHTTP.RateLimit
{
    /// <summary>
    /// Rate limit policy configuration.
    /// </summary>
    public class RateLimitPolicy
    {
        /// <summary>
        /// Maximum number of requests allowed per time window.
        /// </summary>
        public int MaxRequests { get; set; } = 100;

        /// <summary>
        /// Time window for rate limiting.
        /// </summary>
        public TimeSpan TimeWindow { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Enable per-host rate limiting (default: true).
        /// If false, rate limit applies globally across all hosts.
        /// </summary>
        public bool PerHost { get; set; } = true;

        /// <summary>
        /// Custom rate limits per host.
        /// Key: hostname, Value: (maxRequests, timeWindow)
        /// </summary>
        public Dictionary<string, (int MaxRequests, TimeSpan TimeWindow)> HostOverrides { get; set; }
            = new Dictionary<string, (int, TimeSpan)>();
    }
}
```

### Task 6.6: Token Bucket Rate Limiter

**File:** `Runtime/RateLimit/TokenBucket.cs`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.RateLimit
{
    /// <summary>
    /// Token bucket algorithm for rate limiting.
    /// </summary>
    public class TokenBucket
    {
        private readonly int _capacity;
        private readonly TimeSpan _refillInterval;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private int _tokens;
        private DateTime _lastRefill;

        public TokenBucket(int capacity, TimeSpan refillInterval)
        {
            _capacity = capacity;
            _refillInterval = refillInterval;
            _tokens = capacity;
            _lastRefill = DateTime.UtcNow;
        }

        /// <summary>
        /// Try to acquire a token.
        /// Returns true if token acquired, false if rate limit exceeded.
        /// </summary>
        public async Task<bool> TryAcquireAsync()
        {
            await _lock.WaitAsync();
            try
            {
                Refill();

                if (_tokens > 0)
                {
                    _tokens--;
                    return true;
                }

                return false;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Wait until a token is available, then acquire it.
        /// </summary>
        public async Task AcquireAsync(CancellationToken cancellationToken = default)
        {
            while (!await TryAcquireAsync())
            {
                var delay = GetTimeUntilNextToken();
                await Task.Delay(delay, cancellationToken);
            }
        }

        private void Refill()
        {
            var now = DateTime.UtcNow;
            var elapsed = now - _lastRefill;

            if (elapsed >= _refillInterval)
            {
                var tokensToAdd = (int)(elapsed.TotalMilliseconds / _refillInterval.TotalMilliseconds) * _capacity;
                _tokens = Math.Min(_capacity, _tokens + tokensToAdd);
                _lastRefill = now;
            }
        }

        private TimeSpan GetTimeUntilNextToken()
        {
            var now = DateTime.UtcNow;
            var timeSinceRefill = now - _lastRefill;
            var timeUntilRefill = _refillInterval - timeSinceRefill;

            return timeUntilRefill > TimeSpan.Zero ? timeUntilRefill : TimeSpan.FromMilliseconds(100);
        }

        public int AvailableTokens
        {
            get
            {
                _lock.Wait();
                try
                {
                    Refill();
                    return _tokens;
                }
                finally
                {
                    _lock.Release();
                }
            }
        }
    }
}
```

### Task 6.7: Rate Limit Middleware

**File:** `Runtime/RateLimit/RateLimitMiddleware.cs`

```csharp
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.RateLimit
{
    /// <summary>
    /// Middleware that enforces rate limits using token bucket algorithm.
    /// </summary>
    public class RateLimitMiddleware : IHttpMiddleware
    {
        private readonly RateLimitPolicy _policy;
        private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new ConcurrentDictionary<string, TokenBucket>();
        private readonly TokenBucket _globalBucket;

        public RateLimitMiddleware(RateLimitPolicy policy = null)
        {
            _policy = policy ?? new RateLimitPolicy();

            if (!_policy.PerHost)
            {
                _globalBucket = new TokenBucket(_policy.MaxRequests, _policy.TimeWindow);
            }
        }

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            var bucket = GetBucket(request);

            // Try to acquire token
            var acquired = await bucket.TryAcquireAsync();

            if (!acquired)
            {
                // Rate limit exceeded
                context.RecordEvent("RateLimitExceeded");
                Debug.LogWarning($"[RateLimitMiddleware] Rate limit exceeded for {request.Uri.Host}");

                // Wait for token
                context.RecordEvent("RateLimitWaiting");
                await bucket.AcquireAsync(cancellationToken);
                context.RecordEvent("RateLimitAcquired");
            }

            // Proceed with request
            return await next(request, context, cancellationToken);
        }

        private TokenBucket GetBucket(UHttpRequest request)
        {
            if (!_policy.PerHost)
            {
                return _globalBucket;
            }

            var host = request.Uri.Host;

            return _buckets.GetOrAdd(host, h =>
            {
                // Check for host-specific override
                if (_policy.HostOverrides.TryGetValue(h, out var config))
                {
                    return new TokenBucket(config.MaxRequests, config.TimeWindow);
                }

                return new TokenBucket(_policy.MaxRequests, _policy.TimeWindow);
            });
        }

        /// <summary>
        /// Get the current token count for a specific host.
        /// </summary>
        public int GetAvailableTokens(string host)
        {
            if (_buckets.TryGetValue(host, out var bucket))
            {
                return bucket.AvailableTokens;
            }
            return _policy.MaxRequests;
        }
    }
}
```

## Validation Criteria

### Success Criteria

- [ ] Cache middleware caches GET responses
- [ ] ETag revalidation works (returns 304 Not Modified)
- [ ] Last-Modified revalidation works
- [ ] Cache respects Cache-Control headers
- [ ] Cache eviction works (LRU policy)
- [ ] Rate limit middleware throttles requests
- [ ] Token bucket refills correctly
- [ ] Per-host rate limiting works
- [ ] Rate limit doesn't block when under limit

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

Create test file: `Tests/Runtime/RateLimit/TokenBucketTests.cs`

```csharp
using NUnit.Framework;
using System;
using System.Threading.Tasks;
using TurboHTTP.RateLimit;

namespace TurboHTTP.Tests.RateLimit
{
    public class TokenBucketTests
    {
        [Test]
        public async Task TokenBucket_AllowsRequestsUnderLimit()
        {
            var bucket = new TokenBucket(capacity: 5, refillInterval: TimeSpan.FromMinutes(1));

            for (int i = 0; i < 5; i++)
            {
                var acquired = await bucket.TryAcquireAsync();
                Assert.IsTrue(acquired, $"Request {i + 1} should be allowed");
            }

            var exceeded = await bucket.TryAcquireAsync();
            Assert.IsFalse(exceeded, "6th request should be denied");
        }

        [Test]
        public async Task TokenBucket_RefillsOverTime()
        {
            var bucket = new TokenBucket(capacity: 2, refillInterval: TimeSpan.FromSeconds(1));

            // Exhaust tokens
            await bucket.TryAcquireAsync();
            await bucket.TryAcquireAsync();

            // Wait for refill
            await Task.Delay(TimeSpan.FromSeconds(1.1));

            // Should have tokens again
            var acquired = await bucket.TryAcquireAsync();
            Assert.IsTrue(acquired);
        }
    }
}
```

## Next Steps

Once Phase 6 is complete and validated:

1. Move to [Phase 7: Unity Integration](phase-07-unity-integration.md)
2. Implement Unity-specific content handlers (Texture2D, AudioClip)
3. Add main thread synchronization
4. M2 milestone progress

## Notes

- Cache middleware dramatically reduces bandwidth and latency
- ETag revalidation ensures cache freshness
- Rate limiting prevents API quota exhaustion
- Token bucket is industry-standard algorithm
- Per-host limiting respects different API rate limits
- These features are rare in Unity HTTP libraries

## Review Notes

> **TODO: Phase 6 Complexity** - This phase has high implementation complexity that may warrant splitting:
> - **Cache storage backends**: Memory cache with LRU is straightforward, but disk-based caching (not yet specified) adds significant complexity (serialization, file locking, corruption handling, platform-specific paths)
> - **Cache key generation**: Current implementation uses URL only; production needs to include `Vary` headers, authentication context, and potentially request body hashes for POST caching
> - **Cache invalidation**: No explicit invalidation API or support for cache tags/groups
>
> Consider splitting into sub-phases:
> 1. Phase 6a: Memory cache with basic ETag/Last-Modified support
> 2. Phase 6b: Disk cache storage backend (defer to post-M2 if needed)
> 3. Phase 6c: Advanced cache features (Vary headers, invalidation API, cache tags)

### Security & Privacy Notes

- Be conservative by default: do not cache responses with sensitive headers (e.g., `Authorization`) unless explicitly opted in
- Ensure cache keys partition correctly (at minimum: method + URL + relevant `Vary` headers; consider auth/session context)
- Respect `Cache-Control`, `Pragma`, and `Set-Cookie` semantics to avoid leaking user-specific data across sessions
