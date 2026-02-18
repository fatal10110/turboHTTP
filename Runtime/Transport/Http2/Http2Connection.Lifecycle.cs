using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http2
{
    internal partial class Http2Connection
    {
        // --- Helpers and lifecycle ---

        private int AllocateNextStreamId()
        {
            while (true)
            {
                int current = Volatile.Read(ref _nextStreamId);

                // Keep one increment of headroom so _nextStreamId never wraps into negatives.
                if (current <= 0 || current >= int.MaxValue - 1)
                    throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                        "Stream ID space exhausted, close and reopen connection"));

                int next = current + 2;
                if (Interlocked.CompareExchange(ref _nextStreamId, next, current) == current)
                    return current;
            }
        }

        private async Task SendWindowUpdateAsync(int streamId, int increment, CancellationToken ct)
        {
            var payload = new byte[4];
            payload[0] = (byte)((increment >> 24) & 0x7F);
            payload[1] = (byte)((increment >> 16) & 0xFF);
            payload[2] = (byte)((increment >> 8) & 0xFF);
            payload[3] = (byte)(increment & 0xFF);

            await _writeLock.WaitAsync(CancellationToken.None);
            try
            {
                await _codec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.WindowUpdate,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = payload,
                    Length = 4
                }, CancellationToken.None);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task SendGoAwayAsync(Http2ErrorCode errorCode)
        {
            try
            {
                // Client-initiated GOAWAY should use lastStreamId=0 when server push is disabled.
                int lastStreamId = 0;
                var payload = new byte[8];
                payload[0] = (byte)((lastStreamId >> 24) & 0x7F);
                payload[1] = (byte)((lastStreamId >> 16) & 0xFF);
                payload[2] = (byte)((lastStreamId >> 8) & 0xFF);
                payload[3] = (byte)(lastStreamId & 0xFF);
                payload[4] = (byte)(((uint)errorCode >> 24) & 0xFF);
                payload[5] = (byte)(((uint)errorCode >> 16) & 0xFF);
                payload[6] = (byte)(((uint)errorCode >> 8) & 0xFF);
                payload[7] = (byte)((uint)errorCode & 0xFF);

                await _writeLock.WaitAsync(CancellationToken.None);
                try
                {
                    await _codec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.GoAway,
                        Flags = Http2FrameFlags.None,
                        StreamId = 0,
                        Payload = payload,
                        Length = 8
                    }, CancellationToken.None);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (ObjectDisposedException) { /* Connection being disposed, ignore */ }
        }

        private async Task SendRstStreamAsync(int streamId, Http2ErrorCode errorCode)
        {
            try
            {
                var payload = new byte[4];
                payload[0] = (byte)(((uint)errorCode >> 24) & 0xFF);
                payload[1] = (byte)(((uint)errorCode >> 16) & 0xFF);
                payload[2] = (byte)(((uint)errorCode >> 8) & 0xFF);
                payload[3] = (byte)((uint)errorCode & 0xFF);

                await _writeLock.WaitAsync(CancellationToken.None);
                try
                {
                    await _codec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.RstStream,
                        Flags = Http2FrameFlags.None,
                        StreamId = streamId,
                        Payload = payload,
                        Length = 4
                    }, CancellationToken.None);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (ObjectDisposedException) { /* Connection being disposed, ignore */ }
        }

        private void FailAllStreams(Exception ex)
        {
            _goawayReceived = true;
            _cts.Cancel();

            foreach (var kvp in _activeStreams)
            {
                if (_activeStreams.TryRemove(kvp.Key, out var stream))
                {
                    stream.Fail(ex);
                    stream.Dispose();
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _cts.Cancel();
            SendGoAwayOnDisposeBestEffort();

            FailAllStreams(new ObjectDisposedException(nameof(Http2Connection)));

            _writeLock?.Dispose();
            _cts?.Dispose();
            _windowWaiter?.Dispose();
            _stream?.Dispose();
        }

        private void SendGoAwayOnDisposeBestEffort()
        {
            int lastStreamId = 0;
            var goawayPayload = new byte[8];
            goawayPayload[0] = (byte)((lastStreamId >> 24) & 0x7F);
            goawayPayload[1] = (byte)((lastStreamId >> 16) & 0xFF);
            goawayPayload[2] = (byte)((lastStreamId >> 8) & 0xFF);
            goawayPayload[3] = (byte)(lastStreamId & 0xFF);
            // Error code = NO_ERROR (0x0) — bytes 4-7 are already 0.

            try
            {
                // Best-effort only: don't block dispose if another writer is in-flight.
                if (!_writeLock.Wait(0))
                    return;

                try
                {
                    // Use synchronous writes in Dispose to avoid async context deadlocks.
                    var header = new byte[Http2Constants.FrameHeaderSize];
                    header[0] = 0;
                    header[1] = 0;
                    header[2] = 8; // GOAWAY payload length
                    header[3] = (byte)Http2FrameType.GoAway;
                    header[4] = (byte)Http2FrameFlags.None;
                    header[5] = 0; // stream id = 0
                    header[6] = 0;
                    header[7] = 0;
                    header[8] = 0;

                    _stream.Write(header, 0, header.Length);
                    _stream.Write(goawayPayload, 0, goawayPayload.Length);
                    _stream.Flush();
                }
                catch
                {
                    // Best effort only — Dispose must never block or throw.
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch
            {
                // Best effort only — Dispose must never block or throw.
            }
        }
    }
}
