using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
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
        private readonly Func<string, Task<IPAddress[]>> _dnsResolver;
        private int _disposed;

        public RawSocketWebSocketTransport(
            TlsBackend tlsBackend = TlsBackend.Auto,
            HappyEyeballsOptions happyEyeballsOptions = null)
            : this(tlsBackend, happyEyeballsOptions, null)
        {
        }

        /// <summary>
        /// Test seam: <paramref name="dnsResolver"/> replaces <see cref="Dns.GetHostAddressesAsync(string)"/>
        /// (null keeps it). Localhost names still never reach it (RFC 6761 Section 6.3).
        /// </summary>
        internal RawSocketWebSocketTransport(
            TlsBackend tlsBackend,
            HappyEyeballsOptions happyEyeballsOptions,
            Func<string, Task<IPAddress[]>> dnsResolver)
        {
            _tlsBackend = tlsBackend;
            _happyEyeballsOptions = happyEyeballsOptions?.Clone() ?? new HappyEyeballsOptions();
            _dnsResolver = dnsResolver;
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

            string wireHost = ValidateUri(uri);

            // #368: proxy bypass, the cleartext policy and DNS use the wire host (A-label for an IDN,
            // RFC 5890 Section 2.3.2.1; lookups use the A-label, RFC 5891 Section 5) without IPv6
            // brackets. The TLS server name keeps Uri.Host: the TLS providers normalise it (TlsHostName).
            string host = uri.Host;
            string lookupHost = GetLookupHost(uri, wireHost);
            bool secure = string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);
            int port = uri.IsDefaultPort
                ? (secure ? 443 : 80)
                : uri.Port;

            var proxySettings = options.ProxySettings ?? WebSocketProxySettings.None;
            bool useProxy = proxySettings.IsConfigured && !proxySettings.ShouldBypass(lookupHost);

            string connectHost = useProxy ? GetProxyLookupHost(proxySettings.ProxyUri) : lookupHost;
            int connectPort = useProxy
                ? (proxySettings.ProxyUri.IsDefaultPort ? 80 : proxySettings.ProxyUri.Port)
                : port;

            // #85: raw sockets are not covered by iOS App Transport Security or the Android
            // cleartext policy, so the policy is applied here, before any DNS lookup or socket.
            // An http:// proxy leg is cleartext even for a wss:// target (CONNECT and
            // Proxy-Authorization travel unencrypted).
            // The messages never contain the host (#309, #368).
            if (!secure && !CleartextTrafficPolicy.IsPermitted(lookupHost, options.AllowCleartext))
            {
                throw new WebSocketException(
                    WebSocketError.CleartextNotPermitted,
                    "Cleartext ws:// connection is not permitted. Use wss://, or set " +
                    "WebSocketConnectionOptions.AllowCleartext = true (the platform cleartext policy must also permit the host).");
            }

            if (useProxy && !CleartextTrafficPolicy.IsPermitted(connectHost, options.AllowCleartext))
            {
                throw new WebSocketException(
                    WebSocketError.CleartextNotPermitted,
                    "Cleartext connection to the http:// proxy is not permitted. Set " +
                    "WebSocketConnectionOptions.AllowCleartext = true (the platform cleartext policy must also permit the host).");
            }

            var addresses = await ResolveDnsAsync(connectHost, options.DnsTimeoutMs, _dnsResolver, ct).ConfigureAwait(false);

            Socket socket;
            try
            {
                // An IPv4 literal without an IPv4 route retries through NAT64 synthesis (#167).
                socket = await Nat64AddressSynthesizer.ConnectWithLiteralFallbackAsync(
                    connectHost,
                    addresses,
                    candidates => ConnectSocketAsync(candidates, connectPort, options.ConnectTimeout, ct),
                    name => ResolveDnsAsync(name, options.DnsTimeoutMs, _dnsResolver, ct),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                IsFailureFromCancellation(ex, ct) ||
                (useProxy && !(ex is WebSocketException) && !(ex is OperationCanceledException)))
            {
                // One clause: under IL2CPP an exception thrown by an earlier clause of a filtered try is
                // caught by a later one (#320).
                if (IsFailureFromCancellation(ex, ct))
                    throw new OperationCanceledException("WebSocket connect was canceled.", ex, ct);

                throw new WebSocketException(
                    WebSocketError.ProxyConnectionFailed,
                    "Failed to connect to the configured proxy endpoint.",
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
                catch (Exception ex)
                {
                    // One clause: under IL2CPP an exception thrown by an earlier clause of a filtered try
                    // is caught by a later one (#320).
                    stream.Dispose();
                    if (!(ex is WebSocketException) &&
                        !(ex is OperationCanceledException) &&
                        !ct.IsCancellationRequested)
                    {
                        throw new WebSocketException(
                            WebSocketError.ProxyTunnelFailed,
                            "Failed to establish HTTP CONNECT tunnel through proxy.",
                            ex);
                    }

                    // WebSocketException keeps its own error; cancellation is mapped by the caller.
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

        private static async Task<IPAddress[]> ResolveDnsAsync(
            string host,
            int timeoutMs,
            Func<string, Task<IPAddress[]>> resolver,
            CancellationToken ct)
        {
            // Localhost names are loopback without asking the resolver (RFC 6761 section 6.3, #322).
            if (LocalhostNames.IsLocalhostName(host))
                return LocalhostNames.GetLoopbackAddresses();

            var dnsTask = resolver != null ? resolver(host) : Dns.GetHostAddressesAsync(host);
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
                    "DNS resolution timed out after " + timeoutMs + "ms.");
            }

            return await dnsTask.ConfigureAwait(false);
        }

        /// <summary>
        /// The host proxy bypass, the cleartext policy and DNS see (#368): an IPv6 literal without
        /// brackets (as Unity Mono's <see cref="Uri.Host"/> returns it; .NET keeps the brackets),
        /// otherwise the wire host (A-label for an IDN, an ASCII name or IPv4 literal unchanged).
        /// </summary>
        internal static string GetLookupHost(Uri uri, string wireHost)
        {
            if (uri.HostNameType == UriHostNameType.IPv6)
                return StripBrackets(uri.Host);

            return wireHost;
        }

        /// <summary>
        /// The proxy's lookup host: its wire host like the target's, or <see cref="Uri.Host"/> when it
        /// has none (the proxy URI is only checked for a non-blank host; DNS then fails as before).
        /// </summary>
        private static string GetProxyLookupHost(Uri proxyUri)
        {
            return WireHost.TryFromUri(proxyUri, out var proxyWireHost, out _)
                ? GetLookupHost(proxyUri, proxyWireHost)
                : StripBrackets(proxyUri.Host);
        }

        private static string StripBrackets(string host)
        {
            return host.Length > 2 && host[0] == '[' && host[host.Length - 1] == ']'
                ? host.Substring(1, host.Length - 2)
                : host;
        }

        private static string ValidateUri(Uri uri)
        {
            if (!uri.IsAbsoluteUri)
                throw new ArgumentException("WebSocket URI must be absolute.", nameof(uri));

            if (!string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("WebSocket URI scheme must be 'ws' or 'wss'.", nameof(uri));
            }

            // #309/#340: the handshake Host header and the CONNECT authority carry the RFC 3986 wire
            // host. A host without one (empty, which Unity Mono returns for some IDNA-invalid names,
            // no A-label form, or not an IP literal or DNS name) fails the handshake here, before
            // the cleartext policy, DNS or any socket. A WebSocketException keeps its error through
            // WebSocketConnection; the message does not contain the host.
            if (!WireHost.TryFromUri(uri, out var wireHost, out var hostError))
                throw new WebSocketException(WebSocketError.HandshakeFailed, hostError);

            return wireHost;
        }
    }
}
