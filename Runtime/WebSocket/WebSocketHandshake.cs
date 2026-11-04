using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Represents a serialized WebSocket upgrade request and validation context.
    /// </summary>
    public sealed class WebSocketHandshakeRequest
    {
        internal WebSocketHandshakeRequest(
            Uri uri,
            string clientKey,
            string requestTarget,
            string hostHeader,
            IReadOnlyList<string> requestedSubProtocols,
            IReadOnlyList<string> requestedExtensions,
            byte[] requestBytes)
        {
            Uri = uri;
            ClientKey = clientKey;
            RequestTarget = requestTarget;
            HostHeader = hostHeader;
            RequestedSubProtocols = requestedSubProtocols;
            RequestedExtensions = requestedExtensions;
            RequestBytes = requestBytes;
        }

        public Uri Uri { get; }

        public string ClientKey { get; }

        public string RequestTarget { get; }

        public string HostHeader { get; }

        public IReadOnlyList<string> RequestedSubProtocols { get; }

        public IReadOnlyList<string> RequestedExtensions { get; }

        public byte[] RequestBytes { get; }
    }

    /// <summary>
    /// Builds and sends RFC 6455 HTTP upgrade requests.
    /// </summary>
    public static class WebSocketHandshake
    {
        private static readonly HashSet<string> ReservedHeaderNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "Host",
            "Connection",
            "Upgrade",
            "Sec-WebSocket-Key",
            "Sec-WebSocket-Version",
            "Sec-WebSocket-Protocol",
            "Sec-WebSocket-Extensions"
        };

        /// <summary>
        /// Builds a WebSocket upgrade request payload.
        /// </summary>
        /// <remarks>
        /// The <c>Host</c> header carries the wire form of the URI host (RFC 6455 Section 4.1, RFC
        /// 9110 Section 7.2, RFC 3986 Section 3.2.2): an IDN in its A-label form, an IPv6 literal
        /// in one pair of brackets without a zone id, and the port only when it is not the scheme's
        /// default (80 for ws, 443 for wss).
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// The URI is not an absolute ws/wss URI, or its host has no valid RFC 3986 wire form (empty,
        /// no A-label form, or not an IP literal or DNS labels of 1-63 letters, digits, '-' or '_'
        /// with at most 253 characters). The message does not contain the host.
        /// </exception>
        public static WebSocketHandshakeRequest BuildRequest(
            Uri uri,
            IEnumerable<string> subProtocols = null,
            IEnumerable<string> extensions = null,
            IEnumerable<KeyValuePair<string, string>> customHeaders = null)
        {
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));

            ValidateUri(uri);

            string requestTarget = BuildRequestTarget(uri);
            string hostHeader = BuildHostHeader(uri);
            string clientKey = WebSocketConstants.GenerateClientKey();

            var requestedSubProtocols = NormalizeSubProtocolList(subProtocols);
            var requestedExtensions = NormalizeExtensionList(extensions);

            var builder = new StringBuilder(512);
            builder.Append("GET ");
            builder.Append(requestTarget);
            builder.Append(" HTTP/1.1\r\n");
            builder.Append("Host: ");
            builder.Append(hostHeader);
            builder.Append("\r\n");
            builder.Append("Upgrade: websocket\r\n");
            builder.Append("Connection: Upgrade\r\n");
            builder.Append("Sec-WebSocket-Key: ");
            builder.Append(clientKey);
            builder.Append("\r\n");
            builder.Append("Sec-WebSocket-Version: ");
            builder.Append(WebSocketConstants.SupportedVersion);
            builder.Append("\r\n");

            if (requestedSubProtocols.Count > 0)
            {
                builder.Append("Sec-WebSocket-Protocol: ");
                builder.Append(string.Join(", ", requestedSubProtocols));
                builder.Append("\r\n");
            }

            if (requestedExtensions.Count > 0)
            {
                builder.Append("Sec-WebSocket-Extensions: ");
                builder.Append(string.Join(", ", requestedExtensions));
                builder.Append("\r\n");
            }

            if (customHeaders != null)
            {
                foreach (var header in customHeaders)
                {
                    ValidateCustomHeader(header.Key, header.Value);

                    if (ReservedHeaderNames.Contains(header.Key))
                    {
                        throw new ArgumentException(
                            "Custom headers must not override required WebSocket handshake headers: " +
                            header.Key,
                            nameof(customHeaders));
                    }

                    builder.Append(header.Key);
                    builder.Append(": ");
                    builder.Append(header.Value ?? string.Empty);
                    builder.Append("\r\n");
                }
            }

            builder.Append("\r\n");

            var requestBytes = Encoding.ASCII.GetBytes(builder.ToString());

            return new WebSocketHandshakeRequest(
                uri,
                clientKey,
                requestTarget,
                hostHeader,
                requestedSubProtocols,
                requestedExtensions,
                requestBytes);
        }

        /// <summary>
        /// Writes the serialized request bytes to the stream.
        /// </summary>
        public static async Task WriteRequestAsync(
            Stream stream,
            WebSocketHandshakeRequest request,
            CancellationToken ct)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await stream.WriteAsync(request.RequestBytes, 0, request.RequestBytes.Length, ct)
                .ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static void ValidateUri(Uri uri)
        {
            if (!uri.IsAbsoluteUri)
                throw new ArgumentException("WebSocket URI must be absolute.", nameof(uri));

            if (!string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "WebSocket URI scheme must be 'ws' or 'wss'.",
                    nameof(uri));
            }

            if (string.IsNullOrWhiteSpace(uri.Host))
                throw new ArgumentException("WebSocket URI host is required.", nameof(uri));
        }

        private static string BuildRequestTarget(Uri uri)
        {
            var requestTarget = uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
            return string.IsNullOrEmpty(requestTarget) ? "/" : requestTarget;
        }

        private static string BuildHostHeader(Uri uri)
        {
            // #309: the same wire host as the HTTP Host header (#46/#340): A-label IDN, one pair of
            // brackets around an IPv6 literal, never raw Unicode or an invalid host.
            if (!WireHost.TryFromUri(uri, out var host, out var error))
                throw new ArgumentException(error, nameof(uri));

            int defaultPort = string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
            int port = uri.IsDefaultPort ? defaultPort : uri.Port;

            if (port != defaultPort)
                return host + ":" + port.ToString(CultureInfo.InvariantCulture);

            return host;
        }

        private static IReadOnlyList<string> NormalizeSubProtocolList(IEnumerable<string> values)
        {
            if (values == null)
                return Array.Empty<string>();

            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var rawValue in values)
            {
                if (string.IsNullOrWhiteSpace(rawValue))
                    continue;

                var value = rawValue.Trim();
                if (!IsHeaderToken(value))
                {
                    throw new ArgumentException(
                        "Invalid sub-protocol token: " + value,
                        nameof(values));
                }

                if (seen.Add(value))
                    normalized.Add(value);
            }

            return normalized;
        }

        private static IReadOnlyList<string> NormalizeExtensionList(IEnumerable<string> values)
        {
            if (values == null)
                return Array.Empty<string>();

            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var rawValue in values)
            {
                if (string.IsNullOrWhiteSpace(rawValue))
                    continue;

                var value = rawValue.Trim();
                if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                    throw new ArgumentException("Extension value contains CR/LF characters.", nameof(values));

                if (seen.Add(value))
                    normalized.Add(value);
            }

            return normalized;
        }

        private static void ValidateCustomHeader(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Header name cannot be null or empty.", nameof(name));

            if (name.AsSpan().IndexOfAny('\r', '\n', ':') >= 0)
            {
                throw new ArgumentException(
                    "Header name contains invalid characters: " + name,
                    nameof(name));
            }

            if (value != null && value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                throw new ArgumentException(
                    "Header value contains CR/LF characters: " + name,
                    nameof(value));
            }

            if (!IsAscii(name))
            {
                throw new ArgumentException(
                    "Header name must contain ASCII characters only: " + name,
                    nameof(name));
            }

            if (value != null && !IsAscii(value))
            {
                throw new ArgumentException(
                    "Header value must contain ASCII characters only: " + name,
                    nameof(value));
            }
        }

        private static bool IsHeaderToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                return false;

            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];
                if (c <= 32 || c >= 127)
                    return false;

                switch (c)
                {
                    case '(':
                    case ')':
                    case '<':
                    case '>':
                    case '@':
                    case ',':
                    case ';':
                    case ':':
                    case '\\':
                    case '"':
                    case '/':
                    case '[':
                    case ']':
                    case '?':
                    case '=':
                    case '{':
                    case '}':
                        return false;
                }
            }

            return true;
        }

        private static bool IsAscii(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] > 0x7F)
                    return false;
            }

            return true;
        }
    }
}
