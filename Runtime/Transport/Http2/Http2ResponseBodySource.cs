using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http2
{
    internal enum Http2ResponseBodyEnqueueResult
    {
        Accepted,
        BufferFull,
        Aborted
    }

    internal struct Http2ResponseBodyChunk
    {
        public byte[] Buffer;
        public int Length;
        public int FlowControlledLength;
    }

    internal sealed class Http2ResponseBodySource : IResponseBodySource
    {
        private const int TerminalStateActive = 0;
        private const int TerminalStateCompleted = 1;
        private const int TerminalStateFaulted = 2;
        private const int TerminalStateAborted = 3;

        private readonly Http2Connection _connection;
        private readonly Http2Stream _stream;
        private readonly SingleReaderChannel<Http2ResponseBodyChunk> _queue;
        private readonly object _cleanupGate = new object();
        private readonly object _currentChunkGate = new object();
        private readonly TaskCompletionSource<HttpHeaders> _trailersSource =
            new TaskCompletionSource<HttpHeaders>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly long? _length;
        private readonly int _bufferCapacity;

        private Http2ResponseBodyChunk _currentChunk;
        private int _currentChunkOffset;
        // `_bufferedBytes` tracks payload bytes currently held in pooled chunk buffers.
        // Connection-level accounting separately tracks flow-controlled bytes, including padding.
        private int _bufferedBytes;
        private int _disposed;
        private long _lastConsumptionTick;
        private int _terminalState;
        private Task _cleanupTask;

        internal Http2ResponseBodySource(
            Http2Connection connection,
            Http2Stream stream,
            long? length,
            bool completed)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _length = length;
            _bufferCapacity = connection.PerStreamReceiveBufferBytes;
            _queue = new SingleReaderChannel<Http2ResponseBodyChunk>(int.MaxValue);
            Interlocked.Exchange(ref _lastConsumptionTick, Environment.TickCount64);

            if (completed)
            {
                Volatile.Write(ref _terminalState, TerminalStateCompleted);
                _queue.Complete();
                _trailersSource.TrySetResult(HttpHeaders.Empty);
            }
        }

        public long? Length => _length;

        internal int BufferCapacity => _bufferCapacity;

        internal long LastConsumptionTick => Interlocked.Read(ref _lastConsumptionTick);

        internal bool IsAborted => Volatile.Read(ref _terminalState) == TerminalStateAborted;

        public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            ThrowIfDisposed();
            data = default;
            return false;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            if (destination.IsEmpty)
                return 0;

            while (true)
            {
                if (TryReadCurrentChunk(destination, out var bytesRead, out var completedChunk))
                {
                    if (completedChunk.Buffer != null)
                        await CompleteDetachedChunkAsync(completedChunk, ct).ConfigureAwait(false);

                    if (bytesRead > 0)
                        return bytesRead;

                    continue;
                }

                Http2ResponseBodyChunk nextChunk;
                try
                {
                    if (!_queue.TryRead(out nextChunk))
                        nextChunk = await _queue.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (Volatile.Read(ref _terminalState) == TerminalStateCompleted)
                {
                    return 0;
                }

                if (TrySetCurrentChunk(nextChunk))
                    continue;

                ReleaseDetachedChunk(nextChunk);
                ThrowIfDisposed();
            }
        }

        public async ValueTask DrainAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            int terminalState = Volatile.Read(ref _terminalState);
            if (terminalState == TerminalStateCompleted)
                return;

            if (terminalState == TerminalStateFaulted)
            {
                await _trailersSource.Task.ConfigureAwait(false);
                return;
            }

            await BeginCleanup(sendRst: !_connection.IsStreamClosedForReceive(_stream.StreamId))
                .ConfigureAwait(false);
        }

        public void Abort()
        {
            BeginCleanup(sendRst: !_connection.IsStreamClosedForReceive(_stream.StreamId));
        }

        public async ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            var trailersTask = _trailersSource.Task;
            if (trailersTask.IsCompleted)
                return await trailersTask.ConfigureAwait(false);

            if (!ct.CanBeCanceled)
                return await trailersTask.ConfigureAwait(false);

            var cancellationTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(
                static state => ((TaskCompletionSource<bool>)state).TrySetResult(true),
                cancellationTcs);

            var completed = await Task.WhenAny(trailersTask, cancellationTcs.Task).ConfigureAwait(false);
            if (!ReferenceEquals(completed, trailersTask))
                throw new OperationCanceledException(ct);

            return await trailersTask.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(BeginCleanup(sendRst: !_connection.IsStreamClosedForReceive(_stream.StreamId)));
        }

        internal Http2ResponseBodyEnqueueResult TryEnqueueData(
            byte[] source,
            int offset,
            int length,
            int flowControlledLength)
        {
            if (length <= 0)
                return Http2ResponseBodyEnqueueResult.Accepted;

            if (Volatile.Read(ref _terminalState) == TerminalStateAborted)
                return Http2ResponseBodyEnqueueResult.Aborted;

            if (!TryReserveBufferedBytes(length))
                return Http2ResponseBodyEnqueueResult.BufferFull;

            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                Buffer.BlockCopy(source, offset, buffer, 0, length);
                var chunk = new Http2ResponseBodyChunk
                {
                    Buffer = buffer,
                    Length = length,
                    FlowControlledLength = flowControlledLength
                };

                var written = false;
                try
                {
                    written = _queue.TryWrite(chunk);
                }
                catch (InvalidOperationException)
                {
                    written = false;
                }

                if (written)
                {
                    _connection.OnResponseBytesBuffered(flowControlledLength);
                    buffer = null;
                    return Http2ResponseBodyEnqueueResult.Accepted;
                }
            }
            finally
            {
                if (buffer != null)
                    ArrayPool<byte>.Shared.Return(buffer);
            }

            Interlocked.Add(ref _bufferedBytes, -length);
            return Volatile.Read(ref _terminalState) == TerminalStateAborted
                ? Http2ResponseBodyEnqueueResult.Aborted
                : Http2ResponseBodyEnqueueResult.BufferFull;
        }

        internal void Complete()
        {
            if (Interlocked.CompareExchange(
                    ref _terminalState,
                    TerminalStateCompleted,
                    TerminalStateActive) != TerminalStateActive)
            {
                if (_trailersSource.Task.IsCompleted)
                    return;

                _trailersSource.TrySetResult(HttpHeaders.Empty);
                return;
            }

            _queue.Complete();
            _trailersSource.TrySetResult(HttpHeaders.Empty);
        }

        internal void SetTrailers(HttpHeaders trailers)
        {
            _trailersSource.TrySetResult(trailers ?? HttpHeaders.Empty);
        }

        internal void Fault(Exception error)
        {
            if (error == null)
                error = new InvalidOperationException("HTTP/2 response body faulted.");

            if (Interlocked.CompareExchange(
                    ref _terminalState,
                    TerminalStateFaulted,
                    TerminalStateActive) != TerminalStateActive)
            {
                _trailersSource.TrySetException(error);
                return;
            }

            _queue.Complete(error);
            _trailersSource.TrySetException(error);
        }

        internal bool IsStalled(long nowTick, long stallTimeoutMs)
        {
            if (Volatile.Read(ref _terminalState) != TerminalStateActive)
                return false;

            if (Volatile.Read(ref _bufferedBytes) <= 0)
                return false;

            return nowTick - LastConsumptionTick > stallTimeoutMs;
        }

        private async ValueTask CompleteDetachedChunkAsync(
            Http2ResponseBodyChunk chunk,
            CancellationToken ct)
        {
            if (chunk.Buffer != null)
            {
                ArrayPool<byte>.Shared.Return(chunk.Buffer);
                Interlocked.Add(ref _bufferedBytes, -chunk.Length);
                _connection.OnResponseBytesConsumed(chunk.FlowControlledLength);
            }

            await _connection.OnStreamChunkConsumedAsync(_stream, chunk.FlowControlledLength, ct)
                .ConfigureAwait(false);
        }

        private Task BeginCleanup(bool sendRst)
        {
            lock (_cleanupGate)
            {
                if (_cleanupTask != null)
                    return _cleanupTask;

                Volatile.Write(ref _terminalState, TerminalStateAborted);
                Interlocked.Exchange(ref _disposed, 1);
                _cleanupTask = CleanupAsync(sendRst);
                return _cleanupTask;
            }
        }

        private async Task CleanupAsync(bool sendRst)
        {
            try
            {
                var disposedError = new ObjectDisposedException(nameof(Http2ResponseBodySource));
                _queue.Complete(disposedError);

                var released = ReleaseUnreadBuffers();
                if (released.FlowControlledBytes > 0)
                    _connection.OnResponseBytesReleased(released.FlowControlledBytes);

                if (!_trailersSource.Task.IsCompleted)
                    _trailersSource.TrySetException(disposedError);

                if (sendRst)
                    await _connection.AbortStreamFromBodySourceAsync(_stream).ConfigureAwait(false);
            }
            finally
            {
                _stream.ReleaseBodySourceLifetime();
            }
        }

        private ReleasedBufferCounts ReleaseUnreadBuffers()
        {
            var released = new ReleasedBufferCounts();

            Http2ResponseBodyChunk currentChunk;
            lock (_currentChunkGate)
            {
                currentChunk = DetachCurrentChunk_NoLock();
            }

            if (currentChunk.Buffer != null)
            {
                released.DataBytes += currentChunk.Length;
                released.FlowControlledBytes += currentChunk.FlowControlledLength;
                ArrayPool<byte>.Shared.Return(currentChunk.Buffer);
            }

            while (_queue.TryRead(out var chunk))
            {
                if (chunk.Buffer == null)
                    continue;

                released.DataBytes += chunk.Length;
                released.FlowControlledBytes += chunk.FlowControlledLength;
                ArrayPool<byte>.Shared.Return(chunk.Buffer);
            }

            if (released.DataBytes > 0)
                Interlocked.Add(ref _bufferedBytes, -released.DataBytes);

            return released;
        }

        private bool TryReserveBufferedBytes(int length)
        {
            // Keep the CAS because cleanup/abort can release bytes concurrently with the read loop.
            while (true)
            {
                var current = Volatile.Read(ref _bufferedBytes);
                if (current > _bufferCapacity - length)
                    return false;

                if (Interlocked.CompareExchange(ref _bufferedBytes, current + length, current) == current)
                    return true;
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(Http2ResponseBodySource));
        }

        private bool TryReadCurrentChunk(
            Memory<byte> destination,
            out int bytesRead,
            out Http2ResponseBodyChunk completedChunk)
        {
            lock (_currentChunkGate)
            {
                if (_currentChunk.Buffer == null)
                {
                    bytesRead = 0;
                    completedChunk = default;
                    return false;
                }

                var remaining = _currentChunk.Length - _currentChunkOffset;
                if (remaining <= 0)
                {
                    bytesRead = 0;
                    completedChunk = DetachCurrentChunk_NoLock();
                    return true;
                }

                bytesRead = Math.Min(destination.Length, remaining);
                new ReadOnlySpan<byte>(_currentChunk.Buffer, _currentChunkOffset, bytesRead)
                    .CopyTo(destination.Span);
                _currentChunkOffset += bytesRead;
                Interlocked.Exchange(ref _lastConsumptionTick, Environment.TickCount64);

                completedChunk = _currentChunkOffset >= _currentChunk.Length
                    ? DetachCurrentChunk_NoLock()
                    : default;
                return true;
            }
        }

        private bool TrySetCurrentChunk(Http2ResponseBodyChunk chunk)
        {
            lock (_currentChunkGate)
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    Volatile.Read(ref _terminalState) == TerminalStateAborted)
                {
                    return false;
                }

                _currentChunk = chunk;
                _currentChunkOffset = 0;
                return true;
            }
        }

        private void ReleaseDetachedChunk(Http2ResponseBodyChunk chunk)
        {
            if (chunk.Buffer == null)
                return;

            ArrayPool<byte>.Shared.Return(chunk.Buffer);
            Interlocked.Add(ref _bufferedBytes, -chunk.Length);
            _connection.OnResponseBytesReleased(chunk.FlowControlledLength);
        }

        private Http2ResponseBodyChunk DetachCurrentChunk_NoLock()
        {
            var chunk = _currentChunk;
            _currentChunk = default;
            _currentChunkOffset = 0;
            return chunk;
        }

        private struct ReleasedBufferCounts
        {
            public int DataBytes;
            public int FlowControlledBytes;
        }
    }
}
