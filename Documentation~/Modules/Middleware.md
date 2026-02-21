# Middleware Module

The `Middleware` architectural pattern in TurboHTTP allows you to intercept and modify requests and responses traversing the pipeline.

Middlewares execute in the **order they are added** to `UHttpClientOptions.Middlewares`.

```csharp
var options = new UHttpClientOptions();
options.Middlewares.Add(new LoggingMiddleware());
options.Middlewares.Add(new RetryMiddleware());
```

## Standard Middlewares

### LoggingMiddleware

Logs requests and responses to `Debug.Log`. Useful for debugging during development.

```csharp
var logging = new LoggingMiddleware(
    logLevel: LoggingMiddleware.LogLevel.Detailed,
    logHeaders: true,
    logBody: true
);
```

### RetryMiddleware

Automatically retries failed requests (e.g., 502 Bad Gateway, Network Timeouts) based on a policy.

```csharp
var retry = new RetryMiddleware(new RetryPolicy {
    MaxRetries = 3,
    InitialDelay = TimeSpan.FromSeconds(1), // Backoff
    BackoffMultiplier = 2.0,
    OnlyRetryIdempotent = true // GET, PUT, DELETE, HEAD
});
```

### CacheMiddleware

Implements client-side caching obeying standard HTTP headers (`Cache-Control`, `ETag`, `Last-Modified`).

```csharp
var cache = new CacheMiddleware(new CachePolicy {
    EnableCache = true,
    DefaultTtl = TimeSpan.FromMinutes(5),
    EnableRevalidation = true
});
```

### RateLimitMiddleware

Throttles outbound requests to prevent hitting server limits (especially 429 Too Many Requests).

```csharp
var limit = new RateLimitMiddleware(new RateLimitPolicy {
    MaxRequests = 100,
    TimeWindow = TimeSpan.FromMinutes(1)
});
```

## Advanced Middlewares

Phase 14 introduced Adaptive Networking and Metrics.

### AdaptiveMiddleware

Dynamically adjusts request timeouts based on the current measured network reliability and success rate.

### MetricsMiddleware

Records TTFB (Time To First Byte), DNS lookup times, Connect times, and payload sizes centrally, often exposing them to an external Telemetry system.
