# Phase 3: Client API & Request Builder

**Milestone:** M0 (Spike)
**Dependencies:** Phase 2 (Core Type System)
**Estimated Complexity:** Medium
**Critical:** Yes - Primary API for end users

## Overview

Implement the main client API (`UHttpClient`) and fluent request builder (`UHttpRequestBuilder`). This is the primary interface developers will use to make HTTP requests. Also implement the default transport using UnityWebRequest.

## Goals

1. Create `UHttpClient` - main HTTP client class
2. Create `UHttpRequestBuilder` - fluent API for building requests
3. Create `UHttpClientOptions` - client configuration
4. Implement `UnityWebRequestTransport` - default transport implementation
5. Support basic GET, POST, PUT, DELETE, PATCH requests
6. Provide both async/await and Unity coroutine patterns
7. Enable request cancellation via CancellationToken

## Tasks

### Task 3.1: Client Options

**File:** `Runtime/Core/UHttpClientOptions.cs`

```csharp
using System;
using System.Collections.Generic;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Configuration options for UHttpClient.
    /// </summary>
    public class UHttpClientOptions
    {
        /// <summary>
        /// Base URL for all requests. If set, relative URLs will be resolved against this.
        /// Example: "https://api.example.com/v1"
        /// </summary>
        public string BaseUrl { get; set; }

        /// <summary>
        /// Default timeout for all requests.
        /// Can be overridden per-request.
        /// </summary>
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Default headers to include in all requests.
        /// </summary>
        public HttpHeaders DefaultHeaders { get; set; } = new HttpHeaders();

        /// <summary>
        /// HTTP transport implementation.
        /// If null, uses HttpTransportFactory.Default.
        /// </summary>
        public IHttpTransport Transport { get; set; }

        /// <summary>
        /// Middleware pipeline.
        /// Will be implemented in Phase 4.
        /// </summary>
        public List<IHttpMiddleware> Middlewares { get; set; } = new List<IHttpMiddleware>();

        /// <summary>
        /// Whether to automatically follow redirects (3xx status codes).
        /// </summary>
        public bool FollowRedirects { get; set; } = true;

        /// <summary>
        /// Maximum number of redirects to follow.
        /// </summary>
        public int MaxRedirects { get; set; } = 10;

        /// <summary>
        /// Create a deep copy of these options.
        /// </summary>
        public UHttpClientOptions Clone()
        {
            return new UHttpClientOptions
            {
                BaseUrl = BaseUrl,
                DefaultTimeout = DefaultTimeout,
                DefaultHeaders = DefaultHeaders.Clone(),
                Transport = Transport,
                Middlewares = new List<IHttpMiddleware>(Middlewares),
                FollowRedirects = FollowRedirects,
                MaxRedirects = MaxRedirects
            };
        }
    }
}
```

**Notes:**
- `BaseUrl` simplifies working with REST APIs
- `DefaultHeaders` useful for API keys, User-Agent, etc.
- `Middlewares` is placeholder for Phase 4

### Task 3.2: Request Builder

**File:** `Runtime/Core/UHttpRequestBuilder.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Fluent API for building HTTP requests.
    /// </summary>
    public class UHttpRequestBuilder
    {
        private readonly UHttpClient _client;
        private readonly HttpMethod _method;
        private readonly string _url;
        private readonly HttpHeaders _headers = new HttpHeaders();
        private readonly Dictionary<string, object> _metadata = new Dictionary<string, object>();
        private byte[] _body;
        private TimeSpan? _timeout;

        internal UHttpRequestBuilder(UHttpClient client, HttpMethod method, string url)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _method = method;
            _url = url ?? throw new ArgumentNullException(nameof(url));
        }

        /// <summary>
        /// Add a header to the request.
        /// </summary>
        public UHttpRequestBuilder WithHeader(string name, string value)
        {
            _headers.Set(name, value);
            return this;
        }

        /// <summary>
        /// Add multiple headers to the request.
        /// </summary>
        public UHttpRequestBuilder WithHeaders(HttpHeaders headers)
        {
            foreach (var kvp in headers)
            {
                _headers.Set(kvp.Key, kvp.Value);
            }
            return this;
        }

        /// <summary>
        /// Set the request body as raw bytes.
        /// </summary>
        public UHttpRequestBuilder WithBody(byte[] body)
        {
            _body = body;
            return this;
        }

        /// <summary>
        /// Set the request body as a UTF-8 string.
        /// </summary>
        public UHttpRequestBuilder WithBody(string body)
        {
            _body = Encoding.UTF8.GetBytes(body);
            return this;
        }

        /// <summary>
        /// Set the request body as JSON.
        /// Automatically sets Content-Type header.
        /// </summary>
        public UHttpRequestBuilder WithJsonBody<T>(T data)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(data);
            _body = Encoding.UTF8.GetBytes(json);
            _headers.Set("Content-Type", "application/json");
            return this;
        }

        /// <summary>
        /// Set the timeout for this specific request.
        /// </summary>
        public UHttpRequestBuilder WithTimeout(TimeSpan timeout)
        {
            _timeout = timeout;
            return this;
        }

        /// <summary>
        /// Add metadata to the request.
        /// Metadata can be used by middleware for custom logic.
        /// </summary>
        public UHttpRequestBuilder WithMetadata(string key, object value)
        {
            _metadata[key] = value;
            return this;
        }

        /// <summary>
        /// Set Authorization header with Bearer token.
        /// </summary>
        public UHttpRequestBuilder WithBearerToken(string token)
        {
            _headers.Set("Authorization", $"Bearer {token}");
            return this;
        }

        /// <summary>
        /// Set Accept header.
        /// </summary>
        public UHttpRequestBuilder Accept(string contentType)
        {
            _headers.Set("Accept", contentType);
            return this;
        }

        /// <summary>
        /// Set Content-Type header.
        /// </summary>
        public UHttpRequestBuilder ContentType(string contentType)
        {
            _headers.Set("Content-Type", contentType);
            return this;
        }

        /// <summary>
        /// Build the UHttpRequest object.
        /// </summary>
        public UHttpRequest Build()
        {
            // Resolve URL (relative vs absolute)
            Uri uri;
            if (Uri.TryCreate(_url, UriKind.Absolute, out uri))
            {
                // Already absolute
            }
            else if (!string.IsNullOrEmpty(_client.Options.BaseUrl))
            {
                // Relative URL, combine with base URL
                var baseUri = new Uri(_client.Options.BaseUrl);
                uri = new Uri(baseUri, _url);
            }
            else
            {
                throw new ArgumentException($"Invalid URL: {_url}. Provide an absolute URL or set BaseUrl in client options.");
            }

            // Merge default headers with request-specific headers
            var mergedHeaders = _client.Options.DefaultHeaders.Clone();
            foreach (var kvp in _headers)
            {
                mergedHeaders.Set(kvp.Key, kvp.Value);
            }

            var timeout = _timeout ?? _client.Options.DefaultTimeout;

            return new UHttpRequest(
                _method,
                uri,
                mergedHeaders,
                _body,
                timeout,
                _metadata
            );
        }

        /// <summary>
        /// Build and send the request.
        /// </summary>
        public Task<UHttpResponse> SendAsync(CancellationToken cancellationToken = default)
        {
            var request = Build();
            return _client.SendAsync(request, cancellationToken);
        }
    }
}
```

**Notes:**
- Fluent API allows chaining: `client.Get(url).WithHeader(...).WithTimeout(...).SendAsync()`
- `WithJsonBody()` uses `System.Text.Json` (available in Unity 2021.3+)
- Automatic URL resolution (relative vs absolute)
- Merges default headers with request-specific headers

### Task 3.3: HTTP Client

**File:** `Runtime/Core/UHttpClient.cs`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Main HTTP client for TurboHTTP.
    /// Thread-safe and can be reused for multiple requests.
    /// </summary>
    public class UHttpClient
    {
        public UHttpClientOptions Options { get; }
        private readonly IHttpTransport _transport;

        /// <summary>
        /// Create a new HTTP client with default options.
        /// </summary>
        public UHttpClient() : this(new UHttpClientOptions())
        {
        }

        /// <summary>
        /// Create a new HTTP client with custom options.
        /// </summary>
        public UHttpClient(UHttpClientOptions options)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            _transport = options.Transport ?? HttpTransportFactory.Default;
        }

        /// <summary>
        /// Create a GET request builder.
        /// </summary>
        public UHttpRequestBuilder Get(string url)
        {
            return new UHttpRequestBuilder(this, HttpMethod.GET, url);
        }

        /// <summary>
        /// Create a POST request builder.
        /// </summary>
        public UHttpRequestBuilder Post(string url)
        {
            return new UHttpRequestBuilder(this, HttpMethod.POST, url);
        }

        /// <summary>
        /// Create a PUT request builder.
        /// </summary>
        public UHttpRequestBuilder Put(string url)
        {
            return new UHttpRequestBuilder(this, HttpMethod.PUT, url);
        }

        /// <summary>
        /// Create a DELETE request builder.
        /// </summary>
        public UHttpRequestBuilder Delete(string url)
        {
            return new UHttpRequestBuilder(this, HttpMethod.DELETE, url);
        }

        /// <summary>
        /// Create a PATCH request builder.
        /// </summary>
        public UHttpRequestBuilder Patch(string url)
        {
            return new UHttpRequestBuilder(this, HttpMethod.PATCH, url);
        }

        /// <summary>
        /// Create a HEAD request builder.
        /// </summary>
        public UHttpRequestBuilder Head(string url)
        {
            return new UHttpRequestBuilder(this, HttpMethod.HEAD, url);
        }

        /// <summary>
        /// Create a OPTIONS request builder.
        /// </summary>
        public UHttpRequestBuilder Options(string url)
        {
            return new UHttpRequestBuilder(this, HttpMethod.OPTIONS, url);
        }

        /// <summary>
        /// Send a pre-built request.
        /// This is the core execution method.
        /// </summary>
        public async Task<UHttpResponse> SendAsync(
            UHttpRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var context = new RequestContext(request);
            context.RecordEvent("RequestStart");

            try
            {
                // Middleware pipeline will be executed here in Phase 4
                // For now, directly call transport
                var response = await _transport.SendAsync(request, context, cancellationToken);

                context.RecordEvent("RequestComplete");
                context.Stop();

                return response;
            }
            catch (Exception ex)
            {
                context.RecordEvent("RequestFailed", new System.Collections.Generic.Dictionary<string, object>
                {
                    { "error", ex.Message }
                });
                context.Stop();

                // Convert exception to UHttpError
                var error = new UHttpError(
                    UHttpErrorType.Unknown,
                    ex.Message,
                    ex
                );

                throw new UHttpException(error);
            }
        }
    }
}
```

**Notes:**
- Single instance can be reused for multiple requests
- Middleware pipeline placeholder for Phase 4
- Timeline events recorded in `RequestContext`

### Task 3.4: UnityWebRequest Transport

**File:** `Runtime/Core/UnityWebRequestTransport.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace TurboHTTP.Core
{
    /// <summary>
    /// HTTP transport implementation using Unity's UnityWebRequest.
    /// </summary>
    public class UnityWebRequestTransport : IHttpTransport
    {
        public async Task<UHttpResponse> SendAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            context.RecordEvent("TransportStart");

            UnityWebRequest unityRequest = null;

            try
            {
                // Create UnityWebRequest based on method
                unityRequest = CreateUnityWebRequest(request);

                // Set headers
                foreach (var header in request.Headers)
                {
                    unityRequest.SetRequestHeader(header.Key, header.Value);
                }

                // Set timeout
                unityRequest.timeout = (int)request.Timeout.TotalSeconds;

                context.RecordEvent("TransportSending");

                // Send the request
                var operation = unityRequest.SendWebRequest();

                // Wait for completion with cancellation support
                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        unityRequest.Abort();
                        var cancelError = new UHttpError(
                            UHttpErrorType.Cancelled,
                            "Request was cancelled"
                        );
                        return CreateErrorResponse(request, context, cancelError);
                    }

                    await Task.Yield();
                }

                context.RecordEvent("TransportReceived");

                // Check for errors
                if (unityRequest.result == UnityWebRequest.Result.ConnectionError ||
                    unityRequest.result == UnityWebRequest.Result.DataProcessingError)
                {
                    var error = new UHttpError(
                        UHttpErrorType.NetworkError,
                        unityRequest.error
                    );
                    return CreateErrorResponse(request, context, error);
                }

                if (unityRequest.result == UnityWebRequest.Result.ProtocolError)
                {
                    var statusCode = (HttpStatusCode)unityRequest.responseCode;
                    var error = new UHttpError(
                        UHttpErrorType.HttpError,
                        $"HTTP {unityRequest.responseCode}: {unityRequest.error}",
                        statusCode: statusCode
                    );
                    return CreateErrorResponse(request, context, error, statusCode);
                }

                // Success - build response
                var responseHeaders = ExtractHeaders(unityRequest);
                var body = unityRequest.downloadHandler?.data;
                var statusCode = (HttpStatusCode)unityRequest.responseCode;
                var elapsedTime = context.Elapsed;

                context.RecordEvent("TransportComplete");

                return new UHttpResponse(
                    statusCode,
                    responseHeaders,
                    body,
                    elapsedTime,
                    request
                );
            }
            catch (Exception ex)
            {
                var error = new UHttpError(
                    UHttpErrorType.Unknown,
                    ex.Message,
                    ex
                );
                return CreateErrorResponse(request, context, error);
            }
            finally
            {
                unityRequest?.Dispose();
            }
        }

        private UnityWebRequest CreateUnityWebRequest(UHttpRequest request)
        {
            var url = request.Uri.ToString();
            var method = request.Method.ToUpperString();

            UnityWebRequest unityRequest;

            switch (request.Method)
            {
                case HttpMethod.GET:
                    unityRequest = UnityWebRequest.Get(url);
                    break;

                case HttpMethod.POST:
                    unityRequest = new UnityWebRequest(url, method);
                    unityRequest.downloadHandler = new DownloadHandlerBuffer();
                    if (request.Body != null && request.Body.Length > 0)
                    {
                        unityRequest.uploadHandler = new UploadHandlerRaw(request.Body);
                    }
                    break;

                case HttpMethod.PUT:
                    unityRequest = UnityWebRequest.Put(url, request.Body ?? new byte[0]);
                    break;

                case HttpMethod.DELETE:
                    unityRequest = UnityWebRequest.Delete(url);
                    unityRequest.downloadHandler = new DownloadHandlerBuffer();
                    break;

                case HttpMethod.PATCH:
                case HttpMethod.HEAD:
                case HttpMethod.OPTIONS:
                    unityRequest = new UnityWebRequest(url, method);
                    unityRequest.downloadHandler = new DownloadHandlerBuffer();
                    if (request.Body != null && request.Body.Length > 0)
                    {
                        unityRequest.uploadHandler = new UploadHandlerRaw(request.Body);
                    }
                    break;

                default:
                    throw new ArgumentException($"Unsupported HTTP method: {request.Method}");
            }

            return unityRequest;
        }

        private HttpHeaders ExtractHeaders(UnityWebRequest unityRequest)
        {
            var headers = new HttpHeaders();
            var responseHeaders = unityRequest.GetResponseHeaders();

            if (responseHeaders != null)
            {
                foreach (var kvp in responseHeaders)
                {
                    headers.Set(kvp.Key, kvp.Value);
                }
            }

            return headers;
        }

        private UHttpResponse CreateErrorResponse(
            UHttpRequest request,
            RequestContext context,
            UHttpError error,
            HttpStatusCode statusCode = 0)
        {
            return new UHttpResponse(
                statusCode,
                new HttpHeaders(),
                null,
                context.Elapsed,
                request,
                error
            );
        }
    }
}
```

**Notes:**
- Uses `UnityWebRequest.SendWebRequest()` for all requests
- Proper cancellation token support
- Maps UnityWebRequest errors to `UHttpError` taxonomy
- Records timeline events for observability

### Task 3.5: Update Transport Factory

**File:** `Runtime/Core/HttpTransportFactory.cs` (update)

```csharp
namespace TurboHTTP.Core
{
    /// <summary>
    /// Factory for creating HTTP transport instances.
    /// Allows dependency injection and testing.
    /// </summary>
    public static class HttpTransportFactory
    {
        private static IHttpTransport _defaultTransport;

        /// <summary>
        /// Get or set the default transport.
        /// If not set, returns a new UnityWebRequestTransport instance.
        /// </summary>
        public static IHttpTransport Default
        {
            get
            {
                if (_defaultTransport == null)
                {
                    _defaultTransport = new UnityWebRequestTransport();
                }
                return _defaultTransport;
            }
            set => _defaultTransport = value;
        }

        /// <summary>
        /// Create a new transport instance.
        /// </summary>
        public static IHttpTransport Create()
        {
            return new UnityWebRequestTransport();
        }
    }
}
```

## Validation Criteria

### Success Criteria

- [ ] `UHttpClient` compiles without errors
- [ ] `UHttpRequestBuilder` has fluent API
- [ ] `UnityWebRequestTransport` implements `IHttpTransport`
- [ ] Can make basic GET request
- [ ] Can make POST request with JSON body
- [ ] Request cancellation works
- [ ] Headers are merged correctly (default + request-specific)
- [ ] Relative URLs resolve against BaseUrl
- [ ] Timeout is respected

### Manual Testing

Create a test scene with this MonoBehaviour:

**File:** `Tests/Runtime/TestHttpClient.cs`

```csharp
using System;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

public class TestHttpClient : MonoBehaviour
{
    async void Start()
    {
        await TestBasicGet();
        await TestPostJson();
        await TestHeaders();
        await TestTimeout();
    }

    async Task TestBasicGet()
    {
        Debug.Log("=== Test: Basic GET ===");
        var client = new UHttpClient();

        try
        {
            var response = await client
                .Get("https://httpbin.org/get")
                .SendAsync();

            Debug.Log($"Status: {response.StatusCode}");
            Debug.Log($"Body: {response.GetBodyAsString()}");
            Debug.Log($"Elapsed: {response.ElapsedTime.TotalMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error: {ex.Message}");
        }
    }

    async Task TestPostJson()
    {
        Debug.Log("=== Test: POST JSON ===");
        var client = new UHttpClient();

        var data = new { name = "John", age = 30 };

        try
        {
            var response = await client
                .Post("https://httpbin.org/post")
                .WithJsonBody(data)
                .SendAsync();

            Debug.Log($"Status: {response.StatusCode}");
            Debug.Log($"Body: {response.GetBodyAsString()}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error: {ex.Message}");
        }
    }

    async Task TestHeaders()
    {
        Debug.Log("=== Test: Custom Headers ===");
        var client = new UHttpClient();

        try
        {
            var response = await client
                .Get("https://httpbin.org/headers")
                .WithHeader("X-Custom-Header", "test-value")
                .WithBearerToken("fake-token-123")
                .SendAsync();

            Debug.Log($"Status: {response.StatusCode}");
            Debug.Log($"Body: {response.GetBodyAsString()}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error: {ex.Message}");
        }
    }

    async Task TestTimeout()
    {
        Debug.Log("=== Test: Timeout ===");
        var client = new UHttpClient();

        try
        {
            var response = await client
                .Get("https://httpbin.org/delay/10")
                .WithTimeout(TimeSpan.FromSeconds(2))
                .SendAsync();

            Debug.Log($"Status: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Expected timeout error: {ex.Message}");
        }
    }
}
```

**Expected Results:**
1. Basic GET: Returns 200, shows JSON response
2. POST JSON: Returns 200, echoes back the JSON data
3. Custom Headers: Returns 200, shows headers in response
4. Timeout: Throws exception after 2 seconds

### Unit Tests

Create test file: `Tests/Runtime/Core/UHttpClientTests.cs`

```csharp
using NUnit.Framework;
using System;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    public class UHttpClientTests
    {
        [Test]
        public void Constructor_WithNullOptions_ThrowsException()
        {
            Assert.Throws<ArgumentNullException>(() =>
            {
                new UHttpClient(null);
            });
        }

        [Test]
        public void Get_ReturnsBuilder()
        {
            var client = new UHttpClient();
            var builder = client.Get("https://example.com");
            Assert.IsNotNull(builder);
        }

        [Test]
        public void RequestBuilder_WithRelativeUrl_ResolvesAgainstBaseUrl()
        {
            var options = new UHttpClientOptions
            {
                BaseUrl = "https://api.example.com"
            };
            var client = new UHttpClient(options);
            var request = client.Get("/users").Build();

            Assert.AreEqual("https://api.example.com/users", request.Uri.ToString());
        }

        [Test]
        public void RequestBuilder_MergesDefaultHeaders()
        {
            var options = new UHttpClientOptions();
            options.DefaultHeaders.Set("User-Agent", "TurboHTTP/1.0");

            var client = new UHttpClient(options);
            var request = client
                .Get("https://example.com")
                .WithHeader("Accept", "application/json")
                .Build();

            Assert.AreEqual("TurboHTTP/1.0", request.Headers.Get("User-Agent"));
            Assert.AreEqual("application/json", request.Headers.Get("Accept"));
        }

        [Test]
        public void RequestBuilder_WithJsonBody_SetsContentType()
        {
            var client = new UHttpClient();
            var data = new { name = "test" };
            var request = client
                .Post("https://example.com")
                .WithJsonBody(data)
                .Build();

            Assert.AreEqual("application/json", request.Headers.Get("Content-Type"));
            Assert.IsNotNull(request.Body);
        }
    }
}
```

## Next Steps

Once Phase 3 is complete and validated:

1. Move to [Phase 4: Pipeline Infrastructure](phase-04-pipeline.md)
2. Implement middleware pipeline
3. Create basic middlewares (Logging, Timeout, DefaultHeaders, Retry, Auth, Metrics)

## Notes

- This phase completes M0 milestone (basic spike)
- Users can now make simple HTTP requests
- Foundation is ready for middleware pipeline (Phase 4)
- `async/await` pattern works seamlessly with Unity 2021.3+
- Cancellation token support enables aborting requests
- Timeline events recorded but not yet exposed to users (Phase 8)
