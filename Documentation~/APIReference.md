# TurboHTTP API Reference

Complete API documentation for TurboHTTP.

## Table of Contents

*   [Module Deep Dives](#module-deep-dives)
*   [Core Classes](#core-classes)
    *   [UHttpClient](#uhttpclient)
    *   [UHttpRequestBuilder](#uhttprequestbuilder)
    *   [UHttpResponse](#uhttpresponse)
    *   [UHttpClientOptions](#uhttpclientoptions)
*   [Extension Methods](#extension-methods)
*   [Middleware](#middleware)
*   [Error Handling](#error-handling)
*   [Advanced Features](#advanced-features)

---

## Module Deep Dives

For comprehensive, architectural details on specific parts of TurboHTTP, refer to the individual module documentation:

*   [Core](Modules/Core.md)
*   [Transport](Modules/Transport.md)
*   [WebSocket](Modules/WebSocket.md)
*   [Unity Integration](Modules/Unity.md)
*   [UniTask Support](Modules/UniTask.md)
*   [Authentication](Modules/Auth.md)
*   [Middleware](Modules/Middleware.md)
*   [Testing](Modules/Testing.md)

---

## Core Classes

### UHttpClient

Main HTTP client class. Thread-safe and reusable. **It is recommended to create one instance per application or module.**

```csharp
public class UHttpClient : IDisposable
```

**Constructor:**

```csharp
// Default options
var client = new UHttpClient();

// Custom options
var options = new UHttpClientOptions { BaseUrl = "https://api.example.com" };
var client = new UHttpClient(options);
```

**Methods:**

| Method | Description |
| :--- | :--- |
| `Get(url)` | Create GET request builder |
| `Post(url)` | Create POST request builder |
| `Put(url)` | Create PUT request builder |
| `Delete(url)` | Create DELETE request builder |
| `Patch(url)` | Create PATCH request builder |
| `Head(url)` | Create HEAD request builder |
| `Options(url)` | Create OPTIONS request builder |
| `SendAsync(request)` | Send a pre-built request |

### UHttpRequestBuilder

Fluent API for building HTTP requests.

**Methods:**

| Method | Description | Example |
| :--- | :--- | :--- |
| `WithHeader(name, value)` | Add header | `.WithHeader("Accept", "application/json")` |
| `WithBody(byte[])` | Set body | `.WithBody(data)` |
| `WithBody(string)` | Set body as string | `.WithBody("plain text")` |
| `WithJsonBody<T>(data)` | Set JSON body | `.WithJsonBody(myObject)` |
| `WithTimeout(timespan)` | Set timeout | `.WithTimeout(TimeSpan.FromSeconds(30))` |
| `WithBearerToken(token)` | Set Bearer auth | `.WithBearerToken("token123")` |
| `Accept(contentType)` | Set Accept header | `.Accept("application/json")` |
| `ContentType(contentType)` | Set Content-Type | `.ContentType("text/plain")` |
| `SendAsync()` | Build and send | `.SendAsync()` |

### UHttpResponse

Represents an HTTP response.

**Properties:**

| Property | Type | Description |
| :--- | :--- | :--- |
| `StatusCode` | `HttpStatusCode` | HTTP status code |
| `Headers` | `HttpHeaders` | Response headers |
| `Body` | `byte[]` | Response body |
| `ElapsedTime` | `TimeSpan` | Request duration |
| `Request` | `UHttpRequest` | Original request |
| `Error` | `UHttpError` | Error info (if any) |
| `IsSuccessStatusCode` | `bool` | True if status is 200-299 |
| `IsError` | `bool` | True if error occurred |

**Methods:**

| Method | Description |
| :--- | :--- |
| `GetBodyAsString()` | Get body as UTF-8 string |
| `AsJson<T>()` | Deserialize body as JSON |
| `EnsureSuccessStatusCode()` | Throw `UHttpException` if not successful |

### UHttpClientOptions

Configuration options for `UHttpClient`.

**Properties:**

| Property | Type | Description |
| :--- | :--- | :--- |
| `BaseUrl` | `string` | Base URL for requests |
| `DefaultTimeout` | `TimeSpan` | Default timeout (default: 30s) |
| `DefaultHeaders` | `HttpHeaders` | Headers applied to every request |
| `Transport` | `IHttpTransport` | Custom HTTP transport implementation |
| `Middlewares` | `List<IHttpMiddleware>` | Middleware pipeline |
| `Http2MaxDecodedHeaderBytes` | `int` | Max decoded header bytes for HTTP/2 (default: `262144`) |
| `Proxy` | `ProxySettings` | HTTP/HTTPS proxy configuration (Phase 14) |
| `HappyEyeballs` | `HappyEyeballsOptions` | IPv4/IPv6 dual-stack fallback settings (Phase 14) |
| `BackgroundNetworking` | `BackgroundNetworkingPolicy` | iOS/Android native background exec settings (Phase 14) |
| `Plugins` | `List<IHttpPlugin>` | Zero-overhead extension plugins (Phase 14) |

---

## Extension Methods

### JSON Extensions

```csharp
// Deserialize response
var data = response.AsJson<MyType>();

// POST with JSON
var result = await client.PostJsonAsync<Request, Response>(url, data);

// GET with JSON
var result = await client.GetJsonAsync<MyType>(url);

// PUT with JSON
var result = await client.PutJsonAsync<Request, Response>(url, data);
```

### Unity Extensions

```csharp
// Load texture
var texture = await client.GetTextureAsync(url);

// Load sprite
var sprite = await client.GetSpriteAsync(url);

// Load audio clip
var clip = await client.GetAudioClipAsync(url, AudioType.MP3);

// Download to persistent data path
var path = await client.DownloadToPersistentDataAsync(url, "file.zip");
```

---

## Middleware

TurboHTTP uses a middleware pipeline. You can add default middlewares via `UHttpClientOptions`.

```csharp
using TurboHTTP.Auth;
using TurboHTTP.Cache;
using TurboHTTP.Middleware;
using TurboHTTP.Retry;
```

### LoggingMiddleware

```csharp
var logging = new LoggingMiddleware(
    logLevel: LoggingMiddleware.LogLevel.Detailed,
    logHeaders: true,
    logBody: true
);
options.Middlewares.Add(logging);
```

### RetryMiddleware

```csharp
var retry = new RetryMiddleware(new RetryPolicy {
    MaxRetries = 3,
    InitialDelay = TimeSpan.FromSeconds(1),
    BackoffMultiplier = 2.0,
    OnlyRetryIdempotent = true
});
options.Middlewares.Add(retry);
```

### CacheMiddleware

```csharp
var cache = new CacheMiddleware(new CachePolicy {
    EnableCache = true,
    DefaultTtl = TimeSpan.FromMinutes(5),
    EnableRevalidation = true
});
options.Middlewares.Add(cache);
```

### AuthMiddleware

```csharp
// Static Token
var staticProvider = new StaticTokenProvider("your-token");
options.Middlewares.Add(new AuthMiddleware(staticProvider));

// OAuth 2.0 / OIDC (Phase 14)
var oauthConfig = new OAuthConfig {
    DiscoveryUrl = "https://auth.example.com/.well-known/openid-configuration",
    ClientId = "my-client-id",
    Scopes = { "openid", "profile", "api" }
};
var oauthClient = new OAuthClient(oauthConfig, new InMemoryTokenStore());
options.Middlewares.Add(new AuthMiddleware(new OAuthTokenProvider(oauthClient)));
```

### RateLimitMiddleware

```csharp
var rateLimit = new RateLimitMiddleware(new RateLimitPolicy {
    MaxRequests = 100,
    TimeWindow = TimeSpan.FromMinutes(1),
    PerHost = true
});
options.Middlewares.Add(rateLimit);
```

### MetricsMiddleware & AdaptiveMiddleware

```csharp
// Standard Metrics
var metrics = new MetricsMiddleware();
options.Middlewares.Add(metrics);

// Adaptive Networking (Phase 14 - Adjusts timeouts dynamically)
var adaptive = new AdaptiveMiddleware(new AdaptivePolicy {
    TargetSuccessRate = 0.95,
    MaxTimeoutMultiplier = 3.0
});
options.Middlewares.Add(adaptive);
```

---

## Error Handling

### UHttpErrorType

```csharp
public enum UHttpErrorType
{
    NetworkError,    // Connection failed
    Timeout,         // Request timeout
    HttpError,       // HTTP error status (4xx, 5xx)
    CertificateError,// SSL/TLS error
    Cancelled,       // User cancelled
    InvalidRequest,  // Bad request configuration
    Unknown          // Unexpected error
}
```

### Catching Exceptions

If you use `EnsureSuccessStatusCode()` or encounter a strict failure:

```csharp
try
{
    var response = await client.Get(url).SendAsync();
    response.EnsureSuccessStatusCode();
}
catch (UHttpException ex)
{
    Debug.LogError($"Error: {ex.HttpError.Type} - {ex.HttpError.Message}");
    // Check if retryable
    bool shouldRetry = ex.HttpError.IsRetryable();
}
```

---

## Advanced Features

### File Downloads

```csharp
using TurboHTTP.Files;

var downloader = new FileDownloader();
var options = new DownloadOptions
{
    EnableResume = true,
    VerifyChecksum = true,
    ExpectedMd5 = "abc123...",
    Progress = new Progress<DownloadProgress>(p => Debug.Log($"{p.Percentage:F1}%"))
};

await downloader.DownloadFileAsync(url, savePath, options);
```

### Multipart Uploads

```csharp
using TurboHTTP.Files;

var multipart = new MultipartFormDataBuilder()
    .AddField("title", "My Upload")
    .AddFile("file", "/path/to/file.png", "image/png");

var response = await client.Post("https://api.example.com/upload")
    .WithBody(multipart.Build())
    .ContentType(multipart.GetContentType())
    .SendAsync();
```

### Record/Replay Testing

Deterministic network testing for CI/CD.

```csharp
using TurboHTTP.Testing;

// 1. Record Mode
var transport = new RecordReplayTransport(
    new RawSocketTransport(),
    RecordReplayMode.Record,
    "recordings.json"
);

// ... make requests ...
transport.SaveRecordings();

// 2. Replay Mode
var replayTransport = new RecordReplayTransport(
    new RawSocketTransport(),
    RecordReplayMode.Replay,
    "recordings.json"
);
// Requests will now return recorded responses instantly.
```

### Mock Server

In-memory mocking for robust offline testing without touching the network stack.

```csharp
using TurboHTTP.Testing;

var mockServer = new MockHttpServer();

// Setup a mock route
mockServer.Setup(route => route
    .MatchMethod(HttpMethod.Get)
    .MatchPath("/api/users/1"))
    .Returns(new MockResponseBuilder()
        .WithStatusCode(HttpStatusCode.OK)
        .WithJsonBody(new { id = 1, name = "Test User" }));

// Configure client to use the mock transport
var options = new UHttpClientOptions {
    Transport = new MockTransport(mockServer)
};
var mockClient = new UHttpClient(options);
```

### Interceptors & Plugins

Low-level, zero-overhead mutation of requests and responses before middleware execution.

```csharp
using TurboHTTP.Core;

public class CustomHeaderInterceptor : IHttpInterceptor
{
    public ValueTask<InterceptorResponseAction> OnResponseAsync(
        UHttpResponse response, PluginContext context, CancellationToken ct)
    {
        response.Headers.Add("X-Injected", "true");
        return new ValueTask<InterceptorResponseAction>(InterceptorResponseAction.Continue);
    }
}

// Register via Plugin System
options.Plugins.Add(new CustomInterceptorPlugin(new CustomHeaderInterceptor()));
```
