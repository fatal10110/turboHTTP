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
        /// Not needed in normal usage: TurboHTTP's startup bootstrap (TurboHTTP.Complete) calls it
        /// at <c>RuntimeInitializeLoadType.SubsystemRegistration</c>. Call it at startup when that
        /// assembly is excluded from the build, or after <see cref="HttpTransportFactory.Reset"/>.
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
                            socketIoMode: poolOptions.SocketIoMode,
                            connectTimeout: poolOptions.ConnectTimeout),
                        tlsBackend: tlsBackend,
                        http2Options: http2Options,
                        streamingOptions: streamingOptions));
            HttpTransportFactory.RegisterCapabilities(ProbeCapabilities);
        }

        // HTTP/2 needs ALPN: SslStream exposes it on this runtime, or the BouncyCastle provider is
        // present. Read lazily (HttpTransportFactory.Capabilities), so registration at startup does
        // not initialize the TLS providers.
        private static TransportCapabilities ProbeCapabilities()
        {
            return SslStreamTlsProvider.Instance.IsAlpnSupported() || TlsProviderSelector.IsBouncyCastleAvailable()
                ? TransportCapabilities.Http2
                : TransportCapabilities.None;
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

            if (request.Method.IsIdempotent())
                return true;

            // RFC 9110 sec 9.2.2: a non-idempotent request is replayed only while the server
            // cannot have received it completely: no body byte committed and, for a bodiless
            // request, the head not fully written.
            return writeState != null &&
                !writeState.HasCommittedBodyBytes &&
                !writeState.HasWrittenCompleteMessage;
        }

        /// <summary>
        /// Disposes the connection (stream and socket) when <paramref name="ct"/> fires. Mono and
        /// IL2CPP <see cref="NetworkStream"/> ignore the token once a read is pending, so a peer that
        /// accepts and never writes would otherwise hold the request, the lease and the per-host and
        /// global permits past the request timeout (#162). The caller disposes the registration
        /// before the connection is pooled or handed to a response body source.
        /// </summary>
        private static CancellationTokenRegistration RegisterDisposeOnCancel(
            PooledConnection connection,
            CancellationToken ct)
        {
            if (!ct.CanBeCanceled)
                return default;

            return ct.Register(static state =>
            {
                var pooled = (PooledConnection)state;
                try { pooled.Dispose(); } catch { /* best effort */ }
                try { pooled.Socket?.Dispose(); } catch { /* best effort */ }
            }, connection);
        }

        /// <summary>
        /// A read or write that fails after <see cref="RegisterDisposeOnCancel"/> tore the connection
        /// down surfaces as ObjectDisposedException, IOException or a platform-specific error; it is
        /// a cancellation, which the dispatch catch ladder maps to Timeout or OperationCanceled.
        /// </summary>
        private static bool IsFailureFromCancellation(Exception ex, CancellationToken ct)
        {
            return ct.IsCancellationRequested && !(ex is OperationCanceledException);
        }

        private static bool IsStaleReusedConnectionFailure(Exception ex)
        {
            // RFC 9112 sec 9.3.1: a persistent connection the server closed while idle shows up
            // as a write/read fault or as EOF before the first status-line byte.
            return ex is IOException ||
                ex is SocketException ||
                IsConnectionClosedBeforeStatusLine(ex);
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

            // #85: raw sockets are not covered by iOS App Transport Security or the Android
            // cleartext policy, so the policy is applied here, before any socket. The leg to an
            // http:// proxy is cleartext even for an https:// target.
            var allowCleartext = ResolveAllowCleartext(request);
            if (!secure && !CleartextTrafficPolicy.IsPermitted(host, allowCleartext))
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.InvalidRequest,
                    $"Cleartext http:// request to '{host}' is not permitted. Use https://, or set " +
                    "UHttpClientOptions.AllowCleartext = true (the platform cleartext policy must also permit the host)."));
            }

            if (proxy?.Address != null && !CleartextTrafficPolicy.IsPermitted(proxy.Address.Host, allowCleartext))
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.InvalidRequest,
                    $"Cleartext connection to http:// proxy '{proxy.Address.Host}' is not permitted. Set " +
                    "UHttpClientOptions.AllowCleartext = true (the platform cleartext policy must also permit the host)."));
            }

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
                        IsStaleReusedConnectionFailure(ex) &&
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
                    IsStaleReusedConnectionFailure(ex) &&
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

                        if (establishTunnel &&
                            freshLease.Connection.NegotiatedAlpnProtocol == "h2")
                        {
                            context.RecordEvent("TransportProxyH2Init");
                            freshLease.TransferOwnership();
                            var h2Conn = await _h2Manager.GetOrCreateAsync(
                                    targetHost,
                                    targetPort,
                                    proxyHost,
                                    proxyPort,
                                    freshLease.Connection.Stream,
                                    ct)
                                .ConfigureAwait(false);
                            freshLease = null;

                            await h2Conn.DispatchAsync(dispatchRequest, handler, context, ct)
                                .ConfigureAwait(false);
                            return;
                        }

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
                TlsResult tlsResult;
                using (RegisterDisposeOnCancel(lease.Connection, ct))
                {
                    try
                    {
                        tlsResult = await EstablishConnectTunnelAsync(
                                lease.Connection.Stream,
                                targetHost,
                                targetPort,
                                proxy,
                                ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsFailureFromCancellation(ex, ct))
                    {
                        throw new OperationCanceledException("Proxy CONNECT tunnel setup was cancelled.", ex, ct);
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    // The token fired after the handshake; the proxy socket may already be disposed.
                    try { tlsResult.SecureStream?.Dispose(); } catch { /* best effort */ }
                    ct.ThrowIfCancellationRequested();
                }
                RebindConnectTunnelLease(lease, targetHost, targetPort, poolKeyOverride, tlsResult);
                return lease;
            }
            catch
            {
                lease?.Dispose();
                throw;
            }
        }

        private static bool ResolveAllowCleartext(UHttpRequest request)
        {
            if (request?.Metadata != null &&
                request.Metadata.TryGetValue(RequestMetadataKeys.AllowCleartext, out var value) &&
                value is bool allow)
            {
                return allow;
            }

            return CleartextTrafficPolicy.DefaultAllowCleartext;
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

                // The reader stops at the end of the header block, so the tunnel's first bytes
                // stay on the stream for the TLS handshake.
                var response = await ConnectReplyReader.ReadAsync(proxyStream, ct).ConfigureAwait(false);

                // RFC 9110 section 9.3.6: any 2xx switches to tunnel mode right after the header
                // block; Content-Length / Transfer-Encoding in a 2xx reply are ignored (#44).
                if (response.IsTunnelEstablished)
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

                // RFC 9110 section 11.7.1 / RFC 7617: Basic credentials answer only a Basic
                // challenge (#85). The proxy leg itself already passed the cleartext policy.
                var offersBasic = response.StatusCode == 407 &&
                    AuthChallengeParser.OffersScheme(response.Headers.GetValues("Proxy-Authenticate"), "Basic");

                if (response.StatusCode == 407 &&
                    !attemptedAuth &&
                    offersBasic &&
                    proxy?.Credentials != null &&
                    proxy.AllowPlaintextProxyAuth)
                {
                    // Consume the 407 body so the retried CONNECT reads its own reply.
                    await ConnectReplyReader.DrainBodyAsync(proxyStream, response, ct).ConfigureAwait(false);
                    attemptedAuth = true;
                    continue;
                }

                if (response.StatusCode == 407)
                {
                    // Distinguish the two failure modes so operators can diagnose correctly:
                    //   - Credentials supplied but AllowPlaintextProxyAuth is disabled → retry was
                    //     never attempted; tell the caller how to enable it.
                    //   - Credentials were submitted (attemptedAuth = true) but rejected by the proxy.
                    //   - No credentials were configured at all.
                    //   - The proxy does not offer the Basic scheme, so credentials were not sent.
                    string reason;
                    if (proxy?.Credentials != null && !attemptedAuth && !offersBasic)
                        reason = "the proxy does not offer the Basic scheme, so credentials were not sent";
                    else if (proxy?.Credentials != null && !proxy.AllowPlaintextProxyAuth)
                        reason = "credentials were supplied but AllowPlaintextProxyAuth is disabled";
                    else
                        reason = "credentials were not provided or were rejected";

                    throw new UHttpException(new UHttpError(
                        UHttpErrorType.InvalidRequest,
                        $"Proxy authentication required (407); {reason}."));
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
            ParsedResponseHead parsedHead;
            using (RegisterDisposeOnCancel(lease.Connection, ct))
            {
                try
                {
                    parsedHead = await SendOnLeaseAsync(lease, request, context, requestWriteState, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (IsFailureFromCancellation(ex, ct))
                {
                    throw new OperationCanceledException("HTTP/1.1 response header read was cancelled.", ex, ct);
                }
            }

            // Disposing the registration waits for a running callback; if the token fired after
            // the head was parsed, the connection may already be gone, so it must not reach the
            // body source or the pool.
            if (ct.IsCancellationRequested)
            {
                parsedHead.Dispose();
                ct.ThrowIfCancellationRequested();
            }

            using var head = parsedHead;

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
            Task pendingHeadRead = null;
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
                    pendingHeadRead = initialHeadTask;

                    var winner = await Task.WhenAny(timeoutTask, initialHeadTask).ConfigureAwait(false);
                    if (winner == initialHeadTask)
                    {
                        var initialHead = await initialHeadTask.ConfigureAwait(false);
                        if (ShouldReturnExpectContinueHead(initialHead.StatusCode))
                        {
                            // Early final response: no body byte was sent.
                            timeoutCts.Cancel();
                            readerOwned = false;
                            return CompleteExpectContinueHead(
                                Http11ResponseParser.CreateParsedResponseHead(reader, initialHead),
                                bodyComplete: false,
                                bodySendException: null,
                                context);
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

                        var continuedHead = await ReadExpectContinueFinalHeadAsync(
                                reader,
                                request,
                                context,
                                interim1xxCount,
                                bodySendException,
                                ct)
                            .ConfigureAwait(false);

                        // A throw inside the helper disposes the head and its reader; the finally block's
                        // reader.Dispose is then a no-op (Dispose is idempotent).
                        readerOwned = false;
                        return continuedHead;
                    }

                    // This is the caller's request-scoped cancellation token, not timeoutCts.Token.
                    // The expect-continue timeout already expired if we reached this branch.
                    // A cancellation here leaves initialHeadTask pending; the finally block closes
                    // the connection and waits for that read before the reader buffer is returned.
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
                        return CompleteExpectContinueHead(
                            Http11ResponseParser.CreateParsedResponseHead(reader, timedHead),
                            bodyComplete: timeoutBodySendException == null,
                            timeoutBodySendException,
                            context);
                    }

                    interim1xxCount = IncrementExpectContinueInterimCount(interim1xxCount);

                    var finalHead = await ReadExpectContinueFinalHeadAsync(
                            reader,
                            request,
                            context,
                            interim1xxCount,
                            timeoutBodySendException,
                            ct)
                        .ConfigureAwait(false);

                    // A throw inside the helper disposes the head and its reader; the finally block's
                    // reader.Dispose is then a no-op (Dispose is idempotent).
                    readerOwned = false;
                    return finalHead;
                }
            }
            finally
            {
                RecordRequestBodyBytesSent(context, requestWriteState);
                if (readerOwned && pendingHeadRead != null && !pendingHeadRead.IsCompleted)
                {
                    // A head read is still in flight (caller cancellation during the
                    // expect-continue wait; some platform streams ignore the token once a read
                    // started). Close the connection so the read faults, and return the pooled
                    // buffer only after the read stopped writing into it. A read that outlives
                    // the bounded wait keeps its buffer: it is abandoned to the GC, never pooled.
                    lease.Dispose();
                    readerOwned = await WaitForAbortedReadAsync(pendingHeadRead).ConfigureAwait(false);
                }

                if (readerOwned)
                    reader.Dispose();
            }
        }

        private static async Task<bool> WaitForAbortedReadAsync(Task read)
        {
            using var delayCts = new CancellationTokenSource();
            var winner = await Task.WhenAny(read, Task.Delay(AbortedReadDrainTimeout, delayCts.Token))
                .ConfigureAwait(false);
            delayCts.Cancel();
            if (winner == read)
            {
                _ = read.Exception;
                return true;
            }

            _ = read.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return false;
        }

        private static async Task<ParsedResponseHead> ReadExpectContinueFinalHeadAsync(
            Http11ResponseParser.BufferedStreamReader reader,
            UHttpRequest request,
            RequestContext context,
            int interim1xxCount,
            Exception bodySendException,
            CancellationToken ct)
        {
            ParsedResponseHead finalHead;
            try
            {
                finalHead = await Http11ResponseParser.ParseHeadAsync(
                        reader,
                        request.Method,
                        ct,
                        context,
                        initialInterim1xxCount: interim1xxCount)
                    .ConfigureAwait(false);
            }
            catch (Exception responseReadException) when (bodySendException != null)
            {
                ThrowCapturedBodySendException(
                    bodySendException,
                    context,
                    responseReadException);
                throw;
            }

            return CompleteExpectContinueHead(
                finalHead,
                bodyComplete: bodySendException == null,
                bodySendException,
                context);
        }

        /// <summary>
        /// Applies the single "send failure vs early final response" rule of the expect-continue
        /// path (phase 22b.1). A request whose body was not completely sent never leaves its
        /// connection reusable: the server may still read and discard the declared body
        /// (RFC 9110 sec 10.1.1), so the next request would be consumed as body (RFC 9112 sec 9.3,
        /// 9.6). A non-2xx final response is the server declining the request and is returned to the
        /// caller (RFC 9112 sec 9.5); a 1xx/2xx head cannot vouch for a request whose body failed
        /// to send, so the send failure surfaces instead.
        /// </summary>
        private static ParsedResponseHead CompleteExpectContinueHead(
            ParsedResponseHead head,
            bool bodyComplete,
            Exception bodySendException,
            RequestContext context)
        {
            if (bodyComplete)
                return head;

            head.KeepAlive = false;
            if (bodySendException == null)
                return head;

            if ((int)head.StatusCode < 300)
            {
                using (head)
                {
                    ThrowCapturedBodySendException(
                        bodySendException,
                        context,
                        responseStatusCode: head.StatusCode);
                }
            }

            RecordExpectContinueBodySendFailure(
                bodySendException,
                context,
                responseReadException: null,
                responseStatusCode: head.StatusCode);
            return head;
        }

        // Upper bound on waiting for a read the transport aborted by closing its connection.
        private static readonly TimeSpan AbortedReadDrainTimeout = TimeSpan.FromSeconds(2);

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

            RecordExpectContinueBodySendFailure(
                bodySendException,
                context,
                responseReadException,
                responseStatusCode);
            ExceptionDispatchInfo.Capture(bodySendException).Throw();
        }

        private static void RecordExpectContinueBodySendFailure(
            Exception bodySendException,
            RequestContext context,
            Exception responseReadException,
            HttpStatusCode? responseStatusCode)
        {
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
