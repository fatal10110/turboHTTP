# TurboHTTP API Reference

Complete API documentation for TurboHTTP.

## Core Classes

### UHttpClient

Main HTTP client class. Thread-safe and reusable.

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
|--------|-------------|
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
|--------|-------------|---------|
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
|----------|------|-------------|
| `StatusCode` | `HttpStatusCode` | HTTP status code |
| `Headers` | `HttpHeaders` | Response headers |
| `Body` | `byte[]` | Response body |
| `ElapsedTime` | `TimeSpan` | Request duration |
| `Request` | `UHttpRequest` | Original request |
| `Error` | `UHttpError` | Error (if any) |
| `IsSuccessStatusCode` | `bool` | True if 2xx status |
| `IsError` | `bool` | True if error occurred |

**Methods:**

| Method | Description |
|--------|-------------|
| `GetBodyAsString()` | Get body as UTF-8 string |
| `AsJson<T>()` | Deserialize body as JSON |
| `EnsureSuccessStatusCode()` | Throw if not successful |

### UHttpClientOptions

Configuration options for UHttpClient.

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `BaseUrl` | `string` | Base URL for all requests |
| `DefaultTimeout` | `TimeSpan` | Default timeout |
| `DefaultHeaders` | `HttpHeaders` | Headers for all requests |
| `Transport` | `IHttpTransport` | HTTP transport implementation |
| `Middlewares` | `List<IHttpMiddleware>` | Middleware pipeline |
| `Http2MaxDecodedHeaderBytes` | `int` | Maximum decoded HTTP/2 header bytes per header block (HPACK decompression-bomb guard, default `262144`) |

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

## Middleware

### LoggingMiddleware

```csharp
var middleware = new LoggingMiddleware(
    logLevel: LoggingMiddleware.LogLevel.Detailed,
    logHeaders: true,
    logBody: true
);
options.Middlewares.Add(middleware);
```

### RetryMiddleware

```csharp
var policy = new RetryPolicy
{
    MaxRetries = 3,
    InitialDelay = TimeSpan.FromSeconds(1),
    BackoffMultiplier = 2.0,
    OnlyRetryIdempotent = true
};
options.Middlewares.Add(new RetryMiddleware(policy));
```

### CacheMiddleware

```csharp
var policy = new CachePolicy
{
    EnableCache = true,
    DefaultTtl = TimeSpan.FromMinutes(5),
    EnableRevalidation = true
};
options.Middlewares.Add(new CacheMiddleware(policy));
```

### AuthMiddleware

```csharp
var tokenProvider = new StaticTokenProvider("your-token");
options.Middlewares.Add(new AuthMiddleware(tokenProvider));
```

### RateLimitMiddleware

```csharp
var policy = new RateLimitPolicy
{
    MaxRequests = 100,
    TimeWindow = TimeSpan.FromMinutes(1),
    PerHost = true
};
options.Middlewares.Add(new RateLimitMiddleware(policy));
```

### MetricsMiddleware

```csharp
var metrics = new MetricsMiddleware();
options.Middlewares.Add(metrics);

// Later, get metrics
Debug.Log($"Total requests: {metrics.Metrics.TotalRequests}");
Debug.Log($"Success rate: {metrics.Metrics.SuccessfulRequests / metrics.Metrics.TotalRequests * 100}%");
```

## Error Handling

### UHttpError

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

### UHttpException

```csharp
try
{
    var response = await client.Get(url).SendAsync();
}
catch (UHttpException ex)
{
    Debug.LogError($"Error type: {ex.HttpError.Type}");
    Debug.LogError($"Message: {ex.HttpError.Message}");
    Debug.LogError($"Retryable: {ex.HttpError.IsRetryable()}");
}
```

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
    Progress = new Progress<DownloadProgress>(p =>
    {
        Debug.Log($"Progress: {p.Percentage:F1}%");
    })
};

var result = await downloader.DownloadFileAsync(url, savePath, options);
```

### Multipart Uploads

```csharp
using TurboHTTP.Files;

var multipart = new MultipartFormDataBuilder()
    .AddField("title", "My Upload")
    .AddFile("file", "/path/to/file.png", "image/png");

var response = await client
    .Post("https://api.example.com/upload")
    .WithBody(multipart.Build())
    .ContentType(multipart.GetContentType())
    .SendAsync();
```

### Record/Replay Testing

```csharp
using TurboHTTP.Testing;

// Record mode
var transport = new RecordReplayTransport(
    new RawSocketTransport(),
    RecordReplayMode.Record,
    "recordings.json"
);

// ... make requests ...

transport.SaveRecordings();

// Replay mode
var transport = new RecordReplayTransport(
    new RawSocketTransport(),
    RecordReplayMode.Replay,
    "recordings.json"
);
```
