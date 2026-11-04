using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace TurboHTTP.Transport.Tcp
{
    /// <summary>
    /// Provides send and receive primitives over a raw <see cref="Socket"/> using
    /// synchronous non-blocking <c>Socket.Poll</c>/<c>Socket.Select</c> and
    /// <c>Socket.Send</c>/<c>Socket.Receive</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this mode exists:</b> On some IL2CPP targets (consoles, mobile), the runtime's
    /// async socket callbacks (<see cref="SocketAsyncEventArgs"/>) have historically been
    /// unreliable or unavailable. <c>PollSelect</c> bypasses those entirely: a dedicated
    /// poll thread waits for readiness, performs a synchronous read/write, and delivers the
    /// result via reusable <see cref="PollSelectSource"/> instances so the caller's
    /// <c>async</c> pipeline remains unchanged.
    /// </para>
    /// <para>
    /// <b>Thread model:</b> One background poll thread per channel services both directions.
    /// With nothing pending it blocks on a signal (no CPU). With one direction pending it
    /// polls that direction; with both pending it waits on a single <c>Socket.Select</c> over
    /// read and write interest, so a send stalled on a full kernel buffer never stops the
    /// receive side from draining (full-duplex HTTP/2 cannot deadlock on it). A send whose
    /// data is array-backed is first written inline on the caller's thread (non-blocking);
    /// only the part the kernel did not accept is handed to the poll thread.
    /// </para>
    /// <para>
    /// <b>Concurrency contract:</b> At most one concurrent <see cref="ReceiveAsync"/> and at
    /// most one concurrent <see cref="SendAsync"/> at a time. A second concurrent call to the
    /// same direction throws <see cref="InvalidOperationException"/>.
    /// </para>
    /// <para>
    /// <b>Poll slice:</b> each wait lasts at most 20 ms so a newly issued operation in the
    /// other direction, a cancellation or a dispose is noticed within that bound. An idle
    /// connection with no pending operation does not wake at all; an HTTP/2 connection whose
    /// receive is always pending wakes about 50 times per second (#38 replaced the former
    /// 1 ms spin, documented at 2-10% of a core per idle connection).
    /// </para>
    /// <para>
    /// <b>Cancellation:</b> Cancelling an operation disposes the socket, so the next poll throws
    /// <see cref="ObjectDisposedException"/>, which is surfaced as
    /// <see cref="OperationCanceledException"/>.
    /// </para>
    /// </remarks>
    internal sealed class PollSelectSocketChannel : IDisposable
    {
        private const int BufferSize = 16 * 1024;

        /// <summary>Upper bound of one readiness wait, in microseconds (20 ms).</summary>
        internal const int PollSliceUs = 20_000;

        private static int s_threadCounter;

        private readonly Socket _socket;
        private readonly byte[] _recvBuffer;
        private readonly byte[] _sendBuffer;
        private readonly Thread _pollThread;
        // Set by ReceiveAsync/SendAsync/Dispose; the poll thread blocks on it while idle.
        private readonly ManualResetEventSlim _workSignal = new ManualResetEventSlim(false);
        private int _disposed;
        private long _pollIterations;

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
        private int _sendOffset;
        private bool _sendBusy;    // a SendAsync owns the send direction (inline or queued)
        private bool _sendPending; // the remainder is queued for the poll thread

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

        /// <summary>Poll-loop iterations so far (test hook for the idle-CPU bound, #38).</summary>
        internal long PollIterations => Interlocked.Read(ref _pollIterations);

        /// <summary>
        /// Test hook: runs right before the cancellation callback is registered, so a test can
        /// cancel the token there and make the registration run inline (#170).
        /// </summary>
        internal Action TestHookBeforeRegister { get; set; }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Receives up to <paramref name="count"/> bytes. Returns bytes read (0 = graceful close).
        /// </summary>
        /// <exception cref="InvalidOperationException">A receive is already in flight.</exception>
        public ValueTask<int> ReceiveAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            ThrowIfCancelledOrDisposed(ct);

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
            PublishWork();
            if (!ct.CanBeCanceled)
                return vt;

            // Register cancellation (disposes socket → next poll throws → OperationCanceledException).
            // Direct socket capture avoids the object[] allocation used in naïve patterns.
            TestHookBeforeRegister?.Invoke();
            var reg = ct.Register(static s => { try { ((Socket)s).Dispose(); } catch { } }, _socket);
            return WrapWithCancellationCleanup(vt, reg, ct);
        }

        /// <summary>Sends all bytes in <paramref name="data"/>.</summary>
        /// <exception cref="InvalidOperationException">A send is already in flight.</exception>
        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            ThrowIfCancelledOrDisposed(ct);

            if (data.IsEmpty)
                return default;

            lock (_sendLock)
            {
                if (_sendBusy)
                    throw new InvalidOperationException(
                        "PollSelectSocketChannel does not support concurrent SendAsync calls.");
                _sendBusy = true;
            }

            // Inline fast path: write what the kernel accepts now, straight from the caller's
            // array (the pooled _sendBuffer stays poll-thread-only, so Dispose may return it).
            int sent = 0;
            if (MemoryMarshal.TryGetArray(data, out _))
            {
                try
                {
                    sent = SendAvailable(data, 0, allowCopy: false);
                }
                catch (Exception ex)
                {
                    lock (_sendLock) { _sendBusy = false; }
                    ThrowMapped(ex, ct);
                }

                if (sent == data.Length)
                {
                    lock (_sendLock) { _sendBusy = false; }
                    return default;
                }
            }

            lock (_sendLock)
            {
                _sendSource.Reset();
                _sendData = data;
                _sendOffset = sent;
                _sendPending = true;
            }

            var vt = _sendSource.AsVoidValueTask();
            PublishWork();
            if (!ct.CanBeCanceled)
                return vt;

            TestHookBeforeRegister?.Invoke();
            var reg = ct.Register(static s => { try { ((Socket)s).Dispose(); } catch { } }, _socket);
            return WrapSendWithCancellationCleanup(vt, reg, ct);
        }

        // ── Poll loop (background thread) ─────────────────────────────────────

        private void RunPollLoop()
        {
            // Reused for Socket.Select (only when both directions are pending).
            var readList = new List<Socket>(1);
            var writeList = new List<Socket>(1);

            try
            {
                while (Volatile.Read(ref _disposed) == 0)
                {
                    Interlocked.Increment(ref _pollIterations);

                    // Reset BEFORE sampling the pending flags: a producer that publishes after the
                    // sample sets the signal after this reset, so its wake-up is never lost.
                    _workSignal.Reset();

                    bool recv, send;
                    lock (_recvLock) { recv = _recvPending; }
                    lock (_sendLock) { send = _sendPending; }

                    if (!recv && !send)
                    {
                        if (Volatile.Read(ref _disposed) != 0)
                            break;
                        _workSignal.Wait();
                        continue;
                    }

                    bool readable = false, writable = false;
                    try
                    {
                        if (recv && send)
                        {
                            readList.Clear();
                            writeList.Clear();
                            readList.Add(_socket);
                            writeList.Add(_socket);
                            Socket.Select(readList, writeList, null, PollSliceUs);
                            readable = readList.Count > 0;
                            writable = writeList.Count > 0;
                        }
                        else if (recv)
                        {
                            readable = _socket.Poll(PollSliceUs, SelectMode.SelectRead);
                        }
                        else
                        {
                            writable = _socket.Poll(PollSliceUs, SelectMode.SelectWrite);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Socket disposed (cancellation) or failed: fail whatever is pending.
                        if (recv) FailRecv(ex);
                        if (send) FailSend(ex);
                        continue;
                    }

                    if (readable) ServiceRecv();
                    if (writable) ServiceSend();
                }
            }
            finally
            {
                // Poll loop exited — signal any pending sources so awaiters don't hang.
                AbortPendingOperations();
            }
        }

        private void ServiceRecv()
        {
            PollSelectSource src;
            byte[] target;
            int offset, count;

            lock (_recvLock)
            {
                if (!_recvPending) return;
                src = _recvSource;
                target = _recvTarget;
                offset = _recvOffset;
                count = _recvCount;
            }

            int received;
            try
            {
                received = _socket.Receive(_recvBuffer, 0, count, SocketFlags.None, out var err);

                if (err == SocketError.WouldBlock)
                {
                    // Spurious readiness (TOCTOU on non-blocking socket) — loop back.
                    // Do NOT deliver 0 bytes, which would falsely signal EOF to the parser.
                    return;
                }

                if (err != SocketError.Success)
                    throw new SocketException((int)err);

                if (received > 0)
                    Buffer.BlockCopy(_recvBuffer, 0, target, offset, received);
            }
            catch (Exception ex)
            {
                FailRecv(ex);
                return;
            }

            lock (_recvLock) { _recvPending = false; }
            src.SetResult(received);
        }

        private void ServiceSend()
        {
            PollSelectSource src;
            ReadOnlyMemory<byte> data;
            int offset;

            lock (_sendLock)
            {
                if (!_sendPending) return;
                src = _sendSource;
                data = _sendData;
                offset = _sendOffset;
            }

            int total;
            try
            {
                total = SendAvailable(data, offset, allowCopy: true);
            }
            catch (Exception ex)
            {
                FailSend(ex);
                return;
            }

            if (total < data.Length)
            {
                // Kernel buffer full: record progress and return to the loop, which waits on
                // read+write interest so the receive side keeps being serviced meanwhile.
                lock (_sendLock) { _sendOffset = total; }
                return;
            }

            lock (_sendLock)
            {
                _sendPending = false;
                _sendBusy = false;
                _sendData = default;
            }

            src.SetResult(0);
        }

        /// <summary>
        /// Writes from <paramref name="offset"/> until the payload is done or the kernel would
        /// block. Returns the new offset. Array-backed data is sent in place; other memory is
        /// staged through <c>_sendBuffer</c>, which only the poll thread may use
        /// (<paramref name="allowCopy"/>).
        /// </summary>
        private int SendAvailable(ReadOnlyMemory<byte> data, int offset, bool allowCopy)
        {
            bool isArray = MemoryMarshal.TryGetArray(data, out var segment);
            if (!isArray && !allowCopy)
                return offset;

            while (offset < data.Length)
            {
                int sent;
                SocketError err;
                if (isArray)
                {
                    sent = _socket.Send(segment.Array, segment.Offset + offset, data.Length - offset,
                        SocketFlags.None, out err);
                }
                else
                {
                    int toSend = Math.Min(data.Length - offset, _sendBuffer.Length);
                    data.Slice(offset, toSend).Span.CopyTo(_sendBuffer.AsSpan(0, toSend));
                    sent = _socket.Send(_sendBuffer, 0, toSend, SocketFlags.None, out err);
                }

                if (err == SocketError.WouldBlock || (err == SocketError.Success && sent == 0))
                    break;

                if (err != SocketError.Success)
                    throw new SocketException((int)err);

                offset += sent;
            }

            return offset;
        }

        private void FailRecv(Exception ex)
        {
            PollSelectSource src;
            lock (_recvLock)
            {
                if (!_recvPending) return;
                _recvPending = false;
                src = _recvSource;
            }

            src.SetException(MapException(ex, default));
        }

        private void FailSend(Exception ex)
        {
            PollSelectSource src;
            lock (_sendLock)
            {
                if (!_sendPending) return;
                _sendPending = false;
                _sendBusy = false;
                _sendData = default;
                src = _sendSource;
            }

            src.SetException(MapException(ex, default));
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
                if (_sendPending)
                {
                    sendSrc = _sendSource;
                    _sendPending = false;
                    _sendBusy = false;
                    _sendData = default;
                }
            }
            recvSrc?.SetException(new OperationCanceledException("Channel disposed."));
            sendSrc?.SetException(new OperationCanceledException("Channel disposed."));
        }

        private void PublishWork()
        {
            _workSignal.Set();

            // Dispose raced the publish: the poll thread may already be gone, so fail the
            // operation here instead of leaving it pending forever (idempotent with the thread).
            if (Volatile.Read(ref _disposed) != 0)
                AbortPendingOperations();
        }

        // ── Disposal ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            // Disposing the socket makes the next poll throw; the signal wakes an idle thread.
            // Either way the poll thread exits within one poll slice, calls
            // AbortPendingOperations(), and terminates.
            try { _socket.Dispose(); } catch { }
            _workSignal.Set();

            // Wait for the poll thread to exit before returning the pooled buffers.
            // 500ms is generous enough for mobile GC pauses; if it still hasn't exited,
            // we accept the minor leak rather than returning a buffer the thread might
            // still be writing into. (_workSignal is deliberately not disposed: a racing
            // ReceiveAsync/SendAsync may still Set it; it holds no unmanaged handle until a
            // wait blocks, and that handle is finalizable.)
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

        /// <summary>
        /// Cancellation wins over disposal: a token that is already cancelled surfaces as
        /// <see cref="OperationCanceledException"/> even when that cancellation already disposed
        /// the socket (#170).
        /// </summary>
        private void ThrowIfCancelledOrDisposed(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(PollSelectSocketChannel));
        }

        /// <summary>
        /// Maps a socket failure: disposal/abort while <paramref name="ct"/> is cancelled becomes
        /// <c>OperationCanceledException(ct)</c>; any other disposal/abort becomes a plain
        /// <see cref="OperationCanceledException"/> (the socket is only disposed by cancellation or
        /// channel disposal); everything else passes through.
        /// </summary>
        private static Exception MapException(Exception ex, CancellationToken ct)
        {
            bool aborted = ex is ObjectDisposedException ||
                           (ex is SocketException se &&
                            (se.SocketErrorCode == SocketError.OperationAborted ||
                             se.SocketErrorCode == SocketError.Interrupted));

            if (ex is OperationCanceledException oce)
            {
                return ct.IsCancellationRequested ? new OperationCanceledException(oce.Message, oce, ct) : oce;
            }

            if (!aborted)
                return ex;

            return ct.IsCancellationRequested
                ? new OperationCanceledException("Socket I/O was cancelled.", ex, ct)
                : new OperationCanceledException("Socket I/O was cancelled (socket disposed or aborted).", ex);
        }

        private static void ThrowMapped(Exception ex, CancellationToken ct)
        {
            var mapped = MapException(ex, ct);
            if (ReferenceEquals(mapped, ex))
                ExceptionDispatchInfo.Capture(ex).Throw();
            throw mapped;
        }

        private static async ValueTask<int> WrapWithCancellationCleanup(
            ValueTask<int> task, CancellationTokenRegistration reg, CancellationToken ct)
        {
            try { return await task.ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested && ex.CancellationToken != ct)
            {
                throw new OperationCanceledException(ex.Message, ex, ct);
            }
            finally { reg.Dispose(); }
        }

        private static async ValueTask WrapSendWithCancellationCleanup(
            ValueTask task, CancellationTokenRegistration reg, CancellationToken ct)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested && ex.CancellationToken != ct)
            {
                throw new OperationCanceledException(ex.Message, ex, ct);
            }
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
