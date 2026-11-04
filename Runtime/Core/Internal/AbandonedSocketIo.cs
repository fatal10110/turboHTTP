using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// Process-wide record of socket operations (reads, handshakes, connects) that were abandoned on
    /// cancellation while still pending, and the gate that keeps a new socket from being created
    /// before they ended (#369).
    /// <para>
    /// IL2CPP's socket close does not remove a pending receive from the runtime's I/O selector,
    /// whose state is keyed by the descriptor number (libil2cpp <c>Socket::Close_icall</c> never
    /// calls <c>threadpool_ms_io_remove_socket</c>). A new socket that reuses the number before the
    /// selector dispatched the stale job appends its own job behind it; the selector dispatches only
    /// the first job per event and, on an error event (Darwin reports <c>POLLHUP</c> with the peer's
    /// FIN), drops the rest, so the new socket's receive is lost and waits for its timeout. An
    /// abandoned operation ends only once the selector dispatched its job, so a socket created after
    /// every abandoned operation ended can never sit behind a stale job. The races of #293/#345/#346
    /// return before that, which is why the creation of the next socket waits here instead.
    /// </para>
    /// <para>
    /// Every entry is bounded by <see cref="SettleTimeout"/>: a platform that never ends the
    /// operation (#293) delays the next socket by at most that long, and an entry that outlived it
    /// stops counting. The state is an O(1) counter; nothing here runs unless an operation was
    /// abandoned, so the connect fast path is one volatile read.
    /// </para>
    /// </summary>
    internal static class AbandonedSocketIo
    {
        private static readonly object s_settledLock = new object();
        private static readonly Action<Task, object> s_endOnCompletion = static (_, state) => ((Entry)state).End();
        private static readonly TimerCallback s_expire = static state => ((Entry)state).End();

        // Ticks, so a 32-bit runtime never reads a torn value (Interlocked.Read/Exchange).
        private static long s_settleTimeoutTicks = TimeSpan.FromSeconds(1).Ticks;

        // Interlocked: abandoned operations that neither ended nor expired.
        private static int s_pending;

        // Guarded by s_settledLock: completed (and cleared) when s_pending drops to zero.
        private static TaskCompletionSource<bool> s_settled;

        /// <summary>
        /// How long one abandoned operation may hold back the creation of new sockets. On IL2CPP the
        /// operation ends within milliseconds of its socket's disposal (the disposal shuts the socket
        /// down, which wakes the selector); the bound only applies where it never ends (#293).
        /// Tests set it.
        /// </summary>
        internal static TimeSpan SettleTimeout
        {
            get => new TimeSpan(Interlocked.Read(ref s_settleTimeoutTicks));
            set => Interlocked.Exchange(ref s_settleTimeoutTicks, value.Ticks);
        }

        /// <summary>Abandoned operations that have neither ended nor expired.</summary>
        internal static int PendingCount => Volatile.Read(ref s_pending);

        /// <summary>True while a socket creation waits in <see cref="WaitForSettledAsync"/>. Tests read it.</summary>
        internal static bool HasWaiters
        {
            get
            {
                lock (s_settledLock)
                    return s_settled != null;
            }
        }

        /// <summary>
        /// Records <paramref name="operation"/>, abandoned while pending, until it ends or
        /// <see cref="SettleTimeout"/> expires. A completed operation is not recorded.
        /// </summary>
        internal static void Track(Task operation)
        {
            if (operation == null || operation.IsCompleted)
                return;

            var entry = Begin();
            _ = operation.ContinueWith(
                s_endOnCompletion,
                entry,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Records one abandoned operation that has no <see cref="Task"/> (the HTTP/1.1 body read,
        /// #345). The owner calls <see cref="Entry.End"/> once the operation ended; the entry expires
        /// on its own after <see cref="SettleTimeout"/>.
        /// </summary>
        internal static Entry Begin()
        {
            var entry = new Entry();
            Interlocked.Increment(ref s_pending);

            var bound = SettleTimeout;
            if (bound <= TimeSpan.Zero && bound != Timeout.InfiniteTimeSpan)
                entry.End();
            else if (bound != Timeout.InfiniteTimeSpan)
                entry.ArmExpiry(bound);

            return entry;
        }

        /// <summary>
        /// Completes once no abandoned operation is pending, at most <see cref="SettleTimeout"/>
        /// after the call. Called right before a socket is created. Allocation-free and
        /// synchronous when nothing was abandoned.
        /// </summary>
        /// <exception cref="OperationCanceledException"><paramref name="ct"/> fired first.</exception>
        internal static Task WaitForSettledAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref s_pending) == 0)
                return Task.CompletedTask;

            return WaitForSettledSlowAsync(ct);
        }

        private static async Task WaitForSettledSlowAsync(CancellationToken ct)
        {
            var bound = SettleTimeout;
            if (bound <= TimeSpan.Zero && bound != Timeout.InfiniteTimeSpan)
                return;

            Task settled;
            lock (s_settledLock)
            {
                if (Volatile.Read(ref s_pending) == 0)
                    return;

                s_settled ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                settled = s_settled.Task;
            }

            // The delay's own source releases its timer as soon as the operations settled.
            using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var delay = Task.Delay(bound, delayCts.Token);
                await Task.WhenAny(settled, delay).ConfigureAwait(false);
                delayCts.Cancel();
            }

            ct.ThrowIfCancellationRequested();
        }

        private static void Release()
        {
            if (Interlocked.Decrement(ref s_pending) != 0)
                return;

            TaskCompletionSource<bool> settled;
            lock (s_settledLock)
            {
                // Another operation may have been abandoned since: its waiters keep waiting.
                if (Volatile.Read(ref s_pending) != 0)
                    return;

                settled = s_settled;
                s_settled = null;
            }

            settled?.TrySetResult(true);
        }

        /// <summary>One abandoned operation. <see cref="End"/> is idempotent and thread-safe.</summary>
        internal sealed class Entry
        {
            /// <summary>An entry that already ended: a marker for owners that race the end against the start.</summary>
            internal static readonly Entry Ended = new Entry { _ended = 1 };

            // Interlocked: 0 while counted, 1 once released (by the operation's end or the expiry).
            private int _ended;

            // Written once by ArmExpiry; disposed by whichever of End/ArmExpiry runs last.
            private Timer _expiry;

            internal void ArmExpiry(TimeSpan bound)
            {
                var timer = new Timer(s_expire, this, bound, Timeout.InfiniteTimeSpan);
                Volatile.Write(ref _expiry, timer);
                if (Volatile.Read(ref _ended) != 0)
                    timer.Dispose();
            }

            /// <summary>The operation ended (or its bound expired): it no longer holds back new sockets.</summary>
            internal void End()
            {
                if (Interlocked.Exchange(ref _ended, 1) != 0)
                    return;

                Volatile.Read(ref _expiry)?.Dispose();
                Release();
            }
        }
    }
}
