using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Tcp
{
    /// <summary>
    /// The <see cref="NetworkStream"/> of the NetworkStream socket mode: it owns its socket through a
    /// <see cref="SocketHandleHold"/> instead of closing it in <see cref="Dispose(bool)"/>. Disposal
    /// shuts the socket down at once; the handle is released once a read or write that was pending on
    /// it ended (or after <see cref="SocketHandleHold.HoldTimeout"/>), so IL2CPP cannot hand the
    /// descriptor number to a new socket while its selector still holds the abandoned read's job
    /// (#384). Every caller that abandons a read (the HTTP/1.1 head and body reads, the 407 drain,
    /// the CONNECT reply, the TLS and WebSocket handshakes, the first-byte watchdog) and every loop
    /// whose stream is closed under its read disposes this stream, directly or through a TLS stream,
    /// so this is the one place that decides when the descriptor is released.
    /// <para>
    /// The APM <c>BeginRead</c>/<c>BeginWrite</c> pair is not tracked: no wrapper in the pipeline
    /// uses it (SslStream, BouncyCastle and the HTTP/WebSocket layers call the task and synchronous
    /// overloads tracked here).
    /// </para>
    /// </summary>
    internal sealed class HoldingNetworkStream : NetworkStream
    {
        private readonly SocketHandleHold _hold;

        internal HoldingNetworkStream(Socket socket)
            : base(socket, ownsSocket: false)
        {
            _hold = new SocketHandleHold(socket);
        }

        /// <summary>The hold that owns the socket handle. Tests read it.</summary>
        internal SocketHandleHold Hold => _hold;

        public override int Read(byte[] buffer, int offset, int count)
        {
            _hold.EnterIo();
            try { return base.Read(buffer, offset, count); }
            finally { _hold.ExitIo(null, write: false); }
        }

        public override int Read(Span<byte> buffer)
        {
            _hold.EnterIo();
            try { return base.Read(buffer); }
            finally { _hold.ExitIo(null, write: false); }
        }

        public override int ReadByte()
        {
            _hold.EnterIo();
            try { return base.ReadByte(); }
            finally { _hold.ExitIo(null, write: false); }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Task<int> task = null;
            _hold.EnterIo();
            try
            {
                task = base.ReadAsync(buffer, offset, count, cancellationToken);
                return task;
            }
            finally
            {
                _hold.ExitIo(task, write: false);
            }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Task<int> task = null;
            _hold.EnterIo();
            try
            {
                var read = base.ReadAsync(buffer, cancellationToken);
                if (read.IsCompleted)
                    return read;

                // A ValueTask may be consumed once: the task it wraps is both returned and held.
                task = read.AsTask();
                return new ValueTask<int>(task);
            }
            finally
            {
                _hold.ExitIo(task, write: false);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hold.EnterIo();
            try { base.Write(buffer, offset, count); }
            finally { _hold.ExitIo(null, write: true); }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hold.EnterIo();
            try { base.Write(buffer); }
            finally { _hold.ExitIo(null, write: true); }
        }

        public override void WriteByte(byte value)
        {
            _hold.EnterIo();
            try { base.WriteByte(value); }
            finally { _hold.ExitIo(null, write: true); }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Task task = null;
            _hold.EnterIo();
            try
            {
                task = base.WriteAsync(buffer, offset, count, cancellationToken);
                return task;
            }
            finally
            {
                _hold.ExitIo(task, write: true);
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Task task = null;
            _hold.EnterIo();
            try
            {
                var write = base.WriteAsync(buffer, cancellationToken);
                if (write.IsCompleted)
                    return write;

                task = write.AsTask();
                return new ValueTask(task);
            }
            finally
            {
                _hold.ExitIo(task, write: true);
            }
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                // ownsSocket: false, so the base marks the stream disposed and leaves the socket alone.
                base.Dispose(disposing);
            }
            finally
            {
                // The finalizer path leaves the socket to its own finalizer.
                if (disposing)
                    _hold.Close();
            }
        }
    }
}
