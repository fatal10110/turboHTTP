using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.WebSocket;

namespace TurboHTTP.WebSocket.Transport
{
    internal static class ProxyTunnelConnector
    {
        public static async Task<Stream> EstablishAsync(
            Stream stream,
            Uri targetUri,
            WebSocketProxySettings proxySettings,
            CancellationToken ct)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (targetUri == null)
                throw new ArgumentNullException(nameof(targetUri));
            if (proxySettings == null)
                throw new ArgumentNullException(nameof(proxySettings));

            int targetPort = targetUri.IsDefaultPort
                ? (string.Equals(targetUri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                : targetUri.Port;

            string authority = targetUri.Host + ":" + targetPort;
            bool attemptedAuth = false;

            while (true)
            {
                string proxyAuthorization = null;
                if (attemptedAuth && proxySettings.Credentials.HasValue)
                {
                    proxyAuthorization = BuildBasicAuthorization(proxySettings.Credentials.Value);
                    Debug.WriteLine(
                        "[TurboHTTP] Using Basic proxy authentication over an unencrypted proxy connection.");
                }

                try
                {
                    await WriteConnectRequestAsync(stream, authority, proxyAuthorization, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    attemptedAuth &&
                    !(ex is OperationCanceledException))
                {
                    throw new WebSocketException(
                        WebSocketError.ProxyTunnelFailed,
                        "Proxy closed the connection while retrying CONNECT after 407.",
                        ex);
                }

                var response = await ReadResponseAsync(stream, ct).ConfigureAwait(false);

                if (response.StatusCode == 200)
                {
                    if (response.PrefetchedBytes.Length == 0)
                        return stream;

                    return new PrefetchedStream(stream, response.PrefetchedBytes);
                }

                if (response.StatusCode == 407)
                {
                    if (!proxySettings.Credentials.HasValue)
                    {
                        throw new WebSocketException(
                            WebSocketError.ProxyAuthenticationRequired,
                            "Proxy authentication is required (407) but no credentials were configured.");
                    }

                    if (attemptedAuth)
                    {
                        throw new WebSocketException(
                            WebSocketError.ProxyTunnelFailed,
                            "Proxy rejected CONNECT after Basic authentication.");
                    }

                    if (!response.CanRetryOnSameConnection)
                    {
                        throw new WebSocketException(
                            WebSocketError.ProxyTunnelFailed,
                            "Proxy 407 response cannot be safely retried on the same connection " +
                            "(unsupported body framing or connection close).");
                    }

                    // Assumes proxies keep the TCP connection alive after 407 when framing permits reuse.
                    attemptedAuth = true;
                    continue;
                }

                throw new WebSocketException(
                    WebSocketError.ProxyTunnelFailed,
                    "Proxy CONNECT failed with status code " + response.StatusCode + ".");
            }
        }

        private static async Task WriteConnectRequestAsync(
            Stream stream,
            string authority,
            string proxyAuthorization,
            CancellationToken ct)
        {
            var builder = new StringBuilder(256);
            builder.Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n");
            builder.Append("Host: ").Append(authority).Append("\r\n");
            builder.Append("Proxy-Connection: Keep-Alive\r\n");
            if (!string.IsNullOrWhiteSpace(proxyAuthorization))
                builder.Append("Proxy-Authorization: ").Append(proxyAuthorization).Append("\r\n");
            builder.Append("\r\n");

            byte[] requestBytes = Encoding.ASCII.GetBytes(builder.ToString());
            await stream.WriteAsync(requestBytes, 0, requestBytes.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static async Task<ConnectResponse> ReadResponseAsync(Stream stream, CancellationToken ct)
        {
            var head = await ReadHeaderBlockAsync(stream, ct).ConfigureAwait(false);
            int statusCode = ParseStatusCode(head.HeaderBlock);
            int contentLength = ParseContentLength(head.HeaderBlock, out bool hasContentLength);
            bool transferChunked = HeaderContainsToken(head.HeaderBlock, "Transfer-Encoding", "chunked");
            bool connectionClose = HeaderContainsToken(head.HeaderBlock, "Connection", "close");

            byte[] prefetchedBytes = Array.Empty<byte>();
            int bufferedByteCount = head.PrefetchedBytes.Length;
            bool canRetryOnSameConnection = !connectionClose;

            if (hasContentLength)
            {
                int bodyBytesFromPrefetch = Math.Min(bufferedByteCount, contentLength);
                int remainingBodyBytes = contentLength - bodyBytesFromPrefetch;
                if (remainingBodyBytes > 0)
                    await DrainBodyAsync(stream, remainingBodyBytes, ct).ConfigureAwait(false);

                if (statusCode == 200 && bufferedByteCount > contentLength)
                {
                    int tunnelPrefetchedLength = bufferedByteCount - contentLength;
                    prefetchedBytes = new byte[tunnelPrefetchedLength];
                    Buffer.BlockCopy(
                        head.PrefetchedBytes,
                        contentLength,
                        prefetchedBytes,
                        0,
                        tunnelPrefetchedLength);
                }
            }
            else if (transferChunked)
            {
                // Chunked CONNECT response bodies are not supported for retry flow; fail clearly.
                if (statusCode != 200)
                    canRetryOnSameConnection = false;
            }
            else if (statusCode == 200 && bufferedByteCount > 0)
            {
                prefetchedBytes = head.PrefetchedBytes;
            }
            else if (statusCode != 200)
            {
                // Without explicit framing, the body boundary is unknown; avoid retry on same connection.
                canRetryOnSameConnection = false;
            }

            return new ConnectResponse(statusCode, prefetchedBytes, canRetryOnSameConnection);
        }

        private static async Task<HeaderBlockReadResult> ReadHeaderBlockAsync(Stream stream, CancellationToken ct)
        {
            byte[] readBuffer = ArrayPool<byte>.Shared.Rent(1024);
            using var collected = new MemoryStream(1024);

            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(readBuffer, 0, readBuffer.Length, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new WebSocketException(
                            WebSocketError.ProxyTunnelFailed,
                            "Proxy closed the connection while waiting for CONNECT response.");
                    }

                    collected.Write(readBuffer, 0, read);
                    int length = checked((int)collected.Length);
                    if (length > 64 * 1024)
                    {
                        throw new WebSocketException(
                            WebSocketError.ProxyTunnelFailed,
                            "Proxy CONNECT response headers are too large.");
                    }

                    byte[] data = collected.GetBuffer();
                    int end = IndexOfHeaderTerminator(data, length);
                    if (end < 0)
                        continue;

                    int headerLength = end + 4;
                    string headerBlock = Encoding.ASCII.GetString(data, 0, headerLength);

                    int prefetchedLength = length - headerLength;
                    if (prefetchedLength <= 0)
                        return new HeaderBlockReadResult(headerBlock, Array.Empty<byte>());

                    byte[] prefetched = new byte[prefetchedLength];
                    Buffer.BlockCopy(data, headerLength, prefetched, 0, prefetchedLength);
                    return new HeaderBlockReadResult(headerBlock, prefetched);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(readBuffer);
            }
        }

        private static int IndexOfHeaderTerminator(byte[] data, int length)
        {
            for (int i = 0; i <= length - 4; i++)
            {
                if (data[i] == (byte)'\r' &&
                    data[i + 1] == (byte)'\n' &&
                    data[i + 2] == (byte)'\r' &&
                    data[i + 3] == (byte)'\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private static int ParseStatusCode(string headerBlock)
        {
            if (string.IsNullOrWhiteSpace(headerBlock))
            {
                throw new WebSocketException(
                    WebSocketError.ProxyTunnelFailed,
                    "Proxy returned an empty CONNECT response.");
            }

            int lineEnd = headerBlock.IndexOf("\r\n", StringComparison.Ordinal);
            string statusLine = lineEnd >= 0
                ? headerBlock.Substring(0, lineEnd)
                : headerBlock;

            string[] parts = statusLine.Split(' ');
            if (parts.Length < 2 || !int.TryParse(parts[1], out int code))
            {
                throw new WebSocketException(
                    WebSocketError.ProxyTunnelFailed,
                    "Proxy returned an invalid CONNECT status line: " + statusLine);
            }

            return code;
        }

        private static int ParseContentLength(string headerBlock, out bool hasContentLength)
        {
            hasContentLength = false;
            string[] lines = headerBlock.Split(new[] { "\r\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                const string prefix = "Content-Length:";
                if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                hasContentLength = true;
                string value = line.Substring(prefix.Length).Trim();
                if (int.TryParse(value, out int contentLength) && contentLength >= 0)
                    return contentLength;

                return 0;
            }

            return 0;
        }

        private static bool HeaderContainsToken(string headerBlock, string headerName, string token)
        {
            if (string.IsNullOrWhiteSpace(headerBlock) ||
                string.IsNullOrWhiteSpace(headerName) ||
                string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            string[] lines = headerBlock.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string prefix = headerName + ":";

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = line.Substring(prefix.Length).Trim();
                string[] tokens = value.Split(',');
                for (int j = 0; j < tokens.Length; j++)
                {
                    if (string.Equals(tokens[j].Trim(), token, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private static async Task DrainBodyAsync(Stream stream, int bodyLength, CancellationToken ct)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(bodyLength, 1024));

            try
            {
                int remaining = bodyLength;
                while (remaining > 0)
                {
                    int toRead = Math.Min(buffer.Length, remaining);
                    int read = await stream.ReadAsync(buffer, 0, toRead, ct).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static string BuildBasicAuthorization(ProxyCredentials credentials)
        {
            string userPass = credentials.Username + ":" + credentials.Password;
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(userPass));
            return "Basic " + base64;
        }

        private readonly struct ConnectResponse
        {
            public ConnectResponse(int statusCode, byte[] prefetchedBytes, bool canRetryOnSameConnection)
            {
                StatusCode = statusCode;
                PrefetchedBytes = prefetchedBytes ?? Array.Empty<byte>();
                CanRetryOnSameConnection = canRetryOnSameConnection;
            }

            public int StatusCode { get; }

            public byte[] PrefetchedBytes { get; }

            public bool CanRetryOnSameConnection { get; }
        }

        private readonly struct HeaderBlockReadResult
        {
            public HeaderBlockReadResult(string headerBlock, byte[] prefetchedBytes)
            {
                HeaderBlock = headerBlock;
                PrefetchedBytes = prefetchedBytes ?? Array.Empty<byte>();
            }

            public string HeaderBlock { get; }

            public byte[] PrefetchedBytes { get; }
        }

        private sealed class PrefetchedStream : Stream
        {
            private readonly Stream _inner;
            private readonly byte[] _prefetched;
            private int _offset;

            public PrefetchedStream(Stream inner, byte[] prefetched)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _prefetched = prefetched ?? Array.Empty<byte>();
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => _inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) =>
                _inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (TryReadPrefetched(buffer, offset, count, out int read))
                    return read;

                return _inner.Read(buffer, offset, count);
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                if (TryReadPrefetched(buffer, offset, count, out int read))
                    return Task.FromResult(read);

                return _inner.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                if (TryReadPrefetched(buffer.Span, out int read))
                    return new ValueTask<int>(read);

                return _inner.ReadAsync(buffer, cancellationToken);
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                _inner.Write(buffer, offset, count);

            public override Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken) =>
                _inner.WriteAsync(buffer, offset, count, cancellationToken);

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default) =>
                _inner.WriteAsync(buffer, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();

                base.Dispose(disposing);
            }

            public override ValueTask DisposeAsync()
            {
                return _inner.DisposeAsync();
            }

            private bool TryReadPrefetched(byte[] buffer, int offset, int count, out int read)
            {
                if (_offset >= _prefetched.Length)
                {
                    read = 0;
                    return false;
                }

                read = Math.Min(count, _prefetched.Length - _offset);
                Buffer.BlockCopy(_prefetched, _offset, buffer, offset, read);
                _offset += read;
                return true;
            }

            private bool TryReadPrefetched(Span<byte> destination, out int read)
            {
                if (_offset >= _prefetched.Length)
                {
                    read = 0;
                    return false;
                }

                read = Math.Min(destination.Length, _prefetched.Length - _offset);
                _prefetched.AsSpan(_offset, read).CopyTo(destination);
                _offset += read;
                return true;
            }
        }
    }
}
