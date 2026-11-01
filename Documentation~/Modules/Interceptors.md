# Interceptors

Everything that sits between `UHttpClient` and the transport is an `IHttpInterceptor`: retries, caching, redirects, cookies, authentication, logging and metrics are all interceptors, added to `UHttpClientOptions.Interceptors`. Each optional module ships its interceptors in a namespace named after the module (the redirect, cookie, decompression and default-header interceptors live in the `TurboHTTP.Middleware` module).

Interceptors execute in the **order they are listed**: the first entry is the outermost (it sees the request first and the response last).

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
using TurboHTTP.Middleware;
using TurboHTTP.Retry;

options.Interceptors.Add(new RedirectInterceptor());
options.Interceptors.Add(new RetryInterceptor());
var client = new UHttpClient(options);
```

| Module (assembly) | Interceptors |
| :--- | :--- |
| Observability (`TurboHTTP.Observability`) | `LoggingInterceptor`, `MetricsInterceptor`, `MonitorInterceptor` |
| Redirects, cookies, decompression (`TurboHTTP.Middleware`) | `RedirectInterceptor`, `CookieInterceptor`, `DecompressionInterceptor`, `DefaultHeadersInterceptor` |
| Auth (`TurboHTTP.Auth`) | `AuthInterceptor` |
| Cache (`TurboHTTP.Cache`) | `CacheInterceptor` |
| Retry (`TurboHTTP.Retry`) | `RetryInterceptor` |
| RateLimit (`TurboHTTP.RateLimit`) | `ConcurrencyInterceptor` |
| Unity (`TurboHTTP.Unity`) | `AdaptiveInterceptor`, `BackgroundNetworkingInterceptor` (added by `options.UseAdaptiveNetworking(...)` / `options.UseBackgroundNetworking(...)`) |

## Interceptor order

Canonical order of the first-party interceptors, outermost first:

| # | Interceptor (module) | Why it sits here |
| :--- | :--- | :--- |
| 1 | `LoggingInterceptor`, `MetricsInterceptor` (Observability) | See the request as the caller sent it and the final response. |
| 2 | `RedirectInterceptor` (`TurboHTTP.Middleware`) | Every interceptor below it runs once per hop. |
| 3 | `CookieInterceptor` (`TurboHTTP.Middleware`) | Sends and stores cookies per hop (RFC 6265 section 5). |
| 4 | `AuthInterceptor` (Auth) | Adds `Authorization` before the cache sees the request. |
| 5 | `CacheInterceptor` (Cache) | Must see `Authorization` (RFC 9111 section 3.5). |
| 6 | `RetryInterceptor` (Retry) | Retries only real network attempts, not cache hits. |
| 7 | `DecompressionInterceptor` (`TurboHTTP.Middleware`) | Decodes each attempt's body. |
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

Interceptors added by `UseAdaptiveNetworking` and `UseBackgroundNetworking` go to the front of the
list (adaptive, then background, then yours).

## Redirects

`UHttpClientOptions.FollowRedirects` (default `true`) and `MaxRedirects` (default `10`) are applied by
`RedirectInterceptor`. Without it in `Interceptors`, a 3xx response is returned to the caller, and the
client writes a one-time warning through the diagnostic sink. Add the interceptor, or set
`FollowRedirects = false`.

`AuthInterceptor` does not attach its token to a redirect hop for another origin, and `CookieInterceptor`
sends each hop only the cookies stored for that hop's host (RFC 6265 section 5.4).

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
using TurboHTTP.Middleware;

options.Interceptors.Add(new RedirectInterceptor(defaultMaxRedirects: 5));
```

## RetryInterceptor

Retries failed requests with exponential backoff. It retries 5xx responses and network/transport failures, waiting before each retry.

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
using TurboHTTP.Retry;

options.Interceptors.Add(new RetryInterceptor(new RetryPolicy
{
    MaxRetries = 3,
    InitialDelay = TimeSpan.FromSeconds(1),  // starts with a 1s delay
    BackoffMultiplier = 2.0,                 // 1s -> 2s -> 4s
    MaxDelay = TimeSpan.FromSeconds(30),     // caps the delay between retries
    OnlyRetryIdempotent = true               // GET, HEAD, PUT, DELETE, OPTIONS only
}));
```

`RetryPolicy.Default` and `RetryPolicy.NoRetry` are ready-made policies.

*   **`MaxRetries`**: retry attempts *after* the initial request. Default `3` (four attempts in total). `0` disables retries.
*   **`InitialDelay`**: the delay before the first retry. Default 1 second.
*   **`BackoffMultiplier`**: multiplier applied to the delay after each attempt. Default `2.0`.
*   **`MaxDelay`**: upper limit for the delay between retries. Default 30 seconds.
*   **`OnlyRetryIdempotent`**: if `true`, POST and PATCH are never retried, which avoids duplicate side effects. Default `true`.
*   **`UseJitter`**: randomizes each delay. Default `true`.

*Request-level timeouts apply to each attempt independently, not as a total budget.*

**`Retry-After`:** a retryable 5xx response with a `Retry-After` header (delta-seconds or HTTP-date) raises the next delay to the server's value, but never above `MaxDelay`. When `Retry-After` is greater than `MaxDelay`, the interceptor does not wait and does not retry: it returns that response (status, headers including `Retry-After`, and body) to the caller immediately and records a `RetryAfterExceedsMaxDelay` timeline event. A hostile or misconfigured server therefore cannot park a call for longer than `MaxDelay` per retry; raise `MaxDelay` if you want to honor longer server-requested pauses automatically.

## CacheInterceptor

An RFC 9111 client-side cache: it stores and serves responses based on `Cache-Control`, `ETag`, `Last-Modified` and `Vary`, and revalidates stale entries with conditional requests.

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
using TurboHTTP.Cache;

options.Interceptors.Add(new CacheInterceptor(new CachePolicy
{
    EnableRevalidation = true,           // conditional GET (If-None-Match / If-Modified-Since) when stale
    DoNotCacheWithoutFreshness = true,   // only cache responses that carry explicit freshness (max-age, Expires)
    EnableHeuristicFreshness = false,
    AllowPrivateResponses = true,
    Storage = new MemoryCacheStorage(maxEntries: 200)
}));
```

*   **`EnableCache`**: master switch. Default `true`.
*   **`EnableRevalidation`**: revalidate expired entries with `If-None-Match` or `If-Modified-Since`. Default `true`.
*   **`DoNotCacheWithoutFreshness`**: responses without explicit freshness information are not stored. Default `true`.
*   **`EnableHeuristicFreshness`** / **`HeuristicFreshnessLifetime`**: opt in to a heuristic lifetime for responses without explicit freshness (default off, one minute).
*   **`AllowCacheForAuthorizedRequests`**, **`AllowSetCookieResponses`**, **`AllowVaryCookie`**, **`AllowVaryAuthorization`**: all default `false`; per-user responses are not stored unless you opt in.
*   **`InvalidateOnUnsafeMethods`**: a successful POST, PUT, PATCH or DELETE invalidates the cached entry for its URL. Default `true`.
*   **`Storage`**: an `ICacheStorage`; defaults to an in-memory `MemoryCacheStorage` (100 entries, 10 MB).

There is no default TTL: an entry lives exactly as long as the response's own freshness allows.

## Auth, logging and metrics

`AuthInterceptor` is documented in [Auth](Auth.md).

`LoggingInterceptor` writes request and response lines to a callback you provide. `Authorization`, `Cookie` and similar headers are redacted unless you turn `redactSensitiveHeaders` off.

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
using TurboHTTP.Observability;

options.Interceptors.Insert(0, new LoggingInterceptor(
    log: Debug.Log,
    logLevel: LoggingInterceptor.LogLevel.Detailed,
    logHeaders: true,
    logBody: false));
```

`LogLevel` is `None`, `Minimal` (URL and status), `Standard` (adds elapsed time, the default) or `Detailed` (headers and body when enabled).

`MetricsInterceptor` aggregates counters in `HttpMetrics`:

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
using TurboHTTP.Observability;

var metrics = new MetricsInterceptor();
options.Interceptors.Insert(0, metrics);

// later
Debug.Log($"{metrics.Metrics.TotalRequests} requests, " +
          $"{metrics.Metrics.FailedRequests} failed, " +
          $"{metrics.Metrics.GetAverageResponseTimeMs():F0} ms average");
metrics.Reset();
```

`MonitorInterceptor` feeds the Editor HTTP Monitor window.

## ConcurrencyInterceptor

Limits how many requests are on the wire at once, queueing the rest until a permit frees up. It is a concurrency limiter, not a requests-per-minute rate limiter.

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
using TurboHTTP.RateLimit;

options.Interceptors.Add(new ConcurrencyInterceptor(maxConnectionsPerHost: 6, maxTotalConnections: 64));
```

## Redirect-adjacent interceptors

*   **`CookieInterceptor`** keeps a `CookieJar` (RFC 6265): `options.Interceptors.Add(new CookieInterceptor());` The jar is exposed as `Jar`.
*   **`DecompressionInterceptor`** decodes `gzip` and `deflate` bodies with a bound on the decompressed size.
*   **`DefaultHeadersInterceptor`** adds headers to every request, optionally overriding the ones already set.

## Adaptive and background networking

Both live in the `TurboHTTP.Unity` module; see [Unity](Unity.md) and PlatformNotes.

## Writing your own interceptor

An interceptor wraps the next stage of the pipeline.

```csharp
using System.Diagnostics;
using TurboHTTP.Core;

public sealed class TimingInterceptor : IHttpInterceptor
{
    public DispatchFunc Wrap(DispatchFunc next)
    {
        return async (request, handler, context, cancellationToken) =>
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await next(request, handler, context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                UnityEngine.Debug.Log($"{request.Method} {request.Uri} took {watch.ElapsedMilliseconds} ms");
            }
        };
    }
}
```
