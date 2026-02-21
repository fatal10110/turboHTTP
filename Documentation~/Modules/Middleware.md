# Middleware Module

The `Middleware` architectural pattern in TurboHTTP allows you to intercept and modify requests and responses traversing the pipeline.

Middlewares execute in the **order they are added** to `UHttpClientOptions.Middlewares`.

```csharp
var options = new UHttpClientOptions();
options.Middlewares.Add(new LoggingMiddleware());
options.Middlewares.Add(new RetryMiddleware());
```

## Standard Middlewares

### 1. RetryMiddleware

Automatically retries failed requests with exponential backoff. It catches 5xx server errors and network/transport-level exceptions, waiting for a specified delay before retrying. 

#### Configuration (`RetryPolicy`)

You can instantiate `RetryMiddleware` with a custom `RetryPolicy`:

```csharp
var retry = new RetryMiddleware(new RetryPolicy {
    MaxRetries = 3,
    InitialDelay = TimeSpan.FromSeconds(1), // Starts with 1s delay
    BackoffMultiplier = 2.0,                // 1s -> 2s -> 4s
    MaxDelay = TimeSpan.FromSeconds(30),    // Caps the maximum delay between retries
    OnlyRetryIdempotent = true              // Only retries GET, HEAD, PUT, DELETE, OPTIONS
});
```

**Key Properties:**
*   **`MaxRetries`**: Maximum number of retry attempts *after* the initial request. Default is `3` (total of 4 attempts). Set to `0` to disable retries.
*   **`InitialDelay`**: The delay before the first retry. Default is `1 second`.
*   **`BackoffMultiplier`**: Multiplier applied to the delay after each attempt (exponential backoff). Default is `2.0`.
*   **`MaxDelay`**: The upper limit for the delay between retries, preventing unbounded wait times. Default is `30 seconds`.
*   **`OnlyRetryIdempotent`**: If `true`, avoids retrying non-idempotent methods like POST or PATCH to prevent duplicate side effects. Default is `true`.

*Note: Request-level timeouts apply to each attempt independently, not as a total budget.*

### 2. LoggingMiddleware

Logs HTTP requests and responses to `Debug.Log`. It is highly useful for debugging and tracing network calls. Sensitive headers (like `Authorization` or `Cookie`) are automatically redacted by default.

#### Configuration

```csharp
var logging = new LoggingMiddleware(
    logLevel: LoggingMiddleware.LogLevel.Detailed,
    logHeaders: true,
    logBody: true,
    redactSensitiveHeaders: true
);
```

**Log Levels (`LogLevel`):**
*   **`None`**: Disables logging entirely.
*   **`Minimal`**: Logs only the URL and response status.
*   **`Standard`**: (Default) Logs URL, status, and elapsed time.
*   **`Detailed`**: Logs all request and response details, including headers and bodies (if enabled).

### 3. CacheMiddleware

Implements an RFC-aware client-side cache that automatically stores and serves responses based on standard HTTP headers (`Cache-Control`, `ETag`, `Last-Modified`). It supports conditional revalidation to minimize bandwidth usage.

#### Configuration (`CachePolicy`)

```csharp
var cache = new CacheMiddleware(new CachePolicy {
    EnableCache = true,
    EnableRevalidation = true,
    CacheHeadRequests = false,
    DoNotCacheWithoutFreshness = true,
    EnableHeuristicFreshness = false,
    AllowPrivateResponses = true
});
```

**Key Properties:**
*   **`EnableCache`**: Master switch to enable or disable caching. Default is `true`.
*   **`EnableRevalidation`**: When a cached item expires, the middleware will automatically perform a conditional GET (using `If-None-Match` or `If-Modified-Since`) to revalidate it.
*   **`DoNotCacheWithoutFreshness`**: If `true`, responses lacking explicit freshness information (like `max-age`) will not be cached to avoid serving stale data unexpectedly.
*   **`Storage`**: Defines where the cache data is kept. Defaults to an in-memory `MemoryCacheStorage`, but can be backed by persistent storage.

### 4. RateLimitMiddleware

Throttles outbound requests to prevent hitting server limits (especially `429 Too Many Requests`). 

```csharp
var limit = new RateLimitMiddleware(new RateLimitPolicy {
    MaxRequests = 100,
    TimeWindow = TimeSpan.FromMinutes(1)
});
```

## Advanced Middlewares

### AdaptiveMiddleware

Dynamically adjusts request timeouts and behavior based on the current measured network reliability and success rate. Highly useful for mobile environments with fluctuating connections.

### MetricsMiddleware

Records TTFB (Time To First Byte), DNS lookup times, Connect times, and payload sizes centrally. Often used to expose data to an external Telemetry or observability system.

### ConcurrencyMiddleware

Limits the maximum number of concurrent requests executing at the same time, queuing excess requests until slots become available.
