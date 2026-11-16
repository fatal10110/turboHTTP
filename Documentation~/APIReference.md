# TurboHTTP API Reference

API documentation for TurboHTTP. Every C# snippet in this documentation is compiled against the current API by the repository's CI (`Tools~/ci/shadow_build.py`), except the custom serializer example under [JSON serializers](#json-serializers), which needs your own serializer type. `Tools~/ci/doc_snippets.py` lists every block the build skips and why.

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

`UHttpClient` is sealed and implements `IDisposable` and `IAsyncDisposable`. Prefer `await client.DisposeAsync()`: it waits for every plugin's `ShutdownAsync` (each bounded by `PluginShutdownTimeout`) before it disposes owned interceptors and the owned transport. `Dispose()` never blocks: it starts plugin shutdown without waiting and disposes the owned components at once.

**What the client owns.** A client created with default options (no `Transport`, and no `TlsBackend`, `ConnectionPool`, `Http2` or `Streaming` override) uses the shared `HttpTransportFactory.Default` transport and does **not** own it: disposing the client closes no connection and cancels no in-flight request, and idle pooled sockets stay open until the pool idle timeout (`ConnectionPoolOptions.ConnectionIdleTimeout`: 2 minutes in the Editor and on desktop, 45 seconds on iOS/Android). A client with a non-default `TlsBackend`, `ConnectionPool`, `Http2` or `Streaming` option owns the transport it created and disposes it. A `Transport` you set yourself is disposed only when `DisposeTransport` is `true`. To stop in-flight requests, cancel the `CancellationToken` you passed to them.

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
| `DisposeAsync()` | Wait for plugin shutdown (bounded by `PluginShutdownTimeout`), then release owned interceptors and the owned transport (not the shared default transport; see above) |
| `Dispose()` | Same release without waiting: plugin shutdown is started but not awaited. Does not cancel in-flight requests itself; with an owned transport they fail once it is disposed |

### UHttpRequest

A mutable request bound to the client that created it. It is single-use: sending it with `SendBufferedAsync()` or `SendStreamingAsync()`, or calling `Dispose()`, ends its lease, after which any further use throws `ObjectDisposedException`.

**Methods:**

| Method | Description | Example |
| :--- | :--- | :--- |
| `WithHeader(name, value)` | Set a header | `.WithHeader("Accept", "application/json")` |
| `WithHeaders(headers)` | **Replaces all headers** on the request with `headers`, including those that came from `DefaultHeaders`; to add or change one header and keep the rest, use `WithHeader` | `.WithHeaders(myHeaders)` |
| `WithBody(byte[])` / `WithBody(ReadOnlyMemory<byte>)` / `WithBody(string)` | Set a buffered body | `.WithBody("plain text")` |
| `WithLeasedBody(owner, length)`, `WithBodyFactory(...)` | Pooled or re-creatable bodies | |
| `WithRequestTrailers(...)` | Send request trailers | |
| `WithExpectContinue(...)` | Send `Expect: 100-continue` | |
| `WithTimeout(timespan)` | Timeout for this request. For `SendStreamingAsync` it ends when the response headers arrive; body reads are not bounded (cancel through the token, see [Streaming](Modules/Core.md)) | `.WithTimeout(TimeSpan.FromSeconds(30))` |
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
| `GetBodyAsString()` / `GetBodyAsString(Encoding)` | Get the body as a string. The parameterless overload **always decodes UTF-8** and ignores the `charset` of the content type; both overloads return `null` for an empty body |
| `GetContentEncoding()` | The `Encoding` named by the `charset` parameter of `Content-Type` (UTF-8 if absent or unknown); use it as `response.GetBodyAsString(response.GetContentEncoding())` for non-UTF-8 bodies |
| `EnsureSuccessStatusCode()` | Throw `UHttpException` if the status is not 2xx |
| `AsJson<T>()` | Deserialize the body as JSON (`TurboHTTP.JSON`) |
| `AsTexture2D()` | Decode the body as a texture (`TurboHTTP.Unity`) |

### UHttpStreamingResponse

A response whose body is read as it arrives. It is `IDisposable` and `IAsyncDisposable`; it holds the connection until disposed.

| Member | Description |
| :--- | :--- |
| `StatusCode`, `Headers` | Available immediately |
| `Body` | A read-only, non-seekable `Stream` over the body. Read it with `ReadAsync` / `CopyToAsync`: a synchronous `Read` blocks the calling thread until data arrives, ignores any cancellation token and has no deadline, so avoid it on the Unity main thread |
| `GetTrailersAsync(ct)` | Trailers, after the body reached its end |
| `Dispose()` / `DisposeAsync()` | Release the connection; aborts an unfinished transfer |

### UHttpClientOptions

Configuration options for `UHttpClient`.

**Properties:**

| Property | Type | Description |
| :--- | :--- | :--- |
| `BaseUrl` | `string` | Prefix that relative request URLs are appended to (leading slashes stripped): `https://host/api/v1` + `/users` gives `https://host/api/v1/users` |
| `DefaultTimeout` | `TimeSpan` | Default timeout (default: 30s). For streaming responses it ends when the response headers arrive; body reads are not bounded |
| `DefaultHeaders` | `HttpHeaders` | Headers applied to every request |
| `Transport` | `IHttpTransport` | Transport to use instead of the shared default: a `RawSocketTransport` or a `TurboHTTP.Testing` transport (`MockTransport`, `MockHttpServer.CreateTransport()`, `RecordReplayTransport`). Third-party `IHttpTransport` implementations are not supported in 1.0 ([Supported transports](Modules/Transport.md#supported-transports)) |
| `DisposeTransport` | `bool` | Dispose the `Transport` you set together with the client |
| `DisposeInterceptors` | `bool` | Dispose the `IDisposable` interceptors in `Interceptors` together with the client (default `false`: the creator owns them, and `Clone()` shares them between clients) |
| `Interceptors` | `List<IHttpInterceptor>` | Interceptor pipeline, outermost first; see [Interceptors](Modules/Interceptors.md) for the required order |
| `FollowRedirects` / `MaxRedirects` | `bool` / `int` | Redirect policy (default `true` / `10`); applied only when a `RedirectInterceptor` is configured |
| `MaxBufferedResponseBodySize` | `long` | Cap for buffered bodies (default 100 MB; `0` = unlimited) |
| `Http2` | `Http2Options` | HTTP/2 specific settings |
| `ConnectionPool` | `ConnectionPoolOptions` | Connection pool configuration |
| `Proxy` | `ProxySettings` | HTTP proxy configuration; environment variables are used when unset |
| `TlsBackend` | `TlsBackend` | `Auto` (default, SslStream), `SslStream` or `BouncyCastle` (opt-in, needed for HTTP/2 on Unity); see [Transport](Modules/Transport.md) |
| `AllowCleartext` | `bool` | Allow `http://` and `http://` proxies (default `false` on iOS/Android players) |
| `Streaming` | `StreamingOptions` | Streaming response settings |
| `PluginShutdownTimeout` | `TimeSpan` | How long plugin shutdown callbacks may take |

Background networking and adaptive timeouts are not options properties: they live in the
`TurboHTTP.Unity` module (`TurboHTTP.Unity.Mobile` namespace) and are added with
`options.UseBackgroundNetworking(policy)` and `options.UseAdaptiveNetworking(policy)` (see [Unity](Modules/Unity.md)).

---

## Extension Methods

### JSON Extensions

`TurboHTTP.JSON`. `MyDto` stands for a `[Serializable]` class with public fields (see [JSON serializers](#json-serializers)).

<!-- snippet: with UHttpClient client = null; UHttpResponse response = null; string url = ""; -->
```csharp
using TurboHTTP.JSON;

// Deserialize a response
MyDto data = response.AsJson<MyDto>();

// GET with JSON (throws UHttpException of type HttpError on a non-2xx status)
MyDto got = await client.GetJsonAsync<MyDto>(url);

// POST with JSON
MyDto created = await client.PostJsonAsync<MyDto, MyDto>(url, data);

// PUT, PATCH and DELETE work the same way
MyDto updated = await client.PutJsonAsync<MyDto, MyDto>(url, data);

// Build a request body
using var response2 = await client.Post(url).WithJsonBody(data).SendBufferedAsync();
```

`GetJsonAsync`, `PostJsonAsync`, `PutJsonAsync`, `PatchJsonAsync` and `DeleteJsonAsync` call `EnsureSuccessStatusCode()`: on a non-2xx status they throw `UHttpException` with `UHttpErrorType.HttpError`, and the error response body is not available from the exception. To read the body of an error response, use `SendBufferedAsync()`, check `IsSuccessStatusCode`, then `TryAsJson` or `GetBodyAsString()`.

### JSON serializers

Every JSON helper (`AsJson<T>`, `TryAsJson`, `WithJsonBody`, `GetJsonAsync`/`PostJsonAsync`/..., the mocks,
`GetJsonCoroutine<T>`, `JsonWebSocketSerializer<T>`) goes through `JsonSerializer.Default`:

- **At startup** (TurboHTTP.Complete's bootstrap, every platform except WebGL) the default is
  `JsonUtilitySerializer`, backed by Unity's `JsonUtility`. Custom types follow `JsonUtility` rules:
  `[Serializable]` classes or structs with public (or `[SerializeField]`) fields, nested `[Serializable]`
  types, arrays and `List<T>` fields. Properties, dictionary fields, polymorphic fields and anonymous
  types are not serialized (anonymous types throw `JsonSerializationException`).
  `object`, `Dictionary<string, object>`, `List<object>` and primitives are handled by the built-in
  `LiteJsonSerializer`; a top-level JSON array reads into `T[]` (`GetJsonAsync<Post[]>`), while a
  top-level `List<T>` or `Dictionary<string, T>` of a custom type is not supported: use `T[]` or a wrapper class.
- **Without TurboHTTP.Complete** (or after `JsonSerializer.ResetToDefault()`) the default is
  `LiteJsonSerializer`, which has no engine dependency and reads only `object`, `Dictionary<string, object>`,
  `List<object>` and primitives; call `JsonModuleRegistration.EnsureRegistered()` and set a serializer for
  custom types yourself.
- **Your own serializer** (for example a Newtonsoft.Json adapter implementing `IJsonSerializer`) replaces
  either: call `JsonSerializer.SetDefault(...)` at startup. The bootstrap installs `JsonUtilitySerializer`
  only while the default is still the built-in one, so a serializer you set first is kept.

Under IL2CPP with managed stripping, keep DTOs that are only reached by name with `[Preserve]`.

<!-- snippet: skip needs a project serializer type -->
```csharp
using TurboHTTP.JSON;

// Before the first request, e.g. from [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]:
JsonSerializer.SetDefault(new MyNewtonsoftSerializer());

// Back to the startup default (code referencing TurboHTTP.Complete):
JsonSerializer.SetDefault(JsonUtilitySerializer.Instance);
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

The client disposes these interceptors only when `DisposeInterceptors` is `true`; otherwise the code that created them disposes them (see [Interceptor lifetime](Modules/Interceptors.md#interceptor-lifetime)).

---

## Error Handling

### UHttpErrorType

| Value | Meaning |
| :--- | :--- |
| `NetworkError` | Connection failed (DNS, refused, reset) or a bounded size was exceeded |
| `Timeout` | Request timeout |
| `HttpError` | HTTP error status (4xx, 5xx), raised by `EnsureSuccessStatusCode()` |
| `CertificateError` | TLS certificate validation or handshake (authentication) failure |
| `Cancelled` | Reserved: the client never raises it. Cancelling through your `CancellationToken` throws `OperationCanceledException` |
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
    ExpectedSha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
    Progress = new Progress<DownloadProgress>(p => Debug.Log($"{p.Percentage:F1}%"))
};

DownloadResult result = await downloader.DownloadFileAsync(url, savePath, downloadOptions);
```

Downloads are written to `savePath + ".part"` and moved to `savePath` only after the body completed and the checksum (if any) matched; an existing file at `savePath` is replaced atomically and is never touched by a failed download. With `EnableResume`, the response's strong validator (strong `ETag`, or a `Last-Modified` at least one second older than `Date`) is stored in `savePath + ".part.validator"`, and the next call resumes with `Range`, `If-Range` and `Accept-Encoding: identity`. A changed resource (200), a 416, or a 206 whose `Content-Range` does not continue the partial restarts from scratch; 4xx/5xx responses throw and leave the partial and the destination untouched. Setting `ExpectedSha256` turns verification on by itself; a mismatch deletes the partial and throws `ChecksumMismatchException` (a `UHttpException`), leaving `savePath` untouched. `ExpectedMd5` is a legacy option for existing manifests that only publish MD5; prefer SHA-256. The obsolete `VerifyChecksum` flag is no longer needed, and setting it without an expected hash throws `ArgumentException`.

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

An `IHttpPlugin` is an extension module that can contribute interceptors when it is registered. A plugin declares what it
may do in `Capabilities`; the client enforces it. `RegisterInterceptor` throws a `PluginException` (reported as a plugin
initialization failure) when the plugin declares `PluginCapabilities.None` or only `Diagnostics`/`AllowRedispatch`.

| Flag | Allows |
| --- | --- |
| `None` | Nothing: no interceptors, no diagnostics. |
| `ObserveRequests` | Register interceptors that only observe requests, responses and errors. |
| `ReadOnlyMonitoring` | Observe, and call `PluginContext.LogDiagnostic`. |
| `MutateRequests` | Interceptors may change the request before dispatch. |
| `MutateResponses` | Interceptors may replace or change the response. |
| `HandleErrors` | Interceptors may translate or suppress errors from later stages. |
| `Diagnostics` | Call `PluginContext.LogDiagnostic` (does not allow registering interceptors by itself). |
| `AllowRedispatch` | Interceptors may dispatch the request again (retry, fallback). |

The client checks what an interceptor forwards against what it received. The `TurboHTTP.Observability`
interceptors (`LoggingInterceptor`, `MetricsInterceptor`, `MonitorInterceptor`) only observe, so `ObserveRequests` is
enough for them. A custom handler that wraps, reads or replaces the response body, or changes the status or headers,
needs `MutateResponses`.

```csharp
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Observability;

public class CustomLoggerPlugin : IHttpPlugin
{
    public string Name => "CustomLogger";
    public string Version => "1.0.0";
    public PluginCapabilities Capabilities => PluginCapabilities.ObserveRequests;

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
