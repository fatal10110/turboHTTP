# Phase 9: Testing Infrastructure

**Milestone:** M3 (v1.0 "production")
**Dependencies:** Phase 8 (Editor Tooling)
**Estimated Complexity:** High
**Critical:** Yes - Production readiness

## Overview

Implement comprehensive testing infrastructure including unit tests, integration tests, and a record/replay system for deterministic offline testing. Achieve 80%+ code coverage and ensure all features work correctly.

## Goals

1. Create unit tests for all core types
2. Create integration tests with real HTTP endpoints
3. Implement record/replay transport for deterministic testing
4. Create test helpers and utilities
5. Achieve 80%+ code coverage
6. Test all middleware in isolation and integration
7. Create performance benchmarks

## Tasks

### Task 9.1: Mock Transport

**File:** `Runtime/Testing/MockTransport.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Testing
{
    /// <summary>
    /// Mock HTTP transport for testing.
    /// Allows configuring predefined responses.
    /// </summary>
    public class MockTransport : IHttpTransport
    {
        private readonly Queue<MockResponse> _responses = new Queue<MockResponse>();
        private readonly List<UHttpRequest> _capturedRequests = new List<UHttpRequest>();

        public IReadOnlyList<UHttpRequest> CapturedRequests => _capturedRequests;

        /// <summary>
        /// Enqueue a response to be returned for the next request.
        /// </summary>
        public void EnqueueResponse(MockResponse response)
        {
            _responses.Enqueue(response);
        }

        /// <summary>
        /// Enqueue a successful response with JSON body.
        /// </summary>
        public void EnqueueJsonResponse<T>(T data, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(data);
            var body = System.Text.Encoding.UTF8.GetBytes(json);

            var headers = new HttpHeaders();
            headers.Set("Content-Type", "application/json");

            EnqueueResponse(new MockResponse
            {
                StatusCode = statusCode,
                Headers = headers,
                Body = body
            });
        }

        /// <summary>
        /// Enqueue an error response.
        /// </summary>
        public void EnqueueError(UHttpError error)
        {
            EnqueueResponse(new MockResponse { Error = error });
        }

        public Task<UHttpResponse> SendAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            _capturedRequests.Add(request);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No responses configured. Use EnqueueResponse() to add responses.");
            }

            var mockResponse = _responses.Dequeue();

            // Simulate delay if configured
            if (mockResponse.Delay.HasValue)
            {
                Thread.Sleep(mockResponse.Delay.Value);
            }

            var response = new UHttpResponse(
                mockResponse.StatusCode,
                mockResponse.Headers ?? new HttpHeaders(),
                mockResponse.Body,
                context.Elapsed,
                request,
                mockResponse.Error
            );

            return Task.FromResult(response);
        }

        /// <summary>
        /// Clear all captured requests.
        /// </summary>
        public void ClearCapturedRequests()
        {
            _capturedRequests.Clear();
        }
    }

    /// <summary>
    /// Represents a mock HTTP response.
    /// </summary>
    public class MockResponse
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public HttpHeaders Headers { get; set; }
        public byte[] Body { get; set; }
        public UHttpError Error { get; set; }
        public TimeSpan? Delay { get; set; }
    }
}
```

### Task 9.2: Record/Replay Transport

**File:** `Runtime/Testing/RecordReplayTransport.cs`

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.Testing
{
    /// <summary>
    /// Mode for record/replay transport.
    /// </summary>
    public enum RecordReplayMode
    {
        /// <summary>Record requests and responses to a file.</summary>
        Record,
        /// <summary>Replay requests from a file.</summary>
        Replay,
        /// <summary>Passthrough - no recording or replay.</summary>
        Passthrough
    }

    /// <summary>
    /// Transport that can record HTTP traffic to a file and replay it later.
    /// Useful for deterministic offline testing.
    /// </summary>
    public class RecordReplayTransport : IHttpTransport
    {
        private readonly IHttpTransport _innerTransport;
        private readonly RecordReplayMode _mode;
        private readonly string _recordingPath;
        private readonly List<RecordedInteraction> _recordings = new List<RecordedInteraction>();
        private int _replayIndex = 0;

        public RecordReplayTransport(
            IHttpTransport innerTransport,
            RecordReplayMode mode,
            string recordingPath)
        {
            _innerTransport = innerTransport ?? throw new ArgumentNullException(nameof(innerTransport));
            _mode = mode;
            _recordingPath = recordingPath;

            if (_mode == RecordReplayMode.Replay)
            {
                LoadRecordings();
            }
        }

        public async Task<UHttpResponse> SendAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            switch (_mode)
            {
                case RecordReplayMode.Record:
                    return await RecordAsync(request, context, cancellationToken);

                case RecordReplayMode.Replay:
                    return ReplayAsync(request, context);

                case RecordReplayMode.Passthrough:
                default:
                    return await _innerTransport.SendAsync(request, context, cancellationToken);
            }
        }

        private async Task<UHttpResponse> RecordAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            var response = await _innerTransport.SendAsync(request, context, cancellationToken);

            var interaction = new RecordedInteraction
            {
                RequestMethod = request.Method.ToString(),
                RequestUrl = request.Uri.ToString(),
                RequestHeaders = new Dictionary<string, string>(),
                RequestBody = request.Body != null ? Convert.ToBase64String(request.Body) : null,

                ResponseStatusCode = (int)response.StatusCode,
                ResponseHeaders = new Dictionary<string, string>(),
                ResponseBody = response.Body != null ? Convert.ToBase64String(response.Body) : null,
                Error = response.Error?.ToString()
            };

            foreach (var header in request.Headers)
            {
                interaction.RequestHeaders[header.Key] = header.Value;
            }

            foreach (var header in response.Headers)
            {
                interaction.ResponseHeaders[header.Key] = header.Value;
            }

            _recordings.Add(interaction);

            return response;
        }

        private UHttpResponse ReplayAsync(UHttpRequest request, RequestContext context)
        {
            if (_replayIndex >= _recordings.Count)
            {
                throw new InvalidOperationException($"No more recordings available. Replay index: {_replayIndex}, Total recordings: {_recordings.Count}");
            }

            var recording = _recordings[_replayIndex++];

            // Validate request matches recording
            if (recording.RequestMethod != request.Method.ToString())
            {
                Debug.LogWarning($"[RecordReplay] Method mismatch: Expected {recording.RequestMethod}, Got {request.Method}");
            }

            if (recording.RequestUrl != request.Uri.ToString())
            {
                Debug.LogWarning($"[RecordReplay] URL mismatch: Expected {recording.RequestUrl}, Got {request.Uri}");
            }

            // Build response from recording
            var headers = new HttpHeaders();
            foreach (var kvp in recording.ResponseHeaders)
            {
                headers.Set(kvp.Key, kvp.Value);
            }

            var body = recording.ResponseBody != null ? Convert.FromBase64String(recording.ResponseBody) : null;

            UHttpError error = null;
            if (!string.IsNullOrEmpty(recording.Error))
            {
                error = new UHttpError(UHttpErrorType.Unknown, recording.Error);
            }

            return new UHttpResponse(
                (System.Net.HttpStatusCode)recording.ResponseStatusCode,
                headers,
                body,
                context.Elapsed,
                request,
                error
            );
        }

        /// <summary>
        /// Save recordings to file.
        /// Call this after recording is complete.
        /// </summary>
        public void SaveRecordings()
        {
            if (_mode != RecordReplayMode.Record)
            {
                throw new InvalidOperationException("Can only save recordings in Record mode");
            }

            var json = JsonSerializer.Serialize(_recordings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_recordingPath, json);
            Debug.Log($"[RecordReplay] Saved {_recordings.Count} recordings to {_recordingPath}");
        }

        private void LoadRecordings()
        {
            if (!File.Exists(_recordingPath))
            {
                throw new FileNotFoundException($"Recording file not found: {_recordingPath}");
            }

            var json = File.ReadAllText(_recordingPath);
            _recordings.AddRange(JsonSerializer.Deserialize<List<RecordedInteraction>>(json));

            Debug.Log($"[RecordReplay] Loaded {_recordings.Count} recordings from {_recordingPath}");
        }
    }

    [Serializable]
    public class RecordedInteraction
    {
        public string RequestMethod { get; set; }
        public string RequestUrl { get; set; }
        public Dictionary<string, string> RequestHeaders { get; set; }
        public string RequestBody { get; set; }

        public int ResponseStatusCode { get; set; }
        public Dictionary<string, string> ResponseHeaders { get; set; }
        public string ResponseBody { get; set; }
        public string Error { get; set; }
    }
}
```

### Task 9.3: Test Helpers

**File:** `Tests/Runtime/TestHelpers.cs`

```csharp
using System;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests
{
    /// <summary>
    /// Helper utilities for testing.
    /// </summary>
    public static class TestHelpers
    {
        /// <summary>
        /// Create a client with mock transport.
        /// </summary>
        public static (UHttpClient client, MockTransport transport) CreateMockClient()
        {
            var transport = new MockTransport();
            var options = new UHttpClientOptions
            {
                Transport = transport
            };
            var client = new UHttpClient(options);
            return (client, transport);
        }

        /// <summary>
        /// Create a test request.
        /// </summary>
        public static UHttpRequest CreateRequest(
            HttpMethod method = HttpMethod.GET,
            string url = "https://test.com/api")
        {
            return new UHttpRequest(method, new Uri(url));
        }

        /// <summary>
        /// Assert that a task completes within a timeout.
        /// </summary>
        public static async Task AssertCompletesWithinAsync(Task task, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout));
            if (completed != task)
            {
                throw new TimeoutException($"Task did not complete within {timeout}");
            }
        }

        /// <summary>
        /// Assert that a task throws an exception.
        /// </summary>
        public static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action)
            where TException : Exception
        {
            try
            {
                await action();
                throw new Exception($"Expected exception of type {typeof(TException).Name} but none was thrown");
            }
            catch (TException ex)
            {
                return ex;
            }
        }
    }
}
```

### Task 9.4: Core Type Tests

**File:** `Tests/Runtime/Core/CoreTypesTests.cs`

```csharp
using NUnit.Framework;
using System;
using System.Net;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    public class UHttpRequestTests
    {
        [Test]
        public void Constructor_WithValidParameters_CreatesInstance()
        {
            var uri = new Uri("https://example.com");
            var request = new UHttpRequest(HttpMethod.GET, uri);

            Assert.AreEqual(HttpMethod.GET, request.Method);
            Assert.AreEqual(uri, request.Uri);
            Assert.AreEqual(TimeSpan.FromSeconds(30), request.Timeout);
        }

        [Test]
        public void WithHeaders_CreatesNewInstance()
        {
            var request1 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var newHeaders = new HttpHeaders();
            newHeaders.Set("Accept", "application/json");

            var request2 = request1.WithHeaders(newHeaders);

            Assert.AreNotSame(request1, request2);
            Assert.AreEqual("application/json", request2.Headers.Get("Accept"));
        }

        [Test]
        public void WithBody_CreatesNewInstance()
        {
            var request1 = new UHttpRequest(HttpMethod.POST, new Uri("https://test.com"));
            var body = new byte[] { 1, 2, 3 };

            var request2 = request1.WithBody(body);

            Assert.AreNotSame(request1, request2);
            Assert.AreEqual(body, request2.Body);
        }
    }

    public class UHttpResponseTests
    {
        [Test]
        public void IsSuccessStatusCode_Returns_True_For_2xx()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var response = new UHttpResponse(
                HttpStatusCode.OK,
                new HttpHeaders(),
                null,
                TimeSpan.Zero,
                request
            );

            Assert.IsTrue(response.IsSuccessStatusCode);
        }

        [Test]
        public void IsSuccessStatusCode_Returns_False_For_4xx()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var response = new UHttpResponse(
                HttpStatusCode.NotFound,
                new HttpHeaders(),
                null,
                TimeSpan.Zero,
                request
            );

            Assert.IsFalse(response.IsSuccessStatusCode);
        }

        [Test]
        public void GetBodyAsString_Returns_UTF8_String()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var body = System.Text.Encoding.UTF8.GetBytes("Hello World");
            var response = new UHttpResponse(
                HttpStatusCode.OK,
                new HttpHeaders(),
                body,
                TimeSpan.Zero,
                request
            );

            Assert.AreEqual("Hello World", response.GetBodyAsString());
        }

        [Test]
        public void EnsureSuccessStatusCode_Throws_On_Error()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var response = new UHttpResponse(
                HttpStatusCode.InternalServerError,
                new HttpHeaders(),
                null,
                TimeSpan.Zero,
                request
            );

            Assert.Throws<UHttpException>(() => response.EnsureSuccessStatusCode());
        }
    }
}
```

### Task 9.5: Integration Tests

**File:** `Tests/Runtime/Integration/IntegrationTests.cs`

```csharp
using NUnit.Framework;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.Integration
{
    /// <summary>
    /// Integration tests using real HTTP endpoints.
    /// These tests require internet connectivity.
    /// </summary>
    public class IntegrationTests
    {
        private UHttpClient _client;

        [SetUp]
        public void Setup()
        {
            _client = new UHttpClient();
        }

        [UnityTest]
        public IEnumerator GetRequest_ReturnsSuccessfulResponse()
        {
            var task = _client.Get("https://httpbin.org/get").SendAsync();
            yield return new WaitUntil(() => task.IsCompleted);

            Assert.IsTrue(task.Result.IsSuccessStatusCode);
            Assert.IsNotNull(task.Result.Body);
        }

        [UnityTest]
        public IEnumerator PostJsonRequest_ReturnsEchoedData()
        {
            var data = new { name = "test", value = 123 };
            var task = _client.PostJsonAsync<object, dynamic>(
                "https://httpbin.org/post",
                data
            );

            yield return new WaitUntil(() => task.IsCompleted);

            Assert.IsTrue(task.IsCompletedSuccessfully);
            var result = task.Result;
            Assert.AreEqual("test", (string)result.json.name);
        }

        [UnityTest]
        public IEnumerator GetRequest_With404_ReturnsNotFound()
        {
            var task = _client.Get("https://httpbin.org/status/404").SendAsync();
            yield return new WaitUntil(() => task.IsCompleted);

            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, task.Result.StatusCode);
        }

        [UnityTest]
        public IEnumerator CachedRequest_ReturnsFromCache()
        {
            var cacheMiddleware = new TurboHTTP.Cache.CacheMiddleware();
            var options = new UHttpClientOptions();
            options.Middlewares.Add(cacheMiddleware);
            var cachedClient = new UHttpClient(options);

            // First request
            var task1 = cachedClient.Get("https://httpbin.org/get").SendAsync();
            yield return new WaitUntil(() => task1.IsCompleted);

            // Second request (should be cached)
            var task2 = cachedClient.Get("https://httpbin.org/get").SendAsync();
            yield return new WaitUntil(() => task2.IsCompleted);

            Assert.AreEqual("HIT", task2.Result.Headers.Get("X-Cache"));
        }
    }
}
```

### Task 9.6: Performance Benchmarks

**File:** `Tests/Runtime/Performance/BenchmarkTests.cs`

```csharp
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Performance
{
    /// <summary>
    /// Performance benchmark tests.
    /// </summary>
    public class BenchmarkTests
    {
        [Test]
        public async Task Benchmark_1000_Requests_CompletesQuickly()
        {
            var (client, transport) = TestHelpers.CreateMockClient();

            // Setup mock responses
            for (int i = 0; i < 1000; i++)
            {
                transport.EnqueueResponse(new MockResponse
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Body = new byte[] { 1, 2, 3 }
                });
            }

            var stopwatch = Stopwatch.StartNew();

            // Execute 1000 requests
            for (int i = 0; i < 1000; i++)
            {
                await client.Get($"https://test.com/item/{i}").SendAsync();
            }

            stopwatch.Stop();

            UnityEngine.Debug.Log($"1000 requests completed in {stopwatch.ElapsedMilliseconds}ms");
            Assert.Less(stopwatch.ElapsedMilliseconds, 5000, "Should complete within 5 seconds");
        }

        [Test]
        public void Benchmark_GC_Allocation_IsLow()
        {
            var (client, transport) = TestHelpers.CreateMockClient();
            transport.EnqueueResponse(new MockResponse());

            GC.Collect();
            var beforeMemory = GC.GetTotalMemory(true);

            // Execute request
            var task = client.Get("https://test.com").SendAsync();
            task.Wait();

            var afterMemory = GC.GetTotalMemory(false);
            var allocated = afterMemory - beforeMemory;

            UnityEngine.Debug.Log($"GC allocation: {allocated} bytes");
            Assert.Less(allocated, 10000, "Should allocate less than 10KB per request");
        }
    }
}
```

## Validation Criteria

### Success Criteria

- [ ] All unit tests pass
- [ ] Integration tests pass with real endpoints
- [ ] Mock transport works correctly
- [ ] Record/replay transport records and replays accurately
- [ ] Code coverage is 80% or higher
- [ ] Performance benchmarks meet targets
- [ ] No memory leaks detected
- [ ] All middleware tested in isolation

### Running Tests

```bash
# Run all tests
Unity -runTests -batchmode -projectPath . -testResults ./test-results.xml

# Run specific test category
Unity -runTests -batchmode -projectPath . -testPlatform PlayMode -testFilter TurboHTTP.Tests.Core
```

## Next Steps

Once Phase 9 is complete and validated:

1. Move to [Phase 10: Performance & Hardening](phase-10-performance.md)
2. Implement memory pooling
3. Add backpressure handling
4. Implement concurrency control
5. M3 milestone progress

## Notes

- Record/replay enables offline testing and CI/CD
- Mock transport simplifies unit testing
- Integration tests validate real-world behavior
- Performance benchmarks prevent regressions
- 80% code coverage ensures production quality
