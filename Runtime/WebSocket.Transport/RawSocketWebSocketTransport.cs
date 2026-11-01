using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport.Connection;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.WebSocket.Transport
{
    /// <summary>
    /// Raw socket WebSocket transport for ws:// and wss:// endpoints.
    /// </summary>
    public sealed class RawSocketWebSocketTransport : IWebSocketTransport
    {
        private readonly TlsBackend _tlsBackend;
        private readonly HappyEyeballsOptions _happyEyeballsOptions;
        private int _disposed;

        public RawSocketWebSocketTransport(
            TlsBackend tlsBackend = TlsBackend.Auto,
            HappyEyeballsOptions happyEyeballsOptions = null)
        {
            _tlsBackend = tlsBackend;
            _happyEyeballsOptions = happyEyeballsOptions?.Clone() ?? new HappyEyeballsOptions();
        }

        /// <summary>
        /// Explicitly registers this transport as the default WebSocket transport factory.
        /// Not needed in normal usage: TurboHTTP's startup bootstrap (TurboHTTP.Complete) calls it
        /// at <c>RuntimeInitializeLoadType.SubsystemRegistration</c>. Call it at startup when that
        /// assembly is excluded from the build.
        /// </summary>
        public static void EnsureRegistered()
        {
            WebSocketTransportFactory.Register(
                tlsBackend => new RawSocketWebSocketTransport(tlsBackend));
        }

        public async Task<Stream> ConnectAsync(
            Uri uri,
            WebSocketConnectionOptions options,
            CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(RawSocketWebSocketTransport));

            if (uri == null)
                throw new ArgumentNullException(nameof(uri));

            options = options?.Clone() ?? new WebSocketConnectionOptions();
            options.Validate();

            ValidateUri(uri);

            string host = uri.Host;
            bool secure = string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);
            int port = uri.IsDefaultPort
                ? (secure ? 443 : 80)
                : uri.Port;

            var proxySettings = options.ProxySettings ?? WebSocketProxySettings.None;
            bool useProxy = proxySettings.IsConfigured && !proxySettings.ShouldBypass(host);

            string connectHost = useProxy ? proxySettings.ProxyUri.Host : host;
            int connectPort = useProxy
                ? (proxySettings.ProxyUri.IsDefaultPort ? 80 : proxySettings.ProxyUri.Port)
                : port;

            // #85: raw sockets are not covered by iOS App Transport Security or the Android
            // cleartext policy, so the policy is applied here, before any DNS lookup or socket.
            // An http:// proxy leg is cleartext even for a wss:// target (CONNECT and
            // Proxy-Authorization travel unencrypted).
            if (!secure && !CleartextTrafficPolicy.IsPermitted(host, options.AllowCleartext))
            {
                throw new WebSocketException(
                    WebSocketError.CleartextNotPermitted,
                    "Cleartext ws:// connection to '" + host + "' is not permitted. Use wss://, or set " +
                    "WebSocketConnectionOptions.AllowCleartext = true (the platform cleartext policy must also permit the host).");
            }

            if (useProxy && !CleartextTrafficPolicy.IsPermitted(connectHost, options.AllowCleartext))
            {
                throw new WebSocketException(
                    WebSocketError.CleartextNotPermitted,
                    "Cleartext connection to http:// proxy '" + connectHost + "' is not permitted. Set " +
                    "WebSocketConnectionOptions.AllowCleartext = true (the platform cleartext policy must also permit the host).");
            }

            var addresses = await ResolveDnsAsync(connectHost, options.DnsTimeoutMs, ct).ConfigureAwait(false);

            Socket socket;
            try
            {
                // An IPv4 literal without an IPv4 route retries through NAT64 synthesis (#167).
                socket = await Nat64AddressSynthesizer.ConnectWithLiteralFallbackAsync(
                    connectHost,
                    addresses,
                    candidates => ConnectSocketAsync(candidates, connectPort, options.ConnectTimeout, ct),
                    name => ResolveDnsAsync(name, options.DnsTimeoutMs, ct),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFailureFromCancellation(ex, ct))
            {
                throw new OperationCanceledException("WebSocket connect was canceled.", ex, ct);
            }
            catch (Exception ex) when (useProxy && !(ex is WebSocketException) && !(ex is OperationCanceledException))
            {
                throw new WebSocketException(
                    WebSocketError.ProxyConnectionFailed,
                    "Failed to connect to configured proxy endpoint '" + proxySettings.ProxyUri + "'.",
                    ex);
            }

            Stream stream = new NetworkStream(socket, ownsSocket: true);

            // #226 (same pattern as #162 in RawSocketTransport): Mono/IL2CPP NetworkStream ignores the
            // token once a read is pending, so a proxy that never answers CONNECT or a server that
            // stalls the TLS handshake would hold this call past the handshake timeout. Disposing the
            // stream (and the socket it owns) on cancellation faults the pending read; the fault is
            // reported as cancellation, never as a proxy or tunnel failure.
            Stream connected;
            var disposeOnCancel = RegisterDisposeOnCancel(stream, socket, ct);
            try
            {
                connected = await EstablishStreamAsync(
                    stream, uri, host, secure, useProxy, proxySettings, options, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFailureFromCancellation(ex, ct))
            {
                stream.Dispose();
                throw new OperationCanceledException("WebSocket connect was canceled.", ex, ct);
            }
            finally
            {
                disposeOnCancel.Dispose();
            }

            if (ct.IsCancellationRequested)
            {
                // The token fired after the last read completed: the callback may already have
                // disposed the stream, so it is never handed out.
                connected.Dispose();
                stream.Dispose();
                throw new OperationCanceledException("WebSocket connect was canceled.", ct);
            }

            return connected;
        }

        private async Task<Stream> EstablishStreamAsync(
            Stream stream,
            Uri uri,
            string host,
            bool secure,
            bool useProxy,
            WebSocketProxySettings proxySettings,
            WebSocketConnectionOptions options,
            CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                stream.Dispose();
                throw new ObjectDisposedException(nameof(RawSocketWebSocketTransport));
            }

            if (useProxy)
            {
                try
                {
                    stream = await ProxyTunnelConnector.EstablishAsync(
                            stream, uri, proxySettings, options.AllowCleartext, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is WebSocketException) &&
                                           !(ex is OperationCanceledException) &&
                                           !ct.IsCancellationRequested)
                {
                    stream.Dispose();
                    throw new WebSocketException(
                        WebSocketError.ProxyTunnelFailed,
                        "Failed to establish HTTP CONNECT tunnel through proxy.",
                        ex);
                }
                catch
                {
                    // WebSocketException keeps its own error; cancellation is mapped by the caller.
                    stream.Dispose();
                    throw;
                }
            }

            if (!secure)
                return stream;

            ITlsProvider tlsProvider = ResolveTlsProvider(options);

            try
            {
                // IMPORTANT: WebSocket uses HTTP/1.1 Upgrade; ALPN must remain empty.
                var tlsResult = await tlsProvider.WrapAsync(
                    stream,
                    host,
                    Array.Empty<string>(),
                    ct).ConfigureAwait(false);

                var secureStream = tlsResult.SecureStream;
                if (Volatile.Read(ref _disposed) != 0)
                {
                    secureStream.Dispose();
                    throw new ObjectDisposedException(nameof(RawSocketWebSocketTransport));
                }

                return secureStream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static CancellationTokenRegistration RegisterDisposeOnCancel(
            Stream stream,
            Socket socket,
            CancellationToken ct)
        {
            if (!ct.CanBeCanceled)
                return default;

            // Connect path only (not per frame): the boxed pair is one allocation per connect.
            return ct.Register(static state =>
            {
                var (ownedStream, ownedSocket) = ((Stream, Socket))state;
                try { ownedStream.Dispose(); } catch { }
                try { ownedSocket.Dispose(); } catch { }
            }, (stream, socket));
        }

        /// <summary>
        /// True when <paramref name="ex"/> is the fault a dispose-on-cancel produced (an
        /// <see cref="ObjectDisposedException"/>, <see cref="IOException"/>, socket or TLS error, or a
        /// proxy/tunnel error wrapping one), so the caller reports cancellation instead.
        /// </summary>
        private static bool IsFailureFromCancellation(Exception ex, CancellationToken ct)
        {
            return ct.IsCancellationRequested && !(ex is OperationCanceledException);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
        }

        private ITlsProvider ResolveTlsProvider(WebSocketConnectionOptions options)
        {
            if (options?.TlsProvider == null)
                return TlsProviderSelector.GetProvider(options?.TlsBackend ?? _tlsBackend);

            if (options.TlsProvider is ITlsProvider provider)
                return provider;

            throw new ArgumentException(
                "WebSocketConnectionOptions.TlsProvider must implement TurboHTTP.Transport.Tls.ITlsProvider.",
                nameof(options));
        }

        private async Task<Socket> ConnectSocketAsync(
            IPAddress[] addresses,
            int port,
            TimeSpan connectTimeout,
            CancellationToken ct)
        {
            try
            {
                return await HappyEyeballsConnector.ConnectAsync(
                    addresses,
                    port,
                    connectTimeout,
                    _happyEyeballsOptions,
                    ct).ConfigureAwait(false);
            }
            catch (AggregateException aggregate) when (aggregate.InnerExceptions.Count > 0)
            {
                throw aggregate.InnerExceptions[0];
            }
        }

        private static async Task<IPAddress[]> ResolveDnsAsync(string host, int timeoutMs, CancellationToken ct)
        {
            var dnsTask = Dns.GetHostAddressesAsync(host);
            var timeoutTask = Task.Delay(timeoutMs, ct);

            var completed = await Task.WhenAny(dnsTask, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                _ = dnsTask.ContinueWith(
                    static t => { _ = t.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.NotOnRanToCompletion,
                    TaskScheduler.Default);

                ct.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    "DNS resolution timed out after " + timeoutMs + "ms for host '" + host + "'.");
            }

            return await dnsTask.ConfigureAwait(false);
        }

        private static void ValidateUri(Uri uri)
        {
            if (!uri.IsAbsoluteUri)
                throw new ArgumentException("WebSocket URI must be absolute.", nameof(uri));

            if (!string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("WebSocket URI scheme must be 'ws' or 'wss'.", nameof(uri));
            }

            if (string.IsNullOrWhiteSpace(uri.Host))
                throw new ArgumentException("WebSocket URI host is required.", nameof(uri));
        }
    }
}
