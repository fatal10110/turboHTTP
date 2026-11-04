using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Result of a WebSocket HTTP upgrade response validation.
    /// </summary>
    public sealed class WebSocketHandshakeResult
    {
        internal WebSocketHandshakeResult(
            bool success,
            int statusCode,
            string reasonPhrase,
            HttpHeaders responseHeaders,
            string negotiatedSubProtocol,
            IReadOnlyList<string> negotiatedExtensions,
            byte[] errorBody,
            string errorMessage,
            byte[] prefetchedBytes)
        {
            Success = success;
            StatusCode = statusCode;
            ReasonPhrase = reasonPhrase ?? string.Empty;
            ResponseHeaders = responseHeaders ?? new HttpHeaders();
            NegotiatedSubProtocol = negotiatedSubProtocol;
            NegotiatedExtensions = negotiatedExtensions ?? Array.Empty<string>();
            ErrorBody = errorBody ?? Array.Empty<byte>();
            ErrorMessage = errorMessage ?? string.Empty;
            PrefetchedBytes = prefetchedBytes ?? Array.Empty<byte>();
        }

        public bool Success { get; }

        public int StatusCode { get; }

        public string ReasonPhrase { get; }

        public HttpHeaders ResponseHeaders { get; }

        public string NegotiatedSubProtocol { get; }

        public IReadOnlyList<string> NegotiatedExtensions { get; }

        /// <summary>
        /// Bounded body bytes captured for non-101 responses (up to validator limit).
        /// </summary>
        public byte[] ErrorBody { get; }

        public string ErrorMessage { get; }

        /// <summary>
        /// Bytes read after CRLFCRLF while parsing headers. These bytes belong to the
        /// stream after the HTTP response head (usually empty for handshake responses).
        /// </summary>
        public byte[] PrefetchedBytes { get; }

        public string ErrorBodyText => ErrorBody.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(ErrorBody);

        public void ThrowIfFailed()
        {
            if (Success)
                return;

            throw new WebSocketHandshakeException(this);
        }
    }

    public sealed class WebSocketHandshakeException : Exception
    {
        public WebSocketHandshakeException(WebSocketHandshakeResult result)
            : base(CreateMessage(result))
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        public WebSocketHandshakeResult Result { get; }

        private static string CreateMessage(WebSocketHandshakeResult result)
        {
            if (result == null)
                return "WebSocket handshake failed.";

            var message = "WebSocket handshake failed";
            if (result.StatusCode > 0)
            {
                message += " with status " + result.StatusCode.ToString(CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(result.ReasonPhrase))
                    message += " " + result.ReasonPhrase;
            }

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                message += ": " + result.ErrorMessage;

            return message;
        }
    }

    /// <summary>
    /// Validates HTTP upgrade responses for WebSocket handshakes.
    /// </summary>
    public static class WebSocketHandshakeValidator
    {
        private const int DefaultMaxResponseHeaderBytes = 8 * 1024;
        private const int DefaultMaxErrorBodyBytes = 4 * 1024;
        private const string RequiredUpgradeToken = "websocket";
        private const string RequiredConnectionToken = "Upgrade";

        public static async Task<WebSocketHandshakeResult> ValidateAsync(
            Stream stream,
            WebSocketHandshakeRequest request,
            CancellationToken ct,
            int maxResponseHeaderBytes = DefaultMaxResponseHeaderBytes,
            int maxErrorBodyBytes = DefaultMaxErrorBodyBytes)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (maxResponseHeaderBytes < 1024)
                throw new ArgumentOutOfRangeException(nameof(maxResponseHeaderBytes), "Header limit must be >= 1024.");
            if (maxErrorBodyBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(maxErrorBodyBytes), "Body limit must be non-negative.");

            (byte[] HeaderBytes, byte[] TrailingBytes) raw;
            try
            {
                raw = await ReadResponseHeadAsync(stream, ct, maxResponseHeaderBytes).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // One clause: under IL2CPP an exception thrown by an earlier clause of a filtered try is
                // caught by a later one (#320).
                if (ct.IsCancellationRequested)
                {
                    // #226: the caller disposed the stream to abort a read the platform does not cancel;
                    // that is cancellation, not a malformed or missing handshake response.
                    throw new OperationCanceledException("WebSocket handshake response read was canceled.", ex, ct);
                }

                return new WebSocketHandshakeResult(
                    success: false,
                    statusCode: 0,
                    reasonPhrase: string.Empty,
                    responseHeaders: new HttpHeaders(),
                    negotiatedSubProtocol: null,
                    negotiatedExtensions: Array.Empty<string>(),
                    errorBody: Array.Empty<byte>(),
                    errorMessage: ex.Message,
                    prefetchedBytes: Array.Empty<byte>());
            }

            if (!TryParseHttpResponse(raw.HeaderBytes, out var statusCode, out var reasonPhrase, out var headers, out var parseError))
            {
                return new WebSocketHandshakeResult(
                    success: false,
                    statusCode: statusCode,
                    reasonPhrase: reasonPhrase,
                    responseHeaders: headers,
                    negotiatedSubProtocol: null,
                    negotiatedExtensions: Array.Empty<string>(),
                    // The head is malformed, so its framing cannot be trusted: keep only what was read.
                    errorBody: CopyPrefetchedBody(raw.TrailingBytes, maxErrorBodyBytes),
                    errorMessage: parseError,
                    prefetchedBytes: raw.TrailingBytes);
            }

            if (statusCode != 101)
            {
                return new WebSocketHandshakeResult(
                    success: false,
                    statusCode: statusCode,
                    reasonPhrase: reasonPhrase,
                    responseHeaders: headers,
                    negotiatedSubProtocol: null,
                    negotiatedExtensions: Array.Empty<string>(),
                    errorBody: await ReadErrorBodyAsync(
                        stream,
                        statusCode,
                        headers,
                        raw.TrailingBytes,
                        maxErrorBodyBytes,
                        ct).ConfigureAwait(false),
                    errorMessage: "Expected status 101 Switching Protocols.",
                    prefetchedBytes: raw.TrailingBytes);
            }

            if (!HeaderContainsToken(headers, "Upgrade", RequiredUpgradeToken))
            {
                return Fail(
                    statusCode,
                    reasonPhrase,
                    headers,
                    "Missing required Upgrade: websocket token.",
                    raw.TrailingBytes);
            }

            if (!HeaderContainsToken(headers, "Connection", RequiredConnectionToken))
            {
                return Fail(
                    statusCode,
                    reasonPhrase,
                    headers,
                    "Missing required Connection: Upgrade token.",
                    raw.TrailingBytes);
            }

            var acceptValue = headers.Get("Sec-WebSocket-Accept");
            if (string.IsNullOrWhiteSpace(acceptValue))
            {
                return Fail(
                    statusCode,
                    reasonPhrase,
                    headers,
                    "Missing Sec-WebSocket-Accept header.",
                    raw.TrailingBytes);
            }

            var expectedAccept = WebSocketConstants.ComputeAcceptKey(request.ClientKey);
            if (!FixedTimeEqualsAscii(acceptValue.Trim(), expectedAccept))
            {
                return Fail(
                    statusCode,
                    reasonPhrase,
                    headers,
                    "Sec-WebSocket-Accept mismatch.",
                    raw.TrailingBytes);
            }

            if (!TryValidateSubProtocol(headers, request.RequestedSubProtocols, out var selectedSubProtocol, out var subProtocolError))
            {
                return Fail(
                    statusCode,
                    reasonPhrase,
                    headers,
                    subProtocolError,
                    raw.TrailingBytes);
            }

            var negotiatedExtensions = ParseHeaderTokenList(headers.GetValues("Sec-WebSocket-Extensions"));

            return new WebSocketHandshakeResult(
                success: true,
                statusCode: statusCode,
                reasonPhrase: reasonPhrase,
                responseHeaders: headers,
                negotiatedSubProtocol: selectedSubProtocol,
                negotiatedExtensions: negotiatedExtensions,
                errorBody: Array.Empty<byte>(),
                errorMessage: string.Empty,
                prefetchedBytes: raw.TrailingBytes);
        }

        private static async Task<(byte[] HeaderBytes, byte[] TrailingBytes)> ReadResponseHeadAsync(
            Stream stream,
            CancellationToken ct,
            int maxResponseHeaderBytes)
        {
            var headerBuffer = ArrayPool<byte>.Shared.Rent(maxResponseHeaderBytes);
            var readBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(1024, maxResponseHeaderBytes));
            Task<int> abandonedRead = null;

            try
            {
                int totalBytes = 0;

                while (true)
                {
                    int remaining = maxResponseHeaderBytes - totalBytes;
                    if (remaining <= 0)
                    {
                        throw new IOException("Handshake response headers exceeded the configured size limit.");
                    }

                    int toRead = Math.Min(remaining, readBuffer.Length);
                    var pending = stream.ReadAsync(readBuffer, 0, toRead, ct);
                    int read;
                    if (!ct.CanBeCanceled || pending.IsCompleted)
                    {
                        read = await pending.ConfigureAwait(false);
                    }
                    else
                    {
                        // #226/#345: Mono and IL2CPP may never end a pending socket read after the token
                        // fired and the connection disposed the stream, so the token wins the race.
                        // The abandoned read keeps readBuffer (see finally) and its end disposes the
                        // stream, which can no longer be read in order.
                        abandonedRead = pending;
                        read = await AbandonOnCancel.WaitAsync(pending, ct, stream).ConfigureAwait(false);
                        abandonedRead = null;
                    }

                    if (read == 0)
                    {
                        throw new IOException("Unexpected end of stream while reading handshake response headers.");
                    }

                    Buffer.BlockCopy(readBuffer, 0, headerBuffer, totalBytes, read);
                    totalBytes += read;

                    if (TryFindHeaderTerminator(headerBuffer, totalBytes, out var endIndex))
                    {
                        var headerBytes = new byte[endIndex];
                        Buffer.BlockCopy(headerBuffer, 0, headerBytes, 0, endIndex);

                        int trailingCount = totalBytes - endIndex;
                        if (trailingCount <= 0)
                            return (headerBytes, Array.Empty<byte>());

                        var trailing = new byte[trailingCount];
                        Buffer.BlockCopy(headerBuffer, endIndex, trailing, 0, trailingCount);
                        return (headerBytes, trailing);
                    }
                }
            }
            finally
            {
                // An abandoned read may still write into readBuffer: it goes to the GC, not the pool.
                if (abandonedRead == null || abandonedRead.IsCompleted)
                    ArrayPool<byte>.Shared.Return(readBuffer);
                ArrayPool<byte>.Shared.Return(headerBuffer);
            }
        }

        private static bool TryFindHeaderTerminator(byte[] buffer, int length, out int endIndex)
        {
            endIndex = -1;
            if (length < 4)
                return false;

            for (int i = 0; i <= length - 4; i++)
            {
                if (buffer[i] == (byte)'\r' &&
                    buffer[i + 1] == (byte)'\n' &&
                    buffer[i + 2] == (byte)'\r' &&
                    buffer[i + 3] == (byte)'\n')
                {
                    endIndex = i + 4;
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseHttpResponse(
            byte[] headerBytes,
            out int statusCode,
            out string reasonPhrase,
            out HttpHeaders headers,
            out string error)
        {
            statusCode = 0;
            reasonPhrase = string.Empty;
            headers = new HttpHeaders();
            error = string.Empty;

            if (headerBytes == null || headerBytes.Length == 0)
            {
                error = "Handshake response headers are empty.";
                return false;
            }

            var text = Encoding.ASCII.GetString(headerBytes);
            var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);

            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
            {
                error = "Handshake response status line is missing.";
                return false;
            }

            if (!TryParseStatusLine(lines[0], out statusCode, out reasonPhrase, out error))
                return false;

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0)
                    break;

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    error = "Malformed response header line: " + line;
                    return false;
                }

                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();

                if (name.Length == 0)
                {
                    error = "Malformed response header line with empty name.";
                    return false;
                }

                headers.Add(name, value);
            }

            return true;
        }

        private static bool TryParseStatusLine(
            string statusLine,
            out int statusCode,
            out string reasonPhrase,
            out string error)
        {
            statusCode = 0;
            reasonPhrase = string.Empty;
            error = string.Empty;

            int firstSpace = statusLine.IndexOf(' ');
            if (firstSpace <= 0)
            {
                error = "Invalid HTTP status line.";
                return false;
            }

            var version = statusLine.Substring(0, firstSpace);
            if (!version.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                error = "Invalid HTTP version in status line.";
                return false;
            }

            int secondSpace = statusLine.IndexOf(' ', firstSpace + 1);
            var statusCodeText = secondSpace > 0
                ? statusLine.Substring(firstSpace + 1, secondSpace - firstSpace - 1)
                : statusLine.Substring(firstSpace + 1);

            if (!int.TryParse(statusCodeText, NumberStyles.None, CultureInfo.InvariantCulture, out statusCode))
            {
                error = "Invalid HTTP status code in handshake response.";
                return false;
            }
            if (statusCode < 100 || statusCode > 999)
            {
                error = "HTTP status code out of range in handshake response.";
                return false;
            }

            reasonPhrase = secondSpace > 0 ? statusLine.Substring(secondSpace + 1).Trim() : string.Empty;
            return true;
        }

        private static bool HeaderContainsToken(HttpHeaders headers, string name, string token)
        {
            var values = headers.GetValues(name);
            for (int i = 0; i < values.Count; i++)
            {
                if (ContainsToken(values[i], token))
                    return true;
            }

            return false;
        }

        private static bool ContainsToken(string value, string expectedToken)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var parts = value.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i].Trim(), expectedToken, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool TryValidateSubProtocol(
            HttpHeaders headers,
            IReadOnlyList<string> requestedSubProtocols,
            out string selectedSubProtocol,
            out string error)
        {
            selectedSubProtocol = null;
            error = string.Empty;

            var protocolValues = headers.GetValues("Sec-WebSocket-Protocol");
            if (protocolValues.Count == 0)
                return true;

            var selectedTokens = ParseHeaderTokenList(protocolValues);
            if (selectedTokens.Count != 1)
            {
                error = "Server must select exactly one sub-protocol.";
                return false;
            }

            selectedSubProtocol = selectedTokens[0];

            if (requestedSubProtocols == null || requestedSubProtocols.Count == 0)
            {
                error = "Server selected a sub-protocol but client did not request one.";
                return false;
            }

            var requestedSet = new HashSet<string>(requestedSubProtocols, StringComparer.Ordinal);
            if (!requestedSet.Contains(selectedSubProtocol))
            {
                error = "Server selected unsupported sub-protocol: " + selectedSubProtocol;
                return false;
            }

            return true;
        }

        private static IReadOnlyList<string> ParseHeaderTokenList(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
                return Array.Empty<string>();

            var result = new List<string>(values.Count);

            for (int i = 0; i < values.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(values[i]))
                    continue;

                var split = values[i].Split(',');
                for (int j = 0; j < split.Length; j++)
                {
                    var token = split[j].Trim();
                    if (token.Length > 0)
                        result.Add(token);
                }
            }

            return result;
        }

        private static bool FixedTimeEqualsAscii(string left, string right)
        {
            if (left == null || right == null)
                return false;

            if (left.Length != right.Length)
                return false;

            int diff = 0;
            int nonAscii = 0;

            for (int i = 0; i < left.Length; i++)
            {
                char leftChar = left[i];
                char rightChar = right[i];

                diff |= leftChar ^ rightChar;
                nonAscii |= leftChar & ~0x7F;
                nonAscii |= rightChar & ~0x7F;
            }

            return diff == 0 && nonAscii == 0;
        }

        /// <summary>
        /// Reads the body of a non-101 response with the framing its headers declare (RFC 9112 section 6.3),
        /// so a server that keeps the connection open after a 401/403 does not hold the handshake until
        /// <c>HandshakeTimeout</c> (#83). Reading stops at the end of the declared body, at
        /// <paramref name="maxBytes"/>, or at the first error; the status code decides the outcome, so a
        /// reset, malformed chunk or cancellation after the status line keeps whatever was read.
        /// </summary>
        private static async Task<byte[]> ReadErrorBodyAsync(
            Stream stream,
            int statusCode,
            HttpHeaders headers,
            byte[] prefetched,
            int maxBytes,
            CancellationToken ct)
        {
            if (maxBytes <= 0 || !ResponseCanHaveBody(statusCode))
                return Array.Empty<byte>();

            var framing = DetermineErrorBodyFraming(headers, out long contentLength);
            if (framing == ErrorBodyFraming.None)
                return Array.Empty<byte>();

            using var output = new MemoryStream();
            var source = new ErrorBodySource(stream, prefetched);
            try
            {
                switch (framing)
                {
                    case ErrorBodyFraming.ContentLength:
                        await CopyBodyBytesAsync(source, output, Math.Min(contentLength, maxBytes), ct)
                            .ConfigureAwait(false);
                        break;

                    case ErrorBodyFraming.Chunked:
                        await CopyChunkedBodyAsync(source, output, maxBytes, ct).ConfigureAwait(false);
                        break;

                    default:
                        // Close-delimited (RFC 9112 section 6.3, items 4 and 8): until EOF or the limit.
                        await CopyBodyBytesAsync(source, output, maxBytes, ct).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception)
            {
                // Best effort: the error body is diagnostic only.
            }
            finally
            {
                source.Dispose();
            }

            return output.Length == 0 ? Array.Empty<byte>() : output.ToArray();
        }

        private static byte[] CopyPrefetchedBody(byte[] prefetched, int maxBytes)
        {
            if (maxBytes <= 0 || prefetched == null || prefetched.Length == 0)
                return Array.Empty<byte>();

            int count = Math.Min(prefetched.Length, maxBytes);
            var body = new byte[count];
            Buffer.BlockCopy(prefetched, 0, body, 0, count);
            return body;
        }

        private static bool ResponseCanHaveBody(int statusCode)
        {
            // RFC 9112 section 6.3, item 1.
            return statusCode >= 200 && statusCode != 204 && statusCode != 304;
        }

        private static ErrorBodyFraming DetermineErrorBodyFraming(HttpHeaders headers, out long contentLength)
        {
            contentLength = 0;
            if (headers == null)
                return ErrorBodyFraming.CloseDelimited;

            var transferEncoding = headers.GetValues("Transfer-Encoding");
            if (transferEncoding != null && transferEncoding.Count > 0)
            {
                // Transfer-Encoding overrides Content-Length; without chunked as the final coding the body
                // is close-delimited (RFC 9112 section 6.3, items 3 and 4).
                return IsChunkedFinalCoding(transferEncoding)
                    ? ErrorBodyFraming.Chunked
                    : ErrorBodyFraming.CloseDelimited;
            }

            var lengthValues = headers.GetValues("Content-Length");
            if (lengthValues != null && lengthValues.Count > 0)
            {
                // An invalid Content-Length is unrecoverable framing (RFC 9112 section 6.3, item 5): skip
                // the body rather than guess its end.
                if (!TryParseContentLength(lengthValues, out contentLength))
                    return ErrorBodyFraming.None;

                return contentLength == 0 ? ErrorBodyFraming.None : ErrorBodyFraming.ContentLength;
            }

            return ErrorBodyFraming.CloseDelimited;
        }

        private static bool IsChunkedFinalCoding(IReadOnlyList<string> values)
        {
            string last = null;
            for (int i = 0; i < values.Count; i++)
            {
                var value = values[i];
                if (string.IsNullOrEmpty(value))
                    continue;

                var parts = value.Split(',');
                for (int j = 0; j < parts.Length; j++)
                {
                    var token = parts[j].Trim();
                    int parameters = token.IndexOf(';');
                    if (parameters >= 0)
                        token = token.Substring(0, parameters).Trim();

                    if (token.Length > 0)
                        last = token;
                }
            }

            return last != null && string.Equals(last, "chunked", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseContentLength(IReadOnlyList<string> values, out long length)
        {
            // A list of identical values is one length (RFC 9110 section 8.6); anything else is invalid.
            length = -1;
            for (int i = 0; i < values.Count; i++)
            {
                var parts = (values[i] ?? string.Empty).Split(',');
                for (int j = 0; j < parts.Length; j++)
                {
                    var token = parts[j].Trim();
                    if (token.Length == 0 || token.Length > 18)
                        return false;

                    long parsed = 0;
                    for (int k = 0; k < token.Length; k++)
                    {
                        char c = token[k];
                        if (c < '0' || c > '9')
                            return false;
                        parsed = parsed * 10 + (c - '0');
                    }

                    if (length >= 0 && parsed != length)
                        return false;
                    length = parsed;
                }
            }

            return length >= 0;
        }

        private static async Task<bool> CopyBodyBytesAsync(
            ErrorBodySource source,
            MemoryStream output,
            long count,
            CancellationToken ct)
        {
            long remaining = count;
            while (remaining > 0)
            {
                var segment = await source.ReadAsync((int)Math.Min(remaining, int.MaxValue), ct).ConfigureAwait(false);
                if (segment.Count == 0)
                    return false;

                output.Write(segment.Array, segment.Offset, segment.Count);
                remaining -= segment.Count;
            }

            return true;
        }

        private static async Task CopyChunkedBodyAsync(
            ErrorBodySource source,
            MemoryStream output,
            int maxBytes,
            CancellationToken ct)
        {
            // RFC 9112 section 7.1: chunk-size [ chunk-ext ] CRLF chunk-data CRLF ... last-chunk. Trailers are
            // not read: the connection is never reused after a failed upgrade.
            var line = new byte[MaxChunkLineBytes];
            while (output.Length < maxBytes)
            {
                int lineLength = await source.ReadLineAsync(line, ct).ConfigureAwait(false);
                if (lineLength < 0 || !TryParseChunkSize(line, lineLength, out long chunkSize) || chunkSize == 0)
                    return;

                long take = Math.Min(chunkSize, maxBytes - output.Length);
                if (!await CopyBodyBytesAsync(source, output, take, ct).ConfigureAwait(false) || take < chunkSize)
                    return;

                if (await source.ReadLineAsync(line, ct).ConfigureAwait(false) != 0)
                    return;
            }
        }

        private static bool TryParseChunkSize(byte[] line, int length, out long size)
        {
            size = 0;
            int digits = 0;
            for (int i = 0; i < length; i++)
            {
                byte b = line[i];
                int value;
                if (b >= '0' && b <= '9')
                    value = b - '0';
                else if (b >= 'a' && b <= 'f')
                    value = b - 'a' + 10;
                else if (b >= 'A' && b <= 'F')
                    value = b - 'A' + 10;
                else if (b == ';' || b == ' ' || b == '\t')
                    break;
                else
                    return false;

                if (++digits > 15)
                    return false;

                size = (size << 4) | (long)value;
            }

            return digits > 0;
        }

        private const int MaxChunkLineBytes = 1024;

        private enum ErrorBodyFraming
        {
            None,
            ContentLength,
            Chunked,
            CloseDelimited
        }

        /// <summary>
        /// Serves the bytes read past the response head first, then the stream, through one pooled buffer.
        /// </summary>
        private sealed class ErrorBodySource : IDisposable
        {
            private readonly Stream _stream;
            private byte[] _prefetched;
            private int _prefetchedOffset;
            private byte[] _buffer;
            private int _bufferOffset;
            private int _bufferCount;
            private Task<int> _abandonedRead;

            public ErrorBodySource(Stream stream, byte[] prefetched)
            {
                _stream = stream;
                _prefetched = prefetched ?? Array.Empty<byte>();
            }

            public async ValueTask<ArraySegment<byte>> ReadAsync(int maxCount, CancellationToken ct)
            {
                if (maxCount <= 0)
                    return new ArraySegment<byte>(Array.Empty<byte>());

                if (_prefetchedOffset < _prefetched.Length)
                {
                    int count = Math.Min(maxCount, _prefetched.Length - _prefetchedOffset);
                    var segment = new ArraySegment<byte>(_prefetched, _prefetchedOffset, count);
                    _prefetchedOffset += count;
                    return segment;
                }

                if (_bufferOffset >= _bufferCount)
                {
                    if (_buffer == null)
                        _buffer = ArrayPool<byte>.Shared.Rent(1024);

                    _bufferOffset = 0;
                    _bufferCount = 0;
                    var pending = _stream.ReadAsync(_buffer, 0, _buffer.Length, ct);
                    if (!ct.CanBeCanceled || pending.IsCompleted)
                    {
                        _bufferCount = await pending.ConfigureAwait(false);
                    }
                    else
                    {
                        // #345: as the head read, the token wins over a read the platform never ends;
                        // the abandoned read keeps _buffer (Dispose) and its end disposes the stream.
                        _abandonedRead = pending;
                        _bufferCount = await AbandonOnCancel.WaitAsync(pending, ct, _stream).ConfigureAwait(false);
                        _abandonedRead = null;
                    }

                    if (_bufferCount <= 0)
                    {
                        _bufferCount = 0;
                        return new ArraySegment<byte>(Array.Empty<byte>());
                    }
                }

                int fromBuffer = Math.Min(maxCount, _bufferCount - _bufferOffset);
                var result = new ArraySegment<byte>(_buffer, _bufferOffset, fromBuffer);
                _bufferOffset += fromBuffer;
                return result;
            }

            /// <summary>
            /// Reads one LF-terminated line (a trailing CR is dropped) into <paramref name="line"/>; returns its
            /// length, or -1 at EOF or when the line does not fit.
            /// </summary>
            public async ValueTask<int> ReadLineAsync(byte[] line, CancellationToken ct)
            {
                int length = 0;
                while (true)
                {
                    var segment = await ReadAsync(1, ct).ConfigureAwait(false);
                    if (segment.Count == 0)
                        return -1;

                    byte b = segment.Array[segment.Offset];
                    if (b == (byte)'\n')
                    {
                        if (length > 0 && line[length - 1] == (byte)'\r')
                            length--;
                        return length;
                    }

                    if (length == line.Length)
                        return -1;

                    line[length++] = b;
                }
            }

            public void Dispose()
            {
                var buffer = _buffer;
                _buffer = null;
                _prefetched = Array.Empty<byte>();
                var abandonedRead = _abandonedRead;
                if (buffer != null && (abandonedRead == null || abandonedRead.IsCompleted))
                    ArrayPool<byte>.Shared.Return(buffer);
            }
        }


        private static WebSocketHandshakeResult Fail(
            int statusCode,
            string reasonPhrase,
            HttpHeaders headers,
            string errorMessage,
            byte[] prefetched)
        {
            return new WebSocketHandshakeResult(
                success: false,
                statusCode: statusCode,
                reasonPhrase: reasonPhrase,
                responseHeaders: headers,
                negotiatedSubProtocol: null,
                negotiatedExtensions: Array.Empty<string>(),
                errorBody: Array.Empty<byte>(),
                errorMessage: errorMessage,
                prefetchedBytes: prefetched ?? Array.Empty<byte>());
        }
    }
}
