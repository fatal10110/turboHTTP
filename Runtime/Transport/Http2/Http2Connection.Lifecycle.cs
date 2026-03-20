using System;
using System.Buffers;
using System.Collections.Generic;
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
            await SendFrameWithPooledPayloadAsync(
                Http2FrameType.WindowUpdate,
                streamId,
                4,
                ct,
                payload =>
                {
                    payload[0] = (byte)((increment >> 24) & 0x7F);
                    payload[1] = (byte)((increment >> 16) & 0xFF);
                    payload[2] = (byte)((increment >> 8) & 0xFF);
                    payload[3] = (byte)(increment & 0xFF);
                }).ConfigureAwait(false);
        }

        private async Task SendGoAwayAsync(Http2ErrorCode errorCode)
        {
            await SendFrameWithPooledPayloadAsync(
                Http2FrameType.GoAway,
                0,
                8,
                _cts.Token,
                payload =>
                {
                    // Client-initiated GOAWAY should use lastStreamId=0 when server push is disabled.
                    int lastStreamId = 0;
                    payload[0] = (byte)((lastStreamId >> 24) & 0x7F);
                    payload[1] = (byte)((lastStreamId >> 16) & 0xFF);
                    payload[2] = (byte)((lastStreamId >> 8) & 0xFF);
                    payload[3] = (byte)(lastStreamId & 0xFF);
                    payload[4] = (byte)(((uint)errorCode >> 24) & 0xFF);
                    payload[5] = (byte)(((uint)errorCode >> 16) & 0xFF);
                    payload[6] = (byte)(((uint)errorCode >> 8) & 0xFF);
                    payload[7] = (byte)((uint)errorCode & 0xFF);
                }).ConfigureAwait(false);
        }

        private async Task SendRstStreamAsync(int streamId, Http2ErrorCode errorCode)
        {
            await SendFrameWithPooledPayloadAsync(
                Http2FrameType.RstStream,
                streamId,
                4,
                _cts.Token,
                payload =>
                {
                    payload[0] = (byte)(((uint)errorCode >> 24) & 0xFF);
                    payload[1] = (byte)(((uint)errorCode >> 16) & 0xFF);
                    payload[2] = (byte)(((uint)errorCode >> 8) & 0xFF);
                    payload[3] = (byte)((uint)errorCode & 0xFF);
                }).ConfigureAwait(false);
        }

        private async Task KeepAliveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(KeepAlivePingInterval, ct).ConfigureAwait(false);
                    await SendKeepAlivePingAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected during disposal.
            }
            catch (ObjectDisposedException)
            {
                // Connection is shutting down.
            }
            catch
            {
                // Keepalive must never crash the process.
            }
        }

        private async Task SendKeepAlivePingAsync(CancellationToken ct)
        {
            await SendFrameWithPooledPayloadAsync(
                Http2FrameType.Ping,
                0,
                8,
                ct,
                payload => Array.Clear(payload, 0, 8)).ConfigureAwait(false);
        }

        private async Task SendFrameWithPooledPayloadAsync(
            Http2FrameType type,
            int streamId,
            int payloadLength,
            CancellationToken ct,
            Action<byte[]> fillPayload)
        {
            var payload = ArrayPool<byte>.Shared.Rent(payloadLength);
            try
            {
                fillPayload?.Invoke(payload);

                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _codec.WriteFrameAsync(new Http2Frame
                    {
                        Type = type,
                        Flags = Http2FrameFlags.None,
                        StreamId = streamId,
                        Payload = payload,
                        Length = payloadLength
                    }, ct).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested || ct.IsCancellationRequested)
            {
                // Connection is shutting down.
            }
            catch (ObjectDisposedException)
            {
                // Connection is shutting down.
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }

        internal void RemoveActiveStream(int streamId)
        {
            _activeStreams.TryRemove(streamId, out _);
        }

        internal bool IsStreamClosedForReceive(int streamId)
        {
            return !_activeStreams.ContainsKey(streamId);
        }

        internal void TrackRecentlyResetStream(int streamId)
        {
            long nowTick = Environment.TickCount64;
            _recentlyResetStreams[streamId] = nowTick;

            if (_recentlyResetStreams.Count > RecentlyResetStreamHardLimit)
                TrimRecentlyResetStreams(nowTick, enforceHardLimit: true);
        }

        internal bool IsRecentlyResetStream(int streamId)
        {
            return _recentlyResetStreams.ContainsKey(streamId);
        }

        internal void OnResponseBytesBuffered(int flowControlledBytes)
        {
            if (flowControlledBytes <= 0)
                return;

            // Connection-level buffering follows HTTP/2 flow-controlled bytes, not payload bytes.
            Interlocked.Add(ref _connectionBufferedBytes, flowControlledBytes);
        }

        internal void OnResponseBytesConsumed(int flowControlledBytes)
        {
            if (flowControlledBytes <= 0)
                return;

            Interlocked.Add(ref _connectionBufferedBytes, -flowControlledBytes);
            _ = MaybeSendConnectionWindowUpdateAsync(CancellationToken.None);
        }

        internal void OnResponseBytesReleased(int flowControlledBytes)
        {
            if (flowControlledBytes <= 0)
                return;

            Interlocked.Add(ref _connectionBufferedBytes, -flowControlledBytes);
            _ = MaybeSendConnectionWindowUpdateAsync(CancellationToken.None);
        }

        internal async ValueTask OnStreamChunkConsumedAsync(
            Http2Stream stream,
            int flowControlledLength,
            CancellationToken ct)
        {
            if (stream == null || flowControlledLength <= 0 || IsStreamClosedForReceive(stream.StreamId))
                return;

            try
            {
                await SendWindowUpdateAsync(stream.StreamId, flowControlledLength, ct).ConfigureAwait(false);
                stream.AdjustRecvWindowSize(flowControlledLength);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested || ct.IsCancellationRequested)
            {
                // Connection is shutting down.
            }
            catch (ObjectDisposedException)
            {
                // Connection is shutting down.
            }
            catch (Exception ex)
            {
                FailAllStreams(ex);
            }
        }

        internal async Task AbortStreamFromBodySourceAsync(Http2Stream stream)
        {
            if (stream == null)
                return;

            RemoveActiveStream(stream.StreamId);
            TrackRecentlyResetStream(stream.StreamId);

            try
            {
                await SendRstStreamAsync(stream.StreamId, Http2ErrorCode.Cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                FailAllStreams(ex);
            }
        }

        private async Task MaybeSendConnectionWindowUpdateAsync(CancellationToken ct)
        {
            int currentWindow = Interlocked.CompareExchange(ref _connectionRecvWindow, 0, 0);
            if (currentWindow >= _connectionRecvWindowTarget / 2)
                return;

            if (Interlocked.Read(ref _connectionBufferedBytes) > _maxConnectionBufferedBytes)
                return;

            try
            {
                await _connectionRecvWindowUpdateLock.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested || ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                currentWindow = Interlocked.CompareExchange(ref _connectionRecvWindow, 0, 0);
                if (currentWindow >= _connectionRecvWindowTarget / 2)
                    return;

                if (Interlocked.Read(ref _connectionBufferedBytes) > _maxConnectionBufferedBytes)
                    return;

                int increment = _connectionRecvWindowTarget - currentWindow;
                if (increment <= 0)
                    return;

                Interlocked.Add(ref _connectionRecvWindow, increment);
                await SendWindowUpdateAsync(0, increment, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested || ct.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _connectionRecvWindowUpdateLock.Release();
            }
        }

        private async Task MaintenanceLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(_maintenanceInterval, ct).ConfigureAwait(false);
                    CleanupRecentlyResetStreams();
                    ScanForStalledStreams();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void ScanForStalledStreams()
        {
            long nowTick = Environment.TickCount64;
            long stallTimeoutMs = Volatile.Read(ref _stallTimeoutMs);
            foreach (var kvp in _activeStreams)
            {
                var stream = kvp.Value;
                if (!stream.IsStalled(nowTick, stallTimeoutMs))
                    continue;

                stream.ResponseBodySource?.Abort();
            }
        }

        private void CleanupRecentlyResetStreams()
        {
            TrimRecentlyResetStreams(Environment.TickCount64, enforceHardLimit: false);
        }

        private void TrimRecentlyResetStreams(long nowTick, bool enforceHardLimit)
        {
            long cutoff = nowTick - (long)DefaultHttp2StallTimeout.TotalMilliseconds;
            foreach (var kvp in _recentlyResetStreams)
            {
                if (kvp.Value >= cutoff)
                    continue;

                _recentlyResetStreams.TryRemove(kvp.Key, out _);
            }

            if (!enforceHardLimit)
                return;

            int currentCount = _recentlyResetStreams.Count;
            if (currentCount <= RecentlyResetStreamHardLimit)
                return;

            int excess = currentCount - RecentlyResetStreamSoftLimit;
            if (excess <= 0)
                return;

            var oldestEntries = new List<KeyValuePair<int, long>>(currentCount);
            foreach (var kvp in _recentlyResetStreams)
                oldestEntries.Add(kvp);

            oldestEntries.Sort(static (left, right) => left.Value.CompareTo(right.Value));
            for (int i = 0; i < excess && i < oldestEntries.Count; i++)
                _recentlyResetStreams.TryRemove(oldestEntries[i].Key, out _);
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

            try
            {
                _readLoopTask?.Wait(100);
            }
            catch
            {
                // Best effort only.
            }

            try
            {
                _keepAliveTask?.Wait(100);
            }
            catch
            {
                // Best effort only.
            }

            try
            {
                _maintenanceTask?.Wait(100);
            }
            catch
            {
                // Best effort only.
            }

            _cts?.Dispose();
            _windowWaiter?.Dispose();
            _connectionRecvWindowUpdateLock?.Dispose();
            _writeLock?.Dispose();
            _stream?.Dispose();
            // Return the HpackEncoder's reusable output buffer to ArrayPool.
            _hpackEncoder?.Dispose();
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
