# Middleware Module

The `Middleware` architectural pattern in TurboHTTP allows you to intercept and modify requests and responses traversing the pipeline.

Interceptors execute in the **order they are listed** in `UHttpClientOptions.Interceptors`: the first
entry is the outermost (it sees the request first and the response last).

```csharp
var options = new UHttpClientOptions();
options.Interceptors.Add(new RedirectInterceptor());
options.Interceptors.Add(new RetryInterceptor());
```

## Interceptor order

Canonical order of the first-party interceptors, outermost first:

| # | Interceptor (module) | Why it sits here |
| :--- | :--- | :--- |
| 1 | `LoggingInterceptor`, `MetricsInterceptor` (Observability) | See the request as the caller sent it and the final response. |
| 2 | `RedirectInterceptor` (Middleware) | Every interceptor below it runs once per hop. |
| 3 | `CookieInterceptor` (Middleware) | Sends and stores cookies per hop (RFC 6265 section 5). |
| 4 | `AuthInterceptor` (Auth) | Adds `Authorization` before the cache sees the request. |
| 5 | `CacheInterceptor` (Cache) | Must see `Authorization` (RFC 9111 section 3.5). |
| 6 | `RetryInterceptor` (Retry) | Retries only real network attempts, not cache hits. |
| 7 | `DecompressionInterceptor` (Middleware) | Decodes each attempt's body. |
| 8 | `ConcurrencyInterceptor` (RateLimit) | Holds a permit only while a request is on the wire. |

`UHttpClient` rejects two orders when it is constructed (`ArgumentException`), and when a plugin
registers interceptors (`PluginException`):

- **`CacheInterceptor` outside `AuthInterceptor`.** The cache would never see the `Authorization` header,
  so `CachePolicy.AllowCacheForAuthorizedRequests = false` is bypassed and a per-user response would be
  stored under a user-less key and served to the next user.
- **`ConcurrencyInterceptor` outside `RetryInterceptor`.** A permit would be held through every retry
  backoff delay (up to `RetryPolicy.MaxDelay`), starving other requests.

`RedirectInterceptor` and `AuthInterceptor`, and `RedirectInterceptor` and `CookieInterceptor`, work in
either order: the token stays bound to its origin and each hop's cookies are stored for that hop's host.

## Redirects

`UHttpClientOptions.FollowRedirects` (default `true`) and `MaxRedirects` (default `10`) are applied by
`RedirectInterceptor`. Without it in `Interceptors`, a 3xx response is returned to the caller, and the
client writes a one-time warning through the diagnostic sink. Add the interceptor, or set
`FollowRedirects = false`.

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

**`Retry-After`:** a retryable 5xx response with a `Retry-After` header (delta-seconds or HTTP-date) raises the next delay to the server's value, but never above `MaxDelay`. When `Retry-After` is greater than `MaxDelay`, the interceptor does not wait and does not retry: it returns that response (status, headers including `Retry-After`, and body) to the caller immediately and records a `RetryAfterExceedsMaxDelay` timeline event. A hostile or misconfigured server therefore cannot park a call for longer than `MaxDelay` per retry; raise `MaxDelay` if you want to honor longer server-requested pauses automatically.

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
