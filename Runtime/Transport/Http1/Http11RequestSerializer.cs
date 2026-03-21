using System;
using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Http1
{
    internal sealed class Http11RequestWriteState
    {
        private int _bodyWriteStarted;
        private long _bodyBytesWritten;

        internal bool HasCommittedBodyBytes => Volatile.Read(ref _bodyWriteStarted) != 0;
        internal long BodyBytesWritten => Interlocked.Read(ref _bodyBytesWritten);

        internal void MarkBodyWriteStarted()
        {
            Interlocked.Exchange(ref _bodyWriteStarted, 1);
        }

        internal void RecordBodyBytesWritten(int bytesWritten)
        {
            if (bytesWritten <= 0)
                return;

            MarkBodyWriteStarted();
            Interlocked.Add(ref _bodyBytesWritten, bytesWritten);
        }
    }

    /// <summary>
    /// Serializes a <see cref="UHttpRequest"/> to HTTP/1.1 wire format.
    /// </summary>
    internal static class Http11RequestSerializer
    {
        private const int MaxChunkHeaderBytes = 18;
        private static readonly byte[] ChunkDataTerminator = { (byte)'\r', (byte)'\n' };
        private static readonly byte[] FinalChunkBytes = { (byte)'0', (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };

        /// <summary>
        /// Serialize the request to the given stream in HTTP/1.1 wire format.
        /// </summary>
        public static async Task SerializeAsync(
            UHttpRequest request,
            Stream stream,
            CancellationToken ct,
            Http11RequestWriteState writeState = null,
            StreamingOptions streamingOptions = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            streamingOptions = streamingOptions ?? new StreamingOptions();

            var bodyWriteMode = ResolveBodyWriteMode(request);
            int smallBufferedRequestThresholdBytes = streamingOptions.SmallBufferedRequestThresholdBytes;
            int streamingSendBufferBytes = streamingOptions.DefaultStreamingSendBufferBytes;

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

            // 3. User headers — framing headers are transport-owned in 22a.
            foreach (var name in request.Headers.Names)
            {
                if (IsFramingHeader(name))
                    continue;

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

            // 4. Transport-owned framing headers.
            if (bodyWriteMode.Kind == RequestBodyWriteKind.KnownLength)
            {
                headerWriter.Append("Content-Length: ");
                headerWriter.AppendLong(bodyWriteMode.KnownLength.Value);
                headerWriter.Append("\r\n");
            }
            else if (bodyWriteMode.Kind == RequestBodyWriteKind.Chunked)
            {
                headerWriter.Append("Transfer-Encoding: chunked\r\n");
            }

            // 5. Auto-add User-Agent
            if (!request.Headers.Contains("User-Agent"))
            {
                headerWriter.Append("User-Agent: TurboHTTP/1.0\r\n");
            }

            // 6. Auto-add Connection: keep-alive
            if (!request.Headers.Contains("Connection"))
            {
                headerWriter.Append("Connection: keep-alive\r\n");
            }

            // 7. End of headers
            headerWriter.Append("\r\n");

            // 8. Small buffered bodies can be appended directly to the header writer so
            // the hot JSON/form path reaches the stream with a single write.
            if (await TryWriteSmallBufferedRequestAsync(
                    headerWriter,
                    bodyWriteMode.BufferedBody,
                    stream,
                    writeState,
                    smallBufferedRequestThresholdBytes,
                    ct).ConfigureAwait(false))
            {
                await stream.FlushAsync(ct).ConfigureAwait(false);
                return;
            }

            // 9. Write header block
            await headerWriter.WriteToAsync(stream, ct).ConfigureAwait(false);

            // 10. Write body
            await WriteBodyAsync(
                    request.Content,
                    bodyWriteMode,
                    stream,
                    writeState,
                    streamingSendBufferBytes,
                    ct)
                .ConfigureAwait(false);

            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static async Task<bool> TryWriteSmallBufferedRequestAsync(
            PooledHeaderWriter headerWriter,
            ReadOnlyMemory<byte> bufferedBody,
            Stream stream,
            Http11RequestWriteState writeState,
            int smallBufferedRequestThresholdBytes,
            CancellationToken ct)
        {
            if (bufferedBody.IsEmpty || bufferedBody.Length > smallBufferedRequestThresholdBytes)
                return false;

            writeState?.RecordBodyBytesWritten(bufferedBody.Length);
            headerWriter.Append(bufferedBody.Span);
            await headerWriter.WriteToAsync(stream, ct).ConfigureAwait(false);
            return true;
        }

        private static RequestBodyWriteMode ResolveBodyWriteMode(UHttpRequest request)
        {
            if (request.TryGetBufferedContent(out var bufferedBody))
            {
                if (bufferedBody.IsEmpty)
                    return RequestBodyWriteMode.None;

                return RequestBodyWriteMode.FromKnownLength(bufferedBody.Length, bufferedBody);
            }

            var contentLength = request.Content.Length;
            if (!contentLength.HasValue)
                return RequestBodyWriteMode.Chunked;

            if (contentLength.Value <= 0)
                return RequestBodyWriteMode.None;

            return RequestBodyWriteMode.FromKnownLength(contentLength.Value, default);
        }

        private static async Task WriteBodyAsync(
            UHttpRequestBody content,
            RequestBodyWriteMode bodyWriteMode,
            Stream stream,
            Http11RequestWriteState writeState,
            int streamingSendBufferBytes,
            CancellationToken ct)
        {
            if (bodyWriteMode.Kind == RequestBodyWriteKind.None)
                return;

            if (!bodyWriteMode.BufferedBody.IsEmpty)
            {
                writeState?.RecordBodyBytesWritten(bodyWriteMode.BufferedBody.Length);
                await stream.WriteAsync(bodyWriteMode.BufferedBody, ct).ConfigureAwait(false);
                return;
            }

            using var session = await content.OpenReadSessionAsync(ct).ConfigureAwait(false);
            if (bodyWriteMode.Kind == RequestBodyWriteKind.Chunked)
            {
                await WriteChunkedBodyAsync(session, stream, writeState, streamingSendBufferBytes, ct).ConfigureAwait(false);
                return;
            }

            await WriteKnownLengthBodyAsync(
                    session,
                    bodyWriteMode.KnownLength.Value,
                    stream,
                    writeState,
                    streamingSendBufferBytes,
                    ct)
                .ConfigureAwait(false);
        }

        private static async Task WriteKnownLengthBodyAsync(
            RequestBodyReadSession session,
            long knownLength,
            Stream stream,
            Http11RequestWriteState writeState,
            int streamingSendBufferBytes,
            CancellationToken ct)
        {
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(streamingSendBufferBytes);
            try
            {
                long remaining = knownLength;
                while (remaining > 0)
                {
                    int bytesToRead = remaining > buffer.Length
                        ? buffer.Length
                        : (int)remaining;

                    int bytesRead = await session.ReadAsync(
                            new Memory<byte>(buffer, 0, bytesToRead),
                            ct)
                        .ConfigureAwait(false);

                    if (bytesRead <= 0)
                    {
                        throw new IOException(
                            "Request body ended before the declared Content-Length was fully produced.");
                    }

                    await stream.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, bytesRead), ct)
                        .ConfigureAwait(false);
                    writeState?.RecordBodyBytesWritten(bytesRead);
                    remaining -= bytesRead;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static async Task WriteChunkedBodyAsync(
            RequestBodyReadSession session,
            Stream stream,
            Http11RequestWriteState writeState,
            int streamingSendBufferBytes,
            CancellationToken ct)
        {
            var bodyBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(streamingSendBufferBytes);
            var headerBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(MaxChunkHeaderBytes);
            try
            {
                while (true)
                {
                    int bytesRead = await session.ReadAsync(
                            new Memory<byte>(bodyBuffer, 0, bodyBuffer.Length),
                            ct)
                        .ConfigureAwait(false);
                    if (bytesRead == 0)
                        break;

                    int headerLength = FormatChunkHeader(bytesRead, headerBuffer);
                    await stream.WriteAsync(new ReadOnlyMemory<byte>(headerBuffer, 0, headerLength), ct)
                        .ConfigureAwait(false);
                    await stream.WriteAsync(new ReadOnlyMemory<byte>(bodyBuffer, 0, bytesRead), ct)
                        .ConfigureAwait(false);
                    writeState?.RecordBodyBytesWritten(bytesRead);
                    await stream.WriteAsync(ChunkDataTerminator, ct).ConfigureAwait(false);
                }

                await stream.WriteAsync(FinalChunkBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(bodyBuffer);
                System.Buffers.ArrayPool<byte>.Shared.Return(headerBuffer);
            }
        }

        private static int FormatChunkHeader(int chunkSize, byte[] destination)
        {
            if (!Utf8Formatter.TryFormat(chunkSize, destination, out int written, new StandardFormat('X')))
            {
                throw new InvalidOperationException("Failed to format HTTP chunk size.");
            }

            destination[written++] = (byte)'\r';
            destination[written++] = (byte)'\n';
            return written;
        }

        private static bool IsFramingHeader(string name)
        {
            return string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateHeader(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Header name cannot be null or empty");

            var nameSpan = name.AsSpan();
            for (int i = 0; i < nameSpan.Length; i++)
            {
                if (!IsRfc9110TChar(nameSpan[i]))
                    throw new ArgumentException($"Header name contains invalid characters: {name}");
            }

            if (value != null && value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new ArgumentException($"Header value for '{name}' contains CRLF characters");
        }

        private static bool IsRfc9110TChar(char c)
        {
            if ((c >= '0' && c <= '9') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z'))
            {
                return true;
            }

            switch (c)
            {
                case '!':
                case '#':
                case '$':
                case '%':
                case '&':
                case '\'':
                case '*':
                case '+':
                case '-':
                case '.':
                case '^':
                case '_':
                case '`':
                case '|':
                case '~':
                    return true;
                default:
                    return false;
            }
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
                if (value > 255)
                    throw new ArgumentException(
                        $"Header character U+{(int)value:X4} is not a valid Latin-1 octet.");
                var span = _writer.GetSpan(1);
                span[0] = (byte)value;
                _writer.Advance(1);
            }

            public void Append(ReadOnlySpan<byte> value)
            {
                if (value.IsEmpty)
                    return;

                value.CopyTo(_writer.GetSpan(value.Length));
                _writer.Advance(value.Length);
            }

            public void AppendInt(int value)
            {
                Span<byte> digits = stackalloc byte[11];
                int pos = digits.Length;

                bool negative = value < 0;
                uint uval = negative
                    ? (uint)(-(long)value)
                    : (uint)value;

                do
                {
                    digits[--pos] = (byte)('0' + (uval % 10));
                    uval /= 10;
                } while (uval > 0);

                if (negative)
                    digits[--pos] = (byte)'-';

                int length = digits.Length - pos;
                digits.Slice(pos, length).CopyTo(_writer.GetSpan(length));
                _writer.Advance(length);
            }

            public void AppendLong(long value)
            {
                Span<byte> digits = stackalloc byte[20];
                int pos = digits.Length;

                bool negative = value < 0;
                ulong uval = negative
                    ? (ulong)(-(value + 1)) + 1UL
                    : (ulong)value;

                do
                {
                    digits[--pos] = (byte)('0' + (uval % 10));
                    uval /= 10;
                } while (uval > 0);

                if (negative)
                    digits[--pos] = (byte)'-';

                int length = digits.Length - pos;
                digits.Slice(pos, length).CopyTo(_writer.GetSpan(length));
                _writer.Advance(length);
            }

            public async Task WriteToAsync(Stream stream, CancellationToken ct)
            {
                if (_writer.WrittenCount == 0)
                    return;

                await stream.WriteAsync(_writer.WrittenMemory, ct).ConfigureAwait(false);
            }

            public void Dispose() => _writer.Dispose();
        }

        private readonly struct RequestBodyWriteMode
        {
            private RequestBodyWriteMode(
                RequestBodyWriteKind kind,
                long? knownLength,
                ReadOnlyMemory<byte> bufferedBody)
            {
                Kind = kind;
                KnownLength = knownLength;
                BufferedBody = bufferedBody;
            }

            public RequestBodyWriteKind Kind { get; }

            public long? KnownLength { get; }

            public ReadOnlyMemory<byte> BufferedBody { get; }

            public static RequestBodyWriteMode None => new RequestBodyWriteMode(
                RequestBodyWriteKind.None,
                null,
                ReadOnlyMemory<byte>.Empty);

            public static RequestBodyWriteMode Chunked => new RequestBodyWriteMode(
                RequestBodyWriteKind.Chunked,
                null,
                ReadOnlyMemory<byte>.Empty);

            public static RequestBodyWriteMode FromKnownLength(long length, ReadOnlyMemory<byte> bufferedBody) =>
                new RequestBodyWriteMode(RequestBodyWriteKind.KnownLength, length, bufferedBody);
        }

        private enum RequestBodyWriteKind
        {
            None,
            Chunked,
            KnownLength
        }
    }
}
