# Core Module

The `Core` module provides the foundational classes for sending HTTP requests and receiving responses. It is designed to be highly modular, zero-allocation friendly, and fully async.

## UHttpClient

`UHttpClient` is the main entry point. It is thread-safe and should be reused across the application. Creating multiple instances is fine if they require significantly different configurations, but generally, one instance with specific options is sufficient.

```csharp
using TurboHTTP.Core;

// Create with default options
var client = new UHttpClient();

// Create with specific options
var options = new UHttpClientOptions {
    BaseUrl = "https://api.example.com",
    DefaultTimeout = TimeSpan.FromSeconds(15)
};
var myClient = new UHttpClient(options);
```

### Lifecycle

You must dispose of `UHttpClient` when you are done to release underlying resources such as socket pools and transport channels.

```csharp
myClient.Dispose();
```

## UHttpRequestBuilder

`UHttpClient` exposes fluent builder methods (`Get`, `Post`, `Put`, `Delete`, etc.) that return a `UHttpRequestBuilder`. This allows you to construct requests in a chained manner.

```csharp
var response = await client.Post("/users")
    .WithHeader("X-Custom-Header", "Value")
    .WithJsonBody(new { Name = "Alice", Role = "Admin" })
    .WithTimeout(TimeSpan.FromSeconds(5)) // overrides default
    .SendAsync();
```

### Advanced Body Setting

You can set raw bytes, strings, or streams. When setting a stream, you can omit the length if using chunked transfer encoding (or HTTP/2), otherwise you must specify `Content-Length`.

```csharp
.WithBody(myByteArray)
.WithBody("plain text", getEncoding: Encoding.UTF8)
```

## Response Execution

Calling `SendAsync()` on the builder triggers the middleware pipeline, eventually hitting the transport layer. It returns a `ValueTask<UHttpResponse>`.

### Fast-Path Responses

If using `SendAsync()`, you will receive the full response in memory.

```csharp
if (response.IsSuccessStatusCode)
{
    var text = response.GetBodyAsString();
    // or
    var obj = response.AsJson<MyObject>();
}
```

### Response Streaming (Advanced)

For large responses, use `.SendAndStreamAsync()` to get a `UHttpResponseMessage` with a streaming body.

## UHttpException

By default, an error response (e.g., 404 or 500) or a network failure does *not* throw an exception; instead, it populates `response.Error` and sets `response.IsError = true`. 

However, calling `response.EnsureSuccessStatusCode()` throws a `UHttpException` if the request failed.

```csharp
try 
{
    var response = await client.Get("/").SendAsync();
    response.EnsureSuccessStatusCode();
}
catch (UHttpException ex)
{
    Debug.LogError($"Request failed: {ex.HttpError.Type}");
}
```
