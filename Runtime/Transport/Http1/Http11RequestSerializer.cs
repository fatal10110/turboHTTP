using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Http1
{
    /// <summary>
    /// Serializes a <see cref="UHttpRequest"/> to HTTP/1.1 wire format.
    /// GC hotspot: StringBuilder + Latin1.GetBytes allocates ~600-700 bytes per request.
    /// Phase 10 will rewrite with ArrayPool-backed buffered output.
    /// </summary>
    internal static class Http11RequestSerializer
    {
        /// <summary>
        /// Serialize the request to the given stream in HTTP/1.1 wire format.
        /// </summary>
        public static async Task SerializeAsync(UHttpRequest request, Stream stream, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            var sb = new StringBuilder(256);

            // 1. Request line: METHOD /path HTTP/1.1\r\n
            sb.Append(request.Method.ToUpperString());
            sb.Append(' ');
            var path = request.Uri.PathAndQuery;
            if (string.IsNullOrEmpty(path)) path = "/";
            sb.Append(path);
            sb.Append(" HTTP/1.1\r\n");

            // 2. Host header (required by HTTP/1.1)
            if (!request.Headers.Contains("Host"))
            {
                string hostValue;
                if (request.Uri.HostNameType == UriHostNameType.IPv6)
                    hostValue = $"[{request.Uri.Host}]";
                else
                    hostValue = request.Uri.Host;

                bool isDefaultPort = (request.Uri.Scheme == "https" && request.Uri.Port == 443)
                                  || (request.Uri.Scheme == "http" && request.Uri.Port == 80);
                if (!isDefaultPort)
                    hostValue = $"{hostValue}:{request.Uri.Port}";

                // Defense-in-depth: validate even though Uri.Host is pre-validated by .NET
                ValidateHeader("Host", hostValue);
                sb.Append("Host: ");
                sb.Append(hostValue);
                sb.Append("\r\n");
            }

            // 3. Handle Transfer-Encoding (RFC 9110 §8.6 mutual exclusion with Content-Length)
            // Phase 3 does not implement chunked body encoding, but allows the header to pass
            // through for pre-encoded bodies or bodyless requests (e.g., 100-continue probing).
            bool hasTransferEncoding = request.Headers.Contains("Transfer-Encoding");
            if (hasTransferEncoding)
            {
                var te = request.Headers.Get("Transfer-Encoding");
                bool hasBody = request.Body != null && request.Body.Length > 0;

                // Reject chunked with a body — we can't encode it (Phase 3 limitation)
                if (hasBody && te != null &&
                    te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new ArgumentException(
                        "Transfer-Encoding: chunked is set with a body, but chunked body encoding " +
                        "is not implemented in Phase 3. Pre-encode the body or remove the header.");
                }
                // TE without body is allowed — passes through for protocol signaling
            }

            // 4. User headers — one line per value (multi-value support)
            foreach (var name in request.Headers.Names)
            {
                var values = request.Headers.GetValues(name);
                foreach (var value in values)
                {
                    ValidateHeader(name, value);
                    sb.Append(name);
                    sb.Append(": ");
                    sb.Append(value);
                    sb.Append("\r\n");
                }
            }

            // 5. Validate/auto-add Content-Length (unless Transfer-Encoding is set)
            if (!hasTransferEncoding)
            {
                var userCL = request.Headers.Get("Content-Length");
                int actualBodyLength = request.Body?.Length ?? 0;

                if (userCL != null)
                {
                    // Validate user-provided Content-Length matches actual body size
                    if (!long.TryParse(userCL, out var clValue) || clValue != actualBodyLength)
                        throw new ArgumentException(
                            $"Content-Length header ({userCL}) does not match body size ({actualBodyLength})");
                }
                else if (actualBodyLength > 0)
                {
                    // Auto-add Content-Length only when body is present
                    sb.Append("Content-Length: ");
                    sb.Append(actualBodyLength);
                    sb.Append("\r\n");
                }
            }

            // 6. Auto-add User-Agent
            if (!request.Headers.Contains("User-Agent"))
            {
                sb.Append("User-Agent: TurboHTTP/1.0\r\n");
            }

            // 7. Auto-add Connection: keep-alive
            if (!request.Headers.Contains("Connection"))
            {
                sb.Append("Connection: keep-alive\r\n");
            }

            // 8. End of headers
            sb.Append("\r\n");

            // 9. Write header block using Latin-1
            var headerBytes = EncodingHelper.Latin1.GetBytes(sb.ToString());
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct).ConfigureAwait(false);

            // 10. Write body
            if (request.Body != null && request.Body.Length > 0)
                await stream.WriteAsync(request.Body, 0, request.Body.Length, ct).ConfigureAwait(false);

            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        // TODO Phase 10: Validate header names against full RFC 9110 token grammar (1*tchar).
        // Currently only checks for CRLF, colon, and empty/whitespace (sufficient for security — prevents injection).
        private static void ValidateHeader(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Header name cannot be null or empty");
            if (name.AsSpan().IndexOfAny('\r', '\n', ':') >= 0)
                throw new ArgumentException($"Header name contains invalid characters: {name}");
            if (value != null && value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new ArgumentException($"Header value for '{name}' contains CRLF characters");
        }
    }
}
