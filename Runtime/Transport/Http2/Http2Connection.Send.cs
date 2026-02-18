using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Http2
{
    internal partial class Http2Connection
    {
        private async Task SendHeadersAsync(int streamId, byte[] headerBlock, bool endStream,
            CancellationToken ct)
        {
            int maxPayload = _remoteSettings.MaxFrameSize;

            if (headerBlock.Length <= maxPayload)
            {
                var flags = Http2FrameFlags.EndHeaders;
                if (endStream) flags |= Http2FrameFlags.EndStream;

                await _codec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = flags,
                    StreamId = streamId,
                    Payload = headerBlock,
                    Length = headerBlock.Length
                }, ct);
            }
            else
            {
                var firstPayload = new byte[maxPayload];
                Array.Copy(headerBlock, 0, firstPayload, 0, maxPayload);
                int offset = maxPayload;

                var headersFlags = endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None;
                await _codec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = headersFlags,
                    StreamId = streamId,
                    Payload = firstPayload,
                    Length = firstPayload.Length
                }, ct, flush: false);

                while (offset < headerBlock.Length)
                {
                    int remaining = headerBlock.Length - offset;
                    int chunkSize = Math.Min(remaining, maxPayload);
                    var chunk = new byte[chunkSize];
                    Array.Copy(headerBlock, offset, chunk, 0, chunkSize);
                    offset += chunkSize;

                    bool isLast = offset >= headerBlock.Length;
                    await _codec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Continuation,
                        Flags = isLast ? Http2FrameFlags.EndHeaders : Http2FrameFlags.None,
                        StreamId = streamId,
                        Payload = chunk,
                        Length = chunkSize
                    }, ct, flush: isLast);
                }
            }
        }

        private async Task SendDataAsync(int streamId, byte[] body, Http2Stream stream,
            CancellationToken ct)
        {
            int offset = 0;
            while (offset < body.Length)
            {
                // M6: Stop sending if the stream was reset by the peer
                if (stream.ResponseTcs.Task.IsCompleted)
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
                byte[] payload = null;
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

                    bool isLast = (offset + actualAvailable) >= body.Length;
                    payload = ArrayPool<byte>.Shared.Rent(actualAvailable);
                    Buffer.BlockCopy(body, offset, payload, 0, actualAvailable);

                    Interlocked.Add(ref _connectionSendWindow, -actualAvailable);
                    stream.AdjustSendWindowSize(-actualAvailable);

                    await _codec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Data,
                        Flags = isLast ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                        StreamId = streamId,
                        Payload = payload,
                        Length = actualAvailable
                    }, ct);
                    bytesSent = actualAvailable;
                }
                finally
                {
                    if (payload != null)
                        ArrayPool<byte>.Shared.Return(payload);
                    if (lockHeld) _writeLock.Release();
                }

                offset += bytesSent;
            }
        }

        private async Task WaitForWindowUpdateAsync(CancellationToken ct)
        {
            await _windowWaiter.WaitAsync(ct);
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
