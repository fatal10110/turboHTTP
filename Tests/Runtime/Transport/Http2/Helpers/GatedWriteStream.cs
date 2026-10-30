using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Tests.Transport.Http2.Helpers
{
    /// <summary>
    /// Stream decorator that can hold one chosen write until the test releases it, so a test can
    /// cancel a request while a frame is half written. Like a socket write, a held write honours the
    /// cancellation token it was called with: cancelling that token fails the write without writing
    /// its bytes. Reads, flushes and all other writes pass straight through.
    /// </summary>
    internal sealed class GatedWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly object _gate = new object();
        private Func<int, bool> _blockPredicate;
        private TaskCompletionSource<bool> _blocked;
        private TaskCompletionSource<bool> _release;

        public GatedWriteStream(Stream inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>
        /// Holds the first subsequent write for which <paramref name="predicate"/> (called with the
        /// write's byte count, in write order) returns true. Returns a task that completes once that
        /// write is held.
        /// </summary>
        public Task BlockWrite(Func<int, bool> predicate)
        {
            lock (_gate)
            {
                _blockPredicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
                _blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _blocked.Task;
            }
        }

        /// <summary>Lets the held write (if any) proceed.</summary>
        public void Release()
        {
            TaskCompletionSource<bool> release;
            lock (_gate)
                release = _release;
            release?.TrySetResult(true);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteGatedAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => WriteGatedAsync(buffer, cancellationToken);

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Release();
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private async ValueTask WriteGatedAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> release = null;
            lock (_gate)
            {
                if (_blockPredicate != null && _blockPredicate(buffer.Length))
                {
                    _blockPredicate = null;
                    release = _release;
                    _blocked.TrySetResult(true);
                }
            }

            if (release != null)
            {
                using (cancellationToken.Register(() => release.TrySetCanceled()))
                {
                    await release.Task.ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
