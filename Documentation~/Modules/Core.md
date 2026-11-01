# Core Module

The `TurboHTTP.Core` module provides the foundational classes for sending HTTP requests and receiving responses. It is designed to be modular, allocation-conscious and fully async.

Snippets in this documentation assume the usual Unity script header: `using System; using System.Threading; using System.Threading.Tasks; using UnityEngine; using TurboHTTP.Core;`. Module namespaces (`TurboHTTP.JSON`, `TurboHTTP.Retry`, ...) are always shown explicitly, and `MyDto` / `MyData` stand for your own data classes.

## UHttpClient

`UHttpClient` is the main entry point. It is thread-safe and should be reused across the application. Creating multiple instances is fine if they need significantly different configurations, but one instance with specific options is usually enough.

```csharp
// Default options
var client = new UHttpClient();

// Specific options
var options = new UHttpClientOptions
{
    BaseUrl = "https://api.example.com",
    DefaultTimeout = TimeSpan.FromSeconds(15)
};
var myClient = new UHttpClient(options);
```

### Lifecycle

Dispose the `UHttpClient` when you are done, to release the connection pool and other resources it owns.

<!-- snippet: with UHttpClient myClient = new UHttpClient(); -->
```csharp
myClient.Dispose();
```

`Dispose` may run while `RegisterPluginAsync` or `UnregisterPluginAsync` is in progress: those calls then
end with `ObjectDisposedException` (or the plugin's own failure), and a plugin that finishes initializing
after `Dispose` is shut down instead of registered.

### Diagnostics

TurboHTTP writes nothing on success paths. Failures it deliberately swallows (a background cache store,
a transport or plugin that throws while being disposed) and one-time configuration warnings (for example
`FollowRedirects` without a `RedirectInterceptor`) go to one diagnostic sink. By default each message is a
warning in the Unity console, in the Editor and in players. Messages name exception types only, never
exception text, URLs or header values. Replace or silence the sink with
`TurboHTTP.Observability.TurboHttpDiagnostics.SetSink(...)` (`null` silences it, `ResetSink()` restores
the default); the sink is reset when Unity enters Play Mode.

## UHttpRequest

`UHttpClient` exposes builder methods (`Get`, `Post`, `Put`, `Delete`, `Patch`, `Head`, `Options`, or `CreateRequest(method, url)`) that return a new mutable, single-use `UHttpRequest` bound to that client. Chain the `With...` methods and finish with `SendBufferedAsync()` or `SendStreamingAsync()`.

<!-- snippet: with UHttpClient client = null; -->
```csharp
using TurboHTTP.JSON;

using var response = await client.Post("/users")
    .WithHeader("X-Custom-Header", "Value")
    .WithJsonBody(new { Name = "Alice", Role = "Admin" })
    .WithTimeout(TimeSpan.FromSeconds(5)) // overrides DefaultTimeout
    .SendBufferedAsync();
```

A request is single-use: sending it (or calling `Dispose()`) ends its lease, and any further use throws `ObjectDisposedException`. A relative URL is resolved against `UHttpClientOptions.BaseUrl`.

### Request bodies

`WithBody` takes `byte[]`, `ReadOnlyMemory<byte>` or `string` (UTF-8). `WithJsonBody` (JSON module) and `WithFileBody` (Files module) are extension methods. For large uploads, `WithStreamBody` streams from a `Stream`, and `WithLeasedBody` / `WithBodyFactory` hand the request pooled or re-creatable bodies; a body that can be re-created lets retries and redirects replay it.

<!-- snippet: with UHttpClient client = null; byte[] myByteArray = new byte[0]; -->
```csharp
using var response = await client.Put("https://api.example.com/blob")
    .WithBody(myByteArray)
    .WithHeader("Content-Type", "application/octet-stream")
    .SendBufferedAsync();
```

## Buffered and streaming responses

There are two ways to send a request.

**Buffered:** `SendBufferedAsync()` collects the whole body in memory and returns a `UHttpResponse`. The body is capped by `UHttpClientOptions.MaxBufferedResponseBodySize` (100 MB by default; a larger body fails the request). `UHttpResponse` is `IDisposable`: it owns pooled buffers, so dispose it (`using var`) when you are done with the body.

<!-- snippet: with UHttpClient client = null; -->
```csharp
using TurboHTTP.JSON;

using var response = await client.Get("https://api.example.com/data").SendBufferedAsync();
if (response.IsSuccessStatusCode)
{
    string text = response.GetBodyAsString();
    MyDto dto = response.AsJson<MyDto>();
}
```

**Streaming:** `SendStreamingAsync()` returns a `UHttpStreamingResponse` as soon as the status and headers have arrived. Read `Body` (a `Stream`) at your own pace, so a large download never sits in memory. The response owns the connection until you dispose it; always dispose it, also when you stop reading early (disposing aborts the rest of the transfer).

<!-- snippet: with UHttpClient client = null; CancellationToken ct = default; -->
```csharp
await using var streaming = await client.Get("https://example.com/big.bin").SendStreamingAsync(ct);
Debug.Log($"Status: {streaming.StatusCode}");
var buffer = new byte[16 * 1024];
int read;
while ((read = await streaming.Body.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
{
    // consume buffer[0..read]
}
HttpHeaders trailers = await streaming.GetTrailersAsync(ct); // available after the body reached its end
```

Body ownership in short: whoever holds a `UHttpResponse` or `UHttpStreamingResponse` disposes it exactly once; extension methods such as `AsJson<T>()` read from the response and leave the disposal to you.

## Errors

Network and protocol failures (DNS, connection refused, TLS, timeout, malformed response) throw `UHttpException` carrying a `UHttpError` with a `UHttpErrorType`. HTTP 4xx and 5xx responses are *normal* responses with a body: check `StatusCode` or `IsSuccessStatusCode`, or call `EnsureSuccessStatusCode()`, which throws a `UHttpException` of type `HttpError`. Cancelling through the token you pass to `SendBufferedAsync(ct)` throws `OperationCanceledException`.

<!-- snippet: with UHttpClient client = null; -->
```csharp
try
{
    using var response = await client.Get("/").SendBufferedAsync();
    response.EnsureSuccessStatusCode();
}
catch (UHttpException ex)
{
    Debug.LogError($"Request failed: {ex.HttpError.Type} {ex.HttpError.Message}");
}
```

Timeouts are enforced by the transport: `WithTimeout` (or `DefaultTimeout`) covers each request, and an expired request throws `UHttpException` with `UHttpErrorType.Timeout`.
