# TurboHTTP API Reference

API documentation for TurboHTTP. Every C# snippet in this documentation is compiled against the current API by the repository's CI (`Tools~/ci/shadow_build.py`).

Snippets assume the usual Unity script header (`using System; using System.Threading; using System.Threading.Tasks; using UnityEngine; using TurboHTTP.Core;`); module namespaces are shown explicitly. `MyDto` / `MyData` stand for your own data classes.

## Table of Contents

*   [Module Deep Dives](#module-deep-dives)
*   [Core Classes](#core-classes)
    *   [UHttpClient](#uhttpclient)
    *   [UHttpRequest](#uhttprequest)
    *   [UHttpResponse](#uhttpresponse)
    *   [UHttpStreamingResponse](#uhttpstreamingresponse)
    *   [UHttpClientOptions](#uhttpclientoptions)
*   [Extension Methods](#extension-methods)
*   [Interceptors](#interceptors)
*   [Error Handling](#error-handling)
*   [Advanced Features](#advanced-features)

---

## Module Deep Dives

*   [Core](Modules/Core.md)
*   [Transport (TLS, proxies, HTTP/2)](Modules/Transport.md)
*   [WebSocket](Modules/WebSocket.md)
*   [Unity Integration](Modules/Unity.md)
*   [UniTask Support](Modules/UniTask.md)
*   [Authentication](Modules/Auth.md)
*   [Interceptors](Modules/Interceptors.md)
*   [Testing](Modules/Testing.md)

---

## Core Classes

### UHttpClient

Main HTTP client class. Thread-safe and reusable. **Create one instance per application or module** and dispose it when you are done.

`UHttpClient` implements `IDisposable`.

**Constructor:**

```csharp
using TurboHTTP.Core;

// Default options
var client = new UHttpClient();

// Custom options
var options = new UHttpClientOptions { BaseUrl = "https://api.example.com" };
var configured = new UHttpClient(options);
```

**Methods:**

| Method | Description |
| :--- | :--- |
| `Get(url)`, `Post(url)`, `Put(url)`, `Delete(url)`, `Patch(url)`, `Head(url)`, `Options(url)` | Create a request for that method |
| `CreateRequest(method, url)` | Create a request for an `HttpMethod` |
| `SendBufferedAsync(request, ct)` | Send a request and collect the whole body (`Task<UHttpResponse>`) |
| `SendStreamingAsync(request, ct)` | Send a request and stream the body (`Task<UHttpStreamingResponse>`) |
| `RegisterPluginAsync(plugin, ct)` | Register an `IHttpPlugin` (see Plugins) |
| `GetOptionsSnapshot()` | A copy of the options the client runs with |
| `Dispose()` | Release the connection pool and other resources |

### UHttpRequest

A mutable request bound to the client that created it. It is single-use: sending it with `SendBufferedAsync()` or `SendStreamingAsync()`, or calling `Dispose()`, ends its lease, after which any further use throws `ObjectDisposedException`.

**Methods:**

| Method | Description | Example |
| :--- | :--- | :--- |
| `WithHeader(name, value)` | Set a header | `.WithHeader("Accept", "application/json")` |
| `WithHeaders(headers)` | Set several headers | `.WithHeaders(myHeaders)` |
| `WithBody(byte[])` / `WithBody(ReadOnlyMemory<byte>)` / `WithBody(string)` | Set a buffered body | `.WithBody("plain text")` |
| `WithLeasedBody(owner, length)`, `WithBodyFactory(...)` | Pooled or re-creatable bodies | |
| `WithRequestTrailers(...)` | Send request trailers | |
| `WithExpectContinue(...)` | Send `Expect: 100-continue` | |
| `WithTimeout(timespan)` | Timeout for this request | `.WithTimeout(TimeSpan.FromSeconds(30))` |
| `WithMetadata(key, value)` | Attach data for interceptors | `.WithMetadata("tag", "login")` |
| `SendBufferedAsync(ct)` | Send; buffer the body (`ValueTask<UHttpResponse>`) | `await request.SendBufferedAsync()` |
| `SendStreamingAsync(ct)` | Send; stream the body (`ValueTask<UHttpStreamingResponse>`) | `await request.SendStreamingAsync()` |

Extension methods from the modules add `WithJsonBody(...)` (JSON), `WithBearerToken(token)` (Auth), `WithFileBody(...)` (Files) and `WithBackgroundReplayKey(...)` (Unity).

### UHttpResponse

A buffered response. It is `IDisposable`: dispose it when you are done with the body (`using var response = await ...`).

**Properties:**

| Property | Type | Description |
| :--- | :--- | :--- |
| `StatusCode` | `HttpStatusCode` | HTTP status code |
| `Headers` | `HttpHeaders` | Response headers |
| `Trailers` | `HttpHeaders` | Response trailers |
| `Body` | `ReadOnlySequence<byte>` | Response body sequence |
| `ElapsedTime` | `TimeSpan` | Request duration |
| `Request` | `UHttpRequest` | Original request |
| `Error` | `UHttpError` | Error info for a transport-synthesized failure (normally `null`) |
| `IsSuccessStatusCode` | `bool` | True if status is 200-299 |
| `IsError` | `bool` | True if `Error` is set |

**Methods:**

| Method | Description |
| :--- | :--- |
| `GetBodyAsString()` / `GetBodyAsString(Encoding)` | Get body as a string (UTF-8 unless the content type says otherwise) |
| `EnsureSuccessStatusCode()` | Throw `UHttpException` if the status is not 2xx |
| `AsJson<T>()` | Deserialize the body as JSON (`TurboHTTP.JSON`) |
| `AsTexture2D()` | Decode the body as a texture (`TurboHTTP.Unity`) |

### UHttpStreamingResponse

A response whose body is read as it arrives. It is `IDisposable` and `IAsyncDisposable`; it holds the connection until disposed.

| Member | Description |
| :--- | :--- |
| `StatusCode`, `Headers` | Available immediately |
| `Body` | A read-only `Stream` over the body |
| `GetTrailersAsync(ct)` | Trailers, after the body reached its end |
| `Dispose()` / `DisposeAsync()` | Release the connection; aborts an unfinished transfer |

### UHttpClientOptions

Configuration options for `UHttpClient`.

**Properties:**

| Property | Type | Description |
| :--- | :--- | :--- |
| `BaseUrl` | `string` | Base URL that relative request URLs are resolved against |
| `DefaultTimeout` | `TimeSpan` | Default timeout (default: 30s) |
| `DefaultHeaders` | `HttpHeaders` | Headers applied to every request |
| `Transport` | `IHttpTransport` | Custom HTTP transport implementation |
| `DisposeTransport` | `bool` | Dispose a custom `Transport` together with the client |
| `Interceptors` | `List<IHttpInterceptor>` | Interceptor pipeline, outermost first; see [Interceptors](Modules/Interceptors.md) for the required order |
| `FollowRedirects` / `MaxRedirects` | `bool` / `int` | Redirect policy (default `true` / `10`); applied only when a `RedirectInterceptor` is configured |
| `MaxBufferedResponseBodySize` | `long` | Cap for buffered bodies (default 100 MB; `0` = unlimited) |
| `Http2` | `Http2Options` | HTTP/2 specific settings |
| `ConnectionPool` | `ConnectionPoolOptions` | Connection pool configuration |
| `Proxy` | `ProxySettings` | HTTP proxy configuration; environment variables are used when unset |
| `TlsBackend` | `TlsBackend` | `Auto` (default, SslStream), `SslStream` or `BouncyCastle` (opt-in); see [Transport](Modules/Transport.md) |
| `AllowCleartext` | `bool` | Allow `http://` and `http://` proxies (default `false` on iOS/Android players) |
| `Streaming` | `StreamingOptions` | Streaming response settings |
| `PluginShutdownTimeout` | `TimeSpan` | How long plugin shutdown callbacks may take |

Background networking and adaptive timeouts are not options properties: they live in the
`TurboHTTP.Unity` module (`TurboHTTP.Unity.Mobile` namespace) and are added with
`options.UseBackgroundNetworking(policy)` and `options.UseAdaptiveNetworking(policy)` (see [Unity](Modules/Unity.md)).

---

## Extension Methods

### JSON Extensions

`TurboHTTP.JSON`. Without a custom serializer registered through `JsonSerializer.SetDefault(...)`, the module uses its bundled serializer.

<!-- snippet: with UHttpClient client = null; UHttpResponse response = null; string url = ""; -->
```csharp
using TurboHTTP.JSON;

// Deserialize a response
MyDto data = response.AsJson<MyDto>();

// GET with JSON
MyDto got = await client.GetJsonAsync<MyDto>(url);

// POST with JSON
MyDto created = await client.PostJsonAsync<MyDto, MyDto>(url, data);

// PUT, PATCH and DELETE work the same way
MyDto updated = await client.PutJsonAsync<MyDto, MyDto>(url, data);

// Build a request body
using var response2 = await client.Post(url).WithJsonBody(data).SendBufferedAsync();
```

### Unity Extensions

`TurboHTTP.Unity`; see [Unity](Modules/Unity.md).

<!-- snippet: with UHttpClient client = null; string url = ""; -->
```csharp
using TurboHTTP.Unity;

Texture2D texture = await client.GetTextureAsync(url);
Sprite sprite = await client.GetSpriteAsync(url);
AudioClip clip = await client.GetAudioClipAsync(url, AudioClipType.MP3);
string path = await client.DownloadToPersistentDataAsync(url, "file.zip");
```

---

## Interceptors

Retries, caching, redirects, cookies, authentication, logging and metrics are `IHttpInterceptor`s added to `UHttpClientOptions.Interceptors`. They run in list order, outermost first; `UHttpClient` rejects unsafe orders. The full list, the canonical order and per-interceptor configuration are in [Interceptors](Modules/Interceptors.md) and [Auth](Modules/Auth.md).

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); IAuthTokenProvider tokenProvider = null; -->
```csharp
using TurboHTTP.Auth;
using TurboHTTP.Cache;
using TurboHTTP.Middleware;
using TurboHTTP.Observability;
using TurboHTTP.RateLimit;
using TurboHTTP.Retry;

options.Interceptors.Add(new LoggingInterceptor(Debug.Log));
options.Interceptors.Add(new RedirectInterceptor());
options.Interceptors.Add(new AuthInterceptor(tokenProvider));
options.Interceptors.Add(new CacheInterceptor(new CachePolicy { EnableRevalidation = true }));
options.Interceptors.Add(new RetryInterceptor(new RetryPolicy { MaxRetries = 3 }));
options.Interceptors.Add(new ConcurrencyInterceptor(maxConnectionsPerHost: 6));
```

---

## Error Handling

### UHttpErrorType

| Value | Meaning |
| :--- | :--- |
| `NetworkError` | Connection failed (DNS, refused, reset) or a bounded size was exceeded |
| `Timeout` | Request timeout |
| `HttpError` | HTTP error status (4xx, 5xx), raised by `EnsureSuccessStatusCode()` |
| `CertificateError` | TLS certificate or handshake failure |
| `Cancelled` | Cancelled by the caller |
| `InvalidRequest` | Bad request configuration (for example a refused cleartext request) |
| `Unknown` | Unexpected error |

### Catching exceptions

Network and protocol failures throw `UHttpException`. HTTP 4xx/5xx responses are normal responses (check `StatusCode`, or call `EnsureSuccessStatusCode()` to turn them into an exception).

<!-- snippet: with UHttpClient client = null; string url = ""; -->
```csharp
try
{
    using var response = await client.Get(url).SendBufferedAsync();
    response.EnsureSuccessStatusCode();
}
catch (UHttpException ex)
{
    Debug.LogError($"Error: {ex.HttpError.Type} - {ex.HttpError.Message}");
    bool shouldRetry = ex.HttpError.IsRetryable();
}
```

---

## Advanced Features

### File Downloads

<!-- snippet: with UHttpClient client = null; string url = ""; string savePath = ""; -->
```csharp
using TurboHTTP.Files;

var downloader = new FileDownloader(client);
var downloadOptions = new DownloadOptions
{
    EnableResume = true,
    VerifyChecksum = true,
    ExpectedMd5 = "abc123...",
    Progress = new Progress<DownloadProgress>(p => Debug.Log($"{p.Percentage:F1}%"))
};

DownloadResult result = await downloader.DownloadFileAsync(url, savePath, downloadOptions);
```

Downloads are written to `savePath + ".part"` and moved to `savePath` only after the body completed and the checksum (if any) matched; an existing file at `savePath` is replaced atomically and is never touched by a failed download. With `EnableResume`, the response's strong validator (strong `ETag`, or a `Last-Modified` at least one second older than `Date`) is stored in `savePath + ".part.validator"`, and the next call resumes with `Range`, `If-Range` and `Accept-Encoding: identity`. A changed resource (200), a 416, or a 206 whose `Content-Range` does not continue the partial restarts from scratch; 4xx/5xx responses throw and leave the partial and the destination untouched. A checksum mismatch deletes the partial.

### Multipart Uploads

<!-- snippet: with UHttpClient client = null; -->
```csharp
using TurboHTTP.Files;

var multipart = new MultipartFormDataBuilder()
    .AddField("title", "My Upload")
    .AddFileFromDisk("file", "/path/to/file.png", "image/png");

using var request = client.Post("https://api.example.com/upload");
multipart.ApplyTo(request);
using var response = await request.SendBufferedAsync();
```

### Record/Replay and mock servers

`TurboHTTP.Testing` provides `MockTransport`, `MockHttpServer` and `RecordReplayTransport`; see [Testing](Modules/Testing.md).

### Plugins

An `IHttpPlugin` is an extension module that can contribute interceptors when it is registered.

```csharp
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Observability;

public class CustomLoggerPlugin : IHttpPlugin
{
    public string Name => "CustomLogger";
    public string Version => "1.0.0";
    public PluginCapabilities Capabilities => PluginCapabilities.None;

    public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        context.RegisterInterceptor(new LoggingInterceptor(UnityEngine.Debug.Log));
        return default;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken) => default;
}

public static class PluginExample
{
    public static Task RegisterAsync(UHttpClient client) => client.RegisterPluginAsync(new CustomLoggerPlugin());
}
```
