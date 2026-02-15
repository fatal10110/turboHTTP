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

            var reader = new BufferedStreamReader(stream);

            // 1. Skip 1xx interim responses
            do
            {
                // Status line
                var statusLine = await reader.ReadLineAsync(ct, MaxHeaderLineLength).ConfigureAwait(false);
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
                    var line = await reader.ReadLineAsync(ct, MaxHeaderLineLength).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line))
                        break;

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
                        var result = await ReadBodyByContentLengthOrEnd(reader, headers, ct).ConfigureAwait(false);
                        body = result.Body;
                        usedReadToEnd = result.UsedReadToEnd;
                    }
                    else if (te.EndsWith("chunked", StringComparison.OrdinalIgnoreCase))
                    {
                        body = await ReadChunkedBodyAsync(reader, ct).ConfigureAwait(false);

                        // RFC 9112 Section 6.1: ignore Content-Length when Transfer-Encoding is present.
                        if (contentLengthStr != null)
                            headers.Remove("Content-Length");
                    }
                    else
                    {
                        throw new NotSupportedException(
                            $"Unsupported Transfer-Encoding: {te}. Only 'chunked' and 'identity' are supported.");
                    }
                }
                else
                {
                    var result = await ReadBodyByContentLengthOrEnd(reader, headers, ct).ConfigureAwait(false);
                    body = result.Body;
                    usedReadToEnd = result.UsedReadToEnd;
                }
            }

            // 3. Keep-alive detection
            bool keepAlive = IsKeepAlive(httpVersion, headers);
            if (usedReadToEnd)
                keepAlive = false;

            return new ParsedResponse
            {
                StatusCode = (HttpStatusCode)statusCode,
                Headers = headers,
                Body = body,
                KeepAlive = keepAlive
            };
        }

        private static async Task<(byte[] Body, bool UsedReadToEnd)> ReadBodyByContentLengthOrEnd(
            BufferedStreamReader reader,
            HttpHeaders headers,
            CancellationToken ct)
        {
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

                if (contentLength < 0)
                    throw new FormatException("Negative Content-Length");

                if (contentLength > MaxResponseBodySize)
                    throw new IOException("Response body exceeds maximum size");

                if (contentLength == 0)
                    return (Array.Empty<byte>(), false);

                int length = (int)contentLength;
                return (await ReadFixedBodyAsync(reader, length, ct).ConfigureAwait(false), false);
            }

            // Neither Transfer-Encoding nor Content-Length — read to end.
            return (await ReadToEndAsync(reader, ct).ConfigureAwait(false), true);
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

            // HTTP/1.1 defaults to keep-alive; HTTP/1.0 defaults to close.
            return httpVersion.Contains("1.1");
        }

        /// <summary>
        /// Read a line terminated by CRLF (or bare LF per RFC 9112 §2.2 robustness) from the stream.
        /// Returns the line content without the trailing line terminator.
        /// </summary>
        internal static async Task<string> ReadLineAsync(
            Stream stream, CancellationToken ct, int maxLength = MaxHeaderLineLength)
        {
            var reader = new BufferedStreamReader(stream);
            return await reader.ReadLineAsync(ct, maxLength).ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadChunkedBodyAsync(BufferedStreamReader reader, CancellationToken ct)
        {
            using (var body = new MemoryStream())
            {
                long totalBodyBytes = 0;

                while (true)
                {
                    var sizeLine = await reader.ReadLineAsync(ct, maxLength: 256).ConfigureAwait(false);

                    // Strip chunk extensions (RFC 9112 Section 7.1.1): "1A; ext=value"
                    int semiIndex = sizeLine.IndexOf(';');
                    if (semiIndex >= 0) sizeLine = sizeLine.Substring(0, semiIndex);
                    int spaceIndex = sizeLine.IndexOf(' ');
                    if (spaceIndex >= 0) sizeLine = sizeLine.Substring(0, spaceIndex);
                    sizeLine = sizeLine.Trim();

                    if (!long.TryParse(sizeLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize))
                        throw new FormatException($"Invalid chunk size: {sizeLine}");

                    if (chunkSize == 0)
                        break;

                    if (chunkSize > MaxResponseBodySize)
                        throw new IOException("Response body exceeds maximum size");

                    totalBodyBytes += chunkSize;
                    if (totalBodyBytes > MaxResponseBodySize)
                        throw new IOException("Response body exceeds maximum size");

                    if (chunkSize > int.MaxValue)
                        throw new IOException("Chunk size exceeds supported range");

                    int remaining = (int)chunkSize;
                    var readBuf = ArrayPool<byte>.Shared.Rent(Math.Min(remaining, 8192));
                    try
                    {
                        while (remaining > 0)
                        {
                            int toRead = Math.Min(remaining, readBuf.Length);
                            await reader.ReadExactAsync(readBuf, 0, toRead, ct).ConfigureAwait(false);
                            body.Write(readBuf, 0, toRead);
                            remaining -= toRead;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(readBuf);
                    }

                    // Read and validate trailing CRLF after chunk data (RFC 9112 Section 7.1)
                    var trailing = await reader.ReadLineAsync(ct, maxLength: 16).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(trailing))
                        throw new FormatException("Unexpected data after chunk body (expected CRLF)");
                }

                // Read trailers (zero or more header lines until empty line)
                while (true)
                {
                    var line = await reader.ReadLineAsync(ct, MaxHeaderLineLength).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line))
                        break;
                }

                return body.ToArray();
            }
        }

        private static async Task<byte[]> ReadFixedBodyAsync(BufferedStreamReader reader, int length, CancellationToken ct)
        {
            if (length > MaxResponseBodySize)
                throw new IOException("Response body exceeds maximum size");

            var buffer = new byte[length];
            await reader.ReadExactAsync(buffer, 0, length, ct).ConfigureAwait(false);
            return buffer;
        }

        private static async Task<byte[]> ReadToEndAsync(BufferedStreamReader reader, CancellationToken ct)
        {
            using (var ms = new MemoryStream())
            {
                var buffer = ArrayPool<byte>.Shared.Rent(8192);
                try
                {
                    while (true)
                    {
                        int read = await reader.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                        if (read == 0)
                            break;

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
        }

        private sealed class BufferedStreamReader
        {
            private const int DefaultBufferSize = 4096;

            private readonly Stream _stream;
            private readonly byte[] _buffer;
            private int _start;
            private int _end;

            public BufferedStreamReader(Stream stream, int bufferSize = DefaultBufferSize)
            {
                _stream = stream ?? throw new ArgumentNullException(nameof(stream));
                if (bufferSize <= 0)
                    throw new ArgumentOutOfRangeException(nameof(bufferSize), bufferSize, "Must be > 0.");
                _buffer = new byte[bufferSize];
            }

            public async Task<string> ReadLineAsync(CancellationToken ct, int maxLength)
            {
                if (maxLength <= 0)
                    throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "Must be > 0.");

                byte[] accumulator = null;
                int accumulatorCount = 0;

                try
                {
                    while (true)
                    {
                        int lfIndex = IndexOfLf();
                        if (lfIndex >= 0)
                        {
                            int segmentLength = lfIndex - _start + 1;

                            if (accumulator == null)
                            {
                                var line = DecodeLineFromBuffer(_buffer, _start, segmentLength, maxLength);
                                _start = lfIndex + 1;
                                return line;
                            }

                            EnsureCapacity(ref accumulator, accumulatorCount + segmentLength, accumulatorCount);
                            Buffer.BlockCopy(_buffer, _start, accumulator, accumulatorCount, segmentLength);
                            accumulatorCount += segmentLength;
                            _start = lfIndex + 1;

                            return DecodeLineFromBuffer(accumulator, 0, accumulatorCount, maxLength);
                        }

                        int available = _end - _start;
                        if (available > 0)
                        {
                            EnsureCapacity(ref accumulator, accumulatorCount + available, accumulatorCount);
                            Buffer.BlockCopy(_buffer, _start, accumulator, accumulatorCount, available);
                            accumulatorCount += available;
                            _start = _end;

                            if (accumulatorCount > maxLength + 2)
                                throw new FormatException("HTTP header line exceeds maximum length");
                        }

                        int read = await FillBufferAsync(ct).ConfigureAwait(false);
                        if (read == 0)
                        {
                            if (accumulatorCount == 0)
                                return string.Empty;

                            return DecodeLineFromBuffer(accumulator, 0, accumulatorCount, maxLength);
                        }
                    }
                }
                finally
                {
                    if (accumulator != null)
                        ArrayPool<byte>.Shared.Return(accumulator);
                }
            }

            public async Task ReadExactAsync(byte[] target, int offset, int count, CancellationToken ct)
            {
                int copied = 0;
                while (copied < count)
                {
                    int read = await ReadAsync(target, offset + copied, count - copied, ct).ConfigureAwait(false);
                    if (read == 0)
                        throw new IOException("Unexpected end of stream");
                    copied += read;
                }
            }

            public async Task<int> ReadAsync(byte[] target, int offset, int count, CancellationToken ct)
            {
                if (target == null)
                    throw new ArgumentNullException(nameof(target));
                if (offset < 0 || count < 0 || offset + count > target.Length)
                    throw new ArgumentOutOfRangeException();

                if (count == 0)
                    return 0;

                int available = _end - _start;
                if (available > 0)
                {
                    int toCopy = Math.Min(available, count);
                    Buffer.BlockCopy(_buffer, _start, target, offset, toCopy);
                    _start += toCopy;
                    return toCopy;
                }

                // Fast path for large direct reads when our internal buffer is empty.
                if (count >= _buffer.Length)
                    return await _stream.ReadAsync(target, offset, count, ct).ConfigureAwait(false);

                int read = await FillBufferAsync(ct).ConfigureAwait(false);
                if (read == 0)
                    return 0;

                int copy = Math.Min(read, count);
                Buffer.BlockCopy(_buffer, _start, target, offset, copy);
                _start += copy;
                return copy;
            }

            private int IndexOfLf()
            {
                for (int i = _start; i < _end; i++)
                {
                    if (_buffer[i] == (byte)'\n')
                        return i;
                }

                return -1;
            }

            private async Task<int> FillBufferAsync(CancellationToken ct)
            {
                _start = 0;
                _end = 0;

                int read = await _stream.ReadAsync(_buffer, 0, _buffer.Length, ct).ConfigureAwait(false);
                if (read > 0)
                    _end = read;

                return read;
            }

            private static string DecodeLineFromBuffer(byte[] source, int offset, int length, int maxLength)
            {
                if (length == 0)
                    return string.Empty;

                int end = offset + length;
                if (source[end - 1] == (byte)'\n')
                    end--;
                if (end > offset && source[end - 1] == (byte)'\r')
                    end--;

                int payloadLength = end - offset;
                if (payloadLength > maxLength)
                    throw new FormatException("HTTP header line exceeds maximum length");
                if (payloadLength == 0)
                    return string.Empty;

                return EncodingHelper.Latin1.GetString(source, offset, payloadLength);
            }

            private static void EnsureCapacity(ref byte[] buffer, int required, int bytesToCopy)
            {
                if (buffer == null)
                {
                    buffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, required));
                    return;
                }

                if (buffer.Length >= required)
                    return;

                var resized = ArrayPool<byte>.Shared.Rent(Math.Max(required, buffer.Length * 2));
                if (bytesToCopy > 0)
                    Buffer.BlockCopy(buffer, 0, resized, 0, bytesToCopy);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = resized;
            }
        }
    }
}
