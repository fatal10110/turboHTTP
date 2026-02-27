using System;
using System.Buffers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace TurboHTTP.Transport.Tcp
{
    /// <summary>
    /// Provides send and receive primitives over a raw <see cref="Socket"/> using
    /// synchronous non-blocking <c>Socket.Poll</c> and <c>Socket.Send</c>/<c>Socket.Receive</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this mode exists:</b> On some IL2CPP targets (consoles, mobile), the runtime's
    /// async socket callbacks (<see cref="SocketAsyncEventArgs"/>) have historically been
    /// unreliable or unavailable. <c>PollSelect</c> bypasses those entirely: a dedicated
    /// poll thread blocks on <c>Socket.Poll</c>, performs a synchronous read/write when data
    /// is ready, and delivers the result via reusable <see cref="PollSelectSource"/> instances
    /// so the caller's <c>async</c> pipeline remains unchanged.
    /// </para>
    /// <para>
    /// <b>Thread model:</b> One background poll thread per channel handles both receive and
    /// send. Because HTTP/1.1 is half-duplex (send then receive), both directions are rarely
    /// pending simultaneously, and the single-thread model avoids extra thread overhead.
    /// </para>
    /// <para>
    /// <b>Concurrency contract:</b> At most one concurrent <see cref="ReceiveAsync"/> and at
    /// most one concurrent <see cref="SendAsync"/> at a time. A second concurrent call to the
    /// same direction throws <see cref="InvalidOperationException"/>.
    /// </para>
    /// <para>
    /// <b>Poll timeout:</b> 1 ms per iteration. This limits worst-case latency from when data
    /// arrives to when it is delivered. Idle CPU cost is approximately 2–10% of one core per
    /// connection on ARM IL2CPP — this is the known trade-off of PollSelect mode vs. SAEA.
    /// </para>
    /// <para>
    /// <b>Cancellation:</b> Disposing the socket causes <c>Poll</c> to return immediately
    /// with false or throw <see cref="ObjectDisposedException"/>, which the poll thread maps
    /// to <see cref="OperationCanceledException"/>.
    /// </para>
    /// </remarks>
    internal sealed class PollSelectSocketChannel : IDisposable
    {
        private const int BufferSize = 16 * 1024;

        /// <summary>Poll timeout in microseconds (1 ms = 1000 µs).</summary>
        private const int PollTimeoutUs = 1000;

        private static int s_threadCounter;

        private readonly Socket _socket;
        private readonly byte[] _recvBuffer;
        private readonly byte[] _sendBuffer;
        private readonly Thread _pollThread;
        private int _disposed;

        // ── Per-direction state ───────────────────────────────────────────────
        // Guarded by _recvLock / _sendLock respectively.

        private readonly object _recvLock = new object();
        private readonly PollSelectSource _recvSource = new PollSelectSource();
        private byte[] _recvTarget;
        private int _recvOffset;
        private int _recvCount;
        private bool _recvPending;

        private readonly object _sendLock = new object();
        private readonly PollSelectSource _sendSource = new PollSelectSource();
        private ReadOnlyMemory<byte> _sendData;
        private bool _sendPending;

        public PollSelectSocketChannel(Socket socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _socket.Blocking = false; // Required for non-blocking Poll-based I/O.

            _recvBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            _sendBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);

            int id = Interlocked.Increment(ref s_threadCounter);
            _pollThread = new Thread(RunPollLoop)
            {
                IsBackground = true,
                Name = $"TurboHTTP.PollSelect-{id}",
            };
            _pollThread.Start();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Receives up to <paramref name="count"/> bytes. Returns bytes read (0 = graceful close).
        /// </summary>
        /// <exception cref="InvalidOperationException">A receive is already in flight.</exception>
        public ValueTask<int> ReceiveAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            lock (_recvLock)
            {
                if (_recvPending)
                    throw new InvalidOperationException(
                        "PollSelectSocketChannel does not support concurrent ReceiveAsync calls.");

                _recvSource.Reset();
                _recvTarget = buffer;
                _recvOffset = offset;
                _recvCount = Math.Min(count, _recvBuffer.Length);
                _recvPending = true;
            }

            var vt = _recvSource.AsValueTask();
            if (!ct.CanBeCanceled)
                return vt;

            // Register cancellation (disposes socket → Poll returns → OperationCanceledException).
            // Direct socket capture avoids the object[] allocation used in naïve patterns.
            var reg = ct.Register(static s => { try { ((Socket)s).Dispose(); } catch { } }, _socket);
            return WrapWithCancellationCleanup(vt, reg);
        }

        /// <summary>Sends all bytes in <paramref name="data"/>.</summary>
        /// <exception cref="InvalidOperationException">A send is already in flight.</exception>
        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            if (data.IsEmpty)
                return default;

            lock (_sendLock)
            {
                if (_sendPending)
                    throw new InvalidOperationException(
                        "PollSelectSocketChannel does not support concurrent SendAsync calls.");

                _sendSource.Reset();
                _sendData = data;
                _sendPending = true;
            }

            var vt = _sendSource.AsVoidValueTask();
            if (!ct.CanBeCanceled)
                return vt;

            var reg = ct.Register(static s => { try { ((Socket)s).Dispose(); } catch { } }, _socket);
            return WrapSendWithCancellationCleanup(vt, reg);
        }

        // ── Poll loop (background thread) ─────────────────────────────────────

        private void RunPollLoop()
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                bool didRecv = TryServiceRecv();
                bool didSend = TryServiceSend();

                // Yield CPU when idle to avoid burning a core. Thread.Sleep(0) releases
                // the remainder of the current time slice, allowing other threads to run.
                // This is the documented CPU trade-off of PollSelect mode.
                if (!didRecv && !didSend)
                    Thread.Sleep(0);
            }

            // Poll loop exited — signal any pending sources so awaiters don't hang.
            AbortPendingOperations();
        }

        private bool TryServiceRecv()
        {
            PollSelectSource src;
            byte[] target;
            int offset, count;

            lock (_recvLock)
            {
                if (!_recvPending) return false;
                src = _recvSource;
                target = _recvTarget;
                offset = _recvOffset;
                count = _recvCount;
            }

            try
            {
                bool readable = _socket.Poll(PollTimeoutUs, SelectMode.SelectRead);
                if (!readable) return false; // Not ready — retry next iteration.

                int received = _socket.Receive(_recvBuffer, 0, count, SocketFlags.None, out var err);

                if (err == SocketError.WouldBlock)
                {
                    // Spurious Poll wakeup (TOCTOU on non-blocking socket) — loop back.
                    // Do NOT deliver 0 bytes, which would falsely signal EOF to the parser.
                    return false;
                }

                if (err != SocketError.Success)
                    throw new SocketException((int)err);

                if (received > 0)
                    Buffer.BlockCopy(_recvBuffer, 0, target, offset, received);

                lock (_recvLock) { _recvPending = false; }
                src.SetResult(received);
                return true;
            }
            catch (OperationCanceledException ex)
            {
                lock (_recvLock) { _recvPending = false; }
                src.SetException(ex);
                return true;
            }
            catch (ObjectDisposedException)
            {
                lock (_recvLock) { _recvPending = false; }
                src.SetException(new OperationCanceledException(
                    "Socket I/O was cancelled (socket disposed)."));
                return true;
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode == SocketError.OperationAborted ||
                ex.SocketErrorCode == SocketError.Interrupted)
            {
                lock (_recvLock) { _recvPending = false; }
                src.SetException(new OperationCanceledException(
                    "Socket I/O was cancelled.", ex));
                return true;
            }
            catch (Exception ex)
            {
                lock (_recvLock) { _recvPending = false; }
                src.SetException(ex);
                return true;
            }
        }

        private bool TryServiceSend()
        {
            PollSelectSource src;
            ReadOnlyMemory<byte> sendData;

            lock (_sendLock)
            {
                if (!_sendPending) return false;
                src = _sendSource;
                sendData = _sendData;
            }

            try
            {
                bool writable = _socket.Poll(PollTimeoutUs, SelectMode.SelectWrite);
                if (!writable) return false; // Not ready — retry.

                int remaining = sendData.Length;
                int sendOffset = 0;

                while (remaining > 0)
                {
                    int toSend = Math.Min(remaining, _sendBuffer.Length);
                    sendData.Slice(sendOffset, toSend).Span.CopyTo(_sendBuffer.AsSpan(0, toSend));

                    int sent = _socket.Send(_sendBuffer, 0, toSend, SocketFlags.None, out var err);

                    if (err == SocketError.WouldBlock || (err == SocketError.Success && sent == 0))
                    {
                        // Kernel send buffer full — wait for writability and retry the same chunk.
                        // Loop indefinitely: the outer request timeout (RawSocketTransport) is the
                        // only deadline; a hard inner timeout would cause spurious failures under
                        // TCP congestion on mobile networks.
                        while (!_socket.Poll(PollTimeoutUs * 10, SelectMode.SelectWrite))
                        {
                            if (Volatile.Read(ref _disposed) != 0)
                                throw new ObjectDisposedException(nameof(PollSelectSocketChannel));
                        }
                        continue; // Retry toSend without advancing offset.
                    }

                    if (err != SocketError.Success)
                        throw new SocketException((int)err);

                    sendOffset += sent;
                    remaining -= sent;
                }

                lock (_sendLock) { _sendPending = false; }
                src.SetResult(0);
                return true;
            }
            catch (OperationCanceledException ex)
            {
                lock (_sendLock) { _sendPending = false; }
                src.SetException(ex);
                return true;
            }
            catch (ObjectDisposedException)
            {
                lock (_sendLock) { _sendPending = false; }
                src.SetException(new OperationCanceledException(
                    "Socket I/O was cancelled (socket disposed)."));
                return true;
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode == SocketError.OperationAborted ||
                ex.SocketErrorCode == SocketError.Interrupted)
            {
                lock (_sendLock) { _sendPending = false; }
                src.SetException(new OperationCanceledException(
                    "Socket I/O was cancelled.", ex));
                return true;
            }
            catch (Exception ex)
            {
                lock (_sendLock) { _sendPending = false; }
                src.SetException(ex);
                return true;
            }
        }

        private void AbortPendingOperations()
        {
            // Signal any pending sources so awaiters receive OperationCanceledException
            // rather than hanging indefinitely after the poll loop exits.
            PollSelectSource recvSrc = null, sendSrc = null;
            lock (_recvLock)
            {
                if (_recvPending) { recvSrc = _recvSource; _recvPending = false; }
            }
            lock (_sendLock)
            {
                if (_sendPending) { sendSrc = _sendSource; _sendPending = false; }
            }
            recvSrc?.SetException(new OperationCanceledException("Channel disposed."));
            sendSrc?.SetException(new OperationCanceledException("Channel disposed."));
        }

        // ── Disposal ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            // Disposing the socket causes any blocked Poll() to return immediately,
            // waking the poll thread. The poll thread checks _disposed, exits the loop,
            // calls AbortPendingOperations(), and terminates.
            try { _socket.Dispose(); } catch { }

            // Wait for the poll thread to exit before returning the pooled buffers.
            // 500ms is generous enough for mobile GC pauses; if it still hasn't exited,
            // we accept the minor leak rather than returning a buffer the thread might
            // still be writing into.
            _pollThread.Join(millisecondsTimeout: 500);

            if (!_pollThread.IsAlive)
            {
                ArrayPool<byte>.Shared.Return(_recvBuffer);
                ArrayPool<byte>.Shared.Return(_sendBuffer);
            }
            // If the thread is still alive after 500ms (pathological case), the buffers
            // are not returned. This is a deliberate safety trade-off: a small one-time
            // allocation leak is preferable to corrupting a rented buffer from another
            // operation.
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(PollSelectSocketChannel));
        }

        private static async ValueTask<int> WrapWithCancellationCleanup(
            ValueTask<int> task, CancellationTokenRegistration reg)
        {
            try   { return await task.ConfigureAwait(false); }
            finally { reg.Dispose(); }
        }

        private static async ValueTask WrapSendWithCancellationCleanup(
            ValueTask task, CancellationTokenRegistration reg)
        {
            try   { await task.ConfigureAwait(false); }
            finally { reg.Dispose(); }
        }
    }

    // ── PollSelectSource ──────────────────────────────────────────────────────

    /// <summary>
    /// Reusable <see cref="IValueTaskSource{T}"/> / <see cref="IValueTaskSource"/> bridge
    /// for <see cref="PollSelectSocketChannel"/>. One instance per direction, reset between
    /// operations via <see cref="Reset"/>. Implements the non-generic <see cref="IValueTaskSource"/>
    /// interface to support zero-allocation <see cref="System.Threading.Tasks.ValueTask"/>
    /// (void) returns for the send direction without wrapping in a <c>Task</c>.
    /// </summary>
    internal sealed class PollSelectSource : IValueTaskSource<int>, IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<int> _core;

        /// <summary>Resets the source for a new operation. Call before each use.</summary>
        public void Reset() => _core.Reset();

        /// <summary>Returns a <see cref="System.Threading.Tasks.ValueTask{T}"/> for the receive path.</summary>
        public System.Threading.Tasks.ValueTask<int> AsValueTask()
            => new System.Threading.Tasks.ValueTask<int>(this, _core.Version);

        /// <summary>
        /// Returns a non-allocating <see cref="System.Threading.Tasks.ValueTask"/> for the send path.
        /// Uses the non-generic <see cref="IValueTaskSource"/> interface to avoid wrapping in a
        /// <c>Task</c>.
        /// </summary>
        public System.Threading.Tasks.ValueTask AsVoidValueTask()
            => new System.Threading.Tasks.ValueTask((IValueTaskSource)this, _core.Version);

        public void SetResult(int value) => _core.SetResult(value);
        public void SetException(Exception ex) => _core.SetException(ex);

        // ── IValueTaskSource<int> ──────────────────────────────────────────────
        int IValueTaskSource<int>.GetResult(short token) => _core.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _core.GetStatus(token);
        void IValueTaskSource<int>.OnCompleted(Action<object> c, object s, short t, ValueTaskSourceOnCompletedFlags f)
            => _core.OnCompleted(c, s, t, f);

        // ── IValueTaskSource (non-generic, for void ValueTask) ─────────────────
        void IValueTaskSource.GetResult(short token) => _core.GetResult(token); // result value ignored
        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);
        void IValueTaskSource.OnCompleted(Action<object> c, object s, short t, ValueTaskSourceOnCompletedFlags f)
            => _core.OnCompleted(c, s, t, f);
    }
}
