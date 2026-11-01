using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http2
{
    internal partial class Http2Connection
    {
        // Stream admission (RFC 9113 Section 5.1.2): a request reserves a slot under the peer's
        // SETTINGS_MAX_CONCURRENT_STREAMS before it opens a stream. A slot is free when open streams plus
        // reserved slots are below the current limit, which SETTINGS may lower or raise at any time
        // (Section 6.5.2), so this is a counter plus a FIFO waiter list rather than a SemaphoreSlim.
        // _admissionWaiters and _pendingAdmissions are guarded by _streamCreateLock. _admissionWaiterCount
        // mirrors the list length (plus a tentative entry during a reservation) through Interlocked, so a
        // stream removal only takes the lock when someone waits.
        private readonly LinkedList<TaskCompletionSource<bool>> _admissionWaiters =
            new LinkedList<TaskCompletionSource<bool>>();
        private int _pendingAdmissions;
        private int _admissionWaiterCount;

        /// <summary>Requests waiting for a stream slot (test and diagnostics only).</summary>
        internal int QueuedAdmissionCount => Volatile.Read(ref _admissionWaiterCount);

        // GOAWAY received or stream ids used up: no new streams, the open ones finish, then the
        // connection closes itself (TryCompleteDrain).
        private bool IsDraining => _goawayReceived || _streamIdsExhausted;

        private bool AcceptsNewStreamsCore() =>
            !_goawayReceived && !_streamIdsExhausted && !_cts.IsCancellationRequested;

        private UHttpException CreateNotAcceptingException()
        {
            string reason;
            if (_cts.IsCancellationRequested)
                reason = "HTTP/2 connection is closed";
            else if (_goawayReceived)
                reason = $"HTTP/2 connection received GOAWAY (error={(Http2ErrorCode)Volatile.Read(ref _goawayErrorCode)})";
            else
                reason = "HTTP/2 stream ID space exhausted";

            return new Http2RetryableRequestException(
                new UHttpError(UHttpErrorType.NetworkError, reason + "; the request was not sent"),
                requestNotProcessed: true);
        }

        private int FreeStreamSlotsLocked()
        {
            long free = (long)_remoteSettings.MaxConcurrentStreams - _activeStreams.Count - _pendingAdmissions;
            return free <= 0 ? 0 : free >= int.MaxValue ? int.MaxValue : (int)free;
        }

        /// <summary>
        /// Reserves a stream slot, waiting in FIFO order while the peer's limit is reached. The caller
        /// owns the reservation and either registers a stream under _streamCreateLock (consuming it) or
        /// calls <see cref="ReleaseReservedStreamSlot"/>. Throws a not-processed
        /// <see cref="Http2RetryableRequestException"/> when the connection stops accepting streams.
        /// </summary>
        private ValueTask AcquireStreamSlotAsync(CancellationToken ct)
        {
            LinkedListNode<TaskCompletionSource<bool>> node;
            lock (_streamCreateLock)
            {
                if (!AcceptsNewStreamsCore())
                    throw CreateNotAcceptingException();

                // Count first, then read the open-stream count: a concurrent removal either is seen by
                // FreeStreamSlotsLocked or sees the count in OnActiveStreamRemoved (no lost wakeup).
                Interlocked.Increment(ref _admissionWaiterCount);
                if (_admissionWaiters.Count == 0 && FreeStreamSlotsLocked() > 0)
                {
                    Interlocked.Decrement(ref _admissionWaiterCount);
                    _pendingAdmissions++;
                    return default;
                }

                if (ct.IsCancellationRequested)
                {
                    Interlocked.Decrement(ref _admissionWaiterCount);
                    ct.ThrowIfCancellationRequested();
                }

                node = _admissionWaiters.AddLast(
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            return new ValueTask(WaitForStreamSlotAsync(node, ct));
        }

        private async Task WaitForStreamSlotAsync(
            LinkedListNode<TaskCompletionSource<bool>> node,
            CancellationToken ct)
        {
            bool admitted;
            using (ct.Register(() => CancelAdmissionWaiter(node, ct), useSynchronizationContext: false))
            {
                // Completed by PumpAdmissionWaitersLocked (true: slot reserved for us; false: refused)
                // or cancelled by CancelAdmissionWaiter, exactly one of them.
                admitted = await node.Value.Task.ConfigureAwait(false);
            }

            if (!admitted)
                throw CreateNotAcceptingException();
        }

        private void CancelAdmissionWaiter(LinkedListNode<TaskCompletionSource<bool>> node, CancellationToken ct)
        {
            lock (_streamCreateLock)
            {
                if (node.List == null)
                    return; // already admitted or refused

                _admissionWaiters.Remove(node);
                Interlocked.Decrement(ref _admissionWaiterCount);
            }

            node.Value.TrySetCanceled(ct);
        }

        /// <summary>
        /// Admits waiters while slots are free, or refuses all of them once the connection stops accepting
        /// streams (GOAWAY, failure, dispose). Continuations run asynchronously, never under the lock.
        /// </summary>
        private void PumpAdmissionWaitersLocked()
        {
            if (_admissionWaiters.First == null)
                return;

            bool accepting = AcceptsNewStreamsCore();
            int free = accepting ? FreeStreamSlotsLocked() : 0;
            while (_admissionWaiters.First != null && (!accepting || free > 0))
            {
                var node = _admissionWaiters.First;
                _admissionWaiters.RemoveFirst();
                Interlocked.Decrement(ref _admissionWaiterCount);
                if (accepting)
                {
                    _pendingAdmissions++;
                    free--;
                }

                node.Value.TrySetResult(accepting);
            }
        }

        private void ReleaseReservedStreamSlot()
        {
            lock (_streamCreateLock)
            {
                _pendingAdmissions--;
                PumpAdmissionWaitersLocked();
            }

            TryCompleteDrain(inline: false);
        }

        /// <summary>
        /// Called by every path that removed a stream from <see cref="_activeStreams"/>: frees its slot
        /// for a waiter and closes a GOAWAY-draining connection once nothing is left on it.
        /// </summary>
        private void OnActiveStreamRemoved()
        {
            if (Interlocked.CompareExchange(ref _admissionWaiterCount, 0, 0) != 0)
            {
                lock (_streamCreateLock)
                    PumpAdmissionWaitersLocked();
            }

            if (IsDraining)
                TryCompleteDrain(inline: false);
        }

        /// <summary>
        /// RFC 9113 Section 6.8: after GOAWAY (or once stream ids are used up) the connection closes once
        /// the streams the peer may still process are done. Inline only from the read loop; other callers may sit inside a stream's
        /// own cleanup, so the close runs on the thread pool there.
        /// </summary>
        private void TryCompleteDrain(bool inline)
        {
            if (!IsDraining ||
                !_activeStreams.IsEmpty ||
                Volatile.Read(ref _pendingAdmissions) != 0 ||
                Interlocked.CompareExchange(ref _drainCloseStarted, 1, 0) != 0)
            {
                return;
            }

            if (inline)
                Dispose();
            else
                _ = Task.Run(Dispose); // Dispose never throws; the connection owns its own close
        }
    }
}
