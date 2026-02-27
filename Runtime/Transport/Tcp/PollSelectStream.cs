using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Tcp
{
    /// <summary>
    /// A <see cref="Stream"/> adapter over <see cref="PollSelectSocketChannel"/> that exposes
    /// the poll-based send/receive hot paths as a standard readable/writable stream.
    /// </summary>
    /// <remarks>
    /// Owns the underlying <see cref="PollSelectSocketChannel"/> and disposes it when the
    /// stream is disposed. TLS wrapping via <c>SslStream</c> over this stream is fully supported
    /// — <c>SslStream</c> treats it as an opaque <see cref="Stream"/> and calls Read/Write as
    /// normal.
    /// </remarks>
    internal sealed class PollSelectStream : Stream
    {
        private readonly PollSelectSocketChannel _channel;
        // volatile: CanRead/CanWrite may be read from other threads (e.g., SslStream internals).
        private volatile bool _disposed;

        public PollSelectStream(PollSelectSocketChannel channel)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        }

        // ── Stream properties ──────────────────────────────────────────────────

        public override bool CanRead => !_disposed;
        public override bool CanWrite => !_disposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        // ── Read ───────────────────────────────────────────────────────────────

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return _channel.ReceiveAsync(buffer, offset, count, cancellationToken).AsTask();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var segment))
            {
                // Non-array-backed Memory — use a temp array and copy result back.
                var tmp = new byte[buffer.Length];
                var vt = _channel.ReceiveAsync(tmp, 0, tmp.Length, cancellationToken);
                return CopyAndReturnAsync(vt, tmp, buffer);
            }
            return _channel.ReceiveAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken);
        }

        private static async ValueTask<int> CopyAndReturnAsync(
            ValueTask<int> readTask, byte[] tmp, Memory<byte> destination)
        {
            int n = await readTask.ConfigureAwait(false);
            tmp.AsMemory(0, n).CopyTo(destination);
            return n;
        }

        // ── Write ──────────────────────────────────────────────────────────────

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return _channel.SendAsync(
                new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return _channel.SendAsync(buffer, cancellationToken);
        }

        // ── Unsupported ────────────────────────────────────────────────────────

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException("PollSelectStream does not support seeking.");

        public override void SetLength(long value)
            => throw new NotSupportedException("PollSelectStream does not support SetLength.");

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        // ── Disposal ───────────────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
                _channel.Dispose();
            base.Dispose(disposing);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PollSelectStream));
        }
    }
}
