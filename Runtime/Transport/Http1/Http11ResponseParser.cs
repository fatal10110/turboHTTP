using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Http1
{
    /// <summary>
    /// Parsed HTTP/1.1 response (internal to Transport assembly).
    /// </summary>
    internal class ParsedResponse
    {
        /// <summary>
        /// HTTP status code. May be cast from a non-standard int (e.g., 451, 425).
        /// Consumers should use integer range checks rather than enum comparisons.
        /// </summary>
        public HttpStatusCode StatusCode { get; set; }
        public HttpHeaders Headers { get; set; }
        public byte[] Body { get; set; }
        public bool KeepAlive { get; set; }
    }

    /// <summary>
    /// Parses HTTP/1.1 responses from a stream.
    /// GC hotspot: byte-by-byte ReadAsync allocates ~400 Task objects (~29KB) per response.
    /// Phase 10 will rewrite with buffered I/O.
    /// </summary>
    internal static class Http11ResponseParser
    {
        private const int MaxHeaderLineLength = 8192;
        private const int MaxTotalHeaderBytes = 102400; // 100KB
        private const int MaxResponseBodySize = 100 * 1024 * 1024; // 100MB
        private const int Max1xxResponses = 10;

        /// <summary>
        /// Parse an HTTP/1.1 response from the stream.
        /// </summary>
        /// <param name="stream">The network stream to read from.</param>
        /// <param name="requestMethod">The HTTP method of the originating request (needed for HEAD handling).</param>
        /// <param name="ct">Cancellation token.</param>
        public static async Task<ParsedResponse> ParseAsync(
            Stream stream, HttpMethod requestMethod, CancellationToken ct)
        {
            int statusCode;
            string httpVersion;
            HttpHeaders headers;
            int interim1xxCount = 0;

            // 1. Skip 1xx interim responses
            do
            {
                // Status line
                var statusLine = await ReadLineAsync(stream, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(statusLine))
                    throw new FormatException("Empty HTTP status line");

                int firstSpace = statusLine.IndexOf(' ');
                if (firstSpace < 0)
                    throw new FormatException("Invalid HTTP status line");

                httpVersion = statusLine.Substring(0, firstSpace);
                if (!httpVersion.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException($"Invalid HTTP version: {httpVersion}");

                int secondSpace = statusLine.IndexOf(' ', firstSpace + 1);
                string statusStr = secondSpace > 0
                    ? statusLine.Substring(firstSpace + 1, secondSpace - firstSpace - 1)
                    : statusLine.Substring(firstSpace + 1);

                if (!int.TryParse(statusStr, NumberStyles.None, CultureInfo.InvariantCulture, out statusCode)
                    || statusCode < 100 || statusCode > 999)
                    throw new FormatException($"Invalid HTTP status code: {statusStr}");

                // Headers
                headers = new HttpHeaders();
                int totalHeaderBytes = 0;
                while (true)
                {
                    var line = await ReadLineAsync(stream, ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line)) break;

                    totalHeaderBytes += line.Length;
                    if (totalHeaderBytes > MaxTotalHeaderBytes)
                        throw new FormatException("Response headers exceed maximum size");

                    var colonIndex = line.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        var name = line.Substring(0, colonIndex).Trim();
                        var value = line.Substring(colonIndex + 1).Trim();
                        headers.Add(name, value);
                    }
                }

                // 101 Switching Protocols is NOT interim — it signals a protocol upgrade.
                // Skipping it would cause the parser to read upgraded protocol data as HTTP,
                // leading to desync or hang. Break out and return it to the caller.
                if (statusCode == 101)
                    break;

                if (statusCode >= 100 && statusCode < 200)
                {
                    interim1xxCount++;
                    if (interim1xxCount > Max1xxResponses)
                        throw new FormatException("Too many 1xx interim responses");
                }
            } while (statusCode >= 100 && statusCode < 200);

            // 2. Determine body reading strategy
            bool usedReadToEnd = false;
            byte[] body;

            // 101 Switching Protocols has no HTTP body — the connection is upgraded.
            bool skipBody = requestMethod == HttpMethod.HEAD
                         || statusCode == 101
                         || statusCode == 204
                         || statusCode == 304;

            if (skipBody)
            {
                body = Array.Empty<byte>();
            }
            else
            {
                var transferEncoding = headers.Get("Transfer-Encoding");
                var contentLengthStr = headers.Get("Content-Length");

                if (transferEncoding != null)
                {
                    var te = transferEncoding.Trim();

                    if (string.Equals(te, "identity", StringComparison.OrdinalIgnoreCase))
                    {
                        // identity is a no-op — fall through to Content-Length or read-to-end
                        body = await ReadBodyByContentLengthOrEnd(
                            stream, headers, ct, out usedReadToEnd).ConfigureAwait(false);
                    }
                    else if (te.EndsWith("chunked", StringComparison.OrdinalIgnoreCase))
                    {
                        body = await ReadChunkedBodyAsync(stream, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new NotSupportedException(
                            $"Unsupported Transfer-Encoding: {te}. Only 'chunked' and 'identity' are supported.");
                    }
                }
                else
                {
                    body = await ReadBodyByContentLengthOrEnd(
                        stream, headers, ct, out usedReadToEnd).ConfigureAwait(false);
                }
            }

            // 3. Keep-alive detection
            bool keepAlive = IsKeepAlive(httpVersion, headers);
            if (usedReadToEnd)
                keepAlive = false; // Connection read to EOF, not reusable

            return new ParsedResponse
            {
                StatusCode = (HttpStatusCode)statusCode,
                Headers = headers,
                Body = body,
                KeepAlive = keepAlive
            };
        }

        private static async Task<byte[]> ReadBodyByContentLengthOrEnd(
            Stream stream, HttpHeaders headers, CancellationToken ct, out bool usedReadToEnd)
        {
            usedReadToEnd = false;
            var contentLengthStr = headers.Get("Content-Length");

            if (contentLengthStr != null)
            {
                // Validate all Content-Length values are consistent (RFC 9110 Section 8.6)
                var clValues = headers.GetValues("Content-Length");
                if (clValues.Count > 1)
                {
                    for (int i = 1; i < clValues.Count; i++)
                    {
                        if (clValues[i].Trim() != clValues[0].Trim())
                            throw new FormatException("Conflicting Content-Length values");
                    }
                }

                if (!long.TryParse(contentLengthStr.Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var contentLength))
                    throw new FormatException("Invalid Content-Length value");

                // Belt-and-suspenders: NumberStyles.None already rejects signs
                if (contentLength < 0)
                    throw new FormatException("Negative Content-Length");

                if (contentLength > MaxResponseBodySize)
                    throw new IOException("Response body exceeds maximum size");

                if (contentLength == 0)
                    return Array.Empty<byte>();

                int length = (int)contentLength;
                return await ReadFixedBodyAsync(stream, length, ct).ConfigureAwait(false);
            }

            // Neither Transfer-Encoding nor Content-Length — read to end
            usedReadToEnd = true;
            return await ReadToEndAsync(stream, ct).ConfigureAwait(false);
        }

        // TODO Phase 10: Handle multi-token Connection header (e.g., "close, Upgrade")
        // per RFC 9110 Section 7.6.1. Currently only checks single-value which covers
        // virtually all real-world servers.
        private static bool IsKeepAlive(string httpVersion, HttpHeaders headers)
        {
            var connection = headers.Get("Connection");
            if (connection != null)
            {
                if (string.Equals(connection.Trim(), "close", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (string.Equals(connection.Trim(), "keep-alive", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // HTTP/1.1 defaults to keep-alive; HTTP/1.0 defaults to close
            return httpVersion.Contains("1.1");
        }

        /// <summary>
        /// Read a line terminated by CRLF (or bare LF per RFC 9112 §2.2 robustness) from the stream.
        /// Returns the line content without the trailing line terminator.
        /// </summary>
        internal static async Task<string> ReadLineAsync(
            Stream stream, CancellationToken ct, int maxLength = MaxHeaderLineLength)
        {
            using (var ms = new MemoryStream(256))
            {
                var singleByte = new byte[1];
                bool lastWasCR = false;

                while (true)
                {
                    int read = await stream.ReadAsync(singleByte, 0, 1, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        // EOF — return what we have (may be empty for end of stream)
                        break;
                    }

                    byte b = singleByte[0];

                    // Accept bare LF as line terminator (robustness per RFC 9112 Section 2.2)
                    if (b == (byte)'\n')
                    {
                        if (lastWasCR)
                            ms.SetLength(ms.Length - 1); // strip the CR we wrote
                        break;
                    }

                    lastWasCR = b == (byte)'\r';
                    ms.WriteByte(b);

                    if (ms.Length > maxLength)
                        throw new FormatException("HTTP header line exceeds maximum length");
                }

                if (ms.Length == 0)
                    return string.Empty;

                if (!ms.TryGetBuffer(out var segment))
                    segment = new ArraySegment<byte>(ms.ToArray());

                return EncodingHelper.Latin1.GetString(segment.Array, segment.Offset, (int)ms.Length);
            }
        }

        private static async Task<byte[]> ReadChunkedBodyAsync(Stream stream, CancellationToken ct)
        {
            using (var body = new MemoryStream())
            {
                long totalBodyBytes = 0;

                while (true)
                {
                    var sizeLine = await ReadLineAsync(stream, ct, maxLength: 256).ConfigureAwait(false);

                    // Strip chunk extensions (RFC 9112 Section 7.1.1): "1A; ext=value"
                    int semiIndex = sizeLine.IndexOf(';');
                    if (semiIndex >= 0) sizeLine = sizeLine.Substring(0, semiIndex);
                    int spaceIndex = sizeLine.IndexOf(' ');
                    if (spaceIndex >= 0) sizeLine = sizeLine.Substring(0, spaceIndex);
                    sizeLine = sizeLine.Trim();

                    if (!long.TryParse(sizeLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize))
                        throw new FormatException($"Invalid chunk size: {sizeLine}");

                    if (chunkSize == 0) break;

                    if (chunkSize > MaxResponseBodySize)
                        throw new IOException("Response body exceeds maximum size");

                    totalBodyBytes += chunkSize;
                    if (totalBodyBytes > MaxResponseBodySize)
                        throw new IOException("Response body exceeds maximum size");

                    // Read chunk data using pooled buffer to avoid per-chunk allocation
                    int remaining = (int)chunkSize;
                    var readBuf = ArrayPool<byte>.Shared.Rent(Math.Min(remaining, 8192));
                    try
                    {
                        while (remaining > 0)
                        {
                            int toRead = Math.Min(remaining, readBuf.Length);
                            await ReadExactAsync(stream, readBuf, 0, toRead, ct).ConfigureAwait(false);
                            body.Write(readBuf, 0, toRead);
                            remaining -= toRead;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(readBuf);
                    }

                    // Read and validate trailing CRLF after chunk data (RFC 9112 Section 7.1)
                    var trailing = await ReadLineAsync(stream, ct, maxLength: 16).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(trailing))
                        throw new FormatException("Unexpected data after chunk body (expected CRLF)");
                }

                // Read trailers (zero or more header lines until empty line)
                // TODO Phase 6: Parse trailer headers and merge into response headers
                while (true)
                {
                    var line = await ReadLineAsync(stream, ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line)) break;
                }

                return body.ToArray();
            }
        }

        private static async Task<byte[]> ReadFixedBodyAsync(Stream stream, int length, CancellationToken ct)
        {
            if (length > MaxResponseBodySize)
                throw new IOException("Response body exceeds maximum size");

            var buffer = new byte[length];
            await ReadExactAsync(stream, buffer, 0, length, ct).ConfigureAwait(false);
            return buffer;
        }

        private static async Task<byte[]> ReadToEndAsync(Stream stream, CancellationToken ct)
        {
            var ms = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                    if (read == 0) break;

                    ms.Write(buffer, 0, read);
                    if (ms.Length > MaxResponseBodySize)
                        throw new IOException("Response body exceeds maximum size");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return ms.ToArray();
        }

        private static async Task ReadExactAsync(
            Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, ct)
                    .ConfigureAwait(false);
                if (read == 0)
                    throw new IOException("Unexpected end of stream");
                totalRead += read;
            }
        }
    }
}
