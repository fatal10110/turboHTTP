# Phase 5: Content Handlers

**Milestone:** M1 (v0.1 "usable")
**Dependencies:** Phase 4 (Pipeline Infrastructure)
**Estimated Complexity:** Medium
**Critical:** Yes - Essential for common use cases

## Overview

Implement content handlers for common serialization formats and file operations. Add JSON support using System.Text.Json, file download with resume capability, and upload support. This phase makes TurboHTTP practical for real-world applications.

## Goals

1. Create JSON extension methods for serialization/deserialization
2. Create `FileDownloader` with resume support and progress tracking
3. Create `MultipartFormDataBuilder` for file uploads
4. Add streaming response support
5. Create content type helpers
6. Implement integrity verification (checksums)

## Tasks

### Task 5.1: JSON Extensions

**File:** `Runtime/Core/JsonExtensions.cs`

```csharp
using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// JSON serialization options for TurboHTTP.
    /// </summary>
    public static class JsonDefaults
    {
        public static JsonSerializerOptions Options { get; } = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <summary>
    /// Extension methods for JSON serialization/deserialization.
    /// </summary>
    public static class JsonExtensions
    {
        /// <summary>
        /// Deserialize the response body as JSON.
        /// </summary>
        public static T AsJson<T>(this UHttpResponse response, JsonSerializerOptions options = null)
        {
            if (response.Body == null || response.Body.Length == 0)
                return default;

            var json = Encoding.UTF8.GetString(response.Body);
            return JsonSerializer.Deserialize<T>(json, options ?? JsonDefaults.Options);
        }

        /// <summary>
        /// Try to deserialize the response body as JSON.
        /// Returns false if deserialization fails.
        /// </summary>
        public static bool TryAsJson<T>(this UHttpResponse response, out T result, JsonSerializerOptions options = null)
        {
            try
            {
                result = response.AsJson<T>(options);
                return true;
            }
            catch
            {
                result = default;
                return false;
            }
        }

        /// <summary>
        /// Send a POST request with JSON body and deserialize the response.
        /// </summary>
        public static async Task<TResponse> PostJsonAsync<TRequest, TResponse>(
            this UHttpClient client,
            string url,
            TRequest data,
            CancellationToken cancellationToken = default,
            JsonSerializerOptions options = null)
        {
            var response = await client
                .Post(url)
                .WithJsonBody(data)
                .SendAsync(cancellationToken);

            response.EnsureSuccessStatusCode();
            return response.AsJson<TResponse>(options);
        }

        /// <summary>
        /// Send a GET request and deserialize the JSON response.
        /// </summary>
        public static async Task<T> GetJsonAsync<T>(
            this UHttpClient client,
            string url,
            CancellationToken cancellationToken = default,
            JsonSerializerOptions options = null)
        {
            var response = await client
                .Get(url)
                .SendAsync(cancellationToken);

            response.EnsureSuccessStatusCode();
            return response.AsJson<T>(options);
        }

        /// <summary>
        /// Send a PUT request with JSON body and deserialize the response.
        /// </summary>
        public static async Task<TResponse> PutJsonAsync<TRequest, TResponse>(
            this UHttpClient client,
            string url,
            TRequest data,
            CancellationToken cancellationToken = default,
            JsonSerializerOptions options = null)
        {
            var response = await client
                .Put(url)
                .WithJsonBody(data)
                .SendAsync(cancellationToken);

            response.EnsureSuccessStatusCode();
            return response.AsJson<TResponse>(options);
        }

        /// <summary>
        /// Send a PATCH request with JSON body and deserialize the response.
        /// </summary>
        public static async Task<TResponse> PatchJsonAsync<TRequest, TResponse>(
            this UHttpClient client,
            string url,
            TRequest data,
            CancellationToken cancellationToken = default,
            JsonSerializerOptions options = null)
        {
            var response = await client
                .Patch(url)
                .WithJsonBody(data)
                .SendAsync(cancellationToken);

            response.EnsureSuccessStatusCode();
            return response.AsJson<TResponse>(options);
        }
    }
}
```

**Usage Example:**

```csharp
// GET JSON
var user = await client.GetJsonAsync<User>("https://api.example.com/users/123");

// POST JSON
var createRequest = new CreateUserRequest { Name = "John", Email = "john@example.com" };
var createdUser = await client.PostJsonAsync<CreateUserRequest, User>(
    "https://api.example.com/users",
    createRequest
);

// Manual deserialization
var response = await client.Get("https://api.example.com/data").SendAsync();
var data = response.AsJson<DataModel>();
```

### Task 5.2: File Downloader

**File:** `Runtime/Files/FileDownloader.cs`

```csharp
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.Files
{
    /// <summary>
    /// Progress information for file downloads.
    /// </summary>
    public class DownloadProgress
    {
        public long BytesDownloaded { get; set; }
        public long TotalBytes { get; set; }
        public float Percentage => TotalBytes > 0 ? (float)BytesDownloaded / TotalBytes * 100f : 0f;
        public TimeSpan Elapsed { get; set; }
        public double SpeedBytesPerSecond { get; set; }
        public TimeSpan EstimatedTimeRemaining { get; set; }
    }

    /// <summary>
    /// Options for file downloads.
    /// </summary>
    public class DownloadOptions
    {
        public bool EnableResume { get; set; } = true;
        public bool VerifyChecksum { get; set; } = false;
        public string ExpectedMd5 { get; set; }
        public string ExpectedSha256 { get; set; }
        public int BufferSize { get; set; } = 8192;
        public IProgress<DownloadProgress> Progress { get; set; }
    }

    /// <summary>
    /// File downloader with resume support and progress tracking.
    /// </summary>
    public class FileDownloader
    {
        private readonly UHttpClient _client;

        public FileDownloader(UHttpClient client = null)
        {
            _client = client ?? new UHttpClient();
        }

        /// <summary>
        /// Download a file to the specified path.
        /// Supports resume if the server supports Range requests.
        /// </summary>
        public async Task<DownloadResult> DownloadFileAsync(
            string url,
            string destinationPath,
            DownloadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new DownloadOptions();

            var startTime = DateTime.UtcNow;
            long existingSize = 0;

            // Check if file already exists and resume is enabled
            if (options.EnableResume && File.Exists(destinationPath))
            {
                var fileInfo = new FileInfo(destinationPath);
                existingSize = fileInfo.Length;
                Debug.Log($"[FileDownloader] Resuming download from {existingSize} bytes");
            }

            // Build request
            var requestBuilder = _client.Get(url);

            // Add Range header for resume
            if (existingSize > 0)
            {
                requestBuilder.WithHeader("Range", $"bytes={existingSize}-");
            }

            var response = await requestBuilder.SendAsync(cancellationToken);

            // Check if resume is supported
            if (existingSize > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                Debug.LogWarning("[FileDownloader] Server doesn't support resume, downloading from start");
                existingSize = 0;
                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }
            }

            // Get total size
            long totalSize = existingSize;
            if (response.Headers.Contains("Content-Length"))
            {
                totalSize += long.Parse(response.Headers.Get("Content-Length"));
            }

            // Write to file
            var fileMode = existingSize > 0 ? FileMode.Append : FileMode.Create;
            using (var fileStream = new FileStream(destinationPath, fileMode, FileAccess.Write, FileShare.None))
            {
                var body = response.Body;
                if (body != null && body.Length > 0)
                {
                    await fileStream.WriteAsync(body, 0, body.Length, cancellationToken);
                }

                // Report progress
                var progress = new DownloadProgress
                {
                    BytesDownloaded = existingSize + (body?.Length ?? 0),
                    TotalBytes = totalSize,
                    Elapsed = DateTime.UtcNow - startTime
                };
                progress.SpeedBytesPerSecond = progress.BytesDownloaded / progress.Elapsed.TotalSeconds;
                options.Progress?.Report(progress);
            }

            // Verify checksum if requested
            if (options.VerifyChecksum)
            {
                if (!string.IsNullOrEmpty(options.ExpectedMd5))
                {
                    var actualMd5 = ComputeMd5(destinationPath);
                    if (!actualMd5.Equals(options.ExpectedMd5, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new Exception($"MD5 checksum mismatch. Expected: {options.ExpectedMd5}, Actual: {actualMd5}");
                    }
                }

                if (!string.IsNullOrEmpty(options.ExpectedSha256))
                {
                    var actualSha256 = ComputeSha256(destinationPath);
                    if (!actualSha256.Equals(options.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new Exception($"SHA256 checksum mismatch. Expected: {options.ExpectedSha256}, Actual: {actualSha256}");
                    }
                }
            }

            var result = new DownloadResult
            {
                FilePath = destinationPath,
                FileSize = new FileInfo(destinationPath).Length,
                ElapsedTime = DateTime.UtcNow - startTime,
                WasResumed = existingSize > 0
            };

            return result;
        }

        private string ComputeMd5(string filePath)
        {
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private string ComputeSha256(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// Result of a file download operation.
    /// </summary>
    public class DownloadResult
    {
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public bool WasResumed { get; set; }
    }
}
```

**Usage Example:**

```csharp
var downloader = new FileDownloader();

var options = new DownloadOptions
{
    EnableResume = true,
    VerifyChecksum = true,
    ExpectedMd5 = "abc123...",
    Progress = new Progress<DownloadProgress>(p =>
    {
        Debug.Log($"Downloaded {p.Percentage:F1}% ({p.BytesDownloaded}/{p.TotalBytes} bytes)");
    })
};

var result = await downloader.DownloadFileAsync(
    "https://example.com/largefile.zip",
    "/path/to/save/file.zip",
    options
);

Debug.Log($"Downloaded {result.FileSize} bytes in {result.ElapsedTime.TotalSeconds:F1}s");
```

### Task 5.3: Multipart Form Data Builder

**File:** `Runtime/Files/MultipartFormDataBuilder.cs`

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TurboHTTP.Core;

namespace TurboHTTP.Files
{
    /// <summary>
    /// Builder for multipart/form-data requests (file uploads).
    /// </summary>
    public class MultipartFormDataBuilder
    {
        private readonly List<Part> _parts = new List<Part>();
        private readonly string _boundary;

        public MultipartFormDataBuilder()
        {
            _boundary = "----TurboHTTP" + Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Add a text field.
        /// </summary>
        public MultipartFormDataBuilder AddField(string name, string value)
        {
            _parts.Add(new TextPart
            {
                Name = name,
                Value = value
            });
            return this;
        }

        /// <summary>
        /// Add a file from a byte array.
        /// </summary>
        public MultipartFormDataBuilder AddFile(string name, string filename, byte[] data, string contentType = "application/octet-stream")
        {
            _parts.Add(new FilePart
            {
                Name = name,
                Filename = filename,
                Data = data,
                ContentType = contentType
            });
            return this;
        }

        /// <summary>
        /// Add a file from disk.
        /// </summary>
        public MultipartFormDataBuilder AddFile(string name, string filePath, string contentType = "application/octet-stream")
        {
            var data = File.ReadAllBytes(filePath);
            var filename = Path.GetFileName(filePath);
            return AddFile(name, filename, data, contentType);
        }

        /// <summary>
        /// Build the multipart/form-data body.
        /// </summary>
        public byte[] Build()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                foreach (var part in _parts)
                {
                    WriteBoundary(writer);

                    if (part is TextPart textPart)
                    {
                        WriteTextField(writer, textPart);
                    }
                    else if (part is FilePart filePart)
                    {
                        WriteFileField(writer, filePart);
                    }
                }

                WriteEndBoundary(writer);

                return stream.ToArray();
            }
        }

        /// <summary>
        /// Get the Content-Type header value.
        /// </summary>
        public string GetContentType()
        {
            return $"multipart/form-data; boundary={_boundary}";
        }

        /// <summary>
        /// Apply this multipart data to a request builder.
        /// </summary>
        public void ApplyTo(UHttpRequestBuilder builder)
        {
            var body = Build();
            builder.WithBody(body);
            builder.ContentType(GetContentType());
        }

        private void WriteBoundary(BinaryWriter writer)
        {
            var boundary = Encoding.UTF8.GetBytes($"--{_boundary}\r\n");
            writer.Write(boundary);
        }

        private void WriteEndBoundary(BinaryWriter writer)
        {
            var boundary = Encoding.UTF8.GetBytes($"--{_boundary}--\r\n");
            writer.Write(boundary);
        }

        private void WriteTextField(BinaryWriter writer, TextPart part)
        {
            var header = Encoding.UTF8.GetBytes($"Content-Disposition: form-data; name=\"{part.Name}\"\r\n\r\n");
            var value = Encoding.UTF8.GetBytes($"{part.Value}\r\n");

            writer.Write(header);
            writer.Write(value);
        }

        private void WriteFileField(BinaryWriter writer, FilePart part)
        {
            var header = Encoding.UTF8.GetBytes(
                $"Content-Disposition: form-data; name=\"{part.Name}\"; filename=\"{part.Filename}\"\r\n" +
                $"Content-Type: {part.ContentType}\r\n\r\n"
            );

            writer.Write(header);
            writer.Write(part.Data);
            writer.Write(Encoding.UTF8.GetBytes("\r\n"));
        }

        private abstract class Part
        {
            public string Name { get; set; }
        }

        private class TextPart : Part
        {
            public string Value { get; set; }
        }

        private class FilePart : Part
        {
            public string Filename { get; set; }
            public byte[] Data { get; set; }
            public string ContentType { get; set; }
        }
    }
}
```

**Usage Example:**

```csharp
var multipart = new MultipartFormDataBuilder()
    .AddField("username", "john_doe")
    .AddField("description", "My profile picture")
    .AddFile("avatar", "/path/to/avatar.png", "image/png");

var response = await client
    .Post("https://api.example.com/upload")
    .WithBody(multipart.Build())
    .ContentType(multipart.GetContentType())
    .SendAsync();

// Or use the helper:
var builder = client.Post("https://api.example.com/upload");
multipart.ApplyTo(builder);
var response = await builder.SendAsync();
```

### Task 5.4: Content Type Helpers

**File:** `Runtime/Core/ContentTypes.cs`

```csharp
namespace TurboHTTP.Core
{
    /// <summary>
    /// Common MIME content types.
    /// </summary>
    public static class ContentTypes
    {
        public const string Json = "application/json";
        public const string Xml = "application/xml";
        public const string FormUrlEncoded = "application/x-www-form-urlencoded";
        public const string MultipartFormData = "multipart/form-data";
        public const string PlainText = "text/plain";
        public const string Html = "text/html";
        public const string OctetStream = "application/octet-stream";
        public const string Png = "image/png";
        public const string Jpeg = "image/jpeg";
        public const string Gif = "image/gif";
        public const string Pdf = "application/pdf";
        public const string Zip = "application/zip";
    }
}
```

## Validation Criteria

### Success Criteria

- [ ] JSON serialization/deserialization works with System.Text.Json
- [ ] `GetJsonAsync()` and `PostJsonAsync()` helper methods work
- [ ] File downloader can download files
- [ ] File resume works when server supports Range requests
- [ ] Progress callback is invoked during downloads
- [ ] Checksum verification works (MD5 and SHA256)
- [ ] Multipart form data builder creates valid requests
- [ ] File upload works with multipart/form-data

### Manual Testing

Create test script: `Tests/Runtime/TestContentHandlers.cs`

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Files;
using UnityEngine;

public class TestContentHandlers : MonoBehaviour
{
    async void Start()
    {
        await TestJsonSerialization();
        await TestFileDownload();
        await TestFileUpload();
    }

    async Task TestJsonSerialization()
    {
        Debug.Log("=== Test: JSON Serialization ===");
        var client = new UHttpClient();

        try
        {
            // GET JSON
            var posts = await client.GetJsonAsync<Post[]>("https://jsonplaceholder.typicode.com/posts");
            Debug.Log($"Fetched {posts.Length} posts");
            Debug.Log($"First post: {posts[0].title}");

            // POST JSON
            var newPost = new Post { userId = 1, title = "Test Post", body = "Test body" };
            var created = await client.PostJsonAsync<Post, Post>(
                "https://jsonplaceholder.typicode.com/posts",
                newPost
            );
            Debug.Log($"Created post with ID: {created.id}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error: {ex.Message}");
        }
    }

    async Task TestFileDownload()
    {
        Debug.Log("=== Test: File Download ===");
        var downloader = new FileDownloader();

        var savePath = Path.Combine(Application.temporaryCachePath, "test_download.jpg");

        var options = new DownloadOptions
        {
            EnableResume = true,
            Progress = new Progress<DownloadProgress>(p =>
            {
                Debug.Log($"Download progress: {p.Percentage:F1}%");
            })
        };

        try
        {
            var result = await downloader.DownloadFileAsync(
                "https://via.placeholder.com/600/92c952",
                savePath,
                options
            );

            Debug.Log($"Downloaded {result.FileSize} bytes to {result.FilePath}");
            Debug.Log($"Elapsed: {result.ElapsedTime.TotalSeconds:F1}s");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error: {ex.Message}");
        }
    }

    async Task TestFileUpload()
    {
        Debug.Log("=== Test: File Upload ===");
        var client = new UHttpClient();

        var multipart = new MultipartFormDataBuilder()
            .AddField("title", "Test Upload")
            .AddField("description", "Uploaded from TurboHTTP");

        var builder = client.Post("https://httpbin.org/post");
        multipart.ApplyTo(builder);

        try
        {
            var response = await builder.SendAsync();
            Debug.Log($"Upload status: {response.StatusCode}");
            Debug.Log($"Response: {response.GetBodyAsString()}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error: {ex.Message}");
        }
    }

    [Serializable]
    public class Post
    {
        public int userId;
        public int id;
        public string title;
        public string body;
    }
}
```

## Next Steps

Once Phase 5 is complete and validated:

1. Move to [Phase 6: Advanced Middleware](phase-06-advanced-middleware.md)
2. Implement cache middleware with ETag support
3. Implement rate limiting middleware
4. M1 milestone is reached

## Notes

- System.Text.Json is built-in for Unity 2021.3+
- File resume requires server support for Range requests
- Checksum verification ensures file integrity
- Multipart form data is standard for file uploads
- Progress tracking enables UI updates during downloads

## Deferred Items from Phase 2

1. **`GetBodyAsString()` charset awareness** — `UHttpResponse.GetBodyAsString()` currently hardcodes UTF-8. Add a `GetBodyAsString(Encoding encoding)` overload and consider auto-detecting charset from `Content-Type` header (RFC 9110 Section 8.3.1) as part of content handling work in this phase.
