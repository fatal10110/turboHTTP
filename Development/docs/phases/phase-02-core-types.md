# Phase 2: Core Type System

**Milestone:** M0 (Spike)
**Dependencies:** Phase 1 (Project Foundation)
**Estimated Complexity:** Medium
**Critical:** Yes - Foundation types for all modules

## Overview

Implement the foundational type system for TurboHTTP: request and response types, HTTP methods, error handling, and context objects. These types form the contract between all modules and the transport layer.

## Goals

1. Create `UHttpRequest` - immutable request representation
2. Create `UHttpResponse` - response with status, headers, body
3. Create `UHttpError` - structured error taxonomy
4. Create `HttpMethod` - HTTP verb enumeration
5. Create `RequestContext` - execution context with timeline
6. Create `ResponseContext` - response metadata
7. Implement header collection with case-insensitive lookup
8. Create transport abstraction (`IHttpTransport`)

## Tasks

### Task 2.1: HTTP Method Enumeration

**File:** `Runtime/Core/HttpMethod.cs`

```csharp
namespace TurboHTTP.Core
{
    /// <summary>
    /// HTTP methods (verbs) for requests.
    /// </summary>
    public enum HttpMethod
    {
        GET,
        POST,
        PUT,
        DELETE,
        PATCH,
        HEAD,
        OPTIONS
    }

    /// <summary>
    /// Extension methods for HttpMethod.
    /// </summary>
    public static class HttpMethodExtensions
    {
        /// <summary>
        /// Returns true if this HTTP method is considered idempotent.
        /// Idempotent methods can be safely retried without side effects.
        /// </summary>
        public static bool IsIdempotent(this HttpMethod method)
        {
            return method == HttpMethod.GET
                || method == HttpMethod.HEAD
                || method == HttpMethod.PUT
                || method == HttpMethod.DELETE
                || method == HttpMethod.OPTIONS;
        }

        /// <summary>
        /// Returns true if this HTTP method typically has a request body.
        /// </summary>
        public static bool HasBody(this HttpMethod method)
        {
            return method == HttpMethod.POST
                || method == HttpMethod.PUT
                || method == HttpMethod.PATCH;
        }

        /// <summary>
        /// Converts HttpMethod to uppercase string (e.g., "GET", "POST").
        /// </summary>
        public static string ToUpperString(this HttpMethod method)
        {
            return method.ToString().ToUpperInvariant();
        }
    }
}
```

**Notes:**
- `IsIdempotent()` is used by retry middleware to decide if automatic retries are safe
- `HasBody()` helps validation and serialization logic

### Task 2.2: Header Collection

**File:** `Runtime/Core/HttpHeaders.cs`

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Case-insensitive HTTP header collection.
    /// </summary>
    public class HttpHeaders : IEnumerable<KeyValuePair<string, string>>
    {
        private readonly Dictionary<string, string> _headers;

        public HttpHeaders()
        {
            _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Add or update a header.
        /// </summary>
        public void Set(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Header name cannot be null or empty", nameof(name));

            _headers[name] = value ?? string.Empty;
        }

        /// <summary>
        /// Get a header value, or null if not present.
        /// </summary>
        public string Get(string name)
        {
            return _headers.TryGetValue(name, out var value) ? value : null;
        }

        /// <summary>
        /// Check if a header exists.
        /// </summary>
        public bool Contains(string name)
        {
            return _headers.ContainsKey(name);
        }

        /// <summary>
        /// Remove a header.
        /// </summary>
        public bool Remove(string name)
        {
            return _headers.Remove(name);
        }

        /// <summary>
        /// Get all header names.
        /// </summary>
        public IEnumerable<string> Names => _headers.Keys;

        /// <summary>
        /// Get the number of headers.
        /// </summary>
        public int Count => _headers.Count;

        /// <summary>
        /// Create a deep copy of this header collection.
        /// </summary>
        public HttpHeaders Clone()
        {
            var clone = new HttpHeaders();
            foreach (var kvp in _headers)
            {
                clone.Set(kvp.Key, kvp.Value);
            }
            return clone;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            return _headers.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// Indexer for convenient access: headers["Content-Type"]
        /// </summary>
        public string this[string name]
        {
            get => Get(name);
            set => Set(name, value);
        }
    }
}
```

**Notes:**
- Case-insensitive lookup matches HTTP spec (headers are case-insensitive)
- `Clone()` is used when creating immutable request copies

### Task 2.3: UHttpRequest Type

**File:** `Runtime/Core/UHttpRequest.cs`

```csharp
using System;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Immutable representation of an HTTP request.
    /// Use UHttpRequestBuilder to construct instances.
    /// </summary>
    public class UHttpRequest
    {
        public HttpMethod Method { get; }
        public Uri Uri { get; }
        public HttpHeaders Headers { get; }
        public byte[] Body { get; }
        public TimeSpan Timeout { get; }

        /// <summary>
        /// User-provided key-value metadata attached to this request.
        /// Can be used by middleware to store/retrieve custom data.
        /// </summary>
        public IReadOnlyDictionary<string, object> Metadata { get; }

        public UHttpRequest(
            HttpMethod method,
            Uri uri,
            HttpHeaders headers = null,
            byte[] body = null,
            TimeSpan? timeout = null,
            IReadOnlyDictionary<string, object> metadata = null)
        {
            Method = method;
            Uri = uri ?? throw new ArgumentNullException(nameof(uri));
            Headers = headers ?? new HttpHeaders();
            Body = body;
            Timeout = timeout ?? TimeSpan.FromSeconds(30);
            Metadata = metadata ?? new Dictionary<string, object>();
        }

        /// <summary>
        /// Create a copy of this request with modified properties.
        /// Useful for middleware that needs to transform requests.
        /// </summary>
        public UHttpRequest WithHeaders(HttpHeaders newHeaders)
        {
            return new UHttpRequest(Method, Uri, newHeaders, Body, Timeout, Metadata);
        }

        public UHttpRequest WithBody(byte[] newBody)
        {
            return new UHttpRequest(Method, Uri, Headers, newBody, Timeout, Metadata);
        }

        public UHttpRequest WithTimeout(TimeSpan newTimeout)
        {
            return new UHttpRequest(Method, Uri, Headers, Body, newTimeout, Metadata);
        }

        public UHttpRequest WithMetadata(IReadOnlyDictionary<string, object> newMetadata)
        {
            return new UHttpRequest(Method, Uri, Headers, Body, Timeout, newMetadata);
        }

        public override string ToString()
        {
            return $"{Method} {Uri}";
        }
    }
}
```

**Notes:**
- Immutable design prevents accidental modification during middleware execution
- `Metadata` allows attaching custom data (e.g., retry count, cache key)
- `With*()` methods follow immutable builder pattern

### Task 2.4: UHttpResponse Type

**File:** `Runtime/Core/UHttpResponse.cs`

```csharp
using System;
using System.Net;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Represents an HTTP response.
    /// </summary>
    public class UHttpResponse
    {
        public HttpStatusCode StatusCode { get; }
        public HttpHeaders Headers { get; }
        public byte[] Body { get; }
        public TimeSpan ElapsedTime { get; }

        /// <summary>
        /// The original request that generated this response.
        /// </summary>
        public UHttpRequest Request { get; }

        /// <summary>
        /// Error that occurred during the request, if any.
        /// Null if the request succeeded.
        /// </summary>
        public UHttpError Error { get; }

        public UHttpResponse(
            HttpStatusCode statusCode,
            HttpHeaders headers,
            byte[] body,
            TimeSpan elapsedTime,
            UHttpRequest request,
            UHttpError error = null)
        {
            StatusCode = statusCode;
            Headers = headers ?? new HttpHeaders();
            Body = body;
            ElapsedTime = elapsedTime;
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Error = error;
        }

        /// <summary>
        /// Returns true if the status code is in the 2xx range.
        /// </summary>
        public bool IsSuccessStatusCode =>
            (int)StatusCode >= 200 && (int)StatusCode < 300;

        /// <summary>
        /// Returns true if an error occurred during the request.
        /// </summary>
        public bool IsError => Error != null;

        /// <summary>
        /// Get the response body as a UTF-8 string.
        /// Returns null if body is null or empty.
        /// </summary>
        public string GetBodyAsString()
        {
            if (Body == null || Body.Length == 0)
                return null;

            return System.Text.Encoding.UTF8.GetString(Body);
        }

        /// <summary>
        /// Throws an exception if the response is not successful or has an error.
        /// </summary>
        public void EnsureSuccessStatusCode()
        {
            if (IsError)
            {
                throw new UHttpException(Error);
            }

            if (!IsSuccessStatusCode)
            {
                var errorMsg = $"HTTP request failed with status {(int)StatusCode} {StatusCode}";
                var error = new UHttpError(
                    UHttpErrorType.HttpError,
                    errorMsg,
                    statusCode: StatusCode
                );
                throw new UHttpException(error);
            }
        }

        public override string ToString()
        {
            return $"{(int)StatusCode} {StatusCode} ({ElapsedTime.TotalMilliseconds:F0}ms)";
        }
    }
}
```

**Notes:**
- `IsSuccessStatusCode` matches .NET HttpClient convention
- `GetBodyAsString()` convenience method for common use case
- `EnsureSuccessStatusCode()` allows fail-fast error handling

### Task 2.5: Error Taxonomy

**File:** `Runtime/Core/UHttpError.cs`

```csharp
using System;
using System.Net;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Categorizes types of HTTP errors for better error handling.
    /// </summary>
    public enum UHttpErrorType
    {
        /// <summary>Network connectivity issue (no internet, DNS failure, connection refused)</summary>
        NetworkError,

        /// <summary>Request timeout</summary>
        Timeout,

        /// <summary>HTTP error status code (4xx, 5xx)</summary>
        HttpError,

        /// <summary>SSL/TLS certificate validation failure</summary>
        CertificateError,

        /// <summary>Request cancelled by user</summary>
        Cancelled,

        /// <summary>Invalid request configuration</summary>
        InvalidRequest,

        /// <summary>Unexpected exception</summary>
        Unknown
    }

    /// <summary>
    /// Structured error information for failed HTTP requests.
    /// </summary>
    public class UHttpError
    {
        public UHttpErrorType Type { get; }
        public string Message { get; }
        public Exception InnerException { get; }
        public HttpStatusCode? StatusCode { get; }

        public UHttpError(
            UHttpErrorType type,
            string message,
            Exception innerException = null,
            HttpStatusCode? statusCode = null)
        {
            Type = type;
            Message = message ?? "Unknown error";
            InnerException = innerException;
            StatusCode = statusCode;
        }

        /// <summary>
        /// Returns true if this error is retryable.
        /// Network errors and timeouts are typically retryable.
        /// 4xx client errors are not retryable.
        /// 5xx server errors may be retryable.
        /// </summary>
        public bool IsRetryable()
        {
            switch (Type)
            {
                case UHttpErrorType.NetworkError:
                case UHttpErrorType.Timeout:
                    return true;

                case UHttpErrorType.HttpError:
                    // 5xx server errors are retryable, 4xx client errors are not
                    if (StatusCode.HasValue)
                    {
                        int code = (int)StatusCode.Value;
                        return code >= 500 && code < 600;
                    }
                    return false;

                case UHttpErrorType.Cancelled:
                case UHttpErrorType.CertificateError:
                case UHttpErrorType.InvalidRequest:
                case UHttpErrorType.Unknown:
                default:
                    return false;
            }
        }

        public override string ToString()
        {
            var statusPart = StatusCode.HasValue ? $" (HTTP {(int)StatusCode.Value})" : "";
            return $"[{Type}]{statusPart} {Message}";
        }
    }

    /// <summary>
    /// Exception thrown by TurboHTTP when a request fails.
    /// </summary>
    public class UHttpException : Exception
    {
        public UHttpError HttpError { get; }

        public UHttpException(UHttpError error)
            : base(error.Message, error.InnerException)
        {
            HttpError = error ?? throw new ArgumentNullException(nameof(error));
        }

        public override string ToString()
        {
            return $"UHttpException: {HttpError}";
        }
    }
}
```

**Notes:**
- Error taxonomy enables intelligent retry logic
- `IsRetryable()` is used by retry middleware
- Separates network issues from application errors

### Task 2.6: Request Context

**File:** `Runtime/Core/RequestContext.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Timeline event representing a stage in request execution.
    /// Used for observability and debugging.
    /// </summary>
    public class TimelineEvent
    {
        public string Name { get; }
        public TimeSpan Timestamp { get; }
        public Dictionary<string, object> Data { get; }

        public TimelineEvent(string name, TimeSpan timestamp, Dictionary<string, object> data = null)
        {
            Name = name;
            Timestamp = timestamp;
            Data = data ?? new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Execution context for a single HTTP request.
    /// Tracks timeline events, metadata, and state across middleware.
    /// </summary>
    public class RequestContext
    {
        private readonly Stopwatch _stopwatch;
        private readonly List<TimelineEvent> _timeline;
        private readonly Dictionary<string, object> _state;

        public UHttpRequest Request { get; private set; }
        public IReadOnlyList<TimelineEvent> Timeline => _timeline;
        public IReadOnlyDictionary<string, object> State => _state;

        /// <summary>
        /// Time elapsed since request started.
        /// </summary>
        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public RequestContext(UHttpRequest request)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            _stopwatch = Stopwatch.StartNew();
            _timeline = new List<TimelineEvent>();
            _state = new Dictionary<string, object>();
        }

        /// <summary>
        /// Record a timeline event.
        /// </summary>
        public void RecordEvent(string eventName, Dictionary<string, object> data = null)
        {
            var evt = new TimelineEvent(eventName, _stopwatch.Elapsed, data);
            _timeline.Add(evt);
        }

        /// <summary>
        /// Update the request (used by middleware that transforms requests).
        /// </summary>
        public void UpdateRequest(UHttpRequest newRequest)
        {
            Request = newRequest ?? throw new ArgumentNullException(nameof(newRequest));
        }

        /// <summary>
        /// Store data in the context state.
        /// This allows middleware to communicate with each other.
        /// </summary>
        public void SetState(string key, object value)
        {
            _state[key] = value;
        }

        /// <summary>
        /// Retrieve data from the context state.
        /// </summary>
        public T GetState<T>(string key, T defaultValue = default)
        {
            if (_state.TryGetValue(key, out var value) && value is T typedValue)
            {
                return typedValue;
            }
            return defaultValue;
        }

        /// <summary>
        /// Stop the stopwatch and return total elapsed time.
        /// </summary>
        public TimeSpan Stop()
        {
            _stopwatch.Stop();
            return _stopwatch.Elapsed;
        }
    }
}
```

**Notes:**
- `Timeline` enables observability (used by Monitor window)
- `State` allows middleware to share data
- `Stopwatch` provides precise timing

### Task 2.7: Transport Abstraction

**File:** `Runtime/Core/IHttpTransport.cs`

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Abstraction for the underlying HTTP transport layer.
    /// Default implementation uses raw TCP sockets with HTTP/1.1 and HTTP/2 support.
    /// Can be replaced for testing (mock transport) or platform-specific backends (e.g., WebGL browser fetch).
    /// </summary>
    public interface IHttpTransport
    {
        /// <summary>
        /// Execute an HTTP request and return the response.
        /// </summary>
        /// <param name="request">The request to execute</param>
        /// <param name="context">Execution context for timeline tracking</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The HTTP response</returns>
        Task<UHttpResponse> SendAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default);
    }
}
```

**Notes:**
- Simple interface allows multiple transport implementations
- `context` parameter enables timeline tracking at transport level
- `RawSocketTransport` is implemented in Phase 3 (HTTP/1.1) and Phase 3B (HTTP/2)

### Task 2.8: Transport Factory

**File:** `Runtime/Core/HttpTransportFactory.cs`

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
        /// If not set, returns a new RawSocketTransport instance.
        /// </summary>
        public static IHttpTransport Default
        {
            get
            {
                if (_defaultTransport == null)
                {
                    // Will be implemented in Phase 3
                    _defaultTransport = new RawSocketTransport();
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
            return new RawSocketTransport();
        }
    }
}
```

**Notes:**
- `RawSocketTransport` is implemented in Phase 3 (HTTP/1.1) and Phase 3B (HTTP/2)
- Allows swapping transports globally for testing or platform-specific backends

## Validation Criteria

### Success Criteria

- [ ] All types compile without errors
- [ ] `HttpMethod` enum has all 7 HTTP verbs
- [ ] `HttpMethodExtensions.IsIdempotent()` returns correct values
- [ ] `HttpHeaders` performs case-insensitive lookups
- [ ] `UHttpRequest` is immutable (no public setters)
- [ ] `UHttpRequest.With*()` methods create new instances
- [ ] `UHttpResponse.IsSuccessStatusCode` works for 2xx codes
- [ ] `UHttpError.IsRetryable()` has correct logic for all error types
- [ ] `RequestContext.RecordEvent()` adds timeline events
- [ ] `IHttpTransport` interface defined

### Unit Tests

Create test file: `Tests/Runtime/Core/HttpMethodTests.cs`

```csharp
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    public class HttpMethodTests
    {
        [Test]
        public void IsIdempotent_ReturnsTrue_ForIdempotentMethods()
        {
            Assert.IsTrue(HttpMethod.GET.IsIdempotent());
            Assert.IsTrue(HttpMethod.PUT.IsIdempotent());
            Assert.IsTrue(HttpMethod.DELETE.IsIdempotent());
            Assert.IsTrue(HttpMethod.HEAD.IsIdempotent());
            Assert.IsTrue(HttpMethod.OPTIONS.IsIdempotent());
        }

        [Test]
        public void IsIdempotent_ReturnsFalse_ForNonIdempotentMethods()
        {
            Assert.IsFalse(HttpMethod.POST.IsIdempotent());
            Assert.IsFalse(HttpMethod.PATCH.IsIdempotent());
        }

        [Test]
        public void HasBody_ReturnsTrue_ForMethodsWithBody()
        {
            Assert.IsTrue(HttpMethod.POST.HasBody());
            Assert.IsTrue(HttpMethod.PUT.HasBody());
            Assert.IsTrue(HttpMethod.PATCH.HasBody());
        }

        [Test]
        public void HasBody_ReturnsFalse_ForMethodsWithoutBody()
        {
            Assert.IsFalse(HttpMethod.GET.HasBody());
            Assert.IsFalse(HttpMethod.DELETE.HasBody());
            Assert.IsFalse(HttpMethod.HEAD.HasBody());
        }
    }
}
```

Create test file: `Tests/Runtime/Core/HttpHeadersTests.cs`

```csharp
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    public class HttpHeadersTests
    {
        [Test]
        public void Get_IsCaseInsensitive()
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "application/json");

            Assert.AreEqual("application/json", headers.Get("content-type"));
            Assert.AreEqual("application/json", headers.Get("CONTENT-TYPE"));
            Assert.AreEqual("application/json", headers.Get("Content-Type"));
        }

        [Test]
        public void Set_OverwritesExistingValue()
        {
            var headers = new HttpHeaders();
            headers.Set("Accept", "text/html");
            headers.Set("Accept", "application/json");

            Assert.AreEqual("application/json", headers.Get("Accept"));
        }

        [Test]
        public void Clone_CreatesDeepCopy()
        {
            var headers = new HttpHeaders();
            headers.Set("Authorization", "Bearer token123");

            var clone = headers.Clone();
            clone.Set("Authorization", "Bearer newtoken");

            Assert.AreEqual("Bearer token123", headers.Get("Authorization"));
            Assert.AreEqual("Bearer newtoken", clone.Get("Authorization"));
        }
    }
}
```

Create test file: `Tests/Runtime/Core/UHttpErrorTests.cs`

```csharp
using NUnit.Framework;
using System.Net;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    public class UHttpErrorTests
    {
        [Test]
        public void IsRetryable_ReturnsTrue_ForNetworkErrors()
        {
            var error = new UHttpError(UHttpErrorType.NetworkError, "Connection failed");
            Assert.IsTrue(error.IsRetryable());
        }

        [Test]
        public void IsRetryable_ReturnsTrue_ForTimeouts()
        {
            var error = new UHttpError(UHttpErrorType.Timeout, "Request timeout");
            Assert.IsTrue(error.IsRetryable());
        }

        [Test]
        public void IsRetryable_ReturnsTrue_For5xxErrors()
        {
            var error = new UHttpError(
                UHttpErrorType.HttpError,
                "Server error",
                statusCode: HttpStatusCode.InternalServerError
            );
            Assert.IsTrue(error.IsRetryable());
        }

        [Test]
        public void IsRetryable_ReturnsFalse_For4xxErrors()
        {
            var error = new UHttpError(
                UHttpErrorType.HttpError,
                "Not found",
                statusCode: HttpStatusCode.NotFound
            );
            Assert.IsFalse(error.IsRetryable());
        }

        [Test]
        public void IsRetryable_ReturnsFalse_ForCancelled()
        {
            var error = new UHttpError(UHttpErrorType.Cancelled, "User cancelled");
            Assert.IsFalse(error.IsRetryable());
        }
    }
}
```

## Next Steps

Once Phase 2 is complete and validated:

1. Move to [Phase 3: Client API & Request Builder](phase-03-client-api.md)
2. Implement the fluent request builder API
3. Create the `UHttpClient` class
4. Implement `RawSocketTransport` (TCP, TLS, HTTP/1.1)

## Notes

- All types are in `TurboHTTP.Core` namespace
- No Unity-specific code yet (pure C#)
- These types are used by ALL modules
- Immutability prevents bugs in concurrent scenarios
- Error taxonomy enables intelligent error handling
- Timeline tracking is foundation for observability

## Implementation Notes (Post-Review)

The following changes were made during implementation based on specialist agent reviews:

### Applied Fixes
1. **`ToUpperString()`** uses pre-allocated string array instead of `Enum.ToString().ToUpperInvariant()` — zero GC, IL2CPP-safe.
2. **`UHttpRequest` immutability** — constructor clones headers via `headers?.Clone()` to prevent shared mutation.
3. **`UHttpException` null-check** — moved before `base()` call to throw `ArgumentNullException` correctly.
4. **`RequestContext` thread safety** — lock-based synchronization on `_timeline` and `_state`. `Timeline` and `State` properties return snapshots.
5. **`IHttpTransport`** — extends `IDisposable` for connection pool resource cleanup.
6. **`HttpHeaders` multi-value support** — backing store changed to `Dictionary<string, List<string>>` with `Add()` and `GetValues()` methods (RFC 9110, RFC 6265).
7. **`HttpTransportFactory`** — throws `InvalidOperationException` if `Default` accessed before configuration (avoids Core → Transport dependency).

### Deferred Items
Items identified during review but intentionally deferred to later phases:

| Item | Deferred To | Rationale |
|------|-------------|-----------|
| `byte[]` body → `ReadOnlyMemory<byte>` | Phase 3 | Architectural decision needed alongside buffer pooling strategy |
| `CONNECT` / `TRACE` HTTP methods | Phase 3 or later | Not needed until proxy support; adding later won't break API |
| `GetBodyAsString()` charset awareness | Phase 5 | Requires Content-Type parsing; add `GetBodyAsString(Encoding)` overload |
| HTTP 429 special-case in `IsRetryable()` | Phase 6 (Retry) | Retry middleware should handle 429 + `Retry-After` header logic |
| `ResponseContext` type | Evaluate in Phase 4 | Phase spec goal mentioned it but no task defined; `RequestContext` may suffice |
| Extended test coverage | Phase 9 | Add UHttpRequest/UHttpResponse/RequestContext/HttpTransportFactory tests |
| Timeline opt-in flag | Phase 7 or 10 | `RequestContext.TimelineEnabled` to skip allocations in production builds |
| `RequestContext : IDisposable` | Phase 4 | Enable cleanup and pooling in middleware pipeline |
