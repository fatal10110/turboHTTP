using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
            ReadOnlyMemory<byte> body,
            Http2Stream stream,
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

                    bool isLast = (offset + actualAvailable) >= body.Length;

                    Interlocked.Add(ref _connectionSendWindow, -actualAvailable);
                    stream.AdjustSendWindowSize(-actualAvailable);

                    // Zero-copy: pass the body slice directly — no intermediate ArrayPool copy.
                    // Http2FrameCodec.WriteFrameAsync accepts ReadOnlyMemory<byte> and writes
                    // it straight to the network stream. The body memory remains valid because
                    // DisposeBodyOwner() is deferred to the outer SendRequestAsync finally.
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
