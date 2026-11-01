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
                {
                    // Not sent: the request may be replayed on a fresh connection (RFC 9113 Section 5.1.1).
                    _streamIdsExhausted = true;
                    throw CreateNotAcceptingException();
                }

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

                // ct only gates the lock; the frame itself is finished with the connection token.
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await WriteFrameLockedAsync(
                            type,
                            Http2FrameFlags.None,
                            streamId,
                            new ReadOnlyMemory<byte>(payload, 0, payloadLength))
                        .ConfigureAwait(false);
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

        /// <summary>
        /// Removes <paramref name="stream"/> only while it is still the entry for
        /// <paramref name="streamId"/>. Returns true for the one caller that removed it: that caller owns
        /// ending the stream on the wire (RST_STREAM), so a stream is never reset twice.
        /// </summary>
        internal bool RemoveActiveStream(int streamId, Http2Stream stream)
        {
            if (!((ICollection<KeyValuePair<int, Http2Stream>>)_activeStreams)
                    .Remove(new KeyValuePair<int, Http2Stream>(streamId, stream)))
            {
                return false;
            }

            OnActiveStreamRemoved();
            return true;
        }

        /// <summary>
        /// Removes whatever stream is registered under <paramref name="streamId"/> and frees its slot.
        /// </summary>
        private bool TryRemoveActiveStream(int streamId, out Http2Stream stream)
        {
            if (!_activeStreams.TryRemove(streamId, out stream))
                return false;

            OnActiveStreamRemoved();
            return true;
        }

        /// <summary>
        /// Called after a frame carrying the request's END_STREAM was written. The stream leaves the
        /// active map once the response side is closed as well (RFC 9113 Section 5.1).
        /// </summary>
        private void OnRequestEndStreamSent(Http2Stream stream)
        {
            if (stream.MarkLocalClosed())
                RemoveActiveStream(stream.StreamId, stream);
        }

        /// <summary>
        /// Ends a stream whose request body will not be completed with RST_STREAM, unless another path
        /// (peer reset, request cancellation, body-source abort, a stream error) already removed and
        /// reset it. RST_STREAM is only sent after HEADERS, never on an idle stream (RFC 9113 Section 6.4).
        /// </summary>
        private async Task ResetStreamIfActiveAsync(int streamId, Http2Stream stream, Http2ErrorCode errorCode)
        {
            if (!RemoveActiveStream(streamId, stream))
                return;

            TrackRecentlyResetStream(streamId);
            await SendRstStreamAsync(streamId, errorCode).ConfigureAwait(false);
        }

        /// <summary>
        /// Fails a stream found through a lookup (already removed by the caller) under a lease, so a
        /// stream that was returned to the pool or re-rented in the meantime is never touched.
        /// </summary>
        private void FailStreamUnderLease(int streamId, Http2Stream stream, Exception error)
        {
            if (stream == null || !stream.TryAcquireLease(streamId, this))
                return;

            try
            {
                stream.Fail(error);
            }
            finally
            {
                stream.ReleaseLease();
            }
        }

        internal void HandleRequestCancellation(Http2Stream stream, CancellationToken cancellationToken)
        {
            if (stream == null)
                return;

            int streamId = stream.StreamId;

            // Always fault the captured stream so DispatchAsync can finish even if
            // active-stream bookkeeping already raced with completion/cleanup.
            stream.Cancel(cancellationToken);

            if (TryRemoveActiveStream(streamId, out _))
            {
                TrackRecentlyResetStream(streamId);
                _ = SendCancelRstAfterRequestCancellationAsync(streamId);
            }
        }

        private async Task SendCancelRstAfterRequestCancellationAsync(int streamId)
        {
            try
            {
                await SendRstStreamAsync(streamId, Http2ErrorCode.Cancel).ConfigureAwait(false);
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

        /// <summary>
        /// True when no more response frames are expected for the stream: it is no longer active, or
        /// the peer's END_STREAM was received while the request body is still being sent.
        /// </summary>
        internal bool IsStreamClosedForReceive(int streamId)
        {
            return !_activeStreams.TryGetValue(streamId, out var stream) || stream.IsRemoteClosed;
        }

        internal void TrackRecentlyResetStream(int streamId)
        {
            long nowTick = Http2MonotonicClock.GetTimestamp();
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
            ScheduleConnectionWindowUpdate();
        }

        internal void OnResponseBytesReleased(int flowControlledBytes)
        {
            if (flowControlledBytes <= 0)
                return;

            Interlocked.Add(ref _connectionBufferedBytes, -flowControlledBytes);
            ScheduleConnectionWindowUpdate();
        }

        internal async ValueTask OnStreamChunkConsumedAsync(
            Http2Stream stream,
            int flowControlledLength,
            CancellationToken ct)
        {
            if (stream == null || flowControlledLength <= 0 || IsStreamClosedForReceive(stream.StreamId))
                return;

            // Credit the local receive window BEFORE the WINDOW_UPDATE is written (RFC 9113 Section 6.9).
            // The read loop runs concurrently with this consumer: once the frame is on the wire the peer
            // may send DATA using the new credit, and the read loop must already count it. Withdrawn
            // again when the frame was not sent.
            stream.AdjustRecvWindowSize(flowControlledLength);
            try
            {
                await SendWindowUpdateAsync(stream.StreamId, flowControlledLength, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested || ct.IsCancellationRequested)
            {
                // Connection is shutting down, or the consumer gave up before the frame was written.
                stream.AdjustRecvWindowSize(-flowControlledLength);
            }
            catch (ObjectDisposedException)
            {
                // Connection is shutting down.
                stream.AdjustRecvWindowSize(-flowControlledLength);
            }
            catch (Exception ex)
            {
                stream.AdjustRecvWindowSize(-flowControlledLength);
                FailAllStreams(ex);
            }
        }

        internal async Task AbortStreamFromBodySourceAsync(Http2Stream stream)
        {
            if (stream == null)
                return;

            int streamId = stream.StreamId;
            if (!RemoveActiveStream(streamId, stream))
                return; // Another path already ended the stream (and sent its RST_STREAM).

            TrackRecentlyResetStream(streamId);

            try
            {
                await SendRstStreamAsync(streamId, Http2ErrorCode.Cancel).ConfigureAwait(false);
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

                // The read loop can continue decrementing the receive window between the optimistic
                // read above and this serialized update. That may slightly over-advertise capacity,
                // but aggregate buffering remains bounded by MaxConnectionBufferedBytes and the lock
                // prevents overlapping WINDOW_UPDATE writes from compounding the race.
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

        private void ScheduleConnectionWindowUpdate()
        {
            if (_cts.IsCancellationRequested)
                return;

            if (Interlocked.CompareExchange(ref _connectionWindowUpdateScheduled, 1, 0) != 0)
                return;

            _ = RunScheduledConnectionWindowUpdateAsync();
        }

        private async Task RunScheduledConnectionWindowUpdateAsync()
        {
            try
            {
                await MaybeSendConnectionWindowUpdateAsync(CancellationToken.None).ConfigureAwait(false);
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
            finally
            {
                Interlocked.Exchange(ref _connectionWindowUpdateScheduled, 0);

                if (!_cts.IsCancellationRequested && ShouldScheduleConnectionWindowUpdate())
                    ScheduleConnectionWindowUpdate();
            }
        }

        private bool ShouldScheduleConnectionWindowUpdate()
        {
            int currentWindow = Interlocked.CompareExchange(ref _connectionRecvWindow, 0, 0);
            if (currentWindow >= _connectionRecvWindowTarget / 2)
                return false;

            return Interlocked.Read(ref _connectionBufferedBytes) <= _maxConnectionBufferedBytes;
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
                    if (FailConnectionIfSendStalled())
                        return;
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
            long nowTick = Http2MonotonicClock.GetTimestamp();
            long stallTimeoutMs = Volatile.Read(ref _stallTimeoutMs);
            foreach (var kvp in _activeStreams)
            {
                var stream = kvp.Value;
                if (!stream.TryAcquireLease(kvp.Key, this))
                    continue;

                try
                {
                    if (stream.IsStalled(nowTick, stallTimeoutMs))
                        stream.ResponseBodySource?.Abort();
                }
                finally
                {
                    stream.ReleaseLease();
                }
            }
        }

        /// <summary>
        /// Send-side stall bound (#197). A started frame is finished with the connection token only,
        /// never the request token (RFC 9113 Sections 4.1 and 6.10), so a peer that stops reading would
        /// hold the frame write, <see cref="_writeLock"/> and every request deadline behind it with no
        /// bound. When the frame write in flight has made no progress for the stall timeout, the
        /// connection is failed (every stream gets a network error) and disposed: disposal closes the
        /// transport stream, which aborts a socket write that ignores cancellation.
        /// </summary>
        private bool FailConnectionIfSendStalled()
        {
            long stallTimeoutMs = Volatile.Read(ref _stallTimeoutMs);
            if (!_codec.IsWriteStalled(Http2MonotonicClock.GetTimestamp(), stallTimeoutMs))
                return false;

            var error = new UHttpException(new UHttpError(
                UHttpErrorType.NetworkError,
                $"HTTP/2 connection send stalled: a frame write made no progress for {stallTimeoutMs} ms"));
            FailAllStreams(error);
            Dispose();
            return true;
        }

        private void CleanupRecentlyResetStreams()
        {
            TrimRecentlyResetStreams(Http2MonotonicClock.GetTimestamp(), enforceHardLimit: false);
        }

        private void TrimRecentlyResetStreams(long nowTick, bool enforceHardLimit)
        {
            long stallTimeoutMs = Volatile.Read(ref _stallTimeoutMs);
            foreach (var kvp in _recentlyResetStreams)
            {
                if (!Http2MonotonicClock.HasElapsedMilliseconds(kvp.Value, nowTick, stallTimeoutMs))
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
            // Cancelling _cts makes IsAlive and AcceptsNewStreams false; queued requests are refused
            // as not sent (retryable), streams in flight fail with ex.
            _cts.Cancel();
            lock (_streamCreateLock)
                PumpAdmissionWaitersLocked();

            // A connection failure: a request whose response has not started may be replayed on a fresh
            // connection when it is idempotent (RawSocketTransport.ShouldRetryH2Request).
            var streamError = new Http2RetryableRequestException(MapException(ex).HttpError, requestNotProcessed: false);
            foreach (var kvp in _activeStreams)
            {
                if (_activeStreams.TryRemove(kvp.Key, out var stream))
                    FailStreamUnderLease(kvp.Key, stream, streamError);
            }

            // After _cts.Cancel(): blocked senders re-check and leave with a network error.
            WakeSendWindowWaiters();
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _cts.Cancel();
            SendGoAwayOnDisposeBestEffort();

            FailAllStreams(new ObjectDisposedException(nameof(Http2Connection)));
            DisposeStreamOnce();

            var shutdownTasks = CollectBackgroundTasks();
            if (shutdownTasks.IsCompleted)
            {
                FinalizeResourceDispose(shutdownTasks);
                return;
            }

            _ = DisposeResourcesAfterShutdownAsync(shutdownTasks);
        }

        private Task CollectBackgroundTasks()
        {
            var tasks = new List<Task>(3);
            if (_readLoopTask != null)
                tasks.Add(_readLoopTask);
            if (_keepAliveTask != null)
                tasks.Add(_keepAliveTask);
            if (_maintenanceTask != null)
                tasks.Add(_maintenanceTask);

            return tasks.Count == 0
                ? Task.CompletedTask
                : Task.WhenAll(tasks);
        }

        private async Task DisposeResourcesAfterShutdownAsync(Task shutdownTasks)
        {
            try
            {
                await shutdownTasks.ConfigureAwait(false);
            }
            catch
            {
                // Best effort only.
            }
            finally
            {
                FinalizeResourceDispose(shutdownTasks);
            }
        }

        private void FinalizeResourceDispose(Task shutdownTasks)
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
                return;

            try
            {
                shutdownTasks?.GetAwaiter().GetResult();
            }
            catch
            {
                // Best effort only.
            }

            _cts?.Dispose();
            _connectionRecvWindowUpdateLock?.Dispose();
            // _writeLock is deliberately not disposed. A frame write blocked past the shutdown (a
            // socket write may ignore its token, #197) still holds it, and requests queued behind
            // that write wait on it: SemaphoreSlim.Dispose never completes async waiters, and a
            // Release after Dispose throws. Each holder releases it in a finally, so every waiter is
            // granted in turn and fails on the closed connection. No AvailableWaitHandle is ever
            // created, so the semaphore owns no unmanaged resource.
            DisposeStreamOnce();
            // Return the HpackEncoder's reusable output buffer to ArrayPool.
            _hpackEncoder?.Dispose();
        }

        private void DisposeStreamOnce()
        {
            if (Interlocked.Exchange(ref _streamDisposed, 1) == 0)
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
