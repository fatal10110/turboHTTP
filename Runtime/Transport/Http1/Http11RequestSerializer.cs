using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Http1
{
    /// <summary>
    /// Serializes a <see cref="UHttpRequest"/> to HTTP/1.1 wire format.
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

            int actualBodyLength = request.Body?.Length ?? 0;

            using var headerWriter = new PooledHeaderWriter();

            // 1. Request line: METHOD /path HTTP/1.1\r\n
            headerWriter.Append(request.Method.ToUpperString());
            headerWriter.Append(' ');
            headerWriter.Append(GetRequestTarget(request));
            headerWriter.Append(" HTTP/1.1\r\n");

            // 2. Host header (required by HTTP/1.1)
            if (!request.Headers.Contains("Host"))
            {
                string hostValue;
                if (request.Uri.HostNameType == UriHostNameType.IPv6)
                {
                    // Mono/Unity can already return bracketed IPv6 hosts.
                    var host = request.Uri.Host;
                    hostValue = host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']'
                        ? host
                        : "[" + host + "]";
                }
                else
                {
                    hostValue = request.Uri.Host;
                }

                bool isDefaultPort = (request.Uri.Scheme == "https" && request.Uri.Port == 443)
                                  || (request.Uri.Scheme == "http" && request.Uri.Port == 80);
                if (!isDefaultPort)
                    hostValue = hostValue + ":" + request.Uri.Port;

                // Defense-in-depth: validate even though Uri.Host is pre-validated by .NET
                ValidateHeader("Host", hostValue);
                headerWriter.Append("Host: ");
                headerWriter.Append(hostValue);
                headerWriter.Append("\r\n");
            }

            // 3. Handle Transfer-Encoding (RFC 9110 §8.6 mutual exclusion with Content-Length)
            // Phase 3 does not implement chunked body encoding, but allows the header to pass
            // through for pre-encoded bodies or bodyless requests (e.g., 100-continue probing).
            bool hasTransferEncoding = request.Headers.Contains("Transfer-Encoding");
            if (hasTransferEncoding)
            {
                var teValues = request.Headers.GetValues("Transfer-Encoding");
                bool hasBody = request.Body != null && request.Body.Length > 0;
                bool hasChunked = false;
                for (int i = 0; i < teValues.Count; i++)
                {
                    var teValue = teValues[i];
                    if (teValue != null &&
                        teValue.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hasChunked = true;
                        break;
                    }
                }

                // Reject chunked with a body — we can't encode it (Phase 3 limitation)
                if (hasBody && hasChunked)
                {
                    throw new ArgumentException(
                        "Transfer-Encoding: chunked is set with a body, but chunked body encoding " +
                        "is not implemented in Phase 3. Pre-encode the body or remove the header.");
                }
                // TE without body is allowed — passes through for protocol signaling
            }

            // 4. Validate Content-Length before serializing user headers
            // (RFC 9110 §8.6 + smuggling hardening).
            var contentLengthValues = request.Headers.GetValues("Content-Length");

            if (hasTransferEncoding && contentLengthValues.Count > 0)
            {
                throw new ArgumentException(
                    "Transfer-Encoding and Content-Length must not both be set.");
            }

            if (contentLengthValues.Count > 0)
            {
                var parsedContentLengths = new List<long>(contentLengthValues.Count);
                long? normalizedContentLength = null;
                for (int i = 0; i < contentLengthValues.Count; i++)
                {
                    var rawHeaderValue = contentLengthValues[i];
                    if (string.IsNullOrWhiteSpace(rawHeaderValue))
                    {
                        throw new ArgumentException($"Invalid Content-Length header value: {rawHeaderValue}");
                    }

                    var rawParts = rawHeaderValue.Split(',');
                    for (int j = 0; j < rawParts.Length; j++)
                    {
                        var rawValue = rawParts[j].Trim();
                        if (rawValue.Length == 0 ||
                            !long.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                            parsed < 0)
                        {
                            throw new ArgumentException($"Invalid Content-Length header value: {rawHeaderValue}");
                        }

                        parsedContentLengths.Add(parsed);

                        if (!normalizedContentLength.HasValue)
                        {
                            normalizedContentLength = parsed;
                        }
                        else if (normalizedContentLength.Value != parsed)
                        {
                            throw new ArgumentException(
                                "Conflicting Content-Length header values are not allowed.");
                        }
                    }
                }

                if (parsedContentLengths.Count == 0)
                    throw new ArgumentException("Invalid Content-Length header value: (empty)");

                if (normalizedContentLength.Value != actualBodyLength)
                {
                    throw new ArgumentException(
                        $"Content-Length header ({normalizedContentLength.Value}) does not match body size ({actualBodyLength})");
                }
            }

            // 5. User headers — one line per value (multi-value support)
            foreach (var name in request.Headers.Names)
            {
                var values = request.Headers.GetValues(name);
                foreach (var value in values)
                {
                    ValidateHeader(name, value);
                    headerWriter.Append(name);
                    headerWriter.Append(": ");
                    headerWriter.Append(value);
                    headerWriter.Append("\r\n");
                }
            }

            // 6. Auto-add Content-Length (unless Transfer-Encoding is set)
            if (!hasTransferEncoding && contentLengthValues.Count == 0)
            {
                if (actualBodyLength > 0)
                {
                    // Auto-add Content-Length only when body is present
                    headerWriter.Append("Content-Length: ");
                    headerWriter.AppendInt(actualBodyLength);
                    headerWriter.Append("\r\n");
                }
            }

            // 7. Auto-add User-Agent
            if (!request.Headers.Contains("User-Agent"))
            {
                headerWriter.Append("User-Agent: TurboHTTP/1.0\r\n");
            }

            // 8. Auto-add Connection: keep-alive
            if (!request.Headers.Contains("Connection"))
            {
                headerWriter.Append("Connection: keep-alive\r\n");
            }

            // 9. End of headers
            headerWriter.Append("\r\n");

            // 10. Write header block
            await headerWriter.WriteToAsync(stream, ct).ConfigureAwait(false);

            // 11. Write body
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

        private static string GetRequestTarget(UHttpRequest request)
        {
            if (request.Metadata != null &&
                request.Metadata.TryGetValue(RequestMetadataKeys.ProxyAbsoluteForm, out var absoluteFormObj) &&
                absoluteFormObj is bool useAbsoluteForm &&
                useAbsoluteForm)
            {
                var absolute = request.Uri.GetComponents(
                    UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                    UriFormat.UriEscaped);
                if (!string.IsNullOrEmpty(absolute))
                    return absolute;
            }

            var path = request.Uri.PathAndQuery;
            return string.IsNullOrEmpty(path) ? "/" : path;
        }

        private sealed class PooledHeaderWriter : IDisposable
        {
            private readonly PooledArrayBufferWriter _writer;

            public PooledHeaderWriter(int initialCapacity = 1024)
            {
                _writer = new PooledArrayBufferWriter(initialCapacity);
            }

            public void Append(string value)
            {
                if (string.IsNullOrEmpty(value))
                    return;

                int byteCount = EncodingHelper.GetLatin1ByteCount(value);
                var destination = _writer.GetSpan(byteCount);
                int written = EncodingHelper.GetLatin1Bytes(value, destination);
                _writer.Advance(written);
            }

            public void Append(char value)
            {
                var span = _writer.GetSpan(1);
                span[0] = value <= byte.MaxValue ? (byte)value : (byte)'?';
                _writer.Advance(1);
            }

            public void AppendInt(int value)
            {
                Append(value.ToString(CultureInfo.InvariantCulture));
            }

            public Task WriteToAsync(Stream stream, CancellationToken ct)
            {
                if (_writer.WrittenCount == 0)
                    return Task.CompletedTask;

                var memory = _writer.WrittenMemory;
                if (!MemoryMarshal.TryGetArray(memory, out var segment) || segment.Array == null)
                    throw new InvalidOperationException("Header buffer is not array-backed.");

                return stream.WriteAsync(segment.Array, segment.Offset, segment.Count, ct);
            }

            public void Dispose() => _writer.Dispose();
        }
    }
}
