using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.WebSocket
{
    public sealed partial class WebSocketConnection
    {
        public void Abort()
        {
            AbortCore(validateDisposed: true);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            AbortCore(validateDisposed: false);

            _sendLock.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            try
            {
                if (State == WebSocketState.Open || State == WebSocketState.Closing)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await CloseAsync(WebSocketCloseCode.NormalClosure, "Disposing connection.", cts.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    AbortCore(validateDisposed: false);
                }
            }
            catch
            {
                AbortCore(validateDisposed: false);
            }
            finally
            {
                _sendLock.Dispose();
            }
        }

        private void AbortCore(bool validateDisposed)
        {
            if (validateDisposed)
                ThrowIfDisposed();

            if (State == WebSocketState.Closed)
                return;

            FinalizeClose(
                new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure),
                new WebSocketException(WebSocketError.ConnectionClosed, "WebSocket connection aborted."));
        }

        private bool TryTransitionState(WebSocketState expected, WebSocketState next)
        {
            if (!IsTransitionAllowed(expected, next))
                return false;

            int previous = Interlocked.CompareExchange(ref _state, (int)next, (int)expected);
            if (previous != (int)expected)
                return false;

            var handler = StateChanged;
            handler?.Invoke(this, next);
            return true;
        }

        private void FinalizeClose(WebSocketCloseStatus status, Exception terminalError)
        {
            if (Interlocked.CompareExchange(ref _finalized, 1, 0) != 0)
                return;

            _hasCloseStatus = true;
            _closeStatus = status;

            if (terminalError != null)
            {
                _terminalError = terminalError;
                var errorHandler = Error;
                errorHandler?.Invoke(this, terminalError);
            }

            _remoteCloseTcs.TrySetResult(status);

            CancelAndDisposeAfterTaskCompletion(ref _keepAliveCts, _keepAliveTask);
            CancelAndDisposeAfterTaskCompletion(ref _lifecycleCts, _receiveLoopTask);
            Interlocked.Exchange(ref _pongWaiter, null)?.TrySetCanceled();

            _messageAssembler?.Reset();
            _receiveQueue?.Complete(terminalError);

            var state = State;
            while (state != WebSocketState.Closed)
            {
                if (state == WebSocketState.Open)
                {
                    if (TryTransitionState(WebSocketState.Open, WebSocketState.Closed))
                        break;
                }
                else if (state == WebSocketState.Closing)
                {
                    if (TryTransitionState(WebSocketState.Closing, WebSocketState.Closed))
                        break;
                }
                else if (state == WebSocketState.Connecting)
                {
                    if (TryTransitionState(WebSocketState.Connecting, WebSocketState.Closed))
                        break;
                }
                else if (state == WebSocketState.None)
                {
                    if (TryTransitionState(WebSocketState.None, WebSocketState.Closed))
                        break;
                }
                else
                {
                    break;
                }

                state = State;
            }

            SafeDispose(_stream);
            _stream = null;

            SafeDispose(_frameWriter);
            _frameWriter = null;

            DisposeExtensions(_activeExtensions);
            _activeExtensions = Array.Empty<IWebSocketExtension>();

            if (_healthMonitor != null)
            {
                _healthMonitor.OnQualityChanged -= HandleHealthQualityChanged;
                _healthMonitor = null;
            }
        }

        private static void SafeDispose(IDisposable disposable)
        {
            if (disposable == null)
                return;

            try
            {
                disposable.Dispose();
            }
            catch
            {
                // Best effort shutdown path.
            }
        }

        private static void CancelAndDisposeAfterTaskCompletion(
            ref CancellationTokenSource ctsField,
            Task ownerTask)
        {
            var cts = Interlocked.Exchange(ref ctsField, null);
            if (cts == null)
                return;

            try
            {
                cts.Cancel();
            }
            catch
            {
                // Cancellation source may already be disposed in racey shutdown paths.
            }

            if (ownerTask == null || ownerTask.IsCompleted)
            {
                cts.Dispose();
                return;
            }

            _ = ownerTask.ContinueWith(
                static (_, state) =>
                {
                    try
                    {
                        ((CancellationTokenSource)state).Dispose();
                    }
                    catch
                    {
                        // Best effort cleanup.
                    }
                },
                cts,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void TryPublishMetricsUpdate()
        {
            var metrics = _metrics;
            var options = _options;
            if (metrics == null || options == null)
                return;

            if (!metrics.ShouldPublishSnapshot(
                options.MetricsUpdateMessageInterval,
                options.MetricsUpdateInterval))
            {
                return;
            }

            var snapshot = metrics.GetSnapshot();
            _healthMonitor?.RecordMetricsSnapshot(snapshot);

            var handler = MetricsUpdated;
            handler?.Invoke(this, snapshot);
        }

        private void HandleHealthQualityChanged(ConnectionQuality quality)
        {
            var handler = ConnectionQualityChanged;
            handler?.Invoke(this, quality);
        }

        private static void CalculateMessageWireStats(
            int payloadLength,
            int fragmentationThreshold,
            out int frameCount,
            out int totalBytes)
        {
            if (payloadLength <= fragmentationThreshold)
            {
                frameCount = 1;
                totalBytes = CalculateFrameWireLength(payloadLength, masked: true);
                return;
            }

            int bytes = 0;
            int count = 0;
            int remaining = payloadLength;
            while (remaining > 0)
            {
                int fragmentLength = Math.Min(fragmentationThreshold, remaining);
                bytes = checked(bytes + CalculateFrameWireLength(fragmentLength, masked: true));
                count++;
                remaining -= fragmentLength;
            }

            frameCount = count;
            totalBytes = bytes;
        }

        private static int CalculateFrameWireLength(int payloadLength, bool masked)
        {
            int headerLength = 2;
            if (payloadLength > ushort.MaxValue)
            {
                headerLength += 8;
            }
            else if (payloadLength > 125)
            {
                headerLength += 2;
            }

            if (masked)
                headerLength += 4;

            return checked(headerLength + payloadLength);
        }

        private static int GetClosePayloadLength(string reason)
        {
            reason = reason ?? string.Empty;

            int reasonBytes = WebSocketConstants.GetTruncatedCloseReasonByteCount(reason, out _);
            return checked(2 + reasonBytes);
        }

        private void TouchActivity(bool applicationMessage)
        {
            long nowStopwatch = System.Diagnostics.Stopwatch.GetTimestamp();
            long nowUtcTicks = DateTimeOffset.UtcNow.UtcTicks;

            Interlocked.Exchange(ref _lastActivityStopwatchTimestamp, nowStopwatch);
            Interlocked.Exchange(ref _lastActivityUtcTicks, nowUtcTicks);

            if (applicationMessage || Volatile.Read(ref _lastApplicationMessageStopwatchTimestamp) == 0)
                Interlocked.Exchange(ref _lastApplicationMessageStopwatchTimestamp, nowStopwatch);
        }

        private static TimeSpan ElapsedSinceStopwatch(long fromTimestamp)
        {
            if (fromTimestamp <= 0)
                return TimeSpan.MaxValue;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long delta = now - fromTimestamp;
            if (delta <= 0)
                return TimeSpan.Zero;

            double seconds = delta / StopwatchFrequency;
            return TimeSpan.FromSeconds(seconds);
        }

        private static TimeSpan ElapsedBetweenStopwatch(long startTimestamp, long endTimestamp)
        {
            if (startTimestamp <= 0 || endTimestamp <= startTimestamp)
                return TimeSpan.Zero;

            long delta = endTimestamp - startTimestamp;
            double seconds = delta / StopwatchFrequency;
            if (seconds <= 0)
                return TimeSpan.Zero;

            return TimeSpan.FromSeconds(seconds);
        }

        private static async Task AwaitWithCancellation(Task task, CancellationToken ct)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var cancellationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(
                static state => ((TaskCompletionSource<bool>)state).TrySetResult(true),
                cancellationTcs);

            var completed = await Task.WhenAny(task, cancellationTcs.Task).ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
                throw new OperationCanceledException(ct);

            await task.ConfigureAwait(false);
        }

        private async Task ObserveReceiveLoopTerminationAsync()
        {
            var receiveLoopTask = _receiveLoopTask;
            if (receiveLoopTask == null)
                return;

            var completed = await Task.WhenAny(receiveLoopTask, Task.Delay(TimeSpan.FromSeconds(1)))
                .ConfigureAwait(false);

            if (!ReferenceEquals(completed, receiveLoopTask))
                return;

            try
            {
                await receiveLoopTask.ConfigureAwait(false);
            }
            catch
            {
                // FinalizeClose already recorded the terminal error path.
            }
        }

        private static bool IsTransitionAllowed(WebSocketState expected, WebSocketState next)
        {
            if (!AllowedTransitions.TryGetValue(expected, out var allowed))
                return false;

            for (int i = 0; i < allowed.Length; i++)
            {
                if (allowed[i] == next)
                    return true;
            }

            return false;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(WebSocketConnection));
        }

        /// <summary>
        /// Read-through wrapper that drains prefetched bytes before delegating to the inner stream.
        /// </summary>
        private sealed class PrefetchedStream : Stream
        {
            private readonly Stream _inner;
            private readonly byte[] _prefetched;
            private int _prefetchedOffset;
            private int _disposed;

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

            public override void Flush()
            {
                _inner.Flush();
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                return _inner.FlushAsync(cancellationToken);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (TryReadPrefetched(buffer, offset, count, out var read))
                    return read;

                return _inner.Read(buffer, offset, count);
            }

            public override async Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                if (TryReadPrefetched(buffer, offset, count, out var read))
                    return read;

                return await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                if (TryReadPrefetched(buffer.Span, out var read))
                    return new ValueTask<int>(read);

                return _inner.ReadAsync(buffer, cancellationToken);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
            }

            public override Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                return _inner.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                return _inner.WriteAsync(buffer, cancellationToken);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    base.Dispose(disposing);
                    return;
                }

                if (disposing)
                    _inner.Dispose();

                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                await _inner.DisposeAsync().ConfigureAwait(false);
                await base.DisposeAsync().ConfigureAwait(false);
            }

            private bool TryReadPrefetched(byte[] buffer, int offset, int count, out int read)
            {
                if (_prefetchedOffset >= _prefetched.Length)
                {
                    read = 0;
                    return false;
                }

                read = Math.Min(count, _prefetched.Length - _prefetchedOffset);
                Buffer.BlockCopy(_prefetched, _prefetchedOffset, buffer, offset, read);
                _prefetchedOffset += read;
                return true;
            }

            private bool TryReadPrefetched(Span<byte> destination, out int read)
            {
                if (_prefetchedOffset >= _prefetched.Length)
                {
                    read = 0;
                    return false;
                }

                read = Math.Min(destination.Length, _prefetched.Length - _prefetchedOffset);
                _prefetched.AsSpan(_prefetchedOffset, read).CopyTo(destination);
                _prefetchedOffset += read;
                return true;
            }
        }
    }
}
