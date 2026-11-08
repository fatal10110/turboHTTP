using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Tcp
{
    /// <summary>
    /// Single owner of one socket's handle for the stream that wraps it (#384). Closing shuts the
    /// socket down at once, so the peer sees the close and a pending receive ends with end of stream,
    /// but releases the handle (closes the descriptor) only once every operation that was pending on
    /// it ended, or after <see cref="HoldTimeout"/> for an operation that never ends.
    /// <para>
    /// IL2CPP's socket close does not remove a pending receive from the runtime's I/O selector,
    /// which keys its jobs by descriptor number (libil2cpp <c>Socket::Close_icall</c> never calls
    /// <c>threadpool_ms_io_remove_socket</c>). A socket closed under a pending read leaves that job
    /// behind; a new socket that reuses the number queues its own receive behind the stale job and
    /// loses it (#369, #384). While the handle is held the operating system cannot hand the number
    /// to another socket, and a shut-down socket is readable (end of stream), so the selector
    /// dispatches the stale job and the read ends before the number is released.
    /// </para>
    /// <para>
    /// Trade-off of the fallback: where an operation never ends (Mono #293), its descriptor (a
    /// socket already shut down, so no traffic) stays open for <see cref="HoldTimeout"/> before it
    /// is closed anyway. Nothing is held when no operation was pending at close, which is every
    /// close outside a cancellation.
    /// </para>
    /// <para>
    /// The owning stream brackets every read and write with <see cref="EnterIo"/> and
    /// <see cref="ExitIo"/>. One read and one write may be pending at a time (the stream contract);
    /// a pending operation is remembered without a continuation, so the I/O path allocates nothing.
    /// </para>
    /// </summary>
    internal sealed class SocketHandleHold
    {
        private static readonly Action<Task, object> s_operationEnded =
            static (task, state) => ((SocketHandleHold)state).OnOperationEnded(task);

        private static readonly TimerCallback s_bound = static state => ((SocketHandleHold)state).Release(atBound: true);

        // Ticks, so a 32-bit runtime never reads a torn value (Interlocked.Read/Exchange).
        private static long s_holdTimeoutTicks = TimeSpan.FromSeconds(30).Ticks;

        // Interlocked: handles held past their close, and how each hold ended.
        private static int s_heldCount;
        private static long s_releasedAfterEndCount;
        private static long s_releasedAtBoundCount;

        private readonly Socket _socket;
        private readonly object _lock = new object();

        // Guarded by _lock.
        private int _starting;
        private Task _pendingRead;
        private Task _pendingWrite;
        private bool _closeRequested;
        private int _holders;

        // Interlocked: 0 while the handle is open, 1 once released.
        private int _released;

        // Written once by Close (Volatile); disposed by whichever of Close/Release runs last.
        private Timer _bound;

        internal SocketHandleHold(Socket socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        }

        /// <summary>
        /// How long a closed socket's handle is held for an operation that never ended. Longer than
        /// <see cref="TurboHTTP.Core.Internal.AbandonedSocketIo.SettleTimeout"/> (the gate on new
        /// sockets), because releasing the handle is what lets the descriptor number be reused.
        /// Tests set it.
        /// </summary>
        internal static TimeSpan HoldTimeout
        {
            get => new TimeSpan(Interlocked.Read(ref s_holdTimeoutTicks));
            set => Interlocked.Exchange(ref s_holdTimeoutTicks, value.Ticks);
        }

        /// <summary>Handles closed with an operation pending that are still held.</summary>
        internal static int HeldCount => Volatile.Read(ref s_heldCount);

        /// <summary>Held handles released because their pending operations ended.</summary>
        internal static long ReleasedAfterEndCount => Interlocked.Read(ref s_releasedAfterEndCount);

        /// <summary>Held handles released by <see cref="HoldTimeout"/> while an operation was still pending.</summary>
        internal static long ReleasedAtBoundCount => Interlocked.Read(ref s_releasedAtBoundCount);

        /// <summary>True once the handle was released (the socket disposed).</summary>
        internal bool IsReleased => Volatile.Read(ref _released) != 0;

        /// <summary>An operation is about to start on the socket. Always paired with <see cref="ExitIo"/>.</summary>
        internal void EnterIo()
        {
            lock (_lock)
                _starting++;
        }

        /// <summary>
        /// The operation started by the matching <see cref="EnterIo"/> returned: <paramref name="operation"/>
        /// is its task when it is still pending, null when it completed or threw.
        /// </summary>
        internal void ExitIo(Task operation, bool write)
        {
            bool pending = operation != null && !operation.IsCompleted;
            bool releaseNow = false;
            lock (_lock)
            {
                _starting--;
                if (!_closeRequested)
                {
                    if (pending)
                    {
                        if (write)
                            _pendingWrite = operation;
                        else
                            _pendingRead = operation;
                    }

                    return;
                }

                // Close ran while this operation was starting: it counted the operation as a holder.
                if (!pending)
                    releaseNow = --_holders == 0;
            }

            if (pending)
                AttachEnd(operation);
            else if (releaseNow)
                Release(atBound: false);
        }

        /// <summary>
        /// Shuts the socket down now and releases its handle once every pending operation ended, or
        /// after <see cref="HoldTimeout"/>. Idempotent.
        /// </summary>
        internal void Close()
        {
            Task read;
            Task write;
            int holders;
            lock (_lock)
            {
                if (_closeRequested)
                    return;

                _closeRequested = true;
                read = Incomplete(_pendingRead);
                write = Incomplete(_pendingWrite);
                _pendingRead = null;
                _pendingWrite = null;
                holders = _starting + (read != null ? 1 : 0) + (write != null ? 1 : 0);
                _holders = holders;

                // Counted before any holder can release it (releases take this lock first).
                if (holders != 0)
                    Interlocked.Increment(ref s_heldCount);
            }

            // The peer sees the close now, and a pending receive ends with end of stream while the
            // descriptor is still ours.
            try { _socket.Shutdown(SocketShutdown.Both); } catch { /* not connected or already closed */ }

            if (holders == 0)
            {
                // Nothing pending: the descriptor can go at once (an operation started after this
                // point fails on the disposed stream before it reaches the socket).
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    ReleaseHandle();
                return;
            }

            var bound = HoldTimeout;
            if (bound != Timeout.InfiniteTimeSpan)
            {
                var timer = new Timer(s_bound, this, bound < TimeSpan.Zero ? TimeSpan.Zero : bound, Timeout.InfiniteTimeSpan);
                Volatile.Write(ref _bound, timer);
                if (IsReleased)
                    timer.Dispose();
            }

            if (read != null)
                AttachEnd(read);
            if (write != null)
                AttachEnd(write);
        }

        private static Task Incomplete(Task task) => task != null && !task.IsCompleted ? task : null;

        private void AttachEnd(Task operation)
        {
            _ = operation.ContinueWith(
                s_operationEnded,
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void OnOperationEnded(Task operation)
        {
            // The operation's result was discarded with the stream: observe a fault.
            _ = operation.Exception;
            bool releaseNow;
            lock (_lock)
                releaseNow = --_holders == 0;

            if (releaseNow)
                Release(atBound: false);
        }

        private void Release(bool atBound)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            Volatile.Read(ref _bound)?.Dispose();
            ReleaseHandle();
            Interlocked.Decrement(ref s_heldCount);
            if (atBound)
                Interlocked.Increment(ref s_releasedAtBoundCount);
            else
                Interlocked.Increment(ref s_releasedAfterEndCount);
        }

        private void ReleaseHandle()
        {
            try { _socket.Dispose(); } catch { /* best effort */ }
        }
    }
}
