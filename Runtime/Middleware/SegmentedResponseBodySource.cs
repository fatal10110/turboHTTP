using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Middleware
{
    /// <summary>
    /// A decoded buffered response body kept in the pooled segments of a <see cref="SegmentedBuffer"/>, so the
    /// buffered decompression path never flattens the body into one array (#74). The response collector detaches
    /// the segments into the <see cref="UHttpResponse"/> without a copy; other consumers read them.
    /// </summary>
    /// <remarks>
    /// Ownership: this source owns the buffer until <see cref="TryDetachBufferedBody"/> hands it to the detached
    /// body, or <see cref="Abort"/>/<see cref="DisposeAsync"/> returns its segments; the <see cref="_disposed"/>
    /// exchange makes exactly one of them win. Reads follow the body-source contract (one consumer at a time,
    /// no read concurrent with dispose).
    /// </remarks>
    internal sealed class SegmentedResponseBodySource : IResponseBodySource
    {
        private readonly long _length;
        private SegmentedBuffer _buffer;
        private ReadOnlySequence<byte> _body;
        private ReadOnlySequence<byte> _remaining;
        private HttpHeaders _trailers;
        private bool _readStarted;
        private int _disposed;

        /// <param name="buffer">The decoded body; ownership moves to this source.</param>
        /// <param name="trailers">Response trailers, or null.</param>
        internal SegmentedResponseBodySource(SegmentedBuffer buffer, HttpHeaders trailers)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _trailers = trailers == null || trailers.Count == 0 ? HttpHeaders.Empty : trailers.Clone();
            _body = buffer.AsSequence();
            _remaining = _body;
            _length = buffer.Length;
        }

        public long? Length
        {
            get
            {
                ThrowIfDisposed();
                return _length;
            }
        }

        public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            ThrowIfDisposed();

            // A contiguous view exists only when the body fits one segment; larger bodies stay segmented.
            if (_body.IsSingleSegment)
            {
                data = _body.First;
                return true;
            }

            data = default;
            return false;
        }

        public bool TryDetachBufferedBody(out DetachedBufferedBody body)
        {
            ThrowIfDisposed();

            // The detached body carries no trailers, so a body with trailers is read instead.
            if (_readStarted || _trailers.Count != 0 || Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                body = default;
                return false;
            }

            body = new DetachedBufferedBody(_body, _buffer);
            _buffer = null;
            _body = default;
            _remaining = default;
            return true;
        }

        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            if (destination.IsEmpty || _remaining.IsEmpty)
                return new ValueTask<int>(0);

            _readStarted = true;
            var count = (int)Math.Min(destination.Length, _remaining.Length);
            var slice = _remaining.Slice(0, count);
            slice.CopyTo(destination.Span);
            _remaining = _remaining.Slice(slice.End);
            return new ValueTask<int>(count);
        }

        public ValueTask DrainAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            _readStarted = true;
            _remaining = ReadOnlySequence<byte>.Empty;
            return default;
        }

        public void Abort()
        {
            DisposeCore();
        }

        public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            return new ValueTask<HttpHeaders>(_trailers);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCore();
            return default;
        }

        private void DisposeCore()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            var buffer = _buffer;
            _buffer = null;
            _body = default;
            _remaining = default;
            _trailers = HttpHeaders.Empty;
            buffer?.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(SegmentedResponseBodySource));
        }
    }
}
