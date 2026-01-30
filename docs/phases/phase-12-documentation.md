# Phase 12: Documentation & Samples

**Milestone:** M3 (v1.0 "production")
**Dependencies:** Phase 11 (Platform Compatibility)
**Estimated Complexity:** Medium
**Critical:** Yes - User experience

## Overview

Create comprehensive documentation and sample projects that enable users to quickly understand and use TurboHTTP. Documentation should cover all features, provide clear examples, and include troubleshooting guides.

## Goals

1. Write Quick Start guide
2. Create API reference documentation
3. Write module-specific guides
4. Create 5 complete sample projects
5. Write troubleshooting guide
6. Create migration guide (from UnityWebRequest and BestHTTP)
7. Add inline code documentation (XML comments)
8. Create video tutorials (optional)

## Tasks

### Task 12.1: Quick Start Guide

**File:** `Documentation~/QuickStart.md`

```markdown
# TurboHTTP Quick Start Guide

Get started with TurboHTTP in under 5 minutes.

## Installation

1. **Import Package:**
   - Open Unity Package Manager (Window → Package Manager)
   - Click "+" → "Add package from disk"
   - Select `package.json` from the TurboHTTP folder

2. **Verify Installation:**
   - Check that "TurboHTTP - Complete HTTP Client" appears in Package Manager
   - No compile errors in Console

## Your First Request

### Simple GET Request

```csharp
using TurboHTTP.Core;
using UnityEngine;

public class Example : MonoBehaviour
{
    async void Start()
    {
        var client = new UHttpClient();
        var response = await client.Get("https://api.example.com/data").SendAsync();

        if (response.IsSuccessStatusCode)
        {
            Debug.Log(response.GetBodyAsString());
        }
    }
}
```

### POST JSON Request

```csharp
using TurboHTTP.Core;
using UnityEngine;

public class Example : MonoBehaviour
{
    async void Start()
    {
        var client = new UHttpClient();

        var data = new { username = "player1", score = 1000 };

        var response = await client
            .Post("https://api.example.com/scores")
            .WithJsonBody(data)
            .SendAsync();

        Debug.Log($"Status: {response.StatusCode}");
    }
}
```

### GET JSON with Deserialization

```csharp
using TurboHTTP.Core;
using UnityEngine;

[System.Serializable]
public class User
{
    public int id;
    public string name;
    public string email;
}

public class Example : MonoBehaviour
{
    async void Start()
    {
        var client = new UHttpClient();

        var user = await client.GetJsonAsync<User>(
            "https://jsonplaceholder.typicode.com/users/1"
        );

        Debug.Log($"User: {user.name} ({user.email})");
    }
}
```

## Common Patterns

### With Headers

```csharp
var response = await client
    .Get("https://api.example.com/protected")
    .WithBearerToken("your-token-here")
    .WithHeader("X-Custom-Header", "value")
    .SendAsync();
```

### With Timeout

```csharp
var response = await client
    .Get("https://api.example.com/slow")
    .WithTimeout(TimeSpan.FromSeconds(10))
    .SendAsync();
```

### Error Handling

```csharp
try
{
    var response = await client.Get("https://api.example.com/data").SendAsync();
    response.EnsureSuccessStatusCode();

    var data = response.AsJson<MyData>();
}
catch (UHttpException ex)
{
    Debug.LogError($"HTTP Error: {ex.HttpError.Type} - {ex.Message}");
}
```

## Next Steps

- [API Reference](APIReference.md) - Complete API documentation
- [Modules Guide](ModulesGuide.md) - Advanced features (cache, retry, etc.)
- [Samples](../Samples~/) - Example projects
- [Platform Notes](PlatformNotes.md) - Platform-specific information
```

### Task 12.2: API Reference

**File:** `Documentation~/APIReference.md`

```markdown
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
```

### Task 12.3: Create Sample 1 - Basic Usage

**File:** `Samples~/01-BasicUsage/BasicUsageExample.cs`

```csharp
using System;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.Samples.BasicUsage
{
    /// <summary>
    /// Basic usage examples for TurboHTTP.
    /// Demonstrates GET, POST, PUT, DELETE requests.
    /// </summary>
    public class BasicUsageExample : MonoBehaviour
    {
        private UHttpClient _client;

        void Start()
        {
            _client = new UHttpClient();
            RunExamples();
        }

        async void RunExamples()
        {
            await Example1_SimpleGet();
            await Example2_GetWithHeaders();
            await Example3_Post();
            await Example4_Put();
            await Example5_Delete();
            await Example6_ErrorHandling();
        }

        async Task Example1_SimpleGet()
        {
            Debug.Log("=== Example 1: Simple GET ===");

            var response = await _client.Get("https://httpbin.org/get").SendAsync();

            Debug.Log($"Status: {response.StatusCode}");
            Debug.Log($"Body: {response.GetBodyAsString()}");
        }

        async Task Example2_GetWithHeaders()
        {
            Debug.Log("=== Example 2: GET with Headers ===");

            var response = await _client
                .Get("https://httpbin.org/headers")
                .WithHeader("X-Custom-Header", "my-value")
                .WithHeader("Accept", "application/json")
                .SendAsync();

            Debug.Log(response.GetBodyAsString());
        }

        async Task Example3_Post()
        {
            Debug.Log("=== Example 3: POST ===");

            var data = new { name = "John Doe", age = 30 };

            var response = await _client
                .Post("https://httpbin.org/post")
                .WithJsonBody(data)
                .SendAsync();

            Debug.Log(response.GetBodyAsString());
        }

        async Task Example4_Put()
        {
            Debug.Log("=== Example 4: PUT ===");

            var data = new { id = 1, title = "Updated Title" };

            var response = await _client
                .Put("https://httpbin.org/put")
                .WithJsonBody(data)
                .SendAsync();

            Debug.Log($"Status: {response.StatusCode}");
        }

        async Task Example5_Delete()
        {
            Debug.Log("=== Example 5: DELETE ===");

            var response = await _client
                .Delete("https://httpbin.org/delete")
                .SendAsync();

            Debug.Log($"Status: {response.StatusCode}");
        }

        async Task Example6_ErrorHandling()
        {
            Debug.Log("=== Example 6: Error Handling ===");

            try
            {
                var response = await _client
                    .Get("https://httpbin.org/status/404")
                    .SendAsync();

                response.EnsureSuccessStatusCode();
            }
            catch (UHttpException ex)
            {
                Debug.LogError($"Error: {ex.HttpError.Type} - {ex.Message}");
            }
        }

        void OnDestroy()
        {
            _client?.Dispose();
        }
    }
}
```

### Task 12.4: Create Sample 2 - JSON API

**File:** `Samples~/02-JsonApi/JsonApiExample.cs`

```csharp
using System;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.Samples.JsonApi
{
    [Serializable]
    public class Post
    {
        public int userId;
        public int id;
        public string title;
        public string body;
    }

    [Serializable]
    public class Comment
    {
        public int postId;
        public int id;
        public string name;
        public string email;
        public string body;
    }

    /// <summary>
    /// Example of working with a REST API using JSON.
    /// Uses JSONPlaceholder (https://jsonplaceholder.typicode.com)
    /// </summary>
    public class JsonApiExample : MonoBehaviour
    {
        private UHttpClient _client;

        void Start()
        {
            var options = new UHttpClientOptions
            {
                BaseUrl = "https://jsonplaceholder.typicode.com"
            };
            _client = new UHttpClient(options);

            RunExamples();
        }

        async void RunExamples()
        {
            await GetAllPosts();
            await GetSinglePost();
            await CreatePost();
            await UpdatePost();
            await DeletePost();
        }

        async Task GetAllPosts()
        {
            Debug.Log("=== Get All Posts ===");

            var posts = await _client.GetJsonAsync<Post[]>("/posts");

            Debug.Log($"Fetched {posts.Length} posts");
            Debug.Log($"First post: {posts[0].title}");
        }

        async Task GetSinglePost()
        {
            Debug.Log("=== Get Single Post ===");

            var post = await _client.GetJsonAsync<Post>("/posts/1");

            Debug.Log($"Post {post.id}: {post.title}");
            Debug.Log($"Body: {post.body}");
        }

        async Task CreatePost()
        {
            Debug.Log("=== Create Post ===");

            var newPost = new Post
            {
                userId = 1,
                title = "My New Post",
                body = "This is the content of my post"
            };

            var created = await _client.PostJsonAsync<Post, Post>("/posts", newPost);

            Debug.Log($"Created post with ID: {created.id}");
        }

        async Task UpdatePost()
        {
            Debug.Log("=== Update Post ===");

            var updatedPost = new Post
            {
                id = 1,
                userId = 1,
                title = "Updated Title",
                body = "Updated content"
            };

            var result = await _client.PutJsonAsync<Post, Post>("/posts/1", updatedPost);

            Debug.Log($"Updated post: {result.title}");
        }

        async Task DeletePost()
        {
            Debug.Log("=== Delete Post ===");

            var response = await _client.Delete("/posts/1").SendAsync();

            Debug.Log($"Delete status: {response.StatusCode}");
        }

        void OnDestroy()
        {
            _client?.Dispose();
        }
    }
}
```

### Task 12.5: Create Sample 3 - File Download

**File:** `Samples~/03-FileDownload/FileDownloadExample.cs`

```csharp
using System.IO;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Files;
using UnityEngine;

namespace TurboHTTP.Samples.FileDownload
{
    /// <summary>
    /// Example of downloading files with progress tracking and resume support.
    /// </summary>
    public class FileDownloadExample : MonoBehaviour
    {
        [SerializeField] private UnityEngine.UI.Slider progressBar;
        [SerializeField] private UnityEngine.UI.Text statusText;

        private FileDownloader _downloader;

        void Start()
        {
            _downloader = new FileDownloader();
        }

        public async void DownloadImage()
        {
            var url = "https://via.placeholder.com/2000";
            var savePath = Path.Combine(Application.persistentDataPath, "downloaded_image.png");

            var options = new DownloadOptions
            {
                EnableResume = true,
                Progress = new Progress<DownloadProgress>(OnProgress)
            };

            try
            {
                statusText.text = "Downloading...";

                var result = await _downloader.DownloadFileAsync(url, savePath, options);

                statusText.text = $"Downloaded {result.FileSize} bytes in {result.ElapsedTime.TotalSeconds:F1}s";
                Debug.Log($"File saved to: {result.FilePath}");
            }
            catch (System.Exception ex)
            {
                statusText.text = $"Error: {ex.Message}";
                Debug.LogError(ex);
            }
        }

        private void OnProgress(DownloadProgress progress)
        {
            progressBar.value = progress.Percentage / 100f;
            statusText.text = $"Downloading: {progress.Percentage:F1}% ({progress.BytesDownloaded}/{progress.TotalBytes} bytes)";
            Debug.Log($"Speed: {progress.SpeedBytesPerSecond / 1024:F0} KB/s");
        }
    }
}
```

### Task 12.6: Create Samples 4 & 5

Create similar comprehensive samples for:
- **Sample 4:** Authentication (token management, refresh logic)
- **Sample 5:** Advanced Features (caching, retry, rate limiting, metrics)

### Task 12.7: Troubleshooting Guide

**File:** `Documentation~/Troubleshooting.md`

```markdown
# Troubleshooting Guide

Common issues and solutions for TurboHTTP.

## Request Timeout

**Symptom:** Requests fail with `UHttpErrorType.Timeout`

**Solutions:**
1. Increase timeout: `.WithTimeout(TimeSpan.FromSeconds(60))`
2. Check network connectivity
3. Verify server is responding
4. On mobile, use longer timeouts (60s+)

## SSL/TLS Errors

**Symptom:** "An SSL error has occurred" or `UHttpErrorType.CertificateError`

**Solutions:**
1. Ensure using HTTPS (not HTTP)
2. Verify certificate is valid
3. On iOS, check App Transport Security (ATS) settings
4. Update Unity to latest version for latest TLS support

## JSON Deserialization Fails

**Symptom:** Exception when calling `.AsJson<T>()`

**Solutions:**
1. Ensure class has public properties
2. Add `[Serializable]` attribute to class
3. Check JSON structure matches class
4. Use `TryAsJson` for safer parsing

## Platform-Specific Issues

### iOS

**Issue:** "Cleartext not permitted"
- Use HTTPS instead of HTTP
- Or configure ATS exceptions in Info.plist

**Issue:** Background requests timeout
- Background execution limited to 30s on iOS
- Use shorter timeouts for background requests

### Android

**Issue:** "java.net.UnknownServiceException: CLEARTEXT"
- Add `android:usesCleartextTraffic="true"` to AndroidManifest.xml
- Or use HTTPS

**Issue:** Permission denied
- Add `<uses-permission android:name="android.permission.INTERNET" />` to manifest

## Memory Issues

**Symptom:** High memory usage or GC spikes

**Solutions:**
1. Dispose clients: `client.Dispose()`
2. Enable memory pooling (automatic in v1.0)
3. Limit concurrent requests with `ConcurrencyMiddleware`
4. Clear cache periodically

## Unity Editor Issues

**Issue:** HTTP Monitor not showing requests

**Solutions:**
1. Check Window → TurboHTTP → HTTP Monitor
2. Verify MonitorMiddleware is in pipeline
3. Check Preferences → TurboHTTP → "Enable HTTP Monitor"

## IL2CPP Build Issues

**Issue:** NotSupportedException or missing methods

**Solutions:**
1. Run `IL2CPPCompatibility.ValidateCompatibility()`
2. Ensure using System.Text.Json (not Newtonsoft.Json)
3. Add link.xml if needed for JSON types

## Getting Help

1. Check [API Reference](APIReference.md)
2. Check [Platform Notes](PlatformNotes.md)
3. Review [Samples](../Samples~/)
4. Contact support: support@yourcompany.com
```

## Validation Criteria

### Success Criteria

- [ ] Quick Start guide complete and tested
- [ ] API Reference covers all public APIs
- [ ] All 5 samples created and working
- [ ] Troubleshooting guide covers common issues
- [ ] Platform notes document all limitations
- [ ] All public classes have XML documentation
- [ ] Documentation reviewed for clarity and accuracy

### Documentation Checklist

- [ ] Installation instructions
- [ ] Quick Start (< 5 minutes to first request)
- [ ] API Reference (complete)
- [ ] Code examples for all major features
- [ ] 5 working sample projects
- [ ] Troubleshooting guide
- [ ] Platform-specific notes
- [ ] Migration guide
- [ ] Performance tips
- [ ] Best practices

## Next Steps

Once Phase 12 is complete and validated:

1. Move to [Phase 13: CI/CD & Release](phase-13-release.md)
2. Set up CI/CD pipeline
3. Prepare Asset Store submission
4. M3 milestone complete

## Notes

- Documentation is as important as code
- Good examples reduce support burden
- Samples should cover real-world use cases
- Keep Quick Start simple and fast
- Update documentation with each release
