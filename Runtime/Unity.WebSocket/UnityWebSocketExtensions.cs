using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Unity.WebSocket
{
    /// <summary>
    /// Unity-focused WebSocket extension helpers for UHttpClient.
    /// </summary>
    public static class UnityWebSocketExtensions
    {
        // Default headers a handshake inherits (#950, #605). Everything else stays on the HTTP client:
        // credentials (Authorization, Cookie, API-key headers: RFC 9110 section 11.6.2, RFC 6265 section 5.4)
        // would reach whatever host the WebSocket URL names, Proxy-Authorization belongs to the proxy hop
        // (RFC 9110 section 11.7.2), and Host, Connection, Upgrade and Sec-WebSocket-* are fixed by the
        // handshake (RFC 6455 section 4.1).
        private static readonly string[] InheritedDefaultHeaders = { "User-Agent", "Accept-Language", "Origin" };

        /// <summary>
        /// Connects a WebSocket to <paramref name="url"/> with settings inherited from <paramref name="client"/>
        /// and returns it once the handshake completes; its events are raised on the Unity main thread.
        /// See <see cref="WebSocket(UHttpClient, Uri, CancellationToken)"/> for what is inherited.
        /// </summary>
        /// <param name="client">The HTTP client whose settings the connection inherits.</param>
        /// <param name="url">An absolute <c>ws://</c> or <c>wss://</c> URL.</param>
        /// <param name="ct">Cancels the connect.</param>
        /// <returns>The connected client (a <see cref="UnityWebSocketBridge"/>).</returns>
        /// <exception cref="ArgumentException"><paramref name="url"/> is empty or not an absolute URL.</exception>
        public static Task<IWebSocketClient> WebSocket(
            this UHttpClient client,
            string url,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("WebSocket URL cannot be null or empty.", nameof(url));

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new ArgumentException("WebSocket URL is invalid.", nameof(url));

            return WebSocket(client, uri, ct);
        }

        /// <summary>
        /// Connects a WebSocket to <paramref name="uri"/> with settings inherited from <paramref name="client"/>
        /// and returns it once the handshake completes; its events are raised on the Unity main thread.
        /// </summary>
        /// <remarks>
        /// <para>Inherited from <see cref="UHttpClientOptions"/>: <c>TlsBackend</c>, <c>AllowCleartext</c>, the
        /// <c>User-Agent</c>, <c>Accept-Language</c> and <c>Origin</c> entries of <c>DefaultHeaders</c> (no other
        /// default header, so credentials such as <c>Authorization</c>, <c>Cookie</c> or API keys are never sent),
        /// and <c>Proxy</c>: the proxy, its bypass list and the proxy environment variables are resolved for
        /// <paramref name="uri"/> as for an HTTP request to the same host (<c>wss</c> as <c>https</c>), and the
        /// proxy credentials are used only when <see cref="ProxySettings.AllowPlaintextProxyAuth"/> is set. To
        /// send other headers, create the client with <c>WebSocketClient.Create(options)</c> and wrap it in a
        /// <see cref="UnityWebSocketBridge"/>.</para>
        /// <para>The connection is open when the task completes, so <c>OnConnected</c> has already been raised
        /// and a handler attached afterwards does not see it. Messages received before the first
        /// <c>OnMessage</c> subscription are held and delivered to that subscriber
        /// (<see cref="UnityWebSocketBridge.OnMessage"/>).</para>
        /// </remarks>
        /// <param name="client">The HTTP client whose settings the connection inherits.</param>
        /// <param name="uri">An absolute <c>ws://</c> or <c>wss://</c> URI.</param>
        /// <param name="ct">Cancels the connect.</param>
        /// <returns>The connected client (a <see cref="UnityWebSocketBridge"/>).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="uri"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="uri"/> is not absolute.</exception>
        /// <exception cref="UHttpException">A proxy environment variable names a proxy that is not
        /// <c>http://</c>.</exception>
        /// <exception cref="WebSocketException">The connect failed; the created client is disposed.</exception>
        public static async Task<IWebSocketClient> WebSocket(
            this UHttpClient client,
            Uri uri,
            CancellationToken ct = default)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));
            if (!uri.IsAbsoluteUri)
                throw new ArgumentException("WebSocket URI must be absolute.", nameof(uri));

            var options = BuildWebSocketOptions(client, uri);
            var bridge = new UnityWebSocketBridge(WebSocketClient.Create(options), bufferMessagesUntilSubscribed: true);
            try
            {
                await bridge.ConnectAsync(uri, options, ct).ConfigureAwait(false);
            }
            catch
            {
                await bridge.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return bridge;
        }

        internal static WebSocketConnectionOptions BuildWebSocketOptions(UHttpClient client, Uri uri)
        {
            var options = new WebSocketConnectionOptions();
            var clientOptions = client.GetOptionsSnapshot();

            options.TlsBackend = clientOptions.TlsBackend;
            options.AllowCleartext = clientOptions.AllowCleartext;
            options.CustomHeaders = InheritDefaultHeaders(clientOptions.DefaultHeaders);
            options.ProxySettings = MapProxy(uri, clientOptions.Proxy);

            return options;
        }

        private static HttpHeaders InheritDefaultHeaders(HttpHeaders defaults)
        {
            var headers = new HttpHeaders();
            if (defaults == null)
                return headers;

            for (int i = 0; i < InheritedDefaultHeaders.Length; i++)
            {
                var name = InheritedDefaultHeaders[i];
                var values = defaults.GetValues(name);
                for (int j = 0; j < values.Count; j++)
                    headers.Add(name, values[j]);
            }

            return headers;
        }

        /// <summary>
        /// The client's proxy decision for <paramref name="uri"/>, made by the resolver an HTTP request to the
        /// same host and port uses (the configured proxy with its full bypass syntax, else
        /// <c>HTTP(S)_PROXY</c>/<c>NO_PROXY</c>), so the result needs no bypass list of its own.
        /// </summary>
        private static WebSocketProxySettings MapProxy(Uri uri, ProxySettings configured)
        {
            bool secure = string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            var httpTarget = new UriBuilder(uri)
            {
                Scheme = secure ? "https" : "http",
                Port = uri.Port
            }.Uri;

            var resolved = ProxyEnvironmentResolver.Resolve(httpTarget, configured);
            if (resolved?.Address == null)
                return WebSocketProxySettings.None;

            // The leg to an http:// proxy is cleartext: like the HTTP client, send Basic credentials only when
            // the client allows plaintext proxy authentication.
            ProxyCredentials? credentials = null;
            if (resolved.Credentials != null && resolved.AllowPlaintextProxyAuth)
                credentials = new ProxyCredentials(resolved.Credentials.UserName, resolved.Credentials.Password);

            return new WebSocketProxySettings(resolved.Address, credentials);
        }
    }
}
