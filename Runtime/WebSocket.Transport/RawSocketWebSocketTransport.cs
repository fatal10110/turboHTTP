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

            var addresses = await ResolveDnsAsync(host, options.DnsTimeoutMs, ct).ConfigureAwait(false);

            var socket = await ConnectSocketAsync(addresses, port, options.ConnectTimeout, ct)
                .ConfigureAwait(false);

            Stream stream = new NetworkStream(socket, ownsSocket: true);
            if (Volatile.Read(ref _disposed) != 0)
            {
                stream.Dispose();
                throw new ObjectDisposedException(nameof(RawSocketWebSocketTransport));
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
