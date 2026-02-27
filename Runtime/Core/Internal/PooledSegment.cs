using System;
using System.Buffers;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// A node in a pooled segment chain. Extends <see cref="ReadOnlySequenceSegment{T}"/>
    /// and owns one rented slice from <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Instances are managed by <see cref="SegmentedBuffer"/> and must not be
    /// created or disposed by callers directly.
    /// </para>
    /// <para>
    /// Each segment stores up to <see cref="SegmentedBuffer.DefaultSegmentSize"/> bytes.
    /// The <see cref="ReadOnlySequenceSegment{T}.RunningIndex"/> is set by
    /// <see cref="SegmentedBuffer"/> to produce a valid multi-segment
    /// <see cref="ReadOnlySequence{T}"/>.
    /// </para>
    /// </remarks>
    internal sealed class PooledSegment : ReadOnlySequenceSegment<byte>
    {
        private byte[] _array;
        private int _written;

        /// <summary>
        /// Initializes a new segment backed by a rented array of at least
        /// <paramref name="minCapacity"/> bytes.
        /// </summary>
        internal PooledSegment(int minCapacity, long runningIndex)
        {
            _array = ArrayPool<byte>.Shared.Rent(minCapacity);
            _written = 0;
            RunningIndex = runningIndex;
            Memory = ReadOnlyMemory<byte>.Empty;
        }

        /// <summary>
        /// Number of bytes available for writing in the current rented array.
        /// </summary>
        internal int AvailableCapacity => _array.Length - _written;

        /// <summary>
        /// Total capacity of the rented array.
        /// </summary>
        internal int Capacity => _array.Length;

        /// <summary>
        /// Number of bytes written into this segment.
        /// </summary>
        internal int Written => _written;

        /// <summary>
        /// Returns a <see cref="Span{T}"/> into the unwritten portion of the rented array.
        /// </summary>
        internal Span<byte> GetWriteSpan() => new Span<byte>(_array, _written, _array.Length - _written);

        /// <summary>
        /// Returns a <see cref="Span{T}"/> of exactly <paramref name="count"/> bytes into
        /// the unwritten portion. Caller must verify <see cref="AvailableCapacity"/> first.
        /// </summary>
        internal Span<byte> GetWriteSpan(int count) => new Span<byte>(_array, _written, count);

        /// <summary>
        /// Commits <paramref name="count"/> bytes as written and updates
        /// <see cref="ReadOnlySequenceSegment{T}.Memory"/> to reflect the new content.
        /// </summary>
        internal void Advance(int count)
        {
            if (count < 0 || _written + count > _array.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            _written += count;
            Memory = new ReadOnlyMemory<byte>(_array, 0, _written);
        }

        /// <summary>
        /// Links this segment to <paramref name="next"/> in the chain.
        /// </summary>
        internal void SetNext(PooledSegment next) => Next = next;

        /// <summary>
        /// Returns the rented array to <see cref="ArrayPool{byte}.Shared"/>.
        /// After disposal the segment must not be used.
        /// </summary>
        internal void ReturnToPool()
        {
            var array = _array;
            _array = null;
            if (array != null)
                ArrayPool<byte>.Shared.Return(array);
        }
    }
}
