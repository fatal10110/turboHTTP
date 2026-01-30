# Phase 3: Client API, Request Builder & HTTP/1.1 Raw Socket Transport

**Milestone:** M0 (Spike)
**Dependencies:** Phase 2 (Core Type System)
**Estimated Complexity:** High
**Critical:** Yes - Primary API for end users + transport foundation

## Overview

Implement the main client API (`UHttpClient`) and fluent request builder (`UHttpRequestBuilder`). This is the primary interface developers will use to make HTTP requests. Implement the default transport using raw TCP sockets with TLS via `SslStream` and a full HTTP/1.1 protocol implementation (request serializer, response parser, chunked transfer encoding, connection pooling with keep-alive). HTTP/2 is added in Phase 3B.

## Goals

1. Create `UHttpClient` - main HTTP client class
2. Create `UHttpRequestBuilder` - fluent API for building requests
3. Create `UHttpClientOptions` - client configuration
4. Implement `TcpConnectionPool` - pooled TCP socket connections with keep-alive
5. Implement `TlsStreamWrapper` - TLS via `SslStream` with certificate validation
6. Implement `Http11RequestSerializer` - HTTP/1.1 request line + headers + body serialization
7. Implement `Http11ResponseParser` - status line, header, and body parsing (Content-Length + chunked transfer encoding)
8. Implement `RawSocketTransport` - default `IHttpTransport` wiring the above together
9. Support basic GET, POST, PUT, DELETE, PATCH requests
10. Provide both async/await and Unity coroutine patterns
11. Enable request cancellation via CancellationToken

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

### Task 3.4: TCP Connection Pool

**File:** `Runtime/Transport/Tcp/TcpConnectionPool.cs`

```csharp
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Tcp
{
    /// <summary>
    /// Represents a pooled connection to a specific host:port endpoint.
    /// </summary>
    public class PooledConnection : IDisposable
    {
        public Stream Stream { get; }
        public string Host { get; }
        public int Port { get; }
        public bool IsSecure { get; }
        public DateTime LastUsed { get; set; }
        public bool IsConnected => _socket.Connected;

        private readonly Socket _socket;

        internal PooledConnection(Socket socket, Stream stream, string host, int port, bool isSecure)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            Stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Host = host;
            Port = port;
            IsSecure = isSecure;
            LastUsed = DateTime.UtcNow;
        }

        public void Dispose()
        {
            Stream?.Dispose();
            _socket?.Dispose();
        }
    }

    /// <summary>
    /// Manages a pool of TCP connections keyed by host:port.
    /// Supports keep-alive connection reuse for HTTP/1.1.
    /// </summary>
    public class TcpConnectionPool : IDisposable
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>> _pools;
        private readonly int _maxConnectionsPerHost;
        private readonly TimeSpan _connectionIdleTimeout;

        public TcpConnectionPool(int maxConnectionsPerHost = 6, TimeSpan? connectionIdleTimeout = null)
        {
            _maxConnectionsPerHost = maxConnectionsPerHost;
            _connectionIdleTimeout = connectionIdleTimeout ?? TimeSpan.FromMinutes(2);
            _pools = new ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>();
        }

        /// <summary>
        /// Get an existing idle connection or create a new one.
        /// </summary>
        public async Task<PooledConnection> GetConnectionAsync(
            string host, int port, bool secure,
            CancellationToken cancellationToken = default)
        {
            var key = $"{host}:{port}:{(secure ? "s" : "")}";

            // Try to reuse an idle connection
            if (_pools.TryGetValue(key, out var queue))
            {
                while (queue.TryDequeue(out var existing))
                {
                    if (existing.IsConnected &&
                        DateTime.UtcNow - existing.LastUsed < _connectionIdleTimeout)
                    {
                        existing.LastUsed = DateTime.UtcNow;
                        return existing;
                    }
                    existing.Dispose(); // Stale connection
                }
            }

            // Create new TCP connection
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true; // Disable Nagle's algorithm for lower latency

            await socket.ConnectAsync(host, port);

            Stream stream = new NetworkStream(socket, ownsSocket: false);

            if (secure)
            {
                // TLS wrapping is handled by TlsStreamWrapper (Task 3.5)
                stream = await Tls.TlsStreamWrapper.WrapAsync(stream, host, cancellationToken);
            }

            return new PooledConnection(socket, stream, host, port, secure);
        }

        /// <summary>
        /// Return a connection to the pool for reuse (HTTP/1.1 keep-alive).
        /// </summary>
        public void ReturnConnection(PooledConnection connection)
        {
            if (connection == null || !connection.IsConnected) return;

            var key = $"{connection.Host}:{connection.Port}:{(connection.IsSecure ? "s" : "")}";
            connection.LastUsed = DateTime.UtcNow;

            var queue = _pools.GetOrAdd(key, _ => new ConcurrentQueue<PooledConnection>());
            queue.Enqueue(connection);
        }

        public void Dispose()
        {
            foreach (var kvp in _pools)
            {
                while (kvp.Value.TryDequeue(out var conn))
                {
                    conn.Dispose();
                }
            }
            _pools.Clear();
        }
    }
}
```

**Notes:**
- Connections keyed by `host:port:secure` for proper isolation
- `NoDelay = true` disables Nagle's algorithm for lower request latency
- Idle timeout evicts stale connections (default 2 minutes)
- `maxConnectionsPerHost` defaults to 6 (matches browser convention)
- Thread-safe via `ConcurrentDictionary` and `ConcurrentQueue`

### Task 3.5: TLS Stream Wrapper

**File:** `Runtime/Transport/Tls/TlsStreamWrapper.cs`

```csharp
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// Wraps a raw TCP stream with TLS using SslStream.
    /// Supports ALPN protocol negotiation for HTTP/2 (used in Phase 3B).
    /// </summary>
    public static class TlsStreamWrapper
    {
        /// <summary>
        /// Wrap a plain TCP stream with TLS, performing the handshake.
        /// </summary>
        /// <param name="innerStream">The raw TCP NetworkStream</param>
        /// <param name="host">The target hostname (used for SNI and cert validation)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="alpnProtocols">Optional ALPN protocols to negotiate (e.g., "h2", "http/1.1")</param>
        /// <returns>The TLS-wrapped stream</returns>
        public static async Task<SslStream> WrapAsync(
            Stream innerStream,
            string host,
            CancellationToken cancellationToken = default,
            string[] alpnProtocols = null)
        {
            var sslStream = new SslStream(
                innerStream,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: ValidateServerCertificate
            );

            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            };

            // ALPN negotiation — critical for HTTP/2
            if (alpnProtocols != null && alpnProtocols.Length > 0)
            {
                sslOptions.ApplicationProtocols = new System.Collections.Generic.List<SslApplicationProtocol>();
                foreach (var proto in alpnProtocols)
                {
                    if (proto == "h2")
                        sslOptions.ApplicationProtocols.Add(SslApplicationProtocol.Http2);
                    else if (proto == "http/1.1")
                        sslOptions.ApplicationProtocols.Add(SslApplicationProtocol.Http11);
                }
            }

            await sslStream.AuthenticateAsClientAsync(sslOptions, cancellationToken);

            return sslStream;
        }

        /// <summary>
        /// Get the negotiated ALPN protocol after TLS handshake.
        /// Returns "h2" for HTTP/2, "http/1.1" for HTTP/1.1, or null if ALPN was not used.
        /// </summary>
        public static string GetNegotiatedProtocol(SslStream sslStream)
        {
            var proto = sslStream.NegotiatedApplicationProtocol;
            if (proto == SslApplicationProtocol.Http2) return "h2";
            if (proto == SslApplicationProtocol.Http11) return "http/1.1";
            return null;
        }

        private static bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            // Default: accept valid certificates, reject invalid ones
            // Can be extended with custom validation / certificate pinning
            return sslPolicyErrors == SslPolicyErrors.None;
        }
    }
}
```

**Notes:**
- Uses `SslClientAuthenticationOptions` for modern TLS configuration
- TLS 1.2 and TLS 1.3 only (TLS 1.0/1.1 are deprecated)
- ALPN support is included but only used in Phase 3B when HTTP/2 is added
- SNI (Server Name Indication) is automatic via `TargetHost`
- Certificate validation callback can be extended for custom pinning
- **Risk:** `SslStream` with ALPN must be validated on IL2CPP builds (see risk spikes in overview)

### Task 3.6: HTTP/1.1 Request Serializer

**File:** `Runtime/Transport/Http1/Http11RequestSerializer.cs`

```csharp
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http1
{
    /// <summary>
    /// Serializes UHttpRequest into raw HTTP/1.1 wire format.
    /// Format: "METHOD /path HTTP/1.1\r\nHeaders\r\n\r\nBody"
    /// </summary>
    public static class Http11RequestSerializer
    {
        private static readonly byte[] CrLf = Encoding.ASCII.GetBytes("\r\n");
        private static readonly byte[] HeaderSeparator = Encoding.ASCII.GetBytes(": ");

        public static async Task SerializeAsync(
            UHttpRequest request,
            Stream stream,
            CancellationToken cancellationToken = default)
        {
            var sb = new StringBuilder();

            // Request line: "GET /path HTTP/1.1\r\n"
            var path = request.Uri.PathAndQuery;
            sb.Append(request.Method.ToUpperString());
            sb.Append(' ');
            sb.Append(path);
            sb.Append(" HTTP/1.1\r\n");

            // Host header (required in HTTP/1.1)
            if (request.Headers.Get("Host") == null)
            {
                sb.Append("Host: ");
                sb.Append(request.Uri.Host);
                if (!request.Uri.IsDefaultPort)
                {
                    sb.Append(':');
                    sb.Append(request.Uri.Port);
                }
                sb.Append("\r\n");
            }

            // User headers
            foreach (var header in request.Headers)
            {
                sb.Append(header.Key);
                sb.Append(": ");
                sb.Append(header.Value);
                sb.Append("\r\n");
            }

            // Content-Length for requests with bodies
            if (request.Body != null && request.Body.Length > 0)
            {
                if (request.Headers.Get("Content-Length") == null)
                {
                    sb.Append("Content-Length: ");
                    sb.Append(request.Body.Length);
                    sb.Append("\r\n");
                }
            }

            // End of headers
            sb.Append("\r\n");

            // Write headers
            var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken);

            // Write body
            if (request.Body != null && request.Body.Length > 0)
            {
                await stream.WriteAsync(request.Body, 0, request.Body.Length, cancellationToken);
            }

            await stream.FlushAsync(cancellationToken);
        }
    }
}
```

**Notes:**
- Automatically adds `Host` header if not provided (required by HTTP/1.1)
- Automatically adds `Content-Length` for request bodies
- Uses ASCII encoding for headers (per HTTP spec)
- Body written as raw bytes after header block

### Task 3.7: HTTP/1.1 Response Parser

**File:** `Runtime/Transport/Http1/Http11ResponseParser.cs`

```csharp
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http1
{
    /// <summary>
    /// Parses raw HTTP/1.1 wire format into structured response data.
    /// Supports Content-Length and chunked transfer encoding.
    /// </summary>
    public static class Http11ResponseParser
    {
        /// <summary>
        /// Parse an HTTP/1.1 response from a stream.
        /// </summary>
        public static async Task<ParsedResponse> ParseAsync(
            Stream stream,
            CancellationToken cancellationToken = default)
        {
            // 1. Read status line: "HTTP/1.1 200 OK\r\n"
            var statusLine = await ReadLineAsync(stream, cancellationToken);
            var (httpVersion, statusCode, reasonPhrase) = ParseStatusLine(statusLine);

            // 2. Read headers until empty line
            var headers = new HttpHeaders();
            while (true)
            {
                var line = await ReadLineAsync(stream, cancellationToken);
                if (string.IsNullOrEmpty(line)) break; // Empty line = end of headers

                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    var name = line.Substring(0, colonIndex).Trim();
                    var value = line.Substring(colonIndex + 1).Trim();
                    headers.Set(name, value);
                }
            }

            // 3. Read body based on Transfer-Encoding or Content-Length
            byte[] body;
            var transferEncoding = headers.Get("Transfer-Encoding");
            var contentLengthStr = headers.Get("Content-Length");

            if (transferEncoding != null &&
                transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                body = await ReadChunkedBodyAsync(stream, cancellationToken);
            }
            else if (contentLengthStr != null && int.TryParse(contentLengthStr, out var contentLength))
            {
                body = await ReadFixedBodyAsync(stream, contentLength, cancellationToken);
            }
            else
            {
                // No Content-Length, no chunked — read until connection closes
                // (rare for keep-alive connections, but spec-compliant)
                body = await ReadToEndAsync(stream, cancellationToken);
            }

            return new ParsedResponse
            {
                StatusCode = (HttpStatusCode)statusCode,
                Headers = headers,
                Body = body,
                KeepAlive = IsKeepAlive(httpVersion, headers)
            };
        }

        /// <summary>
        /// Determines if the connection should be kept alive based on
        /// HTTP version and Connection header.
        /// </summary>
        private static bool IsKeepAlive(string httpVersion, HttpHeaders headers)
        {
            var connection = headers.Get("Connection");
            if (connection != null)
            {
                return !connection.Equals("close", StringComparison.OrdinalIgnoreCase);
            }
            // HTTP/1.1 defaults to keep-alive; HTTP/1.0 defaults to close
            return httpVersion == "HTTP/1.1";
        }

        private static (string httpVersion, int statusCode, string reasonPhrase)
            ParseStatusLine(string statusLine)
        {
            // "HTTP/1.1 200 OK"
            var parts = statusLine.Split(new[] { ' ' }, 3);
            if (parts.Length < 2)
                throw new FormatException($"Invalid HTTP status line: {statusLine}");

            var httpVersion = parts[0];
            if (!int.TryParse(parts[1], out var statusCode))
                throw new FormatException($"Invalid HTTP status code: {parts[1]}");

            var reasonPhrase = parts.Length > 2 ? parts[2] : "";
            return (httpVersion, statusCode, reasonPhrase);
        }

        private static async Task<byte[]> ReadChunkedBodyAsync(
            Stream stream, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadLineAsync(stream, ct);
                var chunkSize = Convert.ToInt32(sizeLine.Trim(), 16);
                if (chunkSize == 0) break; // Last chunk

                var chunk = new byte[chunkSize];
                await ReadExactAsync(stream, chunk, chunkSize, ct);
                ms.Write(chunk, 0, chunkSize);

                // Read trailing \r\n after chunk data
                await ReadLineAsync(stream, ct);
            }
            // Read trailing headers (usually empty line)
            await ReadLineAsync(stream, ct);
            return ms.ToArray();
        }

        private static async Task<byte[]> ReadFixedBodyAsync(
            Stream stream, int length, CancellationToken ct)
        {
            var buffer = new byte[length];
            await ReadExactAsync(stream, buffer, length, ct);
            return buffer;
        }

        private static async Task<byte[]> ReadToEndAsync(
            Stream stream, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                ms.Write(buffer, 0, read);
            }
            return ms.ToArray();
        }

        private static async Task ReadExactAsync(
            Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, ct);
                if (read == 0)
                    throw new IOException("Unexpected end of stream");
                offset += read;
            }
        }

        private static async Task<string> ReadLineAsync(
            Stream stream, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buffer = new byte[1];
            bool lastWasCR = false;

            while (true)
            {
                int read = await stream.ReadAsync(buffer, 0, 1, ct);
                if (read == 0) break; // End of stream

                char c = (char)buffer[0];
                if (c == '\r')
                {
                    lastWasCR = true;
                    continue;
                }
                if (c == '\n' && lastWasCR)
                {
                    break; // End of line
                }
                if (lastWasCR)
                {
                    sb.Append('\r');
                    lastWasCR = false;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Parsed HTTP/1.1 response data (internal transport type).
    /// </summary>
    public class ParsedResponse
    {
        public HttpStatusCode StatusCode { get; set; }
        public HttpHeaders Headers { get; set; }
        public byte[] Body { get; set; }
        public bool KeepAlive { get; set; }
    }
}
```

**Notes:**
- Supports both `Content-Length` and `chunked` transfer encoding
- Falls back to read-to-end for responses without either (connection close)
- `IsKeepAlive()` determines whether to return the connection to the pool
- `ReadLineAsync` reads byte-by-byte for correctness; a buffered reader optimization can be added in Phase 10
- Chunk size parsed as hex per HTTP spec

### Task 3.8: Raw Socket Transport

**File:** `Runtime/Transport/RawSocketTransport.cs`

```csharp
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Http1;

namespace TurboHTTP.Transport
{
    /// <summary>
    /// Default IHttpTransport implementation using raw TCP sockets.
    /// Manages connections via TcpConnectionPool, serializes/parses HTTP/1.1.
    /// HTTP/2 support is added in Phase 3B.
    /// </summary>
    public class RawSocketTransport : IHttpTransport, IDisposable
    {
        private readonly TcpConnectionPool _pool;

        public RawSocketTransport(TcpConnectionPool pool = null)
        {
            _pool = pool ?? new TcpConnectionPool();
        }

        public async Task<UHttpResponse> SendAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            context.RecordEvent("TransportStart");

            var host = request.Uri.Host;
            var port = request.Uri.Port;
            var secure = request.Uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
            if (port == -1) port = secure ? 443 : 80;

            PooledConnection connection = null;

            try
            {
                // Get or create connection
                context.RecordEvent("TransportConnecting");
                connection = await _pool.GetConnectionAsync(host, port, secure, cancellationToken);

                // Serialize and send HTTP/1.1 request
                context.RecordEvent("TransportSending");
                await Http11RequestSerializer.SerializeAsync(request, connection.Stream, cancellationToken);

                // Parse HTTP/1.1 response
                context.RecordEvent("TransportReceiving");
                var parsed = await Http11ResponseParser.ParseAsync(connection.Stream, cancellationToken);

                context.RecordEvent("TransportComplete");

                // Return connection to pool if keep-alive
                if (parsed.KeepAlive)
                {
                    _pool.ReturnConnection(connection);
                    connection = null; // Prevent disposal in finally
                }

                return new UHttpResponse(
                    parsed.StatusCode,
                    parsed.Headers,
                    parsed.Body,
                    context.Elapsed,
                    request
                );
            }
            catch (OperationCanceledException)
            {
                var error = new UHttpError(UHttpErrorType.Cancelled, "Request was cancelled");
                return CreateErrorResponse(request, context, error);
            }
            catch (System.IO.IOException ex)
            {
                var error = new UHttpError(UHttpErrorType.NetworkError, ex.Message, ex);
                return CreateErrorResponse(request, context, error);
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                var error = new UHttpError(UHttpErrorType.NetworkError, ex.Message, ex);
                return CreateErrorResponse(request, context, error);
            }
            catch (System.Security.Authentication.AuthenticationException ex)
            {
                var error = new UHttpError(UHttpErrorType.CertificateError, ex.Message, ex);
                return CreateErrorResponse(request, context, error);
            }
            catch (Exception ex)
            {
                var error = new UHttpError(UHttpErrorType.Unknown, ex.Message, ex);
                return CreateErrorResponse(request, context, error);
            }
            finally
            {
                connection?.Dispose();
            }
        }

        private UHttpResponse CreateErrorResponse(
            UHttpRequest request, RequestContext context,
            UHttpError error, HttpStatusCode statusCode = 0)
        {
            return new UHttpResponse(statusCode, new HttpHeaders(), null,
                context.Elapsed, request, error);
        }

        public void Dispose()
        {
            _pool?.Dispose();
        }
    }
}
```

**Notes:**
- Orchestrates connection pool, HTTP/1.1 serializer, and response parser
- Maps socket/TLS/IO exceptions to the `UHttpError` taxonomy
- Returns connections to the pool when `Connection: keep-alive` (HTTP/1.1 default)
- Disposes connections on error or `Connection: close`
- HTTP/2 support will be added via ALPN detection in Phase 3B
- No Unity engine dependency — pure C#

### Task 3.9: Update Transport Factory

**File:** `Runtime/Core/HttpTransportFactory.cs` (update)

```csharp
using TurboHTTP.Transport;

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
                    _defaultTransport = new RawSocketTransport();
                }
                return _defaultTransport;
            }
            set => _defaultTransport = value;
        }

        /// <summary>
        /// Create a new transport instance with default settings.
        /// </summary>
        public static IHttpTransport Create()
        {
            return new RawSocketTransport();
        }
    }
}
```

## Validation Criteria

### Success Criteria

- [ ] `UHttpClient` compiles without errors
- [ ] `UHttpRequestBuilder` has fluent API
- [ ] `RawSocketTransport` implements `IHttpTransport`
- [ ] `TcpConnectionPool` creates and reuses connections
- [ ] `TlsStreamWrapper` completes TLS handshake with public HTTPS servers
- [ ] `Http11RequestSerializer` produces valid HTTP/1.1 request wire format
- [ ] `Http11ResponseParser` parses status line, headers, Content-Length bodies, and chunked bodies
- [ ] Can make basic GET request over HTTP and HTTPS
- [ ] Can make POST request with JSON body
- [ ] Connection keep-alive reuses TCP connections
- [ ] Request cancellation works
- [ ] Headers are merged correctly (default + request-specific)
- [ ] Relative URLs resolve against BaseUrl
- [ ] Timeout is respected
- [ ] Socket/TLS/IO errors map correctly to `UHttpError` taxonomy

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
        await TestConnectionReuse();
    }

    async Task TestBasicGet()
    {
        Debug.Log("=== Test: Basic GET (HTTPS via raw socket + TLS) ===");
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

    async Task TestConnectionReuse()
    {
        Debug.Log("=== Test: Connection Reuse (keep-alive) ===");
        var client = new UHttpClient();

        try
        {
            // First request establishes connection
            var r1 = await client.Get("https://httpbin.org/get").SendAsync();
            Debug.Log($"Request 1: {r1.StatusCode} in {r1.ElapsedTime.TotalMilliseconds}ms");

            // Second request should reuse the connection (faster)
            var r2 = await client.Get("https://httpbin.org/get").SendAsync();
            Debug.Log($"Request 2: {r2.StatusCode} in {r2.ElapsedTime.TotalMilliseconds}ms");

            // Third request should also reuse
            var r3 = await client.Get("https://httpbin.org/get").SendAsync();
            Debug.Log($"Request 3: {r3.StatusCode} in {r3.ElapsedTime.TotalMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error: {ex.Message}");
        }
    }
}
```

**Expected Results:**
1. Basic GET: Returns 200 via raw socket + SslStream TLS handshake
2. POST JSON: Returns 200, echoes back the JSON data
3. Custom Headers: Returns 200, shows headers in response
4. Timeout: Throws exception after 2 seconds
5. Connection Reuse: Requests 2 and 3 should be faster than request 1 (no TCP+TLS handshake)

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

1. Move to [Phase 3B: HTTP/2 Protocol Implementation](phase-03b-http2.md)
2. Add HTTP/2 binary framing, HPACK, stream multiplexing, flow control
3. Add ALPN negotiation to select HTTP/2 vs HTTP/1.1 during TLS handshake
4. Then move to [Phase 4: Pipeline Infrastructure](phase-04-pipeline.md)

## Notes

- This phase establishes the raw socket transport foundation for TurboHTTP
- No dependency on UnityWebRequest or UnityEngine — pure C# networking
- HTTP/1.1 is fully functional with connection pooling and keep-alive
- HTTP/2 is added in Phase 3B on top of the same TCP/TLS infrastructure
- `async/await` pattern works seamlessly with Unity 2021.3+
- Cancellation token support enables aborting requests at any stage
- Timeline events recorded at connection, send, and receive stages
- **Critical risk:** SslStream with ALPN must be validated on IL2CPP before Phase 3B (see risk spikes)

## Deferred Items from Phase 2

The following items were identified during Phase 2 review and should be addressed in this phase:

1. **`byte[]` body → `ReadOnlyMemory<byte>`** — Evaluate migrating `UHttpRequest.Body` and `UHttpResponse.Body` from `byte[]` to `ReadOnlyMemory<byte>` to enable `ArrayPool<byte>` integration. This is an architectural decision that affects the transport layer's buffer management and the <1KB GC target. Decide during Task 3.8 (RawSocketTransport) implementation.

2. **Register `RawSocketTransport` as default transport** — `HttpTransportFactory.Default` currently throws `InvalidOperationException`. Phase 3 must set it during initialization (e.g., via a static constructor or `RuntimeInitializeOnLoadMethod`).

3. **`CONNECT` / `TRACE` HTTP methods** — Consider adding to the `HttpMethod` enum if proxy tunneling is needed. Not blocking but avoids a breaking change later.
