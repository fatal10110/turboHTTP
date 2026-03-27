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

        public HttpHeaders Trailers { get; set; }

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
            Trailers = null;
            KeepAlive = false;
        }

        internal void ReleaseBodyBuffers()
        {
            // Fixed-body responses currently wrap a direct ArrayPool<byte> rent in
            // ReadOnlyMemory<byte>, so TryGetArray succeeds here. If the storage model
            // changes in a future phase, this release path must be updated too.
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

    internal enum Http11ResponseBodyKind
    {
        Empty,
        Chunked,
        ContentLength,
        ReadToEnd
    }

    internal sealed class ParsedResponseHead : IDisposable
    {
        private Http11ResponseParser.BufferedStreamReader _reader;

        internal ParsedResponseHead(Http11ResponseParser.BufferedStreamReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public HttpStatusCode StatusCode { get; internal set; }

        public HttpHeaders Headers { get; internal set; }

        public bool KeepAlive { get; internal set; }

        public Http11ResponseBodyKind BodyKind { get; internal set; }

        public long? ContentLength { get; internal set; }

        internal Http11ResponseParser.BufferedStreamReader Reader =>
            _reader ?? throw new ObjectDisposedException(nameof(ParsedResponseHead));

        internal Http11ResponseParser.BufferedStreamReader TransferReaderOwnership()
        {
            var reader = _reader;
            if (reader == null)
                throw new ObjectDisposedException(nameof(ParsedResponseHead));

            _reader = null;
            return reader;
        }

        public void Dispose()
        {
            var reader = _reader;
            _reader = null;
            reader?.Dispose();
        }
    }

    /// <summary>
    /// Parses HTTP/1.1 responses from a stream.
    /// </summary>
    internal static class Http11ResponseParser
    {
        internal const int MaxHeaderLineLength = 8192;
        internal const int MaxTotalTrailerBytes = 32 * 1024;
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
            using var head = await ParseHeadAsync(stream, requestMethod, ct).ConfigureAwait(false);
            return await CompleteBufferedResponseAsync(head, ct).ConfigureAwait(false);
        }

        internal static async Task<ParsedResponseHead> ParseHeadAsync(
            Stream stream,
            HttpMethod requestMethod,
            CancellationToken ct,
            RequestContext context = null)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            string httpVersion = null;
            int statusCode = 0;
            HttpHeaders headers = null;
            int interim1xxCount = 0;
            BufferedStreamReader reader = null;
            ParsedResponseHead head = null;

            var scratch = HeaderParseScratchPool.Rent();
            try
            {
                reader = new BufferedStreamReader(stream);

                do
                {
                    var statusLine = await reader.ReadLineAsync(ct, MaxHeaderLineLength).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(statusLine))
                        throw new FormatException("Empty HTTP status line");

                    ParseStatusLine(statusLine, out httpVersion, out statusCode);

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

                    if (statusCode == 101)
                        break;

                    if (statusCode >= 100 && statusCode < 200)
                    {
                        interim1xxCount++;
                        if (interim1xxCount > Max1xxResponses)
                            throw new FormatException("Too many 1xx interim responses");
                    }
                }
                while (statusCode >= 100 && statusCode < 200);

                var bodyKind = DetermineBodyKind(
                    requestMethod,
                    statusCode,
                    headers,
                    context,
                    out var contentLength);

                bool keepAlive = IsKeepAlive(httpVersion, headers);
                if (bodyKind == Http11ResponseBodyKind.ReadToEnd)
                    keepAlive = false;

                head = new ParsedResponseHead(reader)
                {
                    StatusCode = (HttpStatusCode)statusCode,
                    Headers = headers,
                    KeepAlive = keepAlive,
                    BodyKind = bodyKind,
                    ContentLength = contentLength
                };

                reader = null;
                var toReturn = head;
                head = null;
                return toReturn;
            }
            finally
            {
                HeaderParseScratchPool.Return(scratch);
                head?.Dispose();
                reader?.Dispose();
            }
        }

        internal static async Task<ParsedResponse> CompleteBufferedResponseAsync(
            ParsedResponseHead head,
            CancellationToken ct)
        {
            if (head == null)
                throw new ArgumentNullException(nameof(head));

            ParsedResponse parsedResult = null;
            try
            {
                parsedResult = ParsedResponsePool.Rent();
                var bodyResult = await ReadBufferedBodyAsync(head, ct).ConfigureAwait(false);
                parsedResult.StatusCode = head.StatusCode;
                parsedResult.Headers = head.Headers;
                parsedResult.Body = bodyResult.Body;
                parsedResult.BodyFromPool = bodyResult.BodyFromPool;
                parsedResult.SegmentedBody = bodyResult.SegmentedBody;
                parsedResult.Trailers = bodyResult.Trailers;
                parsedResult.KeepAlive = head.KeepAlive;

                var toReturn = parsedResult;
                parsedResult = null;
                return toReturn;
            }
            finally
            {
                if (parsedResult != null)
                    ParsedResponsePool.Return(parsedResult);
            }
        }

        private static async Task<(
            ReadOnlyMemory<byte> Body,
            bool BodyFromPool,
            SegmentedBuffer SegmentedBody,
            HttpHeaders Trailers)>
            ReadBufferedBodyAsync(
            ParsedResponseHead head,
            CancellationToken ct)
        {
            switch (head.BodyKind)
            {
                case Http11ResponseBodyKind.Empty:
                    return (ReadOnlyMemory<byte>.Empty, false, null, HttpHeaders.Empty);

                case Http11ResponseBodyKind.Chunked:
                    var chunkedBody = await ReadChunkedBodyAsync(head.Reader, ct).ConfigureAwait(false);
                    return (
                        ReadOnlyMemory<byte>.Empty,
                        false,
                        chunkedBody.Body,
                        chunkedBody.Trailers);

                case Http11ResponseBodyKind.ContentLength:
                    if (head.ContentLength.GetValueOrDefault() == 0)
                        return (ReadOnlyMemory<byte>.Empty, false, null, HttpHeaders.Empty);

                    var fixedBody = await ReadFixedBodyAsync(
                            head.Reader,
                            checked((int)head.ContentLength.Value),
                            ct)
                        .ConfigureAwait(false);
                    return (fixedBody.Body, fixedBody.BodyFromPool, null, HttpHeaders.Empty);

                case Http11ResponseBodyKind.ReadToEnd:
                    return (
                        ReadOnlyMemory<byte>.Empty,
                        false,
                        await ReadToEndAsync(head.Reader, ct).ConfigureAwait(false),
                        HttpHeaders.Empty);

                default:
                    throw new InvalidOperationException($"Unsupported HTTP/1.1 body kind: {head.BodyKind}");
            }
        }

        private static Http11ResponseBodyKind DetermineBodyKind(
            HttpMethod requestMethod,
            int statusCode,
            HttpHeaders headers,
            RequestContext context,
            out long? contentLength)
        {
            contentLength = null;

            if (requestMethod == HttpMethod.HEAD ||
                statusCode == 101 ||
                (statusCode >= 100 && statusCode < 200) ||
                statusCode == 204 ||
                statusCode == 304)
            {
                return Http11ResponseBodyKind.Empty;
            }

            var transferEncoding = headers.Get("Transfer-Encoding");
            if (transferEncoding != null)
            {
                var te = transferEncoding.Trim();
                if (string.Equals(te, "identity", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryGetValidatedContentLength(headers, out var identityLength))
                    {
                        contentLength = identityLength;
                        return Http11ResponseBodyKind.ContentLength;
                    }

                    return Http11ResponseBodyKind.ReadToEnd;
                }

                if (EndsWithTransferCodingToken(te, "chunked"))
                {
                    if (headers.Get("Content-Length") != null)
                    {
                        context?.RecordEvent(
                            "Http11DualFramingHeaders",
                            new Dictionary<string, object>
                            {
                                ["transfer_encoding"] = te,
                                ["content_length"] = headers.Get("Content-Length")
                            });
                    }

                    return Http11ResponseBodyKind.Chunked;
                }

                throw new NotSupportedException(
                    $"Unsupported Transfer-Encoding: {te}. Only 'chunked' and 'identity' are supported.");
            }

            if (TryGetValidatedContentLength(headers, out var length))
            {
                contentLength = length;
                return Http11ResponseBodyKind.ContentLength;
            }

            return Http11ResponseBodyKind.ReadToEnd;
        }

        private static bool TryGetValidatedContentLength(HttpHeaders headers, out long contentLength)
        {
            contentLength = 0;
            var contentLengthStr = headers.Get("Content-Length");
            if (contentLengthStr == null)
                return false;

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
                CultureInfo.InvariantCulture, out contentLength))
            {
                throw new FormatException("Invalid Content-Length value");
            }

            if (contentLength < 0)
                throw new FormatException("Negative Content-Length");

            if (contentLength > MaxResponseBodySize)
                throw new IOException("Response body exceeds maximum size");

            return true;
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

        internal static bool AddResponseTrailer(HttpHeaders trailers, string line)
        {
            if (trailers == null)
                throw new ArgumentNullException(nameof(trailers));

            if (!TryParseHeaderLine(line, out var name, out var value))
                return false;

            if (!IsValidHeaderFieldName(name))
                return false;

            ValidateHeaderValue(name, value);

            if (TrailerFieldValidator.IsProhibitedResponseTrailer(name))
                return false;

            trailers.Add(name, value);
            return true;
        }

        internal static long ParseChunkSizeLine(string sizeLine)
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
            return ParseChunkSizeToken(sizeSpan);
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

        private static bool EndsWithTransferCodingToken(string transferEncoding, string token)
        {
            if (string.IsNullOrEmpty(transferEncoding))
                return false;

            var trimmed = TrimWhitespace(transferEncoding.AsSpan());
            if (trimmed.Length < token.Length)
                return false;

            var suffix = trimmed.Slice(trimmed.Length - token.Length);
            if (!suffix.Equals(token.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return false;

            if (trimmed.Length == token.Length)
                return true;

            var preceding = trimmed[trimmed.Length - token.Length - 1];
            return preceding == ',' || char.IsWhiteSpace(preceding);
        }

        private static long ParseChunkSizeToken(ReadOnlySpan<char> sizeSpan)
        {
            if (sizeSpan.IsEmpty)
                throw new FormatException("Invalid chunk size: empty");

            long chunkSize = 0;
            for (int i = 0; i < sizeSpan.Length; i++)
            {
                int value = HexValue(sizeSpan[i]);
                if (value < 0)
                    throw new FormatException($"Invalid chunk size: {sizeSpan.ToString()}");

                if (chunkSize > ((long.MaxValue - value) / 16))
                    throw new FormatException($"Invalid chunk size: {sizeSpan.ToString()}");

                chunkSize = (chunkSize * 16) + value;
            }

            return chunkSize;
        }

        private static long ParseChunkSizeToken(ReadOnlySpan<byte> sizeSpan)
        {
            if (sizeSpan.IsEmpty)
                throw new FormatException("Invalid chunk size: empty");

            long chunkSize = 0;
            for (int i = 0; i < sizeSpan.Length; i++)
            {
                int value = HexValue((char)sizeSpan[i]);
                if (value < 0)
                    throw new FormatException($"Invalid chunk size: {EncodingHelper.Latin1.GetString(sizeSpan)}");

                if (chunkSize > ((long.MaxValue - value) / 16))
                    throw new FormatException($"Invalid chunk size: {EncodingHelper.Latin1.GetString(sizeSpan)}");

                chunkSize = (chunkSize * 16) + value;
            }

            return chunkSize;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9')
                return c - '0';
            if (c >= 'A' && c <= 'F')
                return c - 'A' + 10;
            if (c >= 'a' && c <= 'f')
                return c - 'a' + 10;

            return -1;
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
        /// A tuple containing the reassembled body (or <c>null</c> for an empty chunked body)
        /// and any parsed response trailers.
        /// </returns>
        private static async Task<(SegmentedBuffer Body, HttpHeaders Trailers)> ReadChunkedBodyAsync(
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
                    var chunkSize = await reader.ReadChunkSizeAsync(ct, maxLength: 256).ConfigureAwait(false);

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
                    await reader.ReadExpectedCrlfAsync(ct).ConfigureAwait(false);
                }

                var trailers = await ReadChunkedTrailersAsync(reader, ct).ConfigureAwait(false);

                if (segBuffer.IsEmpty)
                {
                    segBuffer.Dispose();
                    return (null, trailers);
                }

                return (segBuffer, trailers);
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

        private static async Task<HttpHeaders> ReadChunkedTrailersAsync(
            BufferedStreamReader reader,
            CancellationToken ct)
        {
            HttpHeaders trailers = null;
            int totalTrailerBytes = 0;
            while (true)
            {
                var line = await reader.ReadLineAsync(ct, MaxHeaderLineLength).ConfigureAwait(false);
                if (string.IsNullOrEmpty(line))
                    break;

                totalTrailerBytes += line.Length;
                if (totalTrailerBytes > MaxTotalTrailerBytes)
                    throw new FormatException("Response trailers exceed maximum size");

                trailers ??= new HttpHeaders();
                AddResponseTrailer(trailers, line);
            }

            return trailers == null || trailers.Count == 0
                ? HttpHeaders.Empty
                : trailers;
        }

        private static void ValidateHeaderValue(string name, string value)
        {
            if (value != null && value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new FormatException($"Header value for '{name}' contains CRLF characters");
        }

        private static bool IsValidHeaderFieldName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var nameSpan = name.AsSpan();
            for (int i = 0; i < nameSpan.Length; i++)
            {
                if (!EncodingHelper.IsRfc9110TChar(nameSpan[i]))
                    return false;
            }

            return true;
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

        internal sealed class BufferedStreamReader : IDisposable
        {
            private const int DefaultBufferSize = 4096;

            private readonly Stream _stream;
            private readonly byte[] _buffer;
            private int _start;
            private int _end;
            private volatile bool _disposed;

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

            public async ValueTask<long> ReadChunkSizeAsync(CancellationToken ct, int maxLength)
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
                                var chunkSize = ParseChunkSizeFromBuffer(_buffer, _start, segmentLength, maxLength);
                                _start = lfIndex + 1;
                                return chunkSize;
                            }

                            if (accumulatorCount + segmentLength > maxLength + 2)
                                throw new FormatException("HTTP header line exceeds maximum length");

                            EnsureCapacity(ref accumulator, accumulatorCount + segmentLength, accumulatorCount);
                            Buffer.BlockCopy(_buffer, _start, accumulator, accumulatorCount, segmentLength);
                            accumulatorCount += segmentLength;
                            _start = lfIndex + 1;
                            return ParseChunkSizeFromBuffer(accumulator, 0, accumulatorCount, maxLength);
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
                                throw new FormatException("Invalid chunk size: empty");

                            return ParseChunkSizeFromBuffer(accumulator, 0, accumulatorCount, maxLength);
                        }
                    }
                }
                finally
                {
                    if (accumulator != null)
                        ArrayPool<byte>.Shared.Return(accumulator, clearArray: true);
                }
            }

            public async ValueTask ReadExpectedCrlfAsync(CancellationToken ct)
            {
                var first = await ReadRequiredByteAsync(ct).ConfigureAwait(false);
                var second = await ReadRequiredByteAsync(ct).ConfigureAwait(false);
                if (first != (byte)'\r' || second != (byte)'\n')
                    throw new FormatException("Unexpected data after chunk body (expected CRLF)");
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

            public Task<int> ReadAsync(byte[] target, int offset, int count, CancellationToken ct)
            {
                if (target == null)
                    throw new ArgumentNullException(nameof(target));
                if (offset < 0 || count < 0 || offset + count > target.Length)
                    throw new ArgumentOutOfRangeException();

                return ReadAsync(new Memory<byte>(target, offset, count), ct).AsTask();
            }

            public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
            {
                if (destination.IsEmpty)
                    return 0;

                int available = _end - _start;
                if (available > 0)
                {
                    int toCopy = Math.Min(available, destination.Length);
                    new ReadOnlyMemory<byte>(_buffer, _start, toCopy).CopyTo(destination);
                    _start += toCopy;
                    return toCopy;
                }

                if (destination.Length >= _buffer.Length)
                    return await _stream.ReadAsync(destination, ct).ConfigureAwait(false);

                int read = await FillBufferAsync(ct).ConfigureAwait(false);
                if (read == 0)
                    return 0;

                int copy = Math.Min(read, destination.Length);
                new ReadOnlyMemory<byte>(_buffer, _start, copy).CopyTo(destination);
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

            private async ValueTask<byte> ReadRequiredByteAsync(CancellationToken ct)
            {
                if (_start < _end)
                    return _buffer[_start++];

                int read = await FillBufferAsync(ct).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException("Unexpected end of stream");

                return _buffer[_start++];
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

            private static long ParseChunkSizeFromBuffer(byte[] source, int offset, int length, int maxLength)
            {
                int end = offset + length;
                if (source[end - 1] == (byte)'\n')
                    end--;
                if (end > offset && source[end - 1] == (byte)'\r')
                    end--;

                int payloadLength = end - offset;
                if (payloadLength > maxLength)
                    throw new FormatException("HTTP header line exceeds maximum length");

                var payload = new ReadOnlySpan<byte>(source, offset, payloadLength);
                int semiIndex = payload.IndexOf((byte)';');
                if (semiIndex >= 0)
                    payload = payload.Slice(0, semiIndex);

                int start = 0;
                while (start < payload.Length && IsAsciiWhitespace(payload[start]))
                    start++;

                int tokenEnd = start;
                while (tokenEnd < payload.Length &&
                    payload[tokenEnd] != (byte)' ' &&
                    payload[tokenEnd] != (byte)'\t')
                {
                    tokenEnd++;
                }

                int endOfToken = tokenEnd - 1;
                while (endOfToken >= start && IsAsciiWhitespace(payload[endOfToken]))
                    endOfToken--;

                if (endOfToken < start)
                    return ParseChunkSizeToken(ReadOnlySpan<byte>.Empty);

                return ParseChunkSizeToken(payload.Slice(start, endOfToken - start + 1));
            }

            private static bool IsAsciiWhitespace(byte value)
            {
                return value == (byte)' ' ||
                    value == (byte)'\t' ||
                    value == (byte)'\r' ||
                    value == (byte)'\n';
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
