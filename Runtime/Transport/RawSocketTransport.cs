using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Transport
{
    /// <summary>
    /// Default <see cref="IHttpTransport"/> implementation using raw TCP sockets
    /// with custom HTTP/1.1 serialization and parsing.
    /// Wires together <see cref="TcpConnectionPool"/>, <see cref="Http11RequestSerializer"/>,
    /// and <see cref="Http11ResponseParser"/>.
    /// </summary>
    public sealed class RawSocketTransport : IHttpTransport
    {
        private readonly TcpConnectionPool _pool;
        private readonly Http2ConnectionManager _h2Manager;
        private readonly TlsBackend _tlsBackend;
        private int _disposed; // 0 = not disposed, 1 = disposed (Interlocked for atomic CAS)

        /// <summary>
        /// Backward-compatible constructor signature retained for binary compatibility.
        /// </summary>
        public RawSocketTransport(TcpConnectionPool pool = null, TlsBackend tlsBackend = TlsBackend.Auto)
            : this(pool, tlsBackend, new Http2Options())
        {
        }

        public RawSocketTransport(
            TcpConnectionPool pool,
            TlsBackend tlsBackend,
            Http2Options http2Options)
        {
            if (http2Options == null)
            {
                throw new ArgumentNullException(nameof(http2Options));
            }

            _pool = pool ?? new TcpConnectionPool(
                maxConnectionsPerHost: PlatformConfig.RecommendedMaxConcurrency,
                tlsBackend: tlsBackend);
            _h2Manager = new Http2ConnectionManager(http2Options);
            _tlsBackend = tlsBackend;
        }

        /// <summary>
        /// Explicitly register this transport with <see cref="HttpTransportFactory"/>.
        /// Not needed in normal usage (module initializer handles it).
        /// Useful for tests that call <see cref="HttpTransportFactory.Reset"/>.
        /// </summary>
        public static void EnsureRegistered()
        {
            HttpTransportFactory.Register(
                () => new RawSocketTransport(),
                tlsBackend => new RawSocketTransport(tlsBackend: tlsBackend),
                (tlsBackend, poolOptions, http2Options) =>
                    new RawSocketTransport(
                        pool: new TcpConnectionPool(
                            maxConnectionsPerHost: poolOptions.MaxConnectionsPerHost,
                            connectionIdleTimeout: poolOptions.ConnectionIdleTimeout,
                            tlsBackend: tlsBackend,
                            dnsTimeout: TimeSpan.FromMilliseconds(poolOptions.DnsTimeoutMs),
                            happyEyeballsOptions: poolOptions.HappyEyeballs),
                        tlsBackend: tlsBackend,
                        http2Options: http2Options));
        }

        public async ValueTask<UHttpResponse> SendAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(RawSocketTransport));

            // 1. Create timeout enforcement
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(request.Timeout);
            var ct = timeoutCts.Token;

            // 2. Validate URI and extract connection params
            if (!request.Uri.IsAbsoluteUri)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.InvalidRequest,
                    "Request URI must be absolute (include scheme and host)."));
            }

            var scheme = request.Uri.Scheme;
            if (!string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.InvalidRequest,
                    $"Unsupported URI scheme: {scheme}. Only http and https are supported."));
            }

            var host = request.Uri.Host;
            var port = request.Uri.Port;
            var secure = string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);
            var proxy = ResolveProxySettings(request);

            // 3. Record transport start
            context.RecordEvent("TransportStart");

            try
            {
                if (proxy != null)
                {
                    return await SendViaProxyAsync(
                        request,
                        context,
                        host,
                        port,
                        secure,
                        proxy,
                        ct).ConfigureAwait(false);
                }

                // 4a. HTTP/2 fast path (TLS only)
                if (secure)
                {
                    var h2Conn = _h2Manager.GetIfExists(host, port);
                    if (h2Conn != null)
                    {
                        try
                        {
                            context.RecordEvent("TransportH2Reuse");
                            return await h2Conn.SendRequestAsync(request, context, ct)
                                .ConfigureAwait(false);
                        }
                        catch (Exception) when (!ct.IsCancellationRequested)
                        {
                            // Stale h2 connection — always remove from manager to avoid
                            // subsequent requests hitting the same dead connection.
                            _h2Manager.Remove(host, port);

                            // Only retry idempotent methods to avoid duplicating side effects.
                            // Non-idempotent requests re-throw to fail the request.
                            if (!request.Method.IsIdempotent())
                                throw;

                            context.RecordEvent("TransportH2StaleRetry");
                        }
                    }
                }

                // 4b. Get connection lease (semaphore permit owned by lease)
                context.RecordEvent("TransportConnecting");
                using var lease = await _pool.GetConnectionAsync(host, port, secure, ct)
                    .ConfigureAwait(false);

                // 4c. Protocol routing based on ALPN
                if (lease.Connection.NegotiatedAlpnProtocol == "h2")
                {
                    context.RecordEvent("TransportH2Init");
                    lease.TransferOwnership();
                    var h2Conn = await _h2Manager.GetOrCreateAsync(
                        host, port, lease.Connection.Stream, ct).ConfigureAwait(false);
                    return await h2Conn.SendRequestAsync(request, context, ct)
                        .ConfigureAwait(false);
                }

                // 4d. HTTP/1.1 path
                try
                {
                    var parsed = await SendOnLeaseAsync(lease, request, context, ct)
                        .ConfigureAwait(false);
                    if (parsed.KeepAlive) lease.ReturnToPool();
                    return BuildResponse(parsed, context, request);
                }
                catch (IOException) when (lease.Connection.IsReused && request.Method.IsIdempotent())
                {
                    lease.Dispose();

                    context.RecordEvent("TransportRetryStale");
                    context.RecordEvent("TransportConnecting");

                    using var freshLease = await _pool.GetConnectionAsync(host, port, secure, ct)
                        .ConfigureAwait(false);

                    // Check ALPN on fresh connection too
                    if (freshLease.Connection.NegotiatedAlpnProtocol == "h2")
                    {
                        context.RecordEvent("TransportH2Init");
                        freshLease.TransferOwnership();
                        var h2Conn = await _h2Manager.GetOrCreateAsync(
                            host, port, freshLease.Connection.Stream, ct).ConfigureAwait(false);
                        return await h2Conn.SendRequestAsync(request, context, ct)
                            .ConfigureAwait(false);
                    }

                    var parsed = await SendOnLeaseAsync(freshLease, request, context, ct)
                        .ConfigureAwait(false);
                    if (parsed.KeepAlive) freshLease.ReturnToPool();
                    return BuildResponse(parsed, context, request);
                }
            }
            // IMPORTANT: UHttpException MUST be the first catch handler. Moving it will
            // cause double-wrapping of pool/TLS-thrown exceptions. See error model in overview.md.
            catch (UHttpException ex) when (
                ex.HttpError != null &&
                ex.HttpError.Type == UHttpErrorType.NetworkError &&
                timeoutCts.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.Timeout,
                    $"Request timed out after {request.Timeout.TotalSeconds}s",
                    ex));
            }
            catch (UHttpException ex) when (
                ex.HttpError != null &&
                ex.HttpError.Type == UHttpErrorType.NetworkError &&
                cancellationToken.IsCancellationRequested)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.Cancelled,
                    "Request was cancelled",
                    ex));
            }
            catch (UHttpException)
            {
                // Already mapped by pool/TLS layer — pass through, do NOT re-wrap.
                throw;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.Timeout,
                    $"Request timed out after {request.Timeout.TotalSeconds}s"));
            }
            catch (OperationCanceledException)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.Cancelled,
                    "Request was cancelled"));
            }
            catch (IOException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.Timeout,
                    $"Request timed out after {request.Timeout.TotalSeconds}s"));
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.Cancelled,
                    "Request was cancelled"));
            }
            catch (IOException ex)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError, ex.Message, ex));
            }
            catch (SocketException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.Timeout,
                    $"Request timed out after {request.Timeout.TotalSeconds}s"));
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.Cancelled,
                    "Request was cancelled"));
            }
            catch (SocketException ex)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError, ex.Message, ex));
            }
            catch (FormatException ex)
            {
                // Malformed HTTP response (e.g., invalid chunk size, bad status line, header size exceeded)
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    $"Malformed HTTP response: {ex.Message}", ex));
            }
            catch (NotSupportedException ex)
            {
                // Unsupported Transfer-Encoding or other protocol feature — treat as broken response
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    $"Unsupported HTTP response: {ex.Message}", ex));
            }
            catch (AuthenticationException ex)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.CertificateError, ex.Message, ex));
            }
            catch (Exception ex)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.Unknown, ex.Message, ex));
            }
        }

        /// <summary>
        /// Sends a request through an HTTP proxy (forward for HTTP, CONNECT tunnel for HTTPS).
        /// </summary>
        private async ValueTask<UHttpResponse> SendViaProxyAsync(
            UHttpRequest request,
            RequestContext context,
            string host,
            int port,
            bool secure,
            ProxySettings proxy,
            CancellationToken ct)
        {
            if (proxy?.Address == null)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.InvalidRequest,
                    "Proxy address is required."));
            }

            var proxyHost = proxy.Address.Host;
            var proxyPort = proxy.Address.IsDefaultPort
                ? 80
                : proxy.Address.Port;

            context.RecordEvent("TransportProxyConnecting");
            using var lease = await _pool.GetConnectionAsync(proxyHost, proxyPort, secure: false, ct)
                .ConfigureAwait(false);
            var stream = lease.Connection.Stream;

            if (!secure)
            {
                var forwardedRequest = PrepareHttpProxyForwardRequest(request, proxy);

                var parsed = await SendOnStreamAsync(
                    stream,
                    forwardedRequest,
                    context,
                    ct).ConfigureAwait(false);

                // Do not return proxy-forwarded sockets to the generic host pool:
                // pool keys currently do not include proxy auth identity or target scheme.
                return BuildResponse(parsed, context, forwardedRequest);
            }

            context.RecordEvent("TransportProxyConnect");
            context.RecordEvent("TransportProxyConnectHttp11Only");
            stream = await EstablishConnectTunnelAsync(
                stream,
                host,
                port,
                proxy,
                ct).ConfigureAwait(false);

            var tunneledRequest = PrepareHttpsProxyTunnelRequest(request);
            var tunneledParsed = await SendOnStreamAsync(
                stream,
                tunneledRequest,
                context,
                ct).ConfigureAwait(false);

            // CONNECT tunnels are authority-bound; do not return this socket to the generic proxy pool.
            return BuildResponse(tunneledParsed, context, tunneledRequest);
        }

        private static ProxySettings ResolveProxySettings(UHttpRequest request)
        {
            if (request?.Metadata == null)
                return null;

            if (request.Metadata.TryGetValue(RequestMetadataKeys.ProxyDisabled, out var disabledObj) &&
                disabledObj is bool disabled &&
                disabled)
            {
                return null;
            }

            if (request.Metadata.TryGetValue(RequestMetadataKeys.ProxySettings, out var settingsObj))
                return settingsObj as ProxySettings;

            return null;
        }

        private static UHttpRequest PrepareHttpProxyForwardRequest(UHttpRequest request, ProxySettings proxy)
        {
            var metadata = CloneMetadataWith(request.Metadata, RequestMetadataKeys.ProxyAbsoluteForm, true);
            var headers = request.Headers.Clone();
            var proxyAuthValue = BuildProxyAuthorizationHeaderValue(proxy);
            if (!string.IsNullOrEmpty(proxyAuthValue))
            {
                if (proxy.AllowPlaintextProxyAuth)
                {
                    headers.Set("Proxy-Authorization", proxyAuthValue);
                }
            }
            else
            {
                headers.Remove("Proxy-Authorization");
            }

            return new UHttpRequest(
                request.Method,
                request.Uri,
                headers,
                request.Body,
                request.Timeout,
                metadata);
        }

        private static UHttpRequest PrepareHttpsProxyTunnelRequest(UHttpRequest request)
        {
            var metadata = CloneMetadataWith(request.Metadata, RequestMetadataKeys.ProxyAbsoluteForm, false);
            var headers = request.Headers.Clone();
            headers.Remove("Proxy-Authorization");

            return new UHttpRequest(
                request.Method,
                request.Uri,
                headers,
                request.Body,
                request.Timeout,
                metadata);
        }

        private async Task<Stream> EstablishConnectTunnelAsync(
            Stream proxyStream,
            string targetHost,
            int targetPort,
            ProxySettings proxy,
            CancellationToken ct)
        {
            var authority = BuildAuthority(targetHost, targetPort);
            var attemptedAuth = false;

            while (true)
            {
                var connectRequest = BuildConnectRequest(authority, proxy, attemptedAuth);
                var buffer = Encoding.ASCII.GetBytes(connectRequest);
                await proxyStream.WriteAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                await proxyStream.FlushAsync(ct).ConfigureAwait(false);

                var response = await ReadProxyConnectResponseAsync(proxyStream, ct).ConfigureAwait(false);
                await DrainProxyConnectBodyAsync(proxyStream, response.Headers, ct).ConfigureAwait(false);

                if (response.StatusCode == 200)
                {
                    var tlsProvider = TlsProviderSelector.GetProvider(_tlsBackend);
                    // CONNECT tunnels currently run request framing through the HTTP/1.1 parser path only.
                    // Advertising h2 ALPN here would negotiate HTTP/2 that this tunnel code path cannot
                    // yet service safely.
                    var tlsResult = await tlsProvider.WrapAsync(
                        proxyStream,
                        targetHost,
                        new[] { "http/1.1" },
                        ct).ConfigureAwait(false);
                    return tlsResult.SecureStream;
                }

                if (response.StatusCode == 407 &&
                    !attemptedAuth &&
                    proxy?.Credentials != null &&
                    proxy.AllowPlaintextProxyAuth)
                {
                    attemptedAuth = true;
                    continue;
                }

                if (response.StatusCode == 407)
                {
                    throw new UHttpException(new UHttpError(
                        UHttpErrorType.InvalidRequest,
                        "Proxy authentication required (407) and credentials were not accepted."));
                }

                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    $"CONNECT tunnel failed with status {response.StatusCode}."));
            }
        }

        private static string BuildConnectRequest(
            string authority,
            ProxySettings proxy,
            bool includeAuth)
        {
            var sb = new StringBuilder(256);
            sb.Append("CONNECT ");
            sb.Append(authority);
            sb.Append(" HTTP/1.1\r\n");
            sb.Append("Host: ");
            sb.Append(authority);
            sb.Append("\r\n");
            sb.Append("Proxy-Connection: keep-alive\r\n");

            if (includeAuth)
            {
                var auth = BuildProxyAuthorizationHeaderValue(proxy);
                if (!string.IsNullOrEmpty(auth))
                {
                    sb.Append("Proxy-Authorization: ");
                    sb.Append(auth);
                    sb.Append("\r\n");
                }
            }

            sb.Append("\r\n");
            return sb.ToString();
        }

        private static string BuildAuthority(string host, int port)
        {
            if (host.Contains(":") && !host.StartsWith("[", StringComparison.Ordinal))
                return "[" + host + "]:" + port;
            return host + ":" + port;
        }

        private sealed class ProxyConnectResponse
        {
            public int StatusCode { get; set; }
            public HttpHeaders Headers { get; } = new HttpHeaders();
        }

        private static async Task<ProxyConnectResponse> ReadProxyConnectResponseAsync(
            Stream stream,
            CancellationToken ct)
        {
            const int maxTotalHeaderBytes = 16 * 1024;
            int totalBytes = 0;

            var statusLine = await ReadAsciiLineAsync(stream, ct).ConfigureAwait(false);
            totalBytes += statusLine.Length;

            if (string.IsNullOrEmpty(statusLine))
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    "Proxy CONNECT response is missing status line."));

            var firstSpace = statusLine.IndexOf(' ');
            if (firstSpace <= 0 || firstSpace + 1 >= statusLine.Length)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    "Invalid proxy CONNECT status line."));
            }

            var secondSpace = statusLine.IndexOf(' ', firstSpace + 1);
            var statusCodeStr = secondSpace > 0
                ? statusLine.Substring(firstSpace + 1, secondSpace - firstSpace - 1)
                : statusLine.Substring(firstSpace + 1);

            if (!int.TryParse(statusCodeStr, out var statusCode))
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    "Invalid proxy CONNECT status code."));
            }

            var response = new ProxyConnectResponse { StatusCode = statusCode };

            while (true)
            {
                var line = await ReadAsciiLineAsync(stream, ct).ConfigureAwait(false);
                totalBytes += line.Length;
                if (totalBytes > maxTotalHeaderBytes)
                {
                    throw new UHttpException(new UHttpError(
                        UHttpErrorType.NetworkError,
                        "Proxy CONNECT response headers exceeded size limit."));
                }

                if (line.Length == 0)
                    break;

                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;

                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                response.Headers.Add(name, value);
            }

            return response;
        }

        private static async Task DrainProxyConnectBodyAsync(
            Stream stream,
            HttpHeaders headers,
            CancellationToken ct)
        {
            if (headers == null)
                return;

            var contentLength = headers.Get("Content-Length");
            if (string.IsNullOrWhiteSpace(contentLength))
                return;

            if (!int.TryParse(contentLength, out var length) || length <= 0)
                return;

            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(4096, length));
            try
            {
                var remaining = length;
                while (remaining > 0)
                {
                    var toRead = Math.Min(remaining, buffer.Length);
                    var read = await stream.ReadAsync(buffer, 0, toRead, ct).ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static async Task<string> ReadAsciiLineAsync(Stream stream, CancellationToken ct)
        {
            var single = ArrayPool<byte>.Shared.Rent(1);
            var lineBytes = ArrayPool<byte>.Shared.Rent(128);
            var count = 0;

            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(single, 0, 1, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        if (count == 0)
                            return string.Empty;
                        break;
                    }

                    if (single[0] == (byte)'\n')
                        break;
                    if (single[0] == (byte)'\r')
                        continue;

                    if (count == lineBytes.Length)
                    {
                        var resized = ArrayPool<byte>.Shared.Rent(lineBytes.Length * 2);
                        try
                        {
                            Buffer.BlockCopy(lineBytes, 0, resized, 0, count);
                        }
                        catch
                        {
                            ArrayPool<byte>.Shared.Return(resized);
                            throw;
                        }

                        ArrayPool<byte>.Shared.Return(lineBytes);
                        lineBytes = resized;
                    }

                    lineBytes[count++] = single[0];
                }

                return count == 0
                    ? string.Empty
                    : Encoding.ASCII.GetString(lineBytes, 0, count);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(single);
                ArrayPool<byte>.Shared.Return(lineBytes);
            }
        }

        private static string BuildProxyAuthorizationHeaderValue(ProxySettings proxy)
        {
            if (proxy?.Credentials == null)
                return null;

            var user = proxy.Credentials.UserName ?? string.Empty;
            var pass = proxy.Credentials.Password ?? string.Empty;
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + pass));
            return "Basic " + token;
        }

        private static IReadOnlyDictionary<string, object> CloneMetadataWith(
            IReadOnlyDictionary<string, object> source,
            string key,
            object value)
        {
            var clone = source != null
                ? new Dictionary<string, object>(source)
                : new Dictionary<string, object>();

            clone[key] = value;
            return clone;
        }

        /// <summary>
        /// Execute a single send attempt on the given lease. Returns the parsed response.
        /// Caller is responsible for lease disposal.
        /// </summary>
        private async Task<ParsedResponse> SendOnLeaseAsync(
            ConnectionLease lease,
            UHttpRequest request,
            RequestContext context,
            CancellationToken ct)
        {
            context.RecordEvent("TransportSending");
            await Http11RequestSerializer.SerializeAsync(request, lease.Connection.Stream, ct)
                .ConfigureAwait(false);

            context.RecordEvent("TransportReceiving");
            var parsed = await Http11ResponseParser.ParseAsync(lease.Connection.Stream, request.Method, ct)
                .ConfigureAwait(false);

            context.RecordEvent("TransportComplete");
            return parsed;
        }

        private static async Task<ParsedResponse> SendOnStreamAsync(
            Stream stream,
            UHttpRequest request,
            RequestContext context,
            CancellationToken ct)
        {
            context.RecordEvent("TransportSending");
            await Http11RequestSerializer.SerializeAsync(request, stream, ct)
                .ConfigureAwait(false);

            context.RecordEvent("TransportReceiving");
            var parsed = await Http11ResponseParser.ParseAsync(stream, request.Method, ct)
                .ConfigureAwait(false);

            context.RecordEvent("TransportComplete");
            return parsed;
        }

        private static UHttpResponse BuildResponse(
            ParsedResponse parsed,
            RequestContext context,
            UHttpRequest request)
        {
            return new UHttpResponse(
                statusCode: parsed.StatusCode,
                headers: parsed.Headers,
                body: parsed.Body,
                elapsedTime: context.Elapsed,
                request: request,
                bodyFromPool: parsed.BodyFromPool);
        }

        internal bool HasHttp2Connection(string host, int port)
        {
            return _h2Manager != null && _h2Manager.HasConnection(host, port);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _h2Manager?.Dispose();
            _pool?.Dispose();
        }
    }
}
