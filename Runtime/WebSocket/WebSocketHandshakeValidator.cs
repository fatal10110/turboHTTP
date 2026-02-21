using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
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
                    errorBody: await ReadErrorBodyAsync(stream, raw.TrailingBytes, maxErrorBodyBytes, ct).ConfigureAwait(false),
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
                    errorBody: await ReadErrorBodyAsync(stream, raw.TrailingBytes, maxErrorBodyBytes, ct).ConfigureAwait(false),
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
                    int read = await stream.ReadAsync(readBuffer, 0, toRead, ct).ConfigureAwait(false);
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

            var leftBytes = Encoding.ASCII.GetBytes(left);
            var rightBytes = Encoding.ASCII.GetBytes(right);

            if (leftBytes.Length != rightBytes.Length)
                return false;

            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }

        private static async Task<byte[]> ReadErrorBodyAsync(
            Stream stream,
            byte[] prefetched,
            int maxBytes,
            CancellationToken ct)
        {
            if (maxBytes <= 0)
                return Array.Empty<byte>();

            int prefetchedCount = 0;
            if (prefetched != null && prefetched.Length > 0)
                prefetchedCount = Math.Min(prefetched.Length, maxBytes);

            if (prefetchedCount >= maxBytes)
            {
                var prefetchedBody = new byte[prefetchedCount];
                Buffer.BlockCopy(prefetched, 0, prefetchedBody, 0, prefetchedCount);
                return prefetchedBody;
            }

            int readChunkSize = Math.Min(1024, Math.Max(1, maxBytes - prefetchedCount));
            var readBuffer = ArrayPool<byte>.Shared.Rent(readChunkSize);
            try
            {
                using var output = new MemoryStream(prefetchedCount);

                if (prefetchedCount > 0)
                {
                    output.Write(prefetched, 0, prefetchedCount);
                }

                while (output.Length < maxBytes)
                {
                    int remaining = maxBytes - (int)output.Length;
                    int toRead = Math.Min(remaining, readBuffer.Length);
                    int read = await stream.ReadAsync(readBuffer, 0, toRead, ct).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    output.Write(readBuffer, 0, read);
                }

                if (output.Length == 0)
                    return Array.Empty<byte>();

                return output.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(readBuffer);
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
