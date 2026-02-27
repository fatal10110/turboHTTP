using System;
using System.Buffers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Tcp
{
    /// <summary>
    /// Provides zero-allocation send and receive primitives over a raw <see cref="Socket"/>
    /// using <see cref="SocketAsyncEventArgs"/> (SAEA) for both directions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Duplex design:</b> Two SAEA instances — one for receive, one for send. Each has its
    /// own pinned pooled buffer and its own <see cref="SaeaCompletionSource"/>. Both directions
    /// can proceed concurrently (HTTP/1.1 is half-duplex in practice, but the channel does not
    /// impose ordering).
    /// </para>
    /// <para>
    /// <b>Concurrency contract:</b> At most one concurrent <see cref="ReceiveAsync"/> and at
    /// most one concurrent <see cref="SendAsync"/> at a time. Violating this contract is detected
    /// at runtime and throws <see cref="InvalidOperationException"/>.
    /// </para>
    /// <para>
    /// <b>Buffer ownership:</b> Both pinned buffers are rented from
    /// <see cref="ArrayPool{T}.Shared"/> at construction. They are NOT returned until any
    /// in-flight SAEA operations have completed (the kernel is done with the buffer), ensuring
    /// no memory-corruption window between socket-disposal-triggered OperationAborted and
    /// the caller-side ArrayPool return.
    /// </para>
    /// <para>
    /// <b>Cancellation:</b> <see cref="CancellationToken"/> cancellation is implemented by
    /// registering a callback that disposes the underlying socket. This triggers
    /// <see cref="SocketError.OperationAborted"/>, which the Completed handler maps to
    /// <see cref="OperationCanceledException"/>. Double-dispose of the socket is safe
    /// (idempotent on all .NET platforms).
    /// </para>
    /// </remarks>
    internal sealed class SaeaSocketChannel : IDisposable
    {
        /// <summary>Size of each pinned SAEA buffer.</summary>
        private const int BufferSize = 16 * 1024;

        private readonly Socket _socket;
        private readonly SocketAsyncEventArgs _recvSaea;
        private readonly SocketAsyncEventArgs _sendSaea;
        private readonly SaeaCompletionSource _recvSource;
        private readonly SaeaCompletionSource _sendSource;
        private readonly byte[] _recvBuffer;
        private readonly byte[] _sendBuffer;

        // In-flight tracking: 0 = idle, 1 = operation submitted to OS.
        // Used to guard against concurrent same-direction calls (runtime detection)
        // and to defer ArrayPool.Return until the OS completion has fired (correct
        // buffer lifetime on IOCP/kqueue/epoll where the kernel owns the buffer
        // until the completion event is posted).
        private volatile int _recvActive;
        private volatile int _sendActive;

        private int _disposed;

        public SaeaSocketChannel(Socket socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));

            _recvBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            _sendBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);

            _recvSource = new SaeaCompletionSource();
            _sendSource = new SaeaCompletionSource();

            _recvSaea = new SocketAsyncEventArgs();
            _recvSaea.SetBuffer(_recvBuffer, 0, _recvBuffer.Length);
            _recvSaea.UserToken = _recvSource;
            _recvSaea.Completed += OnRecvCompleted;

            _sendSaea = new SocketAsyncEventArgs();
            _sendSaea.SetBuffer(_sendBuffer, 0, _sendBuffer.Length);
            _sendSaea.UserToken = _sendSource;
            _sendSaea.Completed += OnSendCompleted;
        }

        // ── Receive ───────────────────────────────────────────────────────────

        /// <summary>
        /// Receives up to <paramref name="count"/> bytes into
        /// <paramref name="buffer"/>.<paramref name="offset"/>.
        /// Returns the number of bytes read, or 0 on graceful close.
        /// </summary>
        /// <exception cref="InvalidOperationException">A receive is already in flight.</exception>
        public async ValueTask<int> ReceiveAsync(
            byte[] buffer, int offset, int count, CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            // Guard against concurrent same-direction calls.
            if (Interlocked.CompareExchange(ref _recvActive, 1, 0) != 0)
                throw new InvalidOperationException(
                    "SaeaSocketChannel does not support concurrent ReceiveAsync calls.");

            int toRead = Math.Min(count, _recvBuffer.Length);
            _recvSaea.SetBuffer(_recvBuffer, 0, toRead);
            _recvSource.Reset();

            CancellationTokenRegistration reg = default;
            if (ct.CanBeCanceled)
            {
                // Cancellation: dispose the socket. Double-dispose is safe (idempotent).
                // The disposal triggers OperationAborted on the pending SAEA, which
                // OnRecvCompleted maps to OperationCanceledException.
                reg = ct.Register(static state =>
                {
                    try { ((Socket)state).Dispose(); } catch { }
                }, _socket);
            }

            try
            {
                bool isPending = _socket.ReceiveAsync(_recvSaea);
                if (!isPending)
                {
                    // Synchronous completion — the Completed event is NOT raised (BCL contract).
                    // Clear the active flag and resolve the source inline.
                    _recvActive = 0;
                    ResolveFromSaea(_recvSaea, _recvSource);
                }

                int bytesRead = await _recvSource.AsValueTask().ConfigureAwait(false);

                if (bytesRead > 0)
                    Buffer.BlockCopy(_recvBuffer, 0, buffer, offset, bytesRead);

                return bytesRead;
            }
            catch
            {
                // If the async path never started (exception before ReceiveAsync),
                // ensure the active flag is cleared so the channel is reusable.
                Interlocked.CompareExchange(ref _recvActive, 0, 1);
                throw;
            }
            finally
            {
                reg.Dispose();
            }
        }

        // ── Send ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Sends all bytes in <paramref name="data"/>. Loops internally until the full
        /// payload is sent, chunking when the payload exceeds the pinned send buffer.
        /// </summary>
        /// <exception cref="InvalidOperationException">A send is already in flight.</exception>
        public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            int offset = 0;
            int remaining = data.Length;

            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();

                if (Interlocked.CompareExchange(ref _sendActive, 1, 0) != 0)
                    throw new InvalidOperationException(
                        "SaeaSocketChannel does not support concurrent SendAsync calls.");

                int toSend = Math.Min(remaining, _sendBuffer.Length);
                data.Slice(offset, toSend).Span.CopyTo(_sendBuffer.AsSpan(0, toSend));

                _sendSaea.SetBuffer(_sendBuffer, 0, toSend);
                _sendSource.Reset();

                CancellationTokenRegistration reg = default;
                if (ct.CanBeCanceled)
                {
                    reg = ct.Register(static state =>
                    {
                        try { ((Socket)state).Dispose(); } catch { }
                    }, _socket);
                }

                try
                {
                    bool isPending = _socket.SendAsync(_sendSaea);
                    if (!isPending)
                    {
                        _sendActive = 0;
                        ResolveFromSaea(_sendSaea, _sendSource);
                    }

                    int sent = await _sendSource.AsValueTask().ConfigureAwait(false);
                    offset += sent;
                    remaining -= sent;
                }
                catch
                {
                    Interlocked.CompareExchange(ref _sendActive, 0, 1);
                    throw;
                }
                finally
                {
                    reg.Dispose();
                }
            }
        }

        // ── Completed handlers (instance methods — access private fields) ──────

        private void OnRecvCompleted(object sender, SocketAsyncEventArgs saea)
        {
            // Clear active flag BEFORE delivering result so Dispose()'s spin-wait unblocks.
            _recvActive = 0;
            ResolveFromSaea(saea, _recvSource);
        }

        private void OnSendCompleted(object sender, SocketAsyncEventArgs saea)
        {
            _sendActive = 0;
            ResolveFromSaea(saea, _sendSource);
        }

        private static void ResolveFromSaea(SocketAsyncEventArgs saea, SaeaCompletionSource source)
        {
            switch (saea.SocketError)
            {
                case SocketError.Success:
                    source.SetResult(saea.BytesTransferred);
                    break;
                case SocketError.OperationAborted:
                case SocketError.Interrupted:
                    source.SetException(new OperationCanceledException(
                        "Socket I/O was cancelled (socket closed or operation aborted)."));
                    break;
                default:
                    source.SetException(new SocketException((int)saea.SocketError));
                    break;
            }
        }

        // ── Disposal ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            // Dispose the socket first. On IOCP (Windows) and kqueue (iOS/macOS), this
            // posts an OperationAborted completion that will fire OnRecv/OnSendCompleted,
            // clearing the active flags and delivering an exception to any awaiter.
            try { _socket.Dispose(); } catch { }

            // Spin-wait until in-flight operations complete. OperationAborted fires
            // essentially immediately after socket disposal, so this is near-zero overhead.
            // We must not return the ArrayPool buffers while the OS/IOCP thread still holds
            // a reference to them.
            var sw = new SpinWait();
            while (Volatile.Read(ref _recvActive) != 0 || Volatile.Read(ref _sendActive) != 0)
                sw.SpinOnce();

            // Safe to dispose SAEAs and return buffers — all completions have fired.
            _recvSaea.Dispose();
            _sendSaea.Dispose();

            ArrayPool<byte>.Shared.Return(_recvBuffer);
            ArrayPool<byte>.Shared.Return(_sendBuffer);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(SaeaSocketChannel));
        }
    }
}
