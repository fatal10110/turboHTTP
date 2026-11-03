using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// Server side of a handshake that never closes: serves the scripted chunks one read at a time, then
    /// either reports EOF or keeps the next read pending until it is canceled or the stream is disposed
    /// (a keep-alive server). Writes are accepted and discarded.
    /// </summary>
    internal sealed class ScriptedResponseStream : Stream
    {
        private readonly byte[][] _chunks;
        private readonly bool _endWithEof;
        private readonly TaskCompletionSource<int> _pendingRead =
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _chunkIndex;
        private int _chunkOffset;
        private int _blockedReads;

        public ScriptedResponseStream(bool endWithEof, params byte[][] chunks)
        {
            _chunks = chunks ?? Array.Empty<byte[]>();
            _endWithEof = endWithEof;
        }

        /// <summary>Reads issued after the script ran out (each one would wait for more server bytes).</summary>
        public int BlockedReads => Volatile.Read(ref _blockedReads);

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (_chunkIndex < _chunks.Length)
            {
                var chunk = _chunks[_chunkIndex];
                int available = chunk.Length - _chunkOffset;
                if (available <= 0)
                {
                    _chunkIndex++;
                    _chunkOffset = 0;
                    continue;
                }

                int toCopy = Math.Min(available, count);
                Buffer.BlockCopy(chunk, _chunkOffset, buffer, offset, toCopy);
                _chunkOffset += toCopy;
                return Task.FromResult(toCopy);
            }

            if (_endWithEof)
                return Task.FromResult(0);

            Interlocked.Increment(ref _blockedReads);
            return WaitPendingAsync(cancellationToken);
        }

        private async Task<int> WaitPendingAsync(CancellationToken ct)
        {
            using (ct.Register(static state => ((TaskCompletionSource<int>)state).TrySetCanceled(), _pendingRead))
                return await _pendingRead.Task.ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _pendingRead.TrySetException(new ObjectDisposedException(nameof(ScriptedResponseStream)));
            base.Dispose(disposing);
        }
    }

    /// <summary>Hands one prepared stream to the connection.</summary>
    internal sealed class SingleStreamTransport : IWebSocketTransport
    {
        private readonly Stream _stream;

        public SingleStreamTransport(Stream stream)
        {
            _stream = stream;
        }

        public Task<Stream> ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)
        {
            return Task.FromResult(_stream);
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }

    /// <summary>
    /// Client-side wrapper that can stall the next flush until released (a full socket buffer), drop
    /// writes (a peer that stopped reading), and reports when it was disposed.
    /// </summary>
    internal sealed class ControlledClientStream : Stream
    {
        private readonly Stream _inner;
        private TaskCompletionSource<bool> _stallGate;
        private TaskCompletionSource<bool> _stalled;
        private readonly TaskCompletionSource<bool> _firstDroppedWrite =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dropWrites;
        private int _disposed;

        public ControlledClientStream(Stream inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>Completes on the first write discarded after <see cref="DropWrites"/>.</summary>
        public Task FirstDroppedWrite => _firstDroppedWrite.Task;

        /// <summary>From now on writes complete without reaching the peer.</summary>
        public void DropWrites()
        {
            Volatile.Write(ref _dropWrites, 1);
        }

        /// <summary>The next flush blocks until <see cref="ReleaseStall"/>; the returned task completes once it does.</summary>
        public Task StallNextFlush()
        {
            var stalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _stalled, stalled);
            Volatile.Write(ref _stallGate, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            return stalled.Task;
        }

        public void ReleaseStall()
        {
            Interlocked.Exchange(ref _stallGate, null)?.TrySetResult(true);
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Volatile.Read(ref _dropWrites) == 0)
                _inner.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(ControlledClientStream));

            if (Volatile.Read(ref _dropWrites) != 0)
            {
                _firstDroppedWrite.TrySetResult(true);
                return Task.CompletedTask;
            }

            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return new ValueTask(WriteAsync(buffer.ToArray(), 0, buffer.Length, cancellationToken));
        }

        public override void Flush() => _inner.Flush();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            var gate = Volatile.Read(ref _stallGate);
            var stalled = Interlocked.Exchange(ref _stalled, null);
            if (gate != null && stalled != null)
            {
                stalled.TrySetResult(true);
                await gate.Task.ConfigureAwait(false);
            }

            if (IsDisposed)
                throw new ObjectDisposedException(nameof(ControlledClientStream));

            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ReleaseStall();
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
