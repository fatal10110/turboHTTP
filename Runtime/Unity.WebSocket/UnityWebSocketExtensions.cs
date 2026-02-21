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

        public static async Task<IWebSocketClient> WebSocket(
            this UHttpClient client,
            Uri uri,
            CancellationToken ct = default)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));

            var options = BuildWebSocketOptions(client);
            var bridge = new UnityWebSocketBridge(WebSocketClient.Create(options));
            await bridge.ConnectAsync(uri, options, ct).ConfigureAwait(false);
            return bridge;
        }

        private static WebSocketConnectionOptions BuildWebSocketOptions(UHttpClient client)
        {
            var options = new WebSocketConnectionOptions();
            var clientOptions = client.GetOptionsSnapshot();

            options.TlsBackend = clientOptions.TlsBackend;
            options.CustomHeaders = clientOptions.DefaultHeaders?.Clone() ?? new HttpHeaders();

            return options;
        }
    }
}
