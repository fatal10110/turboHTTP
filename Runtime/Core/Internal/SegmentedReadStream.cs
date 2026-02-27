using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// A read-only <see cref="Stream"/> adapter over a <see cref="ReadOnlySequence{byte}"/>.
    /// Reads cross segment boundaries correctly without flattening the entire sequence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stream does not own the underlying sequence or its segment lifetimes.
    /// Callers must ensure the sequence remains valid for the lifetime of this stream.
    /// </para>
    /// <para>
    /// <see cref="ReadAsync"/> completes synchronously for in-memory data (returns a
    /// completed <see cref="ValueTask{T}"/>), since the sequence is already in memory.
    /// </para>
    /// <para>
    /// <see cref="Seek"/> and <see cref="Position"/> are supported for full compatibility
    /// with consumers that require seekable streams (e.g. decompressors, JSON readers).
    /// </para>
    /// </remarks>
    public sealed class SegmentedReadStream : Stream
    {
        private readonly ReadOnlySequence<byte> _sequence;
        private SequencePosition _current;
        private long _position;
        private readonly long _length;
        private bool _disposed;

        /// <summary>
        /// Creates a new stream over the given <paramref name="sequence"/>.
        /// The stream starts at position 0.
        /// </summary>
        public SegmentedReadStream(ReadOnlySequence<byte> sequence)
        {
            _sequence = sequence;
            _current = sequence.Start;
            _position = 0;
            _length = sequence.Length;
        }

        // ── Stream properties ──────────────────────────────────────────────────

        /// <inheritdoc/>
        public override bool CanRead => !_disposed;

        /// <inheritdoc/>
        public override bool CanSeek => !_disposed;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <inheritdoc/>
        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return _length;
            }
        }

        /// <inheritdoc/>
        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return _position;
            }
            set
            {
                ThrowIfDisposed();
                Seek(value, SeekOrigin.Begin);
            }
        }

        // ── Read ───────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return 0;

            return ReadIntoSpan(new Span<byte>(buffer, offset, count));
        }

        /// <inheritdoc/>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // All data is in-memory; complete synchronously.
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        /// <inheritdoc/>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return new ValueTask<int>(ReadIntoSpan(buffer.Span));
        }

        // ── Seek ───────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        /// <remarks>
        /// Backward seeks are O(N) in the number of segments because
        /// <see cref="ReadOnlySequence{T}"/> does not support random access —
        /// <c>GetPosition(long)</c> scans linearly from the sequence start.
        /// Forward seeks use <c>GetPosition(delta, _current)</c> and avoid re-scanning.
        /// Avoid repeated backward seeks on large multi-segment bodies.
        /// </remarks>
        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();

            long newPosition;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    newPosition = offset;
                    break;
                case SeekOrigin.Current:
                    newPosition = _position + offset;
                    break;
                case SeekOrigin.End:
                    newPosition = _length + offset;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(origin));
            }

            if (newPosition < 0)
                throw new IOException("Seek before beginning of stream.");
            if (newPosition > _length)
                newPosition = _length;

            if (newPosition == _position)
                return _position;

            if (newPosition < _position)
            {
                // Backward seek: restart from sequence start.
                // GetPosition(long) scans from the first segment — O(N) in segment count.
                _current = _sequence.GetPosition(newPosition);
            }
            else
            {
                // Forward seek: advance relative to _current — avoids re-scanning.
                _current = _sequence.GetPosition(newPosition - _position, _current);
            }

            _position = newPosition;
            return _position;
        }

        // ── Unsupported ────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void SetLength(long value) =>
            throw new NotSupportedException("SegmentedReadStream is read-only.");

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("SegmentedReadStream is read-only.");

        /// <inheritdoc/>
        public override void Flush() { }

        // ── Disposal ───────────────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private int ReadIntoSpan(Span<byte> destination)
        {
            if (destination.IsEmpty || _position >= _length)
                return 0;

            int remaining = destination.Length;
            int totalRead = 0;

            while (remaining > 0 && _position < _length)
            {
                // Two-phase TryGet pattern:
                //   advance: false  — returns the memory at _current WITHOUT moving _current.
                //                     This lets us inspect the segment length before deciding
                //                     whether to consume it fully or partially.
                //   advance: true   — used only on full-segment consumption to step _current
                //                     to the start of the next segment.
                // For partial reads we use GetPosition(toRead, _current) to move _current
                // within the current segment without crossing into the next one (toRead < segment.Length).
                if (!_sequence.TryGet(ref _current, out ReadOnlyMemory<byte> segment, advance: false))
                    break;

                if (segment.IsEmpty)
                {
                    // Skip empty segments defensively (SegmentedBuffer never produces them,
                    // but external sequences might at segment boundaries).
                    _sequence.TryGet(ref _current, out _, advance: true);
                    continue;
                }

                int toRead = Math.Min(remaining, segment.Length);
                segment.Span.Slice(0, toRead).CopyTo(destination.Slice(totalRead));
                totalRead += toRead;
                remaining -= toRead;
                _position += toRead;

                if (toRead == segment.Length)
                {
                    // Full segment consumed: advance _current to the start of the next segment.
                    _sequence.TryGet(ref _current, out _, advance: true);
                }
                else
                {
                    // Partial read: move _current forward within the same segment.
                    // GetPosition(toRead, _current) stays within the segment because toRead < segment.Length.
                    _current = _sequence.GetPosition(toRead, _current);
                }
            }

            return totalRead;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SegmentedReadStream));
        }
    }
}
