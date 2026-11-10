using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;

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
    /// <see cref="ByteArrayPool"/> at construction. They are NOT returned until any
    /// in-flight SAEA operations have completed (the kernel is done with the buffer), ensuring
    /// no memory-corruption window between socket-disposal-triggered OperationAborted and
    /// the caller-side ArrayPool return.
    /// </para>
    /// <para>
    /// <b>Cancellation:</b> <see cref="CancellationToken"/> cancellation disposes the channel
    /// (<see cref="Dispose"/>), which fails the pending operation with
    /// <see cref="OperationCanceledException"/> at once, without waiting for an OS completion that
    /// IL2CPP may never post (#384).
    /// </para>
    /// <para>
    /// <b>Descriptor lifetime (#384):</b> on selector platforms (kqueue/epoll) the socket is shut
    /// down on dispose and closed only once every submitted operation ended, or at
    /// <see cref="SocketHandleHold.HoldTimeout"/>. IL2CPP's close does not remove a pending job from
    /// its selector, so a descriptor closed under a pending receive could be reused by the next
    /// socket and its first receive dispatched to the stale job.
    /// </para>
    /// </remarks>
    internal sealed class SaeaSocketChannel : IDisposable
    {
        /// <summary>Size of each pinned SAEA buffer.</summary>
        private const int BufferSize = 16 * 1024;

        /// <summary>
        /// IOCP (Windows): how long after <see cref="Dispose"/> closed the socket an in-flight
        /// completion may take before the buffers are given up and leaked (#168). Selector platforms
        /// hold the descriptor for <see cref="SocketHandleHold.HoldTimeout"/> instead (#384).
        /// </summary>
        internal const int DisposeDrainTimeoutMs = 500;

        // Volatile: 1 = shut down, drain, then close (selector platforms end a pending operation on
        // shutdown); 0 = close first (IOCP aborts it on close).
        private static int s_shutdownBeforeClose = SaeaPlatformDetection.CloseAbortsPendingIo ? 0 : 1;

        private static long s_buffersLeakedOnDispose;

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

        // 1 once the OS completion of the current operation arrived: Dispose then leaves the result
        // to it, so the operation's own error stays the cause (#415). Reset when an operation starts.
        private volatile int _recvCompleting;
        private volatile int _sendCompleting;

        private int _disposed;
        // Interlocked: 1 once the socket was closed and the buffers released or given up (exactly once).
        private int _released;
        // The fallback bound for an operation that never ends; set by Dispose, disposed on release.
        private Timer _releaseBound;
        // Test override of the release bound in milliseconds, or -1 for the platform default.
        private int _boundOverrideMs = -1;
        private int _buffersReleased;

        public SaeaSocketChannel(Socket socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));

            _recvBuffer = ByteArrayPool.Rent(BufferSize);
            _sendBuffer = ByteArrayPool.Rent(BufferSize);

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

        /// <summary>
        /// Test hook: runs right before the cancellation callback is registered, so a test can
        /// cancel the token there and make the registration dispose the socket inline (#170).
        /// </summary>
        internal Action TestHookBeforeRegister { get; set; }

        /// <summary>
        /// Test hook: runs on the completing thread when a pending receive ends, with its socket error,
        /// before the channel marks the receive as ended. <see cref="Dispose"/> cannot close the socket
        /// on the shutdown-first path until this returned, so a test can check the descriptor's state
        /// at the exact moment the receive ended (#410).
        /// </summary>
        internal Action<SocketError> TestHookReceiveEnded { get; set; }

        /// <summary>True once <see cref="Dispose"/> returned the pooled buffers (test hook, #168).</summary>
        internal bool BuffersReleased => Volatile.Read(ref _buffersReleased) != 0;

        /// <summary>Channels whose Dispose hit the drain bound and leaked their buffers (process-wide).</summary>
        internal static long BuffersLeakedOnDispose => Interlocked.Read(ref s_buffersLeakedOnDispose);

        /// <summary>
        /// The dispose order: true (selector platforms) shuts the socket down and closes it only once
        /// every submitted operation ended (#384); false (IOCP) closes it first. Tests set it.
        /// </summary>
        internal static bool ShutdownBeforeClose
        {
            get => Volatile.Read(ref s_shutdownBeforeClose) != 0;
            set => Volatile.Write(ref s_shutdownBeforeClose, value ? 1 : 0);
        }

        /// <summary>Test hook: the bound after which a dispose with an operation still in flight gives up waiting for it.</summary>
        internal void SetDrainTimeoutForTest(int drainTimeoutMs) => Volatile.Write(ref _boundOverrideMs, drainTimeoutMs);

        /// <summary>True once the channel closed its socket and released or gave up its buffers (test hook, #384).</summary>
        internal bool IsReleased => Volatile.Read(ref _released) != 0;

        /// <summary>
        /// Test hook: marks a receive as submitted to the OS without submitting one, i.e. a
        /// completion that never fires (#168). Also shortens the drain bound for the test.
        /// </summary>
        internal void SimulateStuckReceiveForTest(int drainTimeoutMs)
        {
            Volatile.Write(ref _boundOverrideMs, drainTimeoutMs);
            _recvActive = 1;
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
            ThrowIfCancelledOrDisposed(ct);

            // Guard against concurrent same-direction calls.
            if (Interlocked.CompareExchange(ref _recvActive, 1, 0) != 0)
                throw new InvalidOperationException(
                    "SaeaSocketChannel does not support concurrent ReceiveAsync calls.");

            int toRead = Math.Min(count, _recvBuffer.Length);
            _recvSaea.SetBuffer(_recvBuffer, 0, toRead);
            _recvSource.Reset();
            _recvCompleting = 0;

            CancellationTokenRegistration reg = default;
            if (ct.CanBeCanceled)
            {
                TestHookBeforeRegister?.Invoke();
                // Cancellation disposes the channel: the pending receive fails at once, and the
                // descriptor stays open until the OS operation ended (#384).
                reg = ct.Register(static state => ((SaeaSocketChannel)state).Dispose(), this);
            }

            bool submitted = false;
            try
            {
                bool isPending = _socket.ReceiveAsync(_recvSaea);
                if (!isPending)
                {
                    // Synchronous completion — the Completed event is NOT raised (BCL contract).
                    // Clear the active flag and resolve the source inline.
                    Interlocked.Exchange(ref _recvActive, 0);
                    ResolveFromSaea(_recvSaea, _recvSource);
                }
                else
                {
                    submitted = true;
                }

                int bytesRead = await _recvSource.AsValueTask().ConfigureAwait(false);

                if (bytesRead > 0)
                    Buffer.BlockCopy(_recvBuffer, 0, buffer, offset, bytesRead);

                return bytesRead;
            }
            catch (Exception ex)
            {
                // Only an operation the OS never took is over here. A submitted one failed by
                // Dispose is still in flight: its completion clears the flag (#384).
                if (!submitted)
                    Interlocked.CompareExchange(ref _recvActive, 0, 1);
                if (IsCancellationRace(ex, ct))
                    throw new OperationCanceledException("Socket I/O was cancelled.", ex, ct);
                throw;
            }
            finally
            {
                reg.Dispose();
                // After the copy above: releasing returns the receive buffer to the pool.
                ReleaseIfDisposed();
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
            ThrowIfCancelledOrDisposed(ct);

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
                _sendCompleting = 0;

                CancellationTokenRegistration reg = default;
                if (ct.CanBeCanceled)
                {
                    TestHookBeforeRegister?.Invoke();
                    reg = ct.Register(static state => ((SaeaSocketChannel)state).Dispose(), this);
                }

                bool submitted = false;
                try
                {
                    bool isPending = _socket.SendAsync(_sendSaea);
                    if (!isPending)
                    {
                        Interlocked.Exchange(ref _sendActive, 0);
                        ResolveFromSaea(_sendSaea, _sendSource);
                    }
                    else
                    {
                        submitted = true;
                    }

                    int sent = await _sendSource.AsValueTask().ConfigureAwait(false);
                    offset += sent;
                    remaining -= sent;
                }
                catch (Exception ex)
                {
                    if (!submitted)
                        Interlocked.CompareExchange(ref _sendActive, 0, 1);
                    if (IsCancellationRace(ex, ct))
                        throw new OperationCanceledException("Socket I/O was cancelled.", ex, ct);
                    throw;
                }
                finally
                {
                    reg.Dispose();
                    ReleaseIfDisposed();
                }
            }
        }

        // ── Completed handlers (instance methods — access private fields) ──────

        private void OnRecvCompleted(object sender, SocketAsyncEventArgs saea)
        {
            _recvCompleting = 1;
            TestHookReceiveEnded?.Invoke(saea.SocketError);

            // Interlocked (a full fence): Dispose sets _disposed and then reads this flag; this
            // thread clears the flag and then reads _disposed, so one of the two releases.
            Interlocked.Exchange(ref _recvActive, 0);
            ResolveFromSaea(saea, _recvSource);
            ReleaseIfDisposed();
        }

        private void OnSendCompleted(object sender, SocketAsyncEventArgs saea)
        {
            _sendCompleting = 1;
            Interlocked.Exchange(ref _sendActive, 0);
            ResolveFromSaea(saea, _sendSource);
            ReleaseIfDisposed();
        }

        private static void ResolveFromSaea(SocketAsyncEventArgs saea, SaeaCompletionSource source)
        {
            switch (saea.SocketError)
            {
                case SocketError.Success:
                    source.TrySetResult(saea.BytesTransferred);
                    break;
                case SocketError.OperationAborted:
                case SocketError.Interrupted:
                    source.TrySetException(new OperationCanceledException(
                        "Socket I/O was cancelled (socket closed or operation aborted)."));
                    break;
                default:
                    source.TrySetException(new SocketException((int)saea.SocketError));
                    break;
            }
        }

        // ── Disposal ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            bool shutdownFirst = ShutdownBeforeClose;
            if (shutdownFirst)
            {
                // Selector platforms (Mono/IL2CPP on kqueue/epoll): shut down now, so the peer sees
                // the close and a pending receive ends while the descriptor is still open. The
                // receive reports Interrupted there, not end of stream: the Mono and IL2CPP socket
                // layers turn a 0-byte read after a local SHUT_RD into EINTR (#410). The socket is
                // closed only once every submitted operation ended: IL2CPP's close does not remove
                // a pending job from its selector, and a new socket would get the same number (#384).
                try { _socket.Shutdown(SocketShutdown.Both); } catch { }
            }
            else
            {
                // IOCP (Windows): closing posts an OperationAborted completion for each pending
                // operation. IOCP has no descriptor-keyed selector to go stale.
                try { _socket.Dispose(); } catch { }
            }

            // Callers waiting on an operation get their failure now, not when (or if) the OS posts
            // its completion: IL2CPP did not end about 9% of abandoned reads (#384, #168).
            FailPendingOperations();

            if (TryRelease())
                return;

            int boundMs = Volatile.Read(ref _boundOverrideMs);
            if (boundMs < 0)
                boundMs = shutdownFirst ? (int)SocketHandleHold.HoldTimeout.TotalMilliseconds : DisposeDrainTimeoutMs;
            var bound = new Timer(static state => ((SaeaSocketChannel)state).ReleaseAtBound(), this, boundMs, Timeout.Infinite);
            Volatile.Write(ref _releaseBound, bound);
            if (Volatile.Read(ref _released) != 0)
                bound.Dispose();
        }

        private void FailPendingOperations()
        {
            // An operation whose completion already arrived ends with its own result.
            if (_recvActive != 0 && _recvCompleting == 0)
                _recvSource.TrySetException(new OperationCanceledException("Socket I/O was cancelled (channel disposed)."));
            if (_sendActive != 0 && _sendCompleting == 0)
                _sendSource.TrySetException(new OperationCanceledException("Socket I/O was cancelled (channel disposed)."));
        }

        private void ReleaseIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                TryRelease();
        }

        /// <summary>
        /// Closes the socket and returns the buffers once no submitted operation is in flight; false
        /// while one still is. Runs exactly once, on whichever thread sees the last operation end.
        /// </summary>
        private bool TryRelease()
        {
            if (Volatile.Read(ref _recvActive) != 0 || Volatile.Read(ref _sendActive) != 0)
                return false;
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return true;

            Volatile.Read(ref _releaseBound)?.Dispose();
            try { _socket.Dispose(); } catch { }

            // Safe to dispose SAEAs and return buffers — all completions have fired.
            _recvSaea.Dispose();
            _sendSaea.Dispose();
            ByteArrayPool.Return(_recvBuffer);
            ByteArrayPool.Return(_sendBuffer);
            Volatile.Write(ref _buffersReleased, 1);
            return true;
        }

        /// <summary>
        /// The fallback for an operation that never ended (#168, Mono #293): close the socket
        /// anyway, accepting the descriptor-reuse risk the hold avoids, and give up the buffers.
        /// </summary>
        private void ReleaseAtBound()
        {
            if (Volatile.Read(ref _recvActive) == 0 && Volatile.Read(ref _sendActive) == 0)
            {
                TryRelease();
                return;
            }

            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            Volatile.Read(ref _releaseBound)?.Dispose();
            try { _socket.Dispose(); } catch { }

            // The kernel may still write into the buffers, so returning them to the shared pool
            // could corrupt another renter's data: leak them on purpose.
            Interlocked.Increment(ref s_buffersLeakedOnDispose);
            ByteArrayPool.Abandon(_recvBuffer);
            ByteArrayPool.Abandon(_sendBuffer);
            TurboDiagnostics.LogOnce(
                TurboDiagnostics.OnceSaeaDisposeDrainTimeout,
                "[TurboHTTP] A SAEA socket operation did not end after the socket was disposed; the socket was " +
                "closed at the bound and its buffers were not returned to the pool (logged once).");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Cancellation wins over disposal: an already-cancelled token surfaces as
        /// <see cref="OperationCanceledException"/> even when that cancellation disposed the socket (#170).
        /// </summary>
        private void ThrowIfCancelledOrDisposed(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(SaeaSocketChannel));
        }

        /// <summary>
        /// Cancellation wins: once <paramref name="ct"/> is cancelled, any failure of the operation is
        /// reported as that cancellation. The registration disposes the socket, so the socket call
        /// throws <see cref="ObjectDisposedException"/> or reports an abort (#170), and an operation
        /// that ends with another error while the token is cancelled lost the race to it too (#415).
        /// </summary>
        private static bool IsCancellationRace(Exception ex, CancellationToken ct) =>
            ct.IsCancellationRequested && !(ex is OperationCanceledException oce && oce.CancellationToken == ct);
    }
}
