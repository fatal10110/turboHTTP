using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http1
{
    /// <summary>
    /// Head of a proxy's reply to an HTTP/1.1 CONNECT request (RFC 9110 section 9.3.6).
    /// </summary>
    internal sealed class ConnectReply
    {
        internal ConnectReply(int statusCode, HttpHeaders headers, bool isHttp10 = false)
        {
            StatusCode = statusCode;
            Headers = headers ?? throw new ArgumentNullException(nameof(headers));
            IsHttp10 = isHttp10;
        }

        /// <summary>Three-digit status code from the reply's status line.</summary>
        public int StatusCode { get; }

        /// <summary>Header fields of the reply (status line excluded).</summary>
        public HttpHeaders Headers { get; }

        /// <summary>
        /// True for any 2xx reply. RFC 9110 section 9.3.6: the proxy switches to tunnel mode right
        /// after the header block, so Content-Length and Transfer-Encoding in a 2xx reply are ignored
        /// and every following byte belongs to the tunnel.
        /// </summary>
        public bool IsTunnelEstablished => StatusCode >= 200 && StatusCode <= 299;

        /// <summary>True when the final transfer coding of a non-2xx reply is <c>chunked</c>.</summary>
        public bool IsChunked => ConnectReplyReader.EndsWithChunked(Headers.Get("Transfer-Encoding"));

        /// <summary>True when the reply carries a Content-Length field.</summary>
        public bool HasContentLength => Headers.Get("Content-Length") != null;

        /// <summary>True when the reply carries the <c>close</c> connection option.</summary>
        public bool HasConnectionClose => ConnectReplyReader.ContainsToken(Headers.Get("Connection"), "close");

        /// <summary>True when the status line names HTTP/1.0.</summary>
        public bool IsHttp10 { get; }

        /// <summary>
        /// The one reuse rule for a 407 (or any non-2xx) CONNECT reply, shared by the HTTP transport
        /// and the WebSocket proxy tunnel (#229): another CONNECT may follow on the same connection
        /// only when the body has a known length and the proxy keeps the connection open, that is
        /// Content-Length without Transfer-Encoding (RFC 9112 section 6.3 rules 3 and 8: TE with CL
        /// is an error, no framing runs to close), no <c>close</c> option (RFC 9112 section 9.6),
        /// and for HTTP/1.0 an explicit <c>keep-alive</c> (RFC 9112 section 9.3).
        /// </summary>
        public bool CanRetryOnSameConnection =>
            !HasConnectionClose &&
            Headers.Get("Transfer-Encoding") == null &&
            HasContentLength &&
            (!IsHttp10 || ConnectReplyReader.ContainsToken(Headers.Get("Connection"), "keep-alive"));
    }

    /// <summary>
    /// Bounded reader for CONNECT replies, shared by the HTTP transport and the WebSocket proxy
    /// tunnel. It reads one byte at a time, so it never consumes a byte past the header block (the
    /// tunnel's first bytes, or the next reply after a drained 407), and it fails fast on oversized
    /// input: every byte counts against a per-line cap and a header-block cap.
    /// </summary>
    internal static class ConnectReplyReader
    {
        /// <summary>Largest status or field line accepted, CR/LF excluded (RFC 9112 section 2.3 asks for at least 8000).</summary>
        internal const int MaxLineBytes = 8 * 1024;

        /// <summary>Largest header block accepted, status line and line terminators included.</summary>
        internal const int MaxHeaderBytes = 16 * 1024;

        /// <summary>Largest non-2xx body drained so the connection can carry another CONNECT.</summary>
        internal const int MaxDrainBytes = 1024 * 1024;

        /// <summary>
        /// Reads the status line and header block of a CONNECT reply and leaves the stream positioned
        /// right after the blank line that ends the header block.
        /// </summary>
        /// <exception cref="UHttpException">
        /// <see cref="UHttpErrorType.NetworkError"/> on EOF before the header block ends, a malformed
        /// status line, or input beyond <see cref="MaxLineBytes"/> / <see cref="MaxHeaderBytes"/>.
        /// </exception>
        public static async Task<ConnectReply> ReadAsync(Stream stream, CancellationToken ct)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            using var reader = new LineReader(
                stream,
                MaxHeaderBytes,
                "Proxy CONNECT response header block exceeded " + MaxHeaderBytes + " bytes.");

            var statusLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (statusLine == null)
                throw Fail("Proxy closed the connection before sending a CONNECT response.");

            var statusCode = ParseStatusCode(statusLine);
            var isHttp10 = statusLine.StartsWith("HTTP/1.0 ", StringComparison.Ordinal);
            var headers = new HttpHeaders();

            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null)
                    throw Fail("Proxy closed the connection before the CONNECT response header block ended.");

                if (line.Length == 0)
                    break;

                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;

                headers.Add(line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim());
            }

            return new ConnectReply(statusCode, headers, isHttp10);
        }

        /// <summary>
        /// Consumes the body of a non-2xx CONNECT reply (RFC 9112 section 6.3 framing) so the next
        /// CONNECT can reuse the connection. A 2xx reply has no body: this is a no-op for it.
        /// A reply without Content-Length or Transfer-Encoding is not drained; the caller decides
        /// whether such a connection can be reused.
        /// </summary>
        /// <exception cref="UHttpException">
        /// <see cref="UHttpErrorType.NetworkError"/> on EOF inside the body, invalid framing, a
        /// non-chunked Transfer-Encoding, or a body larger than <see cref="MaxDrainBytes"/>.
        /// </exception>
        public static async Task DrainBodyAsync(Stream stream, ConnectReply reply, CancellationToken ct)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (reply == null)
                throw new ArgumentNullException(nameof(reply));
            if (reply.IsTunnelEstablished)
                return;

            const string drainLimitMessage = "Proxy CONNECT response body is too large to drain.";
            var transferEncoding = reply.Headers.Get("Transfer-Encoding");
            if (!string.IsNullOrWhiteSpace(transferEncoding))
            {
                // RFC 9112 section 6.3: Transfer-Encoding overrides Content-Length; a final coding
                // other than chunked means the body runs to connection close.
                if (!EndsWithChunked(transferEncoding))
                {
                    throw Fail(
                        "Proxy CONNECT response used an unsupported Transfer-Encoding. " +
                        "The connection cannot be safely reused.");
                }

                using var chunkedReader = new LineReader(stream, MaxDrainBytes, drainLimitMessage);
                await DrainChunkedAsync(chunkedReader, ct).ConfigureAwait(false);
                return;
            }

            var contentLength = reply.Headers.Get("Content-Length");
            if (contentLength == null)
                return;

            if (!TryParseContentLength(contentLength, out var length))
                throw Fail("Proxy CONNECT response has an invalid Content-Length.");
            if (length > MaxDrainBytes)
                throw Fail(drainLimitMessage);
            if (length == 0)
                return;

            using var reader = new LineReader(stream, MaxDrainBytes, drainLimitMessage);
            await reader.SkipAsync((int)length, ct).ConfigureAwait(false);
        }

        private static async Task DrainChunkedAsync(LineReader reader, CancellationToken ct)
        {
            while (true)
            {
                var sizeLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (sizeLine == null)
                    throw Fail("Unexpected EOF while draining proxy CONNECT response body.");

                long chunkSize;
                try
                {
                    chunkSize = Http11ResponseParser.ParseChunkSizeLine(sizeLine);
                }
                catch (FormatException ex)
                {
                    throw new UHttpException(new UHttpError(
                        UHttpErrorType.NetworkError,
                        "Invalid chunked proxy CONNECT response body.",
                        ex));
                }

                if (chunkSize < 0 || chunkSize > MaxDrainBytes)
                    throw Fail("Proxy CONNECT response body is too large to drain.");

                if (chunkSize == 0)
                {
                    // Trailer section, ended by a blank line.
                    while (true)
                    {
                        var trailer = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (trailer == null)
                            throw Fail("Unexpected EOF while draining proxy CONNECT response body.");
                        if (trailer.Length == 0)
                            return;
                    }
                }

                await reader.SkipAsync((int)chunkSize, ct).ConfigureAwait(false);

                var delimiter = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (delimiter == null || delimiter.Length != 0)
                    throw Fail("Invalid chunked proxy CONNECT response body framing.");
            }
        }

        private static int ParseStatusCode(string statusLine)
        {
            // RFC 9112 section 4: status-line = HTTP-version SP status-code SP [ reason-phrase ]
            if (!statusLine.StartsWith("HTTP/", StringComparison.Ordinal))
                throw Fail("Invalid proxy CONNECT status line.");

            var firstSpace = statusLine.IndexOf(' ');
            if (firstSpace < 0 || statusLine.Length < firstSpace + 4)
                throw Fail("Invalid proxy CONNECT status line.");

            var code = 0;
            for (var i = firstSpace + 1; i < firstSpace + 4; i++)
            {
                var c = statusLine[i];
                if (c < '0' || c > '9')
                    throw Fail("Invalid proxy CONNECT status code.");
                code = (code * 10) + (c - '0');
            }

            if (statusLine.Length > firstSpace + 4 && statusLine[firstSpace + 4] != ' ')
                throw Fail("Invalid proxy CONNECT status code.");
            if (code < 100)
                throw Fail("Invalid proxy CONNECT status code.");

            return code;
        }

        private static bool TryParseContentLength(string value, out long length)
        {
            length = 0;
            value = value.Trim();
            if (value.Length == 0 || value.Length > 18)
                return false;

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c < '0' || c > '9')
                    return false;
                length = (length * 10) + (c - '0');
            }

            return true;
        }

        internal static bool EndsWithChunked(string transferEncoding)
        {
            if (string.IsNullOrWhiteSpace(transferEncoding))
                return false;

            var lastComma = transferEncoding.LastIndexOf(',');
            var last = lastComma >= 0 ? transferEncoding.Substring(lastComma + 1) : transferEncoding;
            return string.Equals(last.Trim(), "chunked", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ContainsToken(string value, string token)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var parts = value.Split(',');
            for (var i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i].Trim(), token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static UHttpException Fail(string message)
        {
            return new UHttpException(new UHttpError(UHttpErrorType.NetworkError, message));
        }

        /// <summary>
        /// Byte-at-a-time line reader over one reply. Owns one pooled line buffer, returned on
        /// <see cref="Dispose"/>; every byte read is charged against <c>budget</c>.
        /// </summary>
        private sealed class LineReader : IDisposable
        {
            private readonly Stream _stream;
            private readonly string _budgetMessage;
            private byte[] _line;
            private byte[] _single;
            private int _budget;

            public LineReader(Stream stream, int budget, string budgetMessage)
            {
                _stream = stream;
                _budget = budget;
                _budgetMessage = budgetMessage;
                _line = ArrayPool<byte>.Shared.Rent(MaxLineBytes);
                _single = ArrayPool<byte>.Shared.Rent(1);
            }

            /// <summary>
            /// Reads one line without its terminator. Returns null on EOF before the first byte of
            /// the line; EOF inside a line is an error. A bare CR is replaced by SP (RFC 9112 section 2.2).
            /// </summary>
            public async Task<string> ReadLineAsync(CancellationToken ct)
            {
                var count = 0;
                var pendingCr = false;

                while (true)
                {
                    var read = await _stream.ReadAsync(_single, 0, 1, ct).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        if (count == 0 && !pendingCr)
                            return null;
                        throw Fail("Proxy closed the connection in the middle of a CONNECT response line.");
                    }

                    Charge(1);
                    var b = _single[0];
                    if (b == (byte)'\n')
                        return count == 0 ? string.Empty : Encoding.ASCII.GetString(_line, 0, count);

                    if (pendingCr)
                    {
                        Append(ref count, (byte)' ');
                        pendingCr = false;
                    }

                    if (b == (byte)'\r')
                    {
                        pendingCr = true;
                        continue;
                    }

                    Append(ref count, b);
                }
            }

            /// <summary>Reads and discards exactly <paramref name="length"/> bytes.</summary>
            public async Task SkipAsync(int length, CancellationToken ct)
            {
                var remaining = length;
                while (remaining > 0)
                {
                    var read = await _stream.ReadAsync(_line, 0, Math.Min(remaining, _line.Length), ct)
                        .ConfigureAwait(false);
                    if (read <= 0)
                        throw Fail("Unexpected EOF while draining proxy CONNECT response body.");

                    Charge(read);
                    remaining -= read;
                }
            }

            public void Dispose()
            {
                var line = _line;
                var single = _single;
                _line = null;
                _single = null;
                if (line != null)
                    ArrayPool<byte>.Shared.Return(line);
                if (single != null)
                    ArrayPool<byte>.Shared.Return(single);
            }

            private void Charge(int bytes)
            {
                _budget -= bytes;
                if (_budget < 0)
                    throw Fail(_budgetMessage);
            }

            private void Append(ref int count, byte value)
            {
                if (count >= MaxLineBytes)
                    throw Fail("Proxy CONNECT response line exceeded " + MaxLineBytes + " bytes.");
                _line[count++] = value;
            }
        }
    }
}
