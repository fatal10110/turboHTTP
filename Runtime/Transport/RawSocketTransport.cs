using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Tcp;

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
        private readonly Http2ConnectionManager _h2Manager = new Http2ConnectionManager();
        private volatile bool _disposed;

        public RawSocketTransport(TcpConnectionPool pool = null)
        {
            _pool = pool ?? new TcpConnectionPool();
        }

        /// <summary>
        /// Explicitly register this transport with <see cref="HttpTransportFactory"/>.
        /// Not needed in normal usage (module initializer handles it).
        /// Useful for tests that call <see cref="HttpTransportFactory.Reset"/>.
        /// </summary>
        public static void EnsureRegistered()
        {
            HttpTransportFactory.Register(() => new RawSocketTransport());
        }

        public async Task<UHttpResponse> SendAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RawSocketTransport));

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

            // 3. Record transport start
            context.RecordEvent("TransportStart");

            try
            {
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
            catch (IOException ex)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError, ex.Message, ex));
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
                request: request);
        }

        public void Dispose()
        {
            _disposed = true;
            _h2Manager?.Dispose();
            _pool?.Dispose();
        }
    }
}
