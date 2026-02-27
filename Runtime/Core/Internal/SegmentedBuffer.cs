using System;
using System.Buffers;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// A write-once, read-once segmented buffer backed by pooled <see cref="PooledSegment"/>
    /// chains. Prevents contiguous-buffer copy amplification and LOH pressure for large
    /// or chunked payloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Data is written via <see cref="Write(ReadOnlySpan{byte})"/> or the two-step
    /// <see cref="GetWriteSpan"/>/<see cref="Advance"/> pattern.
    /// After writing is complete, call <see cref="AsSequence"/> to project a
    /// <see cref="ReadOnlySequence{byte}"/> over the buffered data without copying.
    /// </para>
    /// <para>
    /// <b>Ownership:</b> The buffer exclusively owns all rented segments.
    /// Call <see cref="Dispose"/> (or <see cref="Reset"/> to reuse the instance)
    /// to return all rented arrays to the pool. The projected sequence is invalidated
    /// after disposal or reset. <see cref="Reset"/> re-enables writing; calling it on
    /// a disposed instance throws <see cref="ObjectDisposedException"/>.
    /// </para>
    /// <para>
    /// <b>Thread safety:</b> Single-writer/single-reader by design. Not thread-safe.
    /// </para>
    /// </remarks>
    public sealed class SegmentedBuffer : IDisposable
    {
        /// <summary>
        /// Default segment size: 16 KB. Stays well below the 85 KB LOH threshold.
        /// </summary>
        public const int DefaultSegmentSize = 16 * 1024;

        private readonly int _segmentSize;
        private PooledSegment _head;
        private PooledSegment _tail;
        private long _totalWritten;
        private bool _disposed;

        public SegmentedBuffer() : this(DefaultSegmentSize) { }

        public SegmentedBuffer(int segmentSize)
        {
            if (segmentSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(segmentSize));
            _segmentSize = segmentSize;
        }

        /// <summary>
        /// Total number of bytes written across all segments.
        /// </summary>
        public long Length => _totalWritten;

        /// <summary>
        /// Returns true if no data has been written.
        /// </summary>
        public bool IsEmpty => _totalWritten == 0;

        // ── Write API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Copies <paramref name="data"/> into the buffer, allocating new segments as needed.
        /// </summary>
        public void Write(ReadOnlySpan<byte> data)
        {
            ThrowIfDisposed();
            if (data.IsEmpty) return;

            int remaining = data.Length;
            int offset = 0;

            while (remaining > 0)
            {
                EnsureWriteCapacity();
                int available = _tail.AvailableCapacity;
                int toWrite = Math.Min(remaining, available);
                data.Slice(offset, toWrite).CopyTo(_tail.GetWriteSpan(toWrite));
                _tail.Advance(toWrite);
                offset += toWrite;
                remaining -= toWrite;
                _totalWritten += toWrite;
            }
        }

        /// <summary>
        /// Returns a writable span into the current segment (at least <paramref name="sizeHint"/> bytes).
        /// Must be followed by a call to <see cref="Advance"/> before the next write.
        /// </summary>
        public Span<byte> GetWriteSpan(int sizeHint = 0)
        {
            ThrowIfDisposed();
            int needed = sizeHint <= 0 ? 1 : sizeHint;
            // If the requested size doesn't fit in the current segment, start a new one.
            if (_tail == null || _tail.AvailableCapacity < needed)
                AppendNewSegment(Math.Max(needed, _segmentSize));
            return _tail.GetWriteSpan();
        }

        /// <summary>
        /// Commits <paramref name="count"/> bytes obtained via <see cref="GetWriteSpan"/>.
        /// <see cref="GetWriteSpan"/> must be called before this method.
        /// </summary>
        public void Advance(int count)
        {
            ThrowIfDisposed();
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return;
            if (_tail == null)
                throw new InvalidOperationException("Advance called before GetWriteSpan.");
            _tail.Advance(count);
            _totalWritten += count;
        }

        // ── Read API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Projects a <see cref="ReadOnlySequence{byte}"/> over all written data.
        /// The sequence is valid until the next <see cref="Reset"/> or <see cref="Dispose"/>.
        /// </summary>
        public ReadOnlySequence<byte> AsSequence()
        {
            ThrowIfDisposed();

            if (_head == null)
                return ReadOnlySequence<byte>.Empty;

            if (_head == _tail)
            {
                // Single-segment fast path. Note: the segment's RunningIndex is not used
                // by the single-Memory overload — positions are relative to the slice start.
                return new ReadOnlySequence<byte>(_head.Memory);
            }

            return new ReadOnlySequence<byte>(_head, 0, _tail, _tail.Written);
        }

        /// <summary>
        /// Flattens all segments into a single contiguous <see cref="byte"/> array.
        /// Allocates exactly <see cref="Length"/> bytes. Use only when contiguous
        /// memory is required (e.g. passing to an API that does not accept sequences).
        /// Throws <see cref="InvalidOperationException"/> if <see cref="Length"/> exceeds
        /// <see cref="int.MaxValue"/> (~2 GB).
        /// </summary>
        public byte[] ToArray()
        {
            ThrowIfDisposed();
            if (_totalWritten == 0) return Array.Empty<byte>();
            if (_totalWritten > int.MaxValue)
                throw new InvalidOperationException(
                    $"Buffer length {_totalWritten} exceeds the maximum array size ({int.MaxValue}). " +
                    "Use AsSequence() and process segments individually.");

            var result = new byte[(int)_totalWritten];
            int offset = 0;
            var seg = _head;
            while (seg != null)
            {
                var span = seg.Memory.Span;
                span.CopyTo(result.AsSpan(offset));
                offset += span.Length;
                seg = (PooledSegment)seg.Next;
            }
            return result;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        /// <summary>
        /// Resets the buffer to empty state, returning all rented arrays to the pool.
        /// The instance may be reused after reset. Throws <see cref="ObjectDisposedException"/>
        /// if <see cref="Dispose"/> was already called.
        /// </summary>
        public void Reset()
        {
            ThrowIfDisposed();
            ReturnAllSegments();
            _head = null;
            _tail = null;
            _totalWritten = 0;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReturnAllSegments();
            _head = null;
            _tail = null;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private void EnsureWriteCapacity()
        {
            if (_tail == null || _tail.AvailableCapacity == 0)
                AppendNewSegment(_segmentSize);
        }

        private void AppendNewSegment(int minSize)
        {
            long runningIndex = _tail == null ? 0 : _tail.RunningIndex + _tail.Written;
            var newSeg = new PooledSegment(minSize, runningIndex);
            if (_head == null)
            {
                _head = newSeg;
                _tail = newSeg;
            }
            else
            {
                _tail.SetNext(newSeg);
                _tail = newSeg;
            }
        }

        private void ReturnAllSegments()
        {
            var seg = _head;
            while (seg != null)
            {
                var next = (PooledSegment)seg.Next;
                seg.ReturnToPool();
                seg = next;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SegmentedBuffer));
        }
    }
}
