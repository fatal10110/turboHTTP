using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Transport.Http2
{
    internal partial class Http2Connection
    {
        private async Task SendHeadersAsync(
            int streamId,
            ReadOnlyMemory<byte> headerBlock,
            bool endStream,
            CancellationToken ct)
        {
            int maxPayload = _remoteSettings.MaxFrameSize;

            if (headerBlock.Length <= maxPayload)
            {
                var flags = Http2FrameFlags.EndHeaders;
                if (endStream) flags |= Http2FrameFlags.EndStream;

                await _codec.WriteFrameAsync(
                        Http2FrameType.Headers,
                        flags,
                        streamId,
                        headerBlock,
                        ct)
                    .ConfigureAwait(false);
            }
            else
            {
                int offset = 0;
                bool first = true;

                while (offset < headerBlock.Length)
                {
                    int remaining = headerBlock.Length - offset;
                    int chunkSize = Math.Min(remaining, maxPayload);
                    var payload = headerBlock.Slice(offset, chunkSize);
                    offset += chunkSize;

                    bool isLast = offset >= headerBlock.Length;
                    var frameType = first ? Http2FrameType.Headers : Http2FrameType.Continuation;
                    var flags = first && endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None;
                    if (isLast)
                        flags |= Http2FrameFlags.EndHeaders;

                    await _codec.WriteFrameAsync(
                            frameType,
                            flags,
                            streamId,
                            payload,
                            ct,
                            flush: isLast)
                        .ConfigureAwait(false);
                    first = false;
                }
            }
        }

        private async Task SendDataAsync(
            int streamId,
            UHttpRequestBody content,
            Http2Stream stream,
            CancellationToken ct)
        {
            if (content == null)
                throw new ArgumentNullException(nameof(content));

            if (content.TryGetBufferedData(out var buffered))
            {
                if (buffered.IsEmpty)
                {
                    await SendEmptyEndStreamDataFrameAsync(streamId, stream, ct).ConfigureAwait(false);
                    return;
                }

                await SendBufferedDataAsync(streamId, buffered, stream, endStreamOnFinalFrame: true, ct)
                    .ConfigureAwait(false);
                return;
            }

            using var session = await content.OpenReadSessionAsync(ct).ConfigureAwait(false);
            await SendStreamingDataAsync(streamId, session, stream, ct).ConfigureAwait(false);
        }

        private async Task SendBufferedDataAsync(
            int streamId,
            ReadOnlyMemory<byte> body,
            Http2Stream stream,
            bool endStreamOnFinalFrame,
            CancellationToken ct)
        {
            int offset = 0;
            while (offset < body.Length)
            {
                // M6: Stop sending if the stream was reset by the peer
                if (stream.IsResponseCompleted)
                    break;

                int available = Math.Min(
                    Interlocked.CompareExchange(ref _connectionSendWindow, 0, 0),
                    stream.SendWindowSize);
                available = Math.Min(available, _remoteSettings.MaxFrameSize);
                available = Math.Min(available, body.Length - offset);

                if (available <= 0)
                {
                    await WaitForWindowUpdateAsync(ct);
                    continue;
                }

                await _writeLock.WaitAsync(ct);
                bool lockHeld = true;
                int bytesSent = 0;
                try
                {
                    int connWindow = Interlocked.CompareExchange(ref _connectionSendWindow, 0, 0);
                    int streamWindow = stream.SendWindowSize;
                    int actualAvailable = Math.Min(connWindow, streamWindow);
                    actualAvailable = Math.Min(actualAvailable, _remoteSettings.MaxFrameSize);
                    actualAvailable = Math.Min(actualAvailable, body.Length - offset);

                    if (actualAvailable <= 0)
                    {
                        _writeLock.Release();
                        lockHeld = false;
                        await WaitForWindowUpdateAsync(ct);
                        continue;
                    }

                    bool isLast = endStreamOnFinalFrame && (offset + actualAvailable) >= body.Length;

                    Interlocked.Add(ref _connectionSendWindow, -actualAvailable);
                    stream.AdjustSendWindowSize(-actualAvailable);

                    // Zero-copy: pass the body slice directly — no intermediate ArrayPool copy.
                    // Http2FrameCodec.WriteFrameAsync accepts ReadOnlyMemory<byte> and writes
                    // it straight to the network stream. Request body ownership stays on
                    // UHttpRequest until the outer request lifecycle ends.
                    await _codec.WriteFrameAsync(
                        Http2FrameType.Data,
                        isLast ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                        streamId,
                        body.Slice(offset, actualAvailable),
                        ct);
                    bytesSent = actualAvailable;
                }
                finally
                {
                    if (lockHeld) _writeLock.Release();
                }

                offset += bytesSent;
            }
        }

        private async Task SendStreamingDataAsync(
            int streamId,
            RequestBodyReadSession session,
            Http2Stream stream,
            CancellationToken ct)
        {
            byte[] buffer = null;
            try
            {
                long? remaining = session.ContentLength;
                if (remaining.HasValue)
                {
                    while (remaining.Value > 0)
                    {
                        if (stream.IsResponseCompleted)
                            return;

                        if (buffer == null)
                            buffer = ArrayPool<byte>.Shared.Rent(_streamingSendBufferBytes);

                        int bytesToRead = remaining.Value > buffer.Length
                            ? buffer.Length
                            : (int)remaining.Value;
                        int bytesRead = await session.ReadAsync(
                                new Memory<byte>(buffer, 0, bytesToRead),
                                ct)
                            .ConfigureAwait(false);
                        if (bytesRead <= 0)
                        {
                            throw new IOException(
                                "Request body ended before the declared Content-Length was fully produced.");
                        }

                        remaining -= bytesRead;
                        await SendBufferedDataAsync(
                                streamId,
                                new ReadOnlyMemory<byte>(buffer, 0, bytesRead),
                                stream,
                                endStreamOnFinalFrame: remaining.Value == 0,
                                ct)
                            .ConfigureAwait(false);
                    }

                    return;
                }

                while (true)
                {
                    if (stream.IsResponseCompleted)
                        return;

                    if (buffer == null)
                        buffer = ArrayPool<byte>.Shared.Rent(_streamingSendBufferBytes);

                    int bytesRead = await session.ReadAsync(
                            new Memory<byte>(buffer, 0, buffer.Length),
                            ct)
                        .ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        // Unknown-length bodies signal stream completion with an explicit
                        // zero-length END_STREAM frame once EOF is observed.
                        await SendEmptyEndStreamDataFrameAsync(streamId, stream, ct).ConfigureAwait(false);
                        return;
                    }

                    await SendBufferedDataAsync(
                            streamId,
                            new ReadOnlyMemory<byte>(buffer, 0, bytesRead),
                            stream,
                            endStreamOnFinalFrame: false,
                            ct)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                if (buffer != null)
                    ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task SendEmptyEndStreamDataFrameAsync(
            int streamId,
            Http2Stream stream,
            CancellationToken ct)
        {
            if (stream.IsResponseCompleted)
                return;

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (stream.IsResponseCompleted)
                    return;

                await _codec.WriteFrameAsync(
                        Http2FrameType.Data,
                        Http2FrameFlags.EndStream,
                        streamId,
                        ReadOnlyMemory<byte>.Empty,
                        ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task WaitForWindowUpdateAsync(CancellationToken ct)
        {
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token))
            {
                await _windowWaiter.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            }
        }

        private static string BuildAuthorityValue(Uri uri)
        {
            var host = uri.Host;
            if (uri.HostNameType == UriHostNameType.IPv6)
                host = $"[{host}]";

            bool isDefaultPort = (uri.Scheme == "https" && uri.Port == 443)
                              || (uri.Scheme == "http" && uri.Port == 80);
            return isDefaultPort ? host : $"{host}:{uri.Port}";
        }

        private static bool IsHttp2ForbiddenHeader(string name)
        {
            return string.Equals(name, "connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "transfer-encoding", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "keep-alive", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "proxy-connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "upgrade", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "host", StringComparison.OrdinalIgnoreCase);
        }
    }
}
