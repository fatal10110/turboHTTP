using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core.Internal
{
    internal sealed class ReadOnlySequenceStream : Stream
    {
        private readonly ReadOnlySequence<byte> _sequence;
        private SequencePosition _position;
        private long _consumed;

        internal ReadOnlySequenceStream(ReadOnlySequence<byte> sequence)
        {
            _sequence = sequence;
            _position = sequence.Start;
            _consumed = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _sequence.Length;

        public override long Position
        {
            get => _consumed;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            return Read(new Span<byte>(buffer, offset, count));
        }

        public override int Read(Span<byte> destination)
        {
            var remaining = _sequence.Slice(_position);
            if (remaining.IsEmpty || destination.IsEmpty)
                return 0;

            var toCopy = (int)Math.Min(destination.Length, remaining.Length);
            remaining.Slice(0, toCopy).CopyTo(destination);
            _position = _sequence.GetPosition(toCopy, _position);
            _consumed += toCopy;
            return toCopy;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<int>(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long absoluteOffset;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    absoluteOffset = offset;
                    break;
                case SeekOrigin.Current:
                    absoluteOffset = _consumed + offset;
                    break;
                case SeekOrigin.End:
                    absoluteOffset = _sequence.Length + offset;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown seek origin.");
            }

            if (absoluteOffset < 0 || absoluteOffset > _sequence.Length)
                throw new IOException("Attempted to seek outside the bounds of the sequence.");

            _position = _sequence.GetPosition(absoluteOffset);
            _consumed = absoluteOffset;
            return _consumed;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
