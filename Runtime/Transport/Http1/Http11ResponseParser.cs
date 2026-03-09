using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
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

        /// <summary>
        /// Contiguous body slice. Non-null only when <see cref="SegmentedBody"/> is null
        /// (fixed-length responses that fit in a single pooled rent).
        /// </summary>
        public ReadOnlyMemory<byte> Body { get; set; }

        /// <summary>
        /// Pool-ownership flag for the <see cref="Body"/> backing array.
        /// </summary>
        public bool BodyFromPool { get; set; }

        /// <summary>
        /// Segmented body for chunked and read-to-end responses. Owns pooled segment
        /// arrays; the consumer is responsible for disposal. When non-null, <see cref="Body"/>
        /// is <see cref="ReadOnlyMemory{T}.Empty"/> and <see cref="BodyFromPool"/> is false.
        /// </summary>
        public SegmentedBuffer SegmentedBody { get; set; }

        public bool KeepAlive { get; set; }

        /// <summary>
        /// Clears all fields so this instance can be safely returned to
        /// <see cref="ParsedResponsePool"/> and rented again for a new response.
        /// Returns any still-owned pooled buffers as a safety net in case the caller
        /// did not transfer or release them before returning the object to the pool.
        /// </summary>
        public void Reset()
        {
            ReleaseBodyBuffers();
            StatusCode = default;
            Headers = null;
            KeepAlive = false;
        }

        internal void ReleaseBodyBuffers()
        {
            if (BodyFromPool &&
                MemoryMarshal.TryGetArray(Body, out ArraySegment<byte> bodySegment) &&
                bodySegment.Array != null)
            {
                ArrayPool<byte>.Shared.Return(bodySegment.Array);
            }

            SegmentedBody?.Dispose();
            Body = default;
            BodyFromPool = false;
            SegmentedBody = null;
        }

        internal IEnumerable<ReadOnlyMemory<byte>> EnumerateBodySegments()
        {
            if (SegmentedBody != null)
            {
                var sequence = SegmentedBody.AsSequence();
                var enumerator = sequence.GetEnumerator();
                while (enumerator.MoveNext())
                    yield return enumerator.Current;

                yield break;
            }

            if (!Body.IsEmpty)
                yield return Body;
        }
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
            ParsedResponse parsedResult = null; // tracked for pool return on exception path

            using var reader = new BufferedStreamReader(stream);

            // Rent a scratch accumulator for header pairs. Reused across 1xx interim
            // responses; returned to pool after the final header block is transferred
            // to the permanent HttpHeaders instance.
            var scratch = HeaderParseScratchPool.Rent();
            try
            {

            // 1. Skip 1xx interim responses
            do
            {
                // Status line
                var statusLine = await reader.ReadLineAsync(ct, MaxHeaderLineLength).ConfigureAwait(false);
                if (string.IsNullOrEmpty(statusLine))
                    throw new FormatException("Empty HTTP status line");

                ParseStatusLine(statusLine, out httpVersion, out statusCode);

                // Headers — accumulate raw pairs into the pooled scratch list, then
                // transfer to a fresh HttpHeaders after the block is complete.
                // This reuses the List<(string, string)> across requests while keeping
                // the final HttpHeaders independent from the pooled scratch state.
                scratch.RawHeaders.Clear();
                int totalHeaderBytes = 0;
                while (true)
                {
                    var line = await reader.ReadLineAsync(ct, MaxHeaderLineLength).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line))
                        break;

                    totalHeaderBytes += line.Length;
                    if (totalHeaderBytes > MaxTotalHeaderBytes)
                        throw new FormatException("Response headers exceed maximum size");

                    if (TryParseHeaderLine(line, out var name, out var value))
                        scratch.RawHeaders.Add((name, value));
                }

                headers = new HttpHeaders();
                foreach (var (n, v) in scratch.RawHeaders)
                    headers.Add(n, v);

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
            ReadOnlyMemory<byte> body = ReadOnlyMemory<byte>.Empty;
            bool bodyFromPool = false;
            SegmentedBuffer segmentedBody = null;

            bool skipBody = requestMethod == HttpMethod.HEAD
                         || statusCode == 101
                         || statusCode == 204
                         || statusCode == 304;

            if (!skipBody)
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
                        bodyFromPool = result.BodyFromPool;
                        segmentedBody = result.SegmentedBody;
                        usedReadToEnd = result.UsedReadToEnd;
                    }
                    else if (te.EndsWith("chunked", StringComparison.OrdinalIgnoreCase))
                    {
                        // Chunked body goes into a SegmentedBuffer — no contiguous copy amplification.
                        segmentedBody = await ReadChunkedBodyAsync(reader, ct).ConfigureAwait(false);

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
                    bodyFromPool = result.BodyFromPool;
                    segmentedBody = result.SegmentedBody;
                    usedReadToEnd = result.UsedReadToEnd;
                }
            }

            // 3. Keep-alive detection
            bool keepAlive = IsKeepAlive(httpVersion, headers);
            if (usedReadToEnd)
                keepAlive = false;

            parsedResult = ParsedResponsePool.Rent();
            parsedResult.StatusCode = (HttpStatusCode)statusCode;
            parsedResult.Headers = headers;
            parsedResult.Body = body;
            parsedResult.BodyFromPool = bodyFromPool;
            parsedResult.SegmentedBody = segmentedBody;
            parsedResult.KeepAlive = keepAlive;
            // Transfer ownership to caller. Null parsedResult so the finally block
            // does not attempt to return it to the pool — BuildResponse owns the return.
            var toReturn = parsedResult;
            parsedResult = null;
            return toReturn;

            } // end try
            finally
            {
                // Return the scratch accumulator to the pool. All raw header string
                // references have been transferred to HttpHeaders by this point.
                HeaderParseScratchPool.Return(scratch);
                // Return ParsedResponse to pool only if an exception fired between
                // Rent() and the ownership-transfer null assignment above.
                if (parsedResult != null)
                    ParsedResponsePool.Return(parsedResult);
            }
        }

        private static async Task<(ReadOnlyMemory<byte> Body, bool BodyFromPool, SegmentedBuffer SegmentedBody, bool UsedReadToEnd)>
            ReadBodyByContentLengthOrEnd(
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
                    return (ReadOnlyMemory<byte>.Empty, false, null, false);

                int length = (int)contentLength;
                var fixedBody = await ReadFixedBodyAsync(reader, length, ct).ConfigureAwait(false);
                return (fixedBody.Body, fixedBody.BodyFromPool, null, false);
            }

            // Neither Transfer-Encoding nor Content-Length — read to end into a SegmentedBuffer.
            var readToEnd = await ReadToEndAsync(reader, ct).ConfigureAwait(false);
            return (ReadOnlyMemory<byte>.Empty, false, readToEnd, true);
        }

        private static bool IsKeepAlive(string httpVersion, HttpHeaders headers)
        {
            bool sawKeepAliveToken = false;
            var connectionValues = headers.GetValues("Connection");
            for (int i = 0; i < connectionValues.Count; i++)
            {
                var value = connectionValues[i];
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var span = value.AsSpan();
                while (span.Length > 0)
                {
                    int comma = span.IndexOf(',');
                    var token = comma >= 0
                        ? TrimWhitespace(span.Slice(0, comma))
                        : TrimWhitespace(span);

                    if (!token.IsEmpty)
                    {
                        if (MemoryExtensions.Equals(
                            token,
                            "close".AsSpan(),
                            StringComparison.OrdinalIgnoreCase))
                            return false;

                        if (MemoryExtensions.Equals(
                            token,
                            "keep-alive".AsSpan(),
                            StringComparison.OrdinalIgnoreCase))
                            sawKeepAliveToken = true;
                    }

                    if (comma < 0)
                        break;

                    span = span.Slice(comma + 1);
                }
            }

            if (sawKeepAliveToken)
                return true;

            // HTTP/1.1 defaults to keep-alive; HTTP/1.0 defaults to close.
            // Exact match prevents malformed version strings (e.g., "HTTP/1.10") from
            // incorrectly being treated as keep-alive, which would cause pool leaks.
            return string.Equals(httpVersion, "HTTP/1.1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseStatusCode(ReadOnlySpan<char> statusCodeSpan, out int statusCode)
        {
            statusCode = 0;
            if (statusCodeSpan.Length != 3)
                return false;

            int c0 = statusCodeSpan[0] - '0';
            int c1 = statusCodeSpan[1] - '0';
            int c2 = statusCodeSpan[2] - '0';
            if ((uint)c0 > 9 || (uint)c1 > 9 || (uint)c2 > 9)
                return false;

            statusCode = (c0 * 100) + (c1 * 10) + c2;
            return statusCode >= 100 && statusCode <= 999;
        }

        private static void ParseStatusLine(string statusLine, out string httpVersion, out int statusCode)
        {
            var statusSpan = statusLine.AsSpan();
            int firstSpace = statusSpan.IndexOf(' ');
            if (firstSpace <= 0)
                throw new FormatException("Invalid HTTP status line");

            var versionSpan = statusSpan.Slice(0, firstSpace);
            if (!MemoryExtensions.StartsWith(
                versionSpan,
                "HTTP/".AsSpan(),
                StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"Invalid HTTP version: {versionSpan.ToString()}");

            httpVersion = versionSpan.ToString();
            var statusCodeSpan = statusSpan.Slice(firstSpace + 1);
            int secondSpace = statusCodeSpan.IndexOf(' ');
            if (secondSpace >= 0)
                statusCodeSpan = statusCodeSpan.Slice(0, secondSpace);
            statusCodeSpan = TrimWhitespace(statusCodeSpan);

            if (!TryParseStatusCode(statusCodeSpan, out statusCode))
                throw new FormatException($"Invalid HTTP status code: {statusCodeSpan.ToString()}");
        }

        private static bool TryParseHeaderLine(string line, out string name, out string value)
        {
            var lineSpan = line.AsSpan();
            int colonIndex = lineSpan.IndexOf(':');
            if (colonIndex <= 0)
            {
                name = null;
                value = null;
                return false;
            }

            name = TrimWhitespace(lineSpan.Slice(0, colonIndex)).ToString();
            value = TrimWhitespace(lineSpan.Slice(colonIndex + 1)).ToString();
            return true;
        }

        private static long ParseChunkSizeLine(string sizeLine)
        {
            // Strip chunk extensions (RFC 9112 Section 7.1.1): "1A; ext=value"
            var sizeSpan = sizeLine.AsSpan();
            int semiIndex = sizeSpan.IndexOf(';');
            if (semiIndex >= 0)
                sizeSpan = sizeSpan.Slice(0, semiIndex);

            int spaceIndex = sizeSpan.IndexOf(' ');
            if (spaceIndex >= 0)
                sizeSpan = sizeSpan.Slice(0, spaceIndex);

            sizeSpan = TrimWhitespace(sizeSpan);
            if (!long.TryParse(
                sizeSpan,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var chunkSize))
            {
                throw new FormatException($"Invalid chunk size: {sizeSpan.ToString()}");
            }

            return chunkSize;
        }

        private static ReadOnlySpan<char> TrimWhitespace(ReadOnlySpan<char> value)
        {
            int start = 0;
            while (start < value.Length && char.IsWhiteSpace(value[start]))
                start++;

            int end = value.Length - 1;
            while (end >= start && char.IsWhiteSpace(value[end]))
                end--;

            if (start == 0 && end == value.Length - 1)
                return value;
            if (start > end)
                return ReadOnlySpan<char>.Empty;

            return value.Slice(start, end - start + 1);
        }

        /// <summary>
        /// Read a line terminated by CRLF (or bare LF per RFC 9112 §2.2 robustness) from the stream.
        /// Returns the line content without the trailing line terminator.
        /// WARNING: This helper creates a temporary buffered reader instance and may prefetch
        /// bytes beyond the returned line. It is intended for tests/single-line probes, not
        /// for continued parsing of a live connection where unread bytes must be preserved.
        /// </summary>
        [Obsolete("Test helper only. Prefer ParseAsync/BufferedStreamReader for production parsing paths.", false)]
        internal static async Task<string> ReadLineAsync(
            Stream stream, CancellationToken ct, int maxLength = MaxHeaderLineLength)
        {
            using var reader = new BufferedStreamReader(stream);
            return await reader.ReadLineAsync(ct, maxLength).ConfigureAwait(false);
        }

        /// <returns>
        /// A <see cref="SegmentedBuffer"/> containing the reassembled body, or <c>null</c>
        /// if the chunked body was empty (terminal chunk immediately followed by trailers).
        /// Callers must check for null before use.
        /// </returns>
        private static async Task<SegmentedBuffer> ReadChunkedBodyAsync(
            BufferedStreamReader reader,
            CancellationToken ct)
        {
            long totalBodyBytes = 0;
            // Read 8KB at a time into a temporary rent; copy directly into the segmented buffer.
            byte[] readBuf = ArrayPool<byte>.Shared.Rent(8192);
            var segBuffer = new SegmentedBuffer();

            try
            {
                while (true)
                {
                    var sizeLine = await reader.ReadLineAsync(ct, maxLength: 256).ConfigureAwait(false);

                    var chunkSize = ParseChunkSizeLine(sizeLine);

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
                    while (remaining > 0)
                    {
                        int toRead = Math.Min(remaining, readBuf.Length);
                        await reader.ReadExactAsync(readBuf, 0, toRead, ct).ConfigureAwait(false);
                        // Write directly into the segmented buffer — no contiguous copy amplification.
                        segBuffer.Write(new ReadOnlySpan<byte>(readBuf, 0, toRead));
                        remaining -= toRead;
                    }

                    // Read and validate trailing CRLF after chunk data (RFC 9112 Section 7.1)
                    var trailing = await reader.ReadLineAsync(ct, maxLength: 16).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(trailing))
                        throw new FormatException("Unexpected data after chunk body (expected CRLF)");
                }

                // Read and discard trailers (RFC 9112 §7.1.2).
                // Known limitation: trailer fields declared via the Trailer header
                // (e.g. Content-MD5, Digest) are consumed but not merged into the
                // response headers. Future work: expose trailers on ParsedResponse.
                while (true)
                {
                    var line = await reader.ReadLineAsync(ct, MaxHeaderLineLength).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line))
                        break;
                }

                if (segBuffer.IsEmpty)
                {
                    segBuffer.Dispose();
                    return null;
                }

                return segBuffer;
            }
            catch
            {
                segBuffer.Dispose();
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(readBuf);
            }
        }

        private static async Task<(ReadOnlyMemory<byte> Body, bool BodyFromPool)> ReadFixedBodyAsync(
            BufferedStreamReader reader,
            int length,
            CancellationToken ct)
        {
            if (length > MaxResponseBodySize)
                throw new IOException("Response body exceeds maximum size");

            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                await reader.ReadExactAsync(buffer, 0, length, ct).ConfigureAwait(false);
                return (new ReadOnlyMemory<byte>(buffer, 0, length), true);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
        }

        /// <returns>
        /// A <see cref="SegmentedBuffer"/> containing the body, or <c>null</c> if the
        /// server closed the connection without sending any body bytes.
        /// Callers must check for null before use.
        /// </returns>
        private static async Task<SegmentedBuffer> ReadToEndAsync(
            BufferedStreamReader reader,
            CancellationToken ct)
        {
            byte[] readBuffer = ArrayPool<byte>.Shared.Rent(8192);
            var segBuffer = new SegmentedBuffer();
            long totalRead = 0;

            try
            {
                while (true)
                {
                    int read = await reader.ReadAsync(readBuffer, 0, readBuffer.Length, ct).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    totalRead += read;
                    if (totalRead > MaxResponseBodySize)
                        throw new IOException("Response body exceeds maximum size");

                    segBuffer.Write(new ReadOnlySpan<byte>(readBuffer, 0, read));
                }

                if (segBuffer.IsEmpty)
                {
                    segBuffer.Dispose();
                    return null;
                }

                return segBuffer;
            }
            catch
            {
                segBuffer.Dispose();
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(readBuffer);
            }
        }

        private sealed class BufferedStreamReader : IDisposable
        {
            private const int DefaultBufferSize = 4096;

            private readonly Stream _stream;
            private readonly byte[] _buffer;
            private int _start;
            private int _end;
            private bool _disposed;

            public BufferedStreamReader(Stream stream, int bufferSize = DefaultBufferSize)
            {
                _stream = stream ?? throw new ArgumentNullException(nameof(stream));
                if (bufferSize <= 0)
                    throw new ArgumentOutOfRangeException(nameof(bufferSize), bufferSize, "Must be > 0.");
                _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                ArrayPool<byte>.Shared.Return(_buffer);
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

                            if (accumulatorCount + segmentLength > maxLength + 2)
                                throw new FormatException("HTTP header line exceeds maximum length");

                            EnsureCapacity(ref accumulator, accumulatorCount + segmentLength, accumulatorCount);
                            Buffer.BlockCopy(_buffer, _start, accumulator, accumulatorCount, segmentLength);
                            accumulatorCount += segmentLength;
                            _start = lfIndex + 1;

                            return DecodeLineFromBuffer(accumulator, 0, accumulatorCount, maxLength);
                        }

                        int available = _end - _start;
                        if (available > 0)
                        {
                            if (accumulatorCount + available > maxLength + 2)
                                throw new FormatException("HTTP header line exceeds maximum length");

                            EnsureCapacity(ref accumulator, accumulatorCount + available, accumulatorCount);
                            Buffer.BlockCopy(_buffer, _start, accumulator, accumulatorCount, available);
                            accumulatorCount += available;
                            _start = _end;
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
                        // clearArray: true — accumulator may contain sensitive header values
                        // (Authorization, Cookie, etc.). Clearing before pool return prevents
                        // stale data from being visible to the next renter.
                        ArrayPool<byte>.Shared.Return(accumulator, clearArray: true);
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
                try
                {
                    if (bytesToCopy > 0)
                        Buffer.BlockCopy(buffer, 0, resized, 0, bytesToCopy);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(resized);
                    throw;
                }

                ArrayPool<byte>.Shared.Return(buffer);
                buffer = resized;
            }
        }
    }
}
