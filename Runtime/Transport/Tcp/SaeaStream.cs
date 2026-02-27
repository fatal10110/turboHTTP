using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Tcp
{
    /// <summary>
    /// A <see cref="Stream"/> adapter over <see cref="SaeaSocketChannel"/> that exposes
    /// the SAEA send/receive hot paths as a standard readable/writable stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This adapter is intentionally thin. It owns the underlying
    /// <see cref="SaeaSocketChannel"/> and disposes it when the stream is disposed.
    /// </para>
    /// <para>
    /// Seek and length operations are not supported (not meaningful for a socket stream).
    /// </para>
    /// </remarks>
    internal sealed class SaeaStream : Stream
    {
        private readonly SaeaSocketChannel _channel;
        // volatile: CanRead/CanWrite are read from arbitrary threads (e.g., SslStream internal
        // machinery). Volatile ensures they observe the disposed state without a data race on
        // ARM64 IL2CPP (store-release / load-acquire semantics).
        private volatile bool _disposed;

        public SaeaStream(SaeaSocketChannel channel)
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
            // SaeaSocketChannel.ReceiveAsync accepts byte[]/offset/count. Extract the array segment.
            if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
                    (ReadOnlyMemory<byte>)buffer, out var segment))
            {
                // Fallback: allocate a temporary array for non-array-backed Memory.
                // This path is uncommon in practice — callers in this codebase use array-backed Memory.
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
            return _channel.SendAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return _channel.SendAsync(buffer, cancellationToken);
        }

        // ── Unsupported ────────────────────────────────────────────────────────

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException("SaeaStream does not support seeking.");

        public override void SetLength(long value)
            => throw new NotSupportedException("SaeaStream does not support SetLength.");

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
                throw new ObjectDisposedException(nameof(SaeaStream));
        }
    }
}
