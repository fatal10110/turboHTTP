using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Internal;
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
        private static readonly string[] s_connectTunnelAlpnProtocols = { "h2", "http/1.1" };
        private readonly TcpConnectionPool _pool;
        private readonly Http2ConnectionManager _h2Manager;
        private readonly TlsBackend _tlsBackend;
        private readonly StreamingOptions _streamingOptions;
        private int _disposed; // 0 = not disposed, 1 = disposed (Interlocked for atomic CAS)

        /// <summary>
        /// Backward-compatible constructor signature retained for binary compatibility.
        /// </summary>
        public RawSocketTransport(TcpConnectionPool pool = null, TlsBackend tlsBackend = TlsBackend.Auto)
            : this(pool, tlsBackend, new Http2Options(), new StreamingOptions())
        {
        }

        public RawSocketTransport(
            TcpConnectionPool pool,
            TlsBackend tlsBackend,
            Http2Options http2Options)
            : this(pool, tlsBackend, http2Options, new StreamingOptions())
        {
        }

        public RawSocketTransport(
            TcpConnectionPool pool,
            TlsBackend tlsBackend,
            Http2Options http2Options,
            StreamingOptions streamingOptions)
        {
            if (http2Options == null)
            {
                throw new ArgumentNullException(nameof(http2Options));
            }
            if (streamingOptions == null)
            {
                throw new ArgumentNullException(nameof(streamingOptions));
            }

            _pool = pool ?? new TcpConnectionPool(
                maxConnectionsPerHost: PlatformConfig.RecommendedMaxConcurrency,
                tlsBackend: tlsBackend);
            _streamingOptions = streamingOptions.Clone();
            _h2Manager = new Http2ConnectionManager(http2Options, _streamingOptions);
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
                (tlsBackend, poolOptions, http2Options, streamingOptions) =>
                    new RawSocketTransport(
                        pool: new TcpConnectionPool(
                            maxConnectionsPerHost: poolOptions.MaxConnectionsPerHost,
                            connectionIdleTimeout: poolOptions.ConnectionIdleTimeout,
                            tlsBackend: tlsBackend,
                            dnsTimeout: TimeSpan.FromMilliseconds(poolOptions.DnsTimeoutMs),
                            happyEyeballsOptions: poolOptions.HappyEyeballs,
                            socketIoMode: poolOptions.SocketIoMode),
                        tlsBackend: tlsBackend,
                        http2Options: http2Options,
                        streamingOptions: streamingOptions));
        }

        public async Task DispatchAsync(
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var effectiveRequest = PrepareEffectiveRequest(request, context);

            context.SetState(TransportBehaviorFlags.SelfDrainsResponseBody, true);

            try
            {
                handler.OnRequestStart(effectiveRequest, context);
            }
            catch (Exception ex)
            {
                context.RecordEvent("RequestFailed");
                handler.OnResponseError(
                    ex as UHttpException
                    ?? new UHttpException(new UHttpError(
                        UHttpErrorType.Unknown,
                        ex.Message,
                        ex)),
                    context);
                return;
            }

            try
            {
                await DispatchCoreAsync(effectiveRequest, handler, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UHttpException ex)
            {
                context.RecordEvent("RequestFailed");
                handler.OnResponseError(ex, context);
            }
            catch (OperationCanceledException)
            {
                context.RecordEvent("RequestCancelled");
                throw;
            }
        }

        private static UHttpException MapException(Exception ex) =>
            ex as UHttpException ??
            new UHttpException(new UHttpError(UHttpErrorType.NetworkError, ex.Message, ex));

        private static bool CanRetryH2RequestAfterTransportFailure(UHttpRequest request)
        {
            return request.Method.IsIdempotent() &&
                request.Content.Replayability != RequestBodyReplayability.NonReplayable;
        }

        private static bool CanRetryHttp11RequestAfterTransportFailure(
            UHttpRequest request,
            Http11RequestWriteState writeState)
        {
            if (request.Content.Replayability == RequestBodyReplayability.NonReplayable)
                return false;

            return request.Method.IsIdempotent() ||
                (writeState != null && !writeState.HasCommittedBodyBytes);
        }

        private static bool ShouldSurfaceCommittedNonReplayableBodyFailure(
            UHttpRequest request,
            Http11RequestWriteState writeState,
            CancellationToken transportToken,
            Exception ex)
        {
            return !transportToken.IsCancellationRequested &&
                request.Content.Replayability == RequestBodyReplayability.NonReplayable &&
                writeState != null &&
                writeState.HasCommittedBodyBytes &&
                (ex is IOException ||
                 ex is SocketException ||
                 IsConnectionClosedBeforeStatusLine(ex));
        }

        private static bool IsConnectionClosedBeforeStatusLine(Exception ex)
        {
            return ex is FormatException formatException &&
                string.Equals(
                    formatException.Message,
                    "Empty HTTP status line",
                    StringComparison.Ordinal);
        }

        private static UHttpException CreateCommittedNonReplayableBodyFailure(Exception ex)
        {
            return new UHttpException(new UHttpError(
                UHttpErrorType.NetworkError,
                "Connection failed after a non-replayable request body started sending. The request cannot be retried.",
                ex));
        }

        private UHttpRequest PrepareEffectiveRequest(UHttpRequest request, RequestContext context)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (!ShouldAutoInjectExpectContinue(request))
            {
                if (!ReferenceEquals(context?.Request, request))
                    context?.UpdateRequest(request);

                return request;
            }

            var effectiveRequest = request.CopyWithSharedContent().WithExpectContinue();
            context?.UpdateRequest(effectiveRequest);
            return effectiveRequest;
        }

        private bool ShouldAutoInjectExpectContinue(UHttpRequest request)
        {
            if (request == null || ExpectContinueHelper.HasExpectContinueHeader(request.Headers))
                return false;

            var threshold = _streamingOptions.AutoExpectContinueThresholdBytes;
            if (!threshold.HasValue || threshold.Value < 0)
                return false;

            return ExpectContinueHelper.TryGetKnownRequestBodyLength(request.Content, out var length) &&
                   length > threshold.Value &&
                   length > 0;
        }

        private async Task DispatchCoreAsync(
            UHttpRequest request,
            IHttpHandler handler,
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
                    await DispatchViaProxyAsync(
                        request,
                        handler,
                        context,
                        host,
                        port,
                        secure,
                        proxy,
                        ct).ConfigureAwait(false);
                    return;
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
                            await h2Conn.DispatchAsync(request, handler, context, ct)
                                .ConfigureAwait(false);
                            return;
                        }
                        catch (Exception) when (!ct.IsCancellationRequested)
                        {
                            // Stale h2 connection — always remove from manager to avoid
                            // subsequent requests hitting the same dead connection.
                            _h2Manager.Remove(host, port);

                            if (!CanRetryH2RequestAfterTransportFailure(request))
                                throw;

                            context.RecordEvent("TransportH2StaleRetry");
                        }
                    }
                }

                // 4b. Get connection lease (semaphore permit owned by lease)
                context.RecordEvent("TransportConnecting");
                ConnectionLease lease = null;
                try
                {
                    lease = await _pool.GetConnectionAsync(host, port, secure, ct)
                        .ConfigureAwait(false);

                    // 4c. Protocol routing based on ALPN
                    if (lease.Connection.NegotiatedAlpnProtocol == "h2")
                    {
                        context.RecordEvent("TransportH2Init");
                        lease.TransferOwnership();
                        var h2Conn = await _h2Manager.GetOrCreateAsync(
                            host, port, lease.Connection.Stream, ct).ConfigureAwait(false);
                        lease = null;
                        await h2Conn.DispatchAsync(request, handler, context, ct)
                            .ConfigureAwait(false);
                        return;
                    }

                    // 4d. HTTP/1.1 path
                    var requestWriteState = new Http11RequestWriteState();
                    try
                    {
                        await DispatchOnLeaseAsync(
                                lease,
                                request,
                                handler,
                                context,
                                requestWriteState,
                                ct)
                            .ConfigureAwait(false);
                        lease = null;

                        return;
                    }
                    catch (Exception ex) when (
                        lease != null &&
                        lease.Connection.IsReused &&
                        (ex is IOException || ex is SocketException) &&
                        CanRetryHttp11RequestAfterTransportFailure(request, requestWriteState))
                    {
                        lease.Dispose();
                        lease = null;

                        context.RecordEvent("TransportRetryStale");
                        context.RecordEvent("TransportConnecting");

                        ConnectionLease freshLease = null;
                        try
                        {
                            freshLease = await _pool.GetConnectionAsync(host, port, secure, ct)
                                .ConfigureAwait(false);

                            if (freshLease.Connection.NegotiatedAlpnProtocol == "h2")
                            {
                                context.RecordEvent("TransportH2Init");
                                freshLease.TransferOwnership();
                                var h2Conn = await _h2Manager.GetOrCreateAsync(
                                    host, port, freshLease.Connection.Stream, ct).ConfigureAwait(false);
                                freshLease = null;
                                await h2Conn.DispatchAsync(request, handler, context, ct)
                                    .ConfigureAwait(false);
                                return;
                            }

                            var retryWriteState = new Http11RequestWriteState();
                            try
                            {
                                await DispatchOnLeaseAsync(
                                        freshLease,
                                        request,
                                        handler,
                                        context,
                                        retryWriteState,
                                        ct)
                                    .ConfigureAwait(false);
                                freshLease = null;

                                return;
                            }
                            catch (Exception retryEx) when (
                                ShouldSurfaceCommittedNonReplayableBodyFailure(
                                    request,
                                    retryWriteState,
                                    ct,
                                    retryEx))
                            {
                                throw CreateCommittedNonReplayableBodyFailure(retryEx);
                            }
                        }
                        finally
                        {
                            freshLease?.Dispose();
                        }
                    }
                    catch (Exception ex) when (
                        ShouldSurfaceCommittedNonReplayableBodyFailure(
                            request,
                            requestWriteState,
                            ct,
                            ex))
                    {
                        throw CreateCommittedNonReplayableBodyFailure(ex);
                    }
                }
                finally
                {
                    lease?.Dispose();
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
                throw new OperationCanceledException("Request was cancelled", ex, cancellationToken);
            }
            catch (UHttpException)
            {
                // Already mapped by pool/TLS layer — pass through, do NOT re-wrap.
                throw;
            }
            catch (HandlerCallbackException ex)
            {
                context.RecordEvent("RequestFailed");
                ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
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
                throw new OperationCanceledException("Request was cancelled", cancellationToken);
            }
            catch (IOException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.Timeout,
                    $"Request timed out after {request.Timeout.TotalSeconds}s"));
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Request was cancelled", cancellationToken);
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
                throw new OperationCanceledException("Request was cancelled", cancellationToken);
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
        private async Task DispatchViaProxyAsync(
            UHttpRequest request,
            IHttpHandler handler,
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

            if (secure)
            {
                var existingH2 = _h2Manager.GetIfExists(host, port, proxyHost, proxyPort);
                if (existingH2 != null)
                {
                    var tunneledRequest = PrepareHttpsProxyTunnelRequest(request);
                    try
                    {
                        context.RecordEvent("TransportProxyH2Reuse");
                        await existingH2.DispatchAsync(tunneledRequest, handler, context, ct)
                            .ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex) when (
                        !ct.IsCancellationRequested &&
                        (ex is IOException ||
                         (ex is UHttpException uhe &&
                          uhe.HttpError != null &&
                          uhe.HttpError.IsRetryable())))
                    {
                        _h2Manager.Remove(host, port, proxyHost, proxyPort);

                        if (!CanRetryH2RequestAfterTransportFailure(request))
                            throw;

                        context.RecordEvent("TransportProxyH2StaleRetry");
                    }
                }
            }

            if (!secure)
            {
                context.RecordEvent("TransportProxyConnecting");
                var forwardedRequest = PrepareHttpProxyForwardRequest(request, proxy);
                await DispatchProxyHttp11WithRetryAsync(
                        proxyHost,
                        proxyPort,
                        poolKeyOverride: null,
                        establishTunnel: false,
                        targetHost: null,
                        targetPort: 0,
                        proxy,
                        forwardedRequest,
                        request,
                        handler,
                        context,
                        ct)
                    .ConfigureAwait(false);
                return;
            }

            var tunnelPoolKey = BuildConnectTunnelPoolKey(proxyHost, proxyPort, host, port);
            ConnectionLease lease = null;
            try
            {
                context.RecordEvent("TransportProxyConnecting");
                lease = await AcquirePreparedProxyLeaseAsync(
                        proxyHost,
                        proxyPort,
                        tunnelPoolKey,
                        establishTunnel: true,
                        host,
                        port,
                        proxy,
                        context,
                        ct)
                    .ConfigureAwait(false);

                var tunneledRequest = PrepareHttpsProxyTunnelRequest(request);
                if (lease.Connection.NegotiatedAlpnProtocol == "h2")
                {
                    context.RecordEvent("TransportProxyH2Init");
                    lease.TransferOwnership();
                    var h2Conn = await _h2Manager.GetOrCreateAsync(
                            host,
                            port,
                            proxyHost,
                            proxyPort,
                            lease.Connection.Stream,
                            ct)
                        .ConfigureAwait(false);
                    lease = null;

                    await h2Conn.DispatchAsync(tunneledRequest, handler, context, ct)
                        .ConfigureAwait(false);
                    return;
                }

                context.RecordEvent("TransportProxyH1Dispatch");
                await DispatchProxyHttp11WithRetryAsync(
                        proxyHost,
                        proxyPort,
                        tunnelPoolKey,
                        establishTunnel: true,
                        host,
                        port,
                        proxy,
                        tunneledRequest,
                        request,
                        handler,
                        context,
                        ct,
                        lease)
                    .ConfigureAwait(false);
                lease = null;
            }
            finally
            {
                lease?.Dispose();
            }
        }

        private async Task DispatchProxyHttp11WithRetryAsync(
            string proxyHost,
            int proxyPort,
            string poolKeyOverride,
            bool establishTunnel,
            string targetHost,
            int targetPort,
            ProxySettings proxy,
            UHttpRequest dispatchRequest,
            UHttpRequest originalRequest,
            IHttpHandler handler,
            RequestContext context,
            CancellationToken ct,
            ConnectionLease preparedLease = null)
        {
            ConnectionLease lease = null;
            try
            {
                lease = preparedLease;
                if (lease == null)
                {
                    lease = await AcquirePreparedProxyLeaseAsync(
                            proxyHost,
                            proxyPort,
                            poolKeyOverride,
                            establishTunnel,
                            targetHost,
                            targetPort,
                            proxy,
                            context,
                            ct)
                        .ConfigureAwait(false);
                }

                var writeState = new Http11RequestWriteState();
                try
                {
                    await DispatchOnLeaseAsync(
                            lease,
                            dispatchRequest,
                            handler,
                            context,
                            writeState,
                            ct)
                        .ConfigureAwait(false);
                    lease = null;
                    return;
                }
                catch (Exception ex) when (
                    lease != null &&
                    lease.Connection.IsReused &&
                    (ex is IOException || ex is SocketException) &&
                    CanRetryHttp11RequestAfterTransportFailure(originalRequest, writeState))
                {
                    lease.Dispose();
                    lease = null;

                    context.RecordEvent("TransportRetryStale");
                    context.RecordEvent("TransportProxyConnecting");

                    ConnectionLease freshLease = null;
                    try
                    {
                        freshLease = await AcquirePreparedProxyLeaseAsync(
                                proxyHost,
                                proxyPort,
                                poolKeyOverride,
                                establishTunnel,
                                targetHost,
                                targetPort,
                                proxy,
                                context,
                                ct)
                            .ConfigureAwait(false);

                        var retryWriteState = new Http11RequestWriteState();
                        try
                        {
                            await DispatchOnLeaseAsync(
                                    freshLease,
                                    dispatchRequest,
                                    handler,
                                    context,
                                    retryWriteState,
                                    ct)
                                .ConfigureAwait(false);
                            freshLease = null;
                            return;
                        }
                        catch (Exception retryEx) when (
                            ShouldSurfaceCommittedNonReplayableBodyFailure(
                                originalRequest,
                                retryWriteState,
                                ct,
                                retryEx))
                        {
                            throw CreateCommittedNonReplayableBodyFailure(retryEx);
                        }
                    }
                    finally
                    {
                        freshLease?.Dispose();
                    }
                }
                catch (Exception ex) when (
                    ShouldSurfaceCommittedNonReplayableBodyFailure(
                        originalRequest,
                        writeState,
                        ct,
                        ex))
                {
                    throw CreateCommittedNonReplayableBodyFailure(ex);
                }
            }
            finally
            {
                lease?.Dispose();
            }
        }

        private async Task<ConnectionLease> AcquirePreparedProxyLeaseAsync(
            string proxyHost,
            int proxyPort,
            string poolKeyOverride,
            bool establishTunnel,
            string targetHost,
            int targetPort,
            ProxySettings proxy,
            RequestContext context,
            CancellationToken ct)
        {
            ConnectionLease lease = null;
            try
            {
                lease = await _pool.GetConnectionAsync(
                        proxyHost,
                        proxyPort,
                        secure: false,
                        poolKeyOverride,
                        ct)
                    .ConfigureAwait(false);

                if (!establishTunnel)
                    return lease;

                // Idle validation for TLS-wrapped tunnels is best-effort because the pool can
                // only observe the outer proxy TCP socket. The stale-retry path remains the
                // correctness backstop if the origin-side tunnel state has already gone bad.
                if (lease.Connection.IsSecure)
                {
                    if (string.Equals(
                            lease.Connection.PoolKey,
                            poolKeyOverride,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return lease;
                    }

                    throw new UHttpException(new UHttpError(
                        UHttpErrorType.NetworkError,
                        "Reused proxy tunnel connection was bound to an unexpected target."));
                }

                context.RecordEvent("TransportProxyConnect");
                context.RecordEvent("TransportProxyConnectTunnelEstablished");
                var tlsResult = await EstablishConnectTunnelAsync(
                        lease.Connection.Stream,
                        targetHost,
                        targetPort,
                        proxy,
                        ct)
                    .ConfigureAwait(false);
                RebindConnectTunnelLease(lease, targetHost, targetPort, poolKeyOverride, tlsResult);
                return lease;
            }
            catch
            {
                lease?.Dispose();
                throw;
            }
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
                else
                {
                    headers.Remove("Proxy-Authorization");
                }
            }
            else
            {
                headers.Remove("Proxy-Authorization");
            }

            return request
                .CopyWithSharedContent()
                .WithHeaders(headers)
                .WithMetadata(metadata);
        }

        private static UHttpRequest PrepareHttpsProxyTunnelRequest(UHttpRequest request)
        {
            var metadata = CloneMetadataWith(request.Metadata, RequestMetadataKeys.ProxyAbsoluteForm, false);
            var headers = request.Headers.Clone();
            headers.Remove("Proxy-Authorization");

            return request
                .CopyWithSharedContent()
                .WithHeaders(headers)
                .WithMetadata(metadata);
        }

        private static string BuildConnectTunnelPoolKey(
            string proxyHost,
            int proxyPort,
            string targetHost,
            int targetPort)
        {
            return "tunnel|" +
                TcpConnectionPool.BuildConnectionKey(proxyHost, proxyPort, false) +
                "|" +
                TcpConnectionPool.BuildConnectionKey(targetHost, targetPort, true);
        }

        private static void RebindConnectTunnelLease(
            ConnectionLease lease,
            string targetHost,
            int targetPort,
            string tunnelPoolKey,
            TlsResult tlsResult)
        {
            if (lease == null)
                throw new ArgumentNullException(nameof(lease));
            if (string.IsNullOrEmpty(targetHost))
                throw new ArgumentNullException(nameof(targetHost));
            if (tlsResult == null)
                throw new ArgumentNullException(nameof(tlsResult));

            // Preserve IsReused through the rebind. It tracks whether the underlying proxy TCP
            // socket came from the pool, not whether the CONNECT tunnel/TLS session is fresh.
            lease.Connection.UpdateTransportBinding(
                tlsResult.SecureStream,
                targetHost,
                targetPort,
                isSecure: true,
                tunnelPoolKey,
                tlsResult.TlsVersion,
                tlsResult.ProviderName,
                tlsResult.NegotiatedAlpn);
        }

        private async Task<TlsResult> EstablishConnectTunnelAsync(
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
                    var tlsResult = await tlsProvider.WrapAsync(
                        proxyStream,
                        targetHost,
                        s_connectTunnelAlpnProtocols,
                        ct).ConfigureAwait(false);

                    MarkSslStreamViableIfAuto(_tlsBackend, tlsResult);
                    return tlsResult;
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

        private static void MarkSslStreamViableIfAuto(TlsBackend tlsBackend, TlsResult tlsResult)
        {
            if (tlsBackend == TlsBackend.Auto && tlsResult.ProviderName == "SslStream")
                TlsProviderSelector.MarkSslStreamViable();
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
            sb.Append("Connection: keep-alive\r\n");

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

            var transferEncoding = headers.Get("Transfer-Encoding");
            if (!string.IsNullOrWhiteSpace(transferEncoding))
            {
                if (EndsWithTransferCodingToken(transferEncoding, "chunked"))
                {
                    await DrainChunkedBodyAsync(stream, ct).ConfigureAwait(false);
                    return;
                }
            }

            var contentLength = headers.Get("Content-Length");
            if (!string.IsNullOrWhiteSpace(contentLength))
            {
                if (!int.TryParse(contentLength, out var length) || length <= 0)
                    return;

                var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(4096, length));
                try
                {
                    await DrainFixedLengthBodyAsync(stream, buffer, length, ct).ConfigureAwait(false);
                    return;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            if (!string.IsNullOrWhiteSpace(transferEncoding))
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    "Proxy CONNECT response used an unsupported Transfer-Encoding without a Content-Length. " +
                    "The connection cannot be safely reused."));
            }
        }

        private static async Task DrainFixedLengthBodyAsync(
            Stream stream,
            byte[] buffer,
            int length,
            CancellationToken ct)
        {
            var remaining = length;
            while (remaining > 0)
            {
                var toRead = Math.Min(remaining, buffer.Length);
                var read = await stream.ReadAsync(buffer, 0, toRead, ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new UHttpException(new UHttpError(
                        UHttpErrorType.NetworkError,
                        "Unexpected EOF while draining proxy CONNECT response body."));
                }

                remaining -= read;
            }
        }

        private static async Task DrainChunkedBodyAsync(
            Stream stream,
            CancellationToken ct)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                while (true)
                {
                    var sizeLine = await ReadAsciiLineAsync(stream, ct).ConfigureAwait(false);
                    var chunkSize = Http11ResponseParser.ParseChunkSizeLine(sizeLine);
                    if (chunkSize < 0 || chunkSize > int.MaxValue)
                    {
                        throw new UHttpException(new UHttpError(
                            UHttpErrorType.NetworkError,
                            "Proxy CONNECT chunked response body exceeded the supported drain size."));
                    }

                    if (chunkSize == 0)
                    {
                        await DrainHeaderSectionAsync(stream, ct).ConfigureAwait(false);
                        return;
                    }

                    await DrainFixedLengthBodyAsync(stream, buffer, (int)chunkSize, ct).ConfigureAwait(false);
                    await ReadExpectedCrlfAsync(stream, ct).ConfigureAwait(false);
                }
            }
            catch (FormatException ex)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    "Invalid chunked proxy CONNECT response body.", ex));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static async Task DrainHeaderSectionAsync(Stream stream, CancellationToken ct)
        {
            while (true)
            {
                var line = await ReadAsciiLineAsync(stream, ct).ConfigureAwait(false);
                if (line.Length == 0)
                    return;
            }
        }

        private static bool EndsWithTransferCodingToken(string transferEncoding, string token)
        {
            if (string.IsNullOrEmpty(transferEncoding))
                return false;

            int start = 0;
            int end = transferEncoding.Length - 1;
            while (start <= end && char.IsWhiteSpace(transferEncoding[start]))
                start++;
            while (end >= start && char.IsWhiteSpace(transferEncoding[end]))
                end--;

            int trimmedLength = end - start + 1;
            if (trimmedLength < token.Length)
                return false;

            var trimmed = transferEncoding.AsSpan(start, trimmedLength);
            var suffix = trimmed.Slice(trimmedLength - token.Length);
            if (!suffix.Equals(token.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return false;

            if (trimmedLength == token.Length)
                return true;

            var preceding = trimmed[trimmedLength - token.Length - 1];
            return preceding == ',' || char.IsWhiteSpace(preceding);
        }

        private static async Task ReadExpectedCrlfAsync(Stream stream, CancellationToken ct)
        {
            var delimiter = ArrayPool<byte>.Shared.Rent(2);
            try
            {
                int read = 0;
                while (read < 2)
                {
                    var current = await stream.ReadAsync(delimiter, read, 2 - read, ct).ConfigureAwait(false);
                    if (current <= 0)
                    {
                        throw new UHttpException(new UHttpError(
                            UHttpErrorType.NetworkError,
                            "Unexpected EOF while draining proxy CONNECT response body."));
                    }

                    read += current;
                }

                if (delimiter[0] != (byte)'\r' || delimiter[1] != (byte)'\n')
                {
                    throw new UHttpException(new UHttpError(
                        UHttpErrorType.NetworkError,
                        "Invalid chunked proxy CONNECT response body framing."));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(delimiter);
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
        private static async Task<bool> EmitParsedResponseAsync(
            ParsedResponse parsed,
            IHttpHandler handler,
            RequestContext context)
        {
            if (parsed == null)
                throw new ArgumentNullException(nameof(parsed));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var keepAlive = parsed.KeepAlive;
            var statusCode = (int)parsed.StatusCode;
            var headers = parsed.Headers;

            var safeHandler = HandlerCallbackSafetyWrapper.Wrap(handler, context);
            BufferedResponseBodySource bodySource;
            if (parsed.SegmentedBody != null)
            {
                var trailers = parsed.Trailers ?? HttpHeaders.Empty;
                var copied = parsed.SegmentedBody.AsSequence().ToArray();
                parsed.ReleaseBodyBuffers();
                ParsedResponsePool.Return(parsed);
                bodySource = new BufferedResponseBodySource(copied, trailers);
            }
            else
            {
                bodySource = new BufferedResponseBodySource(
                    parsed.Body,
                    parsed.Trailers ?? HttpHeaders.Empty,
                    () =>
                    {
                        parsed.ReleaseBodyBuffers();
                        ParsedResponsePool.Return(parsed);
                    });
            }

            await safeHandler.OnResponseStartAsync(
                    statusCode,
                    headers,
                    bodySource,
                    context)
                .ConfigureAwait(false);
            return keepAlive;
        }

        private async Task DispatchOnLeaseAsync(
            ConnectionLease lease,
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            Http11RequestWriteState requestWriteState,
            CancellationToken ct)
        {
            using var head = await SendOnLeaseAsync(lease, request, context, requestWriteState, ct)
                .ConfigureAwait(false);

            var transportBodyReadToken = context.GetState(
                TransportBehaviorFlags.StreamingResponseRequested,
                false)
                ? CancellationToken.None
                : ct;

            await EmitParsedResponseHeadAsync(
                    head,
                    lease,
                    handler,
                    context,
                    transportBodyReadToken,
                    request.Timeout,
                    _streamingOptions)
                .ConfigureAwait(false);

            context.RecordEvent("TransportComplete");
        }

        private static async Task<bool> DispatchOnStreamAsync(
            Stream stream,
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            Http11RequestWriteState requestWriteState,
            CancellationToken ct,
            StreamingOptions streamingOptions)
        {
            var parsed = await SendOnStreamAsync(stream, request, context, requestWriteState, ct, streamingOptions)
                .ConfigureAwait(false);
            return await EmitParsedResponseAsync(parsed, handler, context).ConfigureAwait(false);
        }

        private static async Task EmitParsedResponseHeadAsync(
            ParsedResponseHead head,
            ConnectionLease lease,
            IHttpHandler handler,
            RequestContext context,
            CancellationToken transportBodyReadToken,
            TimeSpan requestTimeout,
            StreamingOptions streamingOptions)
        {
            if (head == null)
                throw new ArgumentNullException(nameof(head));
            if (lease == null)
                throw new ArgumentNullException(nameof(lease));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var safeHandler = HandlerCallbackSafetyWrapper.Wrap(handler, context);
            Http11ResponseBodySource bodySource = null;
            try
            {
                bodySource = new Http11ResponseBodySource(
                    head,
                    lease,
                    transportBodyReadToken,
                    requestTimeout,
                    streamingOptions);

                await safeHandler.OnResponseStartAsync(
                        (int)head.StatusCode,
                        head.Headers,
                        bodySource,
                        context)
                    .ConfigureAwait(false);
            }
            catch
            {
                bodySource?.Abort();
                throw;
            }
        }

        private async Task<ParsedResponseHead> SendOnLeaseAsync(
            ConnectionLease lease,
            UHttpRequest request,
            RequestContext context,
            Http11RequestWriteState requestWriteState,
            CancellationToken ct)
        {
            context.RecordEvent("TransportSending");
            if (ExpectContinueHelper.ShouldAwaitExpectContinue(request))
            {
                return await SendOnLeaseWithExpectContinueAsync(
                        lease,
                        request,
                        context,
                        requestWriteState,
                        ct)
                    .ConfigureAwait(false);
            }

            try
            {
                await Http11RequestSerializer.SerializeAsync(
                        request,
                        lease.Connection.Stream,
                        ct,
                        requestWriteState,
                        _streamingOptions)
                    .ConfigureAwait(false);
            }
            finally
            {
                RecordRequestBodyBytesSent(context, requestWriteState);
            }

            context.RecordEvent("TransportReceiving");
            var parsed = await Http11ResponseParser.ParseHeadAsync(
                    lease.Connection.Stream,
                    request.Method,
                    ct,
                    context)
                .ConfigureAwait(false);

            return parsed;
        }

        private async Task<ParsedResponseHead> SendOnLeaseWithExpectContinueAsync(
            ConnectionLease lease,
            UHttpRequest request,
            RequestContext context,
            Http11RequestWriteState requestWriteState,
            CancellationToken ct)
        {
            var stream = lease.Connection.Stream;
            var reader = new Http11ResponseParser.BufferedStreamReader(stream);
            bool readerOwned = true;
            int interim1xxCount = 0;
            try
            {
                await Http11RequestSerializer.SerializeHeadersAsync(request, stream, ct)
                    .ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                context.RecordEvent("TransportReceiving");

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_streamingOptions.ExpectContinueTimeoutMs);
                var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

                while (true)
                {
                    var initialHeadTask = Http11ResponseParser.ParseNextHeadDataAsync(
                        reader,
                        request.Method,
                        ct,
                        context);

                    var winner = await Task.WhenAny(timeoutTask, initialHeadTask).ConfigureAwait(false);
                    if (winner == initialHeadTask)
                    {
                        var initialHead = await initialHeadTask.ConfigureAwait(false);
                        if (ShouldReturnExpectContinueHead(initialHead.StatusCode))
                        {
                            timeoutCts.Cancel();
                            readerOwned = false;
                            return Http11ResponseParser.CreateParsedResponseHead(reader, initialHead);
                        }

                        interim1xxCount = IncrementExpectContinueInterimCount(interim1xxCount);
                        if ((int)initialHead.StatusCode != 100)
                        {
                            // Non-100 informational responses do not satisfy Expect: 100-continue.
                            // Keep the original deadline running so a server cannot extend the wait
                            // indefinitely by streaming additional interim responses.
                            continue;
                        }

                        timeoutCts.Cancel();

                        var bodySendException = await TrySendRequestBodyAsync(
                                stream,
                                request,
                                requestWriteState,
                                ct)
                            .ConfigureAwait(false);

                        try
                        {
                            var finalHead = await Http11ResponseParser.ParseHeadAsync(
                                    reader,
                                    request.Method,
                                    ct,
                                    context,
                                    initialInterim1xxCount: interim1xxCount)
                                .ConfigureAwait(false);
                            if (bodySendException != null)
                            {
                                using (finalHead)
                                {
                                    ThrowCapturedBodySendException(
                                        bodySendException,
                                        context,
                                        responseStatusCode: finalHead.StatusCode);
                                }
                            }

                            readerOwned = false;
                            return finalHead;
                        }
                        catch (Exception responseReadException) when (bodySendException != null)
                        {
                            ThrowCapturedBodySendException(
                                bodySendException,
                                context,
                                responseReadException);
                            throw;
                        }
                    }

                    // This is the caller's request-scoped cancellation token, not timeoutCts.Token.
                    // The expect-continue timeout already expired if we reached this branch.
                    ct.ThrowIfCancellationRequested();

                    var timeoutBodySendException = await TrySendRequestBodyAsync(
                            stream,
                            request,
                            requestWriteState,
                            ct)
                        .ConfigureAwait(false);

                    Http11ResponseParser.ParsedResponseHeadData timedHead;
                    try
                    {
                        timedHead = await initialHeadTask.ConfigureAwait(false);
                    }
                    catch when (timeoutBodySendException != null)
                    {
                        ExceptionDispatchInfo.Capture(timeoutBodySendException).Throw();
                        throw;
                    }

                    if (ShouldReturnExpectContinueHead(timedHead.StatusCode))
                    {
                        readerOwned = false;
                        return Http11ResponseParser.CreateParsedResponseHead(reader, timedHead);
                    }

                    interim1xxCount = IncrementExpectContinueInterimCount(interim1xxCount);

                    try
                    {
                        var finalHead = await Http11ResponseParser.ParseHeadAsync(
                                reader,
                                request.Method,
                                ct,
                                context,
                                initialInterim1xxCount: interim1xxCount)
                            .ConfigureAwait(false);
                        if (timeoutBodySendException != null)
                        {
                            using (finalHead)
                            {
                                ThrowCapturedBodySendException(
                                    timeoutBodySendException,
                                    context,
                                    responseStatusCode: finalHead.StatusCode);
                            }
                        }

                        readerOwned = false;
                        return finalHead;
                    }
                    catch (Exception responseReadException) when (timeoutBodySendException != null)
                    {
                        ThrowCapturedBodySendException(
                            timeoutBodySendException,
                            context,
                            responseReadException);
                        throw;
                    }
                }
            }
            finally
            {
                RecordRequestBodyBytesSent(context, requestWriteState);
                if (readerOwned)
                    reader.Dispose();
            }
        }

        private static bool ShouldReturnExpectContinueHead(HttpStatusCode statusCode)
        {
            int code = (int)statusCode;
            return code == 101 || code < 100 || code >= 200;
        }

        private static int IncrementExpectContinueInterimCount(int interim1xxCount)
        {
            interim1xxCount++;
            if (interim1xxCount > Http11ResponseParser.Max1xxResponses)
                throw new FormatException("Too many 1xx interim responses");

            return interim1xxCount;
        }

        private static void ThrowCapturedBodySendException(
            Exception bodySendException,
            RequestContext context,
            Exception responseReadException = null,
            HttpStatusCode? responseStatusCode = null)
        {
            if (bodySendException == null)
                return;

            if (responseReadException != null)
            {
                bodySendException.Data["TurboHTTP.ExpectContinue.ResponseReadFailureType"] =
                    responseReadException.GetType().FullName ?? responseReadException.GetType().Name;
                bodySendException.Data["TurboHTTP.ExpectContinue.ResponseReadFailureMessage"] =
                    responseReadException.Message;
            }

            Dictionary<string, object> eventData = null;
            if (context != null)
            {
                eventData = new Dictionary<string, object>
                {
                    ["bodyExceptionType"] = bodySendException.GetType().FullName ?? bodySendException.GetType().Name,
                    ["bodyExceptionMessage"] = bodySendException.Message
                };

                if (responseReadException != null)
                {
                    eventData["responseReadExceptionType"] =
                        responseReadException.GetType().FullName ?? responseReadException.GetType().Name;
                    eventData["responseReadExceptionMessage"] = responseReadException.Message;
                }

                if (responseStatusCode.HasValue)
                    eventData["responseStatusCode"] = (int)responseStatusCode.Value;

                context.RecordEvent("TransportExpectContinueBodySendFailed", eventData);
            }

            ExceptionDispatchInfo.Capture(bodySendException).Throw();
        }

        private async Task<Exception> TrySendRequestBodyAsync(
            Stream stream,
            UHttpRequest request,
            Http11RequestWriteState requestWriteState,
            CancellationToken ct)
        {
            try
            {
                // Body-send faults are captured so the expect-continue path can finish any
                // in-flight response parsing and then re-surface the original send failure.
                using var session = await request.Content.OpenReadSessionAsync(ct).ConfigureAwait(false);
                await Http11RequestSerializer.SerializeBodyAsync(
                        stream,
                        request.Content,
                        session,
                        ct,
                        requestWriteState,
                        _streamingOptions)
                    .ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static async Task<ParsedResponse> SendOnStreamAsync(
            Stream stream,
            UHttpRequest request,
            RequestContext context,
            Http11RequestWriteState requestWriteState,
            CancellationToken ct,
            StreamingOptions streamingOptions)
        {
            context.RecordEvent("TransportSending");
            try
            {
                await Http11RequestSerializer.SerializeAsync(
                        request,
                        stream,
                        ct,
                        requestWriteState,
                        streamingOptions)
                    .ConfigureAwait(false);
            }
            finally
            {
                RecordRequestBodyBytesSent(context, requestWriteState);
            }

            context.RecordEvent("TransportReceiving");
            var parsed = await Http11ResponseParser.ParseAsync(stream, request.Method, ct)
                .ConfigureAwait(false);

            context.RecordEvent("TransportComplete");
            return parsed;
        }

        private static void RecordRequestBodyBytesSent(
            RequestContext context,
            Http11RequestWriteState requestWriteState)
        {
            if (context == null || requestWriteState == null)
                return;

            context.SetState(
                TransportBehaviorFlags.RequestBodyBytesSent,
                requestWriteState.BodyBytesWritten);
        }

        internal bool HasHttp2Connection(string host, int port)
        {
            return _h2Manager != null && _h2Manager.HasConnection(host, port);
        }

        internal bool HasHttp2Connection(
            string originHost,
            int originPort,
            string proxyHost,
            int proxyPort)
        {
            return _h2Manager != null &&
                _h2Manager.HasConnection(originHost, originPort, proxyHost, proxyPort);
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
