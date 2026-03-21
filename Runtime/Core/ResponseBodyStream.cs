using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    public sealed class ResponseBodyStream : Stream
    {
        private readonly UHttpStreamingResponse _owner;
        private long _bytesRead;
        private int _disposed;
        private int _endOfStreamReached;

        internal ResponseBodyStream(UHttpStreamingResponse owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public override bool CanRead => Volatile.Read(ref _disposed) == 0 && !_owner.IsDisposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                ThrowIfDisposed();

                var length = _owner.BodyLength;
                if (!length.HasValue)
                    throw new NotSupportedException("Response body length is not known.");

                return length.Value;
            }
        }

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            ThrowIfDisposed();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateReadArguments(buffer, offset, count);
            ThrowIfDisposed();
            return ReadCoreAsync(new Memory<byte>(buffer, offset, count), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ValidateReadArguments(buffer, offset, count);
            ThrowIfDisposed();
            return ReadCoreAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return ReadCoreAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            throw new NotSupportedException();
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                DisposeStreamCore();

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeStreamCore();
            GC.SuppressFinalize(this);
            return base.DisposeAsync();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0 || _owner.IsDisposed)
                throw new ObjectDisposedException(nameof(ResponseBodyStream));
        }

        internal bool HasReachedEndOfStream => Volatile.Read(ref _endOfStreamReached) != 0;

        private ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (buffer.IsEmpty)
                return new ValueTask<int>(0);

            var pending = _owner.ReadBodyAsync(buffer, cancellationToken);
            if (pending.IsCompletedSuccessfully)
                return new ValueTask<int>(ObserveReadResult(pending.Result));

            return AwaitReadCoreAsync(pending);
        }

        private async ValueTask<int> AwaitReadCoreAsync(ValueTask<int> pending)
        {
            return ObserveReadResult(await pending.ConfigureAwait(false));
        }

        private int ObserveReadResult(int read)
        {
            if (read <= 0)
            {
                Interlocked.Exchange(ref _endOfStreamReached, 1);
                return 0;
            }

            if (_owner.BodyLength.HasValue)
            {
                var totalRead = Interlocked.Add(ref _bytesRead, read);
                if (totalRead >= _owner.BodyLength.Value)
                    Interlocked.Exchange(ref _endOfStreamReached, 1);
            }

            return read;
        }

        private void DisposeStreamCore()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (Volatile.Read(ref _endOfStreamReached) == 0)
                _owner.AbortBody();
        }

        private static void ValidateReadArguments(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if ((uint)offset > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if ((uint)count > buffer.Length - offset)
                throw new ArgumentOutOfRangeException(nameof(count));
        }
    }
}
