using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;
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
                // 4. Get connection lease (semaphore permit owned by lease)
                context.RecordEvent("TransportConnecting");
                using var lease = await _pool.GetConnectionAsync(host, port, secure, ct)
                    .ConfigureAwait(false);

                try
                {
                    var parsed = await SendOnLeaseAsync(lease, request, context, ct)
                        .ConfigureAwait(false);
                    if (parsed.KeepAlive) lease.ReturnToPool();
                    return BuildResponse(parsed, context, request);
                }
                catch (IOException) when (lease.Connection.IsReused && request.Method.IsIdempotent())
                {
                    // Stale connection — dispose and retry once.
                    // ONLY retry idempotent methods (GET, HEAD, PUT, DELETE, OPTIONS).
                    // Retrying POST/PATCH on a stale connection could cause duplicate side effects
                    // if the server received and processed the request before closing the connection.
                    lease.Dispose(); // Idempotent — using var will call again at exit (no-op)

                    context.RecordEvent("TransportRetryStale");
                    context.RecordEvent("TransportConnecting");

                    using var freshLease = await _pool.GetConnectionAsync(host, port, secure, ct)
                        .ConfigureAwait(false);
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
            _pool?.Dispose();
        }
    }
}
