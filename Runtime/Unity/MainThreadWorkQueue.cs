using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Backpressure behavior when the main-thread user-work queue reaches capacity.
    /// </summary>
    public enum MainThreadBackpressurePolicy
    {
        /// <summary>
        /// Reject newly enqueued user work immediately.
        /// </summary>
        Reject = 0,

        /// <summary>
        /// Wait for queue capacity until cancellation/timeout.
        /// </summary>
        Wait = 1,

        /// <summary>
        /// Drop the oldest queued user work and accept the new one.
        /// Control-plane work is never dropped.
        /// </summary>
        DropOldest = 2
    }

    /// <summary>
    /// Work classification for the dispatcher queue.
    /// </summary>
    public enum MainThreadDispatcherWorkKind
    {
        User = 0,
        Control = 1
    }

    /// <summary>
    /// Dispatcher runtime lifecycle state.
    /// </summary>
    public enum MainThreadDispatcherLifecycleState
    {
        Uninitialized = 0,
        Initializing = 1,
        Ready = 2,
        Disposing = 3,
        Reloading = 4
    }

    /// <summary>
    /// Runtime configuration for the dispatcher and its bounded work queue.
    /// </summary>
    public sealed class MainThreadDispatcherSettings
    {
        /// <summary>
        /// Maximum queued user work items.
        /// </summary>
        public int UserQueueCapacity { get; set; } = 512;

        /// <summary>
        /// Backpressure policy when the queue is full.
        /// </summary>
        public MainThreadBackpressurePolicy BackpressurePolicy { get; set; } =
            MainThreadBackpressurePolicy.Reject;

        /// <summary>
        /// Wait timeout for <see cref="MainThreadBackpressurePolicy.Wait"/>.
        /// </summary>
        public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Upper bound for pending queue waiters to avoid unbounded waiter growth.
        /// </summary>
        public int MaxPendingWaiters { get; set; } = 2048;

        /// <summary>
        /// Maximum work items executed per frame.
        /// </summary>
        public int MaxItemsPerFrame { get; set; } = 128;

        /// <summary>
        /// Maximum dispatch time budget per frame.
        /// </summary>
        public double MaxWorkTimeMs { get; set; } = 2.0;

        /// <summary>
        /// Number of user items to shed on low-memory callback.
        /// </summary>
        public int LowMemoryDropCount { get; set; } = 128;

        /// <summary>
        /// Enable direct inline execution when already on the main thread.
        /// </summary>
        public bool AllowInlineExecutionOnMainThread { get; set; } = true;

        public MainThreadDispatcherSettings Clone()
        {
            return new MainThreadDispatcherSettings
            {
                UserQueueCapacity = UserQueueCapacity,
                BackpressurePolicy = BackpressurePolicy,
                WaitTimeout = WaitTimeout,
                MaxPendingWaiters = MaxPendingWaiters,
                MaxItemsPerFrame = MaxItemsPerFrame,
                MaxWorkTimeMs = MaxWorkTimeMs,
                LowMemoryDropCount = LowMemoryDropCount,
                AllowInlineExecutionOnMainThread = AllowInlineExecutionOnMainThread
            };
        }

        public void Validate()
        {
            if (UserQueueCapacity < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(UserQueueCapacity),
                    UserQueueCapacity,
                    "UserQueueCapacity must be at least 1.");
            }

            if (MaxPendingWaiters < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxPendingWaiters),
                    MaxPendingWaiters,
                    "MaxPendingWaiters must be at least 1.");
            }

            if (MaxItemsPerFrame < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxItemsPerFrame),
                    MaxItemsPerFrame,
                    "MaxItemsPerFrame must be at least 1.");
            }

            if (MaxWorkTimeMs <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxWorkTimeMs),
                    MaxWorkTimeMs,
                    "MaxWorkTimeMs must be greater than 0.");
            }

            if (LowMemoryDropCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(LowMemoryDropCount),
                    LowMemoryDropCount,
                    "LowMemoryDropCount must be at least 1.");
            }
        }
    }

    /// <summary>
    /// Snapshot metrics for the bounded dispatcher queue.
    /// </summary>
    public readonly struct MainThreadWorkQueueMetrics
    {
        public MainThreadWorkQueueMetrics(
            int userQueueDepth,
            int controlQueueDepth,
            int waiterDepth,
            long enqueuedItems,
            long dequeuedItems,
            long rejectedItems,
            long droppedItems,
            long waitedItems,
            long lowMemoryDroppedItems)
        {
            UserQueueDepth = userQueueDepth;
            ControlQueueDepth = controlQueueDepth;
            WaiterDepth = waiterDepth;
            EnqueuedItems = enqueuedItems;
            DequeuedItems = dequeuedItems;
            RejectedItems = rejectedItems;
            DroppedItems = droppedItems;
            WaitedItems = waitedItems;
            LowMemoryDroppedItems = lowMemoryDroppedItems;
        }

        public int UserQueueDepth { get; }
        public int ControlQueueDepth { get; }
        public int WaiterDepth { get; }
        public int TotalQueueDepth => UserQueueDepth + ControlQueueDepth;
        public long EnqueuedItems { get; }
        public long DequeuedItems { get; }
        public long RejectedItems { get; }
        public long DroppedItems { get; }
        public long WaitedItems { get; }
        public long LowMemoryDroppedItems { get; }
    }

    internal interface IMainThreadDispatchWorkItem
    {
        void Execute();
        void Fail(Exception exception);
    }

    internal readonly struct MainThreadDequeuedWorkItem
    {
        public MainThreadDequeuedWorkItem(
            IMainThreadDispatchWorkItem workItem,
            MainThreadDispatcherWorkKind kind,
            long enqueueTimestamp)
        {
            WorkItem = workItem;
            Kind = kind;
            EnqueueTimestamp = enqueueTimestamp;
        }

        public IMainThreadDispatchWorkItem WorkItem { get; }
        public MainThreadDispatcherWorkKind Kind { get; }
        public long EnqueueTimestamp { get; }
    }

    internal sealed class MainThreadWorkQueue
    {
        private readonly object _gate = new object();
        private readonly Queue<QueueEntry> _controlQueue = new Queue<QueueEntry>();
        private readonly Queue<QueueEntry> _userQueue = new Queue<QueueEntry>();
        private readonly Queue<TaskCompletionSource<bool>> _spaceWaiters =
            new Queue<TaskCompletionSource<bool>>();

        private int _userQueueCapacity;
        private MainThreadBackpressurePolicy _backpressurePolicy;
        private TimeSpan _waitTimeout;
        private int _maxPendingWaiters;
        private bool _rejectNewWork;
        private string _rejectReason;

        private long _enqueuedItems;
        private long _dequeuedItems;
        private long _rejectedItems;
        private long _droppedItems;
        private long _waitedItems;
        private long _lowMemoryDroppedItems;

        public MainThreadWorkQueue(MainThreadDispatcherSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();

            _userQueueCapacity = settings.UserQueueCapacity;
            _backpressurePolicy = settings.BackpressurePolicy;
            _waitTimeout = settings.WaitTimeout;
            _maxPendingWaiters = settings.MaxPendingWaiters;
            _rejectReason = "MainThreadDispatcher queue is unavailable.";
        }

        public void Reconfigure(MainThreadDispatcherSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();

            List<QueueEntry> dropped = null;

            lock (_gate)
            {
                _userQueueCapacity = settings.UserQueueCapacity;
                _backpressurePolicy = settings.BackpressurePolicy;
                _waitTimeout = settings.WaitTimeout;
                _maxPendingWaiters = settings.MaxPendingWaiters;

                if (_userQueue.Count > _userQueueCapacity)
                {
                    dropped = new List<QueueEntry>(_userQueue.Count - _userQueueCapacity);
                    while (_userQueue.Count > _userQueueCapacity)
                    {
                        dropped.Add(_userQueue.Dequeue());
                        _droppedItems++;
                        SignalSpaceWaiter_NoLock();
                    }
                }
            }

            if (dropped == null || dropped.Count == 0)
                return;

            var dropException = new OperationCanceledException(
                "MainThreadDispatcher queue was resized and dropped oldest queued work.");

            for (var i = 0; i < dropped.Count; i++)
            {
                TryFailWorkItem(dropped[i].WorkItem, dropException);
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _rejectNewWork = false;
                _rejectReason = "MainThreadDispatcher queue is unavailable.";
            }
        }

        public void RejectNewWork(string reason)
        {
            lock (_gate)
            {
                _rejectNewWork = true;
                _rejectReason = string.IsNullOrWhiteSpace(reason)
                    ? "MainThreadDispatcher queue is unavailable."
                    : reason;
            }
        }

        public async ValueTask EnqueueAsync(
            IMainThreadDispatchWorkItem workItem,
            MainThreadDispatcherWorkKind workKind,
            CancellationToken cancellationToken)
        {
            if (workItem == null) throw new ArgumentNullException(nameof(workItem));

            if (workKind == MainThreadDispatcherWorkKind.Control)
            {
                lock (_gate)
                {
                    EnsureAcceptingWork_NoLock();
                    EnqueueControl_NoLock(workItem);
                }

                return;
            }

            while (true)
            {
                TaskCompletionSource<bool> waiter = null;
                IMainThreadDispatchWorkItem droppedWorkItem = null;

                lock (_gate)
                {
                    EnsureAcceptingWork_NoLock();

                    if (_userQueue.Count < _userQueueCapacity)
                    {
                        EnqueueUser_NoLock(workItem);
                        return;
                    }

                    switch (_backpressurePolicy)
                    {
                        case MainThreadBackpressurePolicy.Reject:
                            _rejectedItems++;
                            throw new InvalidOperationException(
                                "MainThreadDispatcher queue is full and rejected new user work.");

                        case MainThreadBackpressurePolicy.DropOldest:
                        {
                            if (_userQueue.Count > 0)
                            {
                                var dropped = _userQueue.Dequeue();
                                droppedWorkItem = dropped.WorkItem;
                                _droppedItems++;
                            }

                            EnqueueUser_NoLock(workItem);
                            SignalSpaceWaiter_NoLock();
                            break;
                        }

                        case MainThreadBackpressurePolicy.Wait:
                        default:
                            if (_spaceWaiters.Count >= _maxPendingWaiters)
                            {
                                _rejectedItems++;
                                throw new InvalidOperationException(
                                    "MainThreadDispatcher queue is full and waiter limit was reached.");
                            }

                            _waitedItems++;
                            waiter = new TaskCompletionSource<bool>(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                            _spaceWaiters.Enqueue(waiter);
                            break;
                    }
                }

                if (droppedWorkItem != null)
                {
                    TryFailWorkItem(
                        droppedWorkItem,
                        new OperationCanceledException(
                            "MainThreadDispatcher dropped oldest queued user work due to backpressure."));
                    return;
                }

                if (waiter == null)
                    return;

                await WaitForQueueSpaceAsync(waiter, cancellationToken).ConfigureAwait(false);
            }
        }

        public bool TryEnqueueImmediate(
            IMainThreadDispatchWorkItem workItem,
            MainThreadDispatcherWorkKind workKind,
            out Exception rejection)
        {
            if (workItem == null) throw new ArgumentNullException(nameof(workItem));

            IMainThreadDispatchWorkItem dropped = null;

            lock (_gate)
            {
                try
                {
                    EnsureAcceptingWork_NoLock();
                }
                catch (Exception ex)
                {
                    rejection = ex;
                    return false;
                }

                if (workKind == MainThreadDispatcherWorkKind.Control)
                {
                    EnqueueControl_NoLock(workItem);
                    rejection = null;
                    return true;
                }

                if (_userQueue.Count < _userQueueCapacity)
                {
                    EnqueueUser_NoLock(workItem);
                    rejection = null;
                    return true;
                }

                switch (_backpressurePolicy)
                {
                    case MainThreadBackpressurePolicy.DropOldest:
                    {
                        if (_userQueue.Count > 0)
                        {
                            var droppedEntry = _userQueue.Dequeue();
                            dropped = droppedEntry.WorkItem;
                            _droppedItems++;
                        }

                        EnqueueUser_NoLock(workItem);
                        SignalSpaceWaiter_NoLock();
                        rejection = null;
                        break;
                    }

                    case MainThreadBackpressurePolicy.Wait:
                        _rejectedItems++;
                        rejection = new InvalidOperationException(
                            "MainThreadDispatcher queue is full. Immediate enqueue cannot wait.");
                        return false;

                    case MainThreadBackpressurePolicy.Reject:
                    default:
                        _rejectedItems++;
                        rejection = new InvalidOperationException(
                            "MainThreadDispatcher queue is full and rejected new user work.");
                        return false;
                }
            }

            if (dropped != null)
            {
                TryFailWorkItem(
                    dropped,
                    new OperationCanceledException(
                        "MainThreadDispatcher dropped oldest queued user work due to backpressure."));
            }

            rejection = null;
            return true;
        }

        public bool TryDequeue(out MainThreadDequeuedWorkItem dequeuedItem)
        {
            lock (_gate)
            {
                if (_controlQueue.Count > 0)
                {
                    var control = _controlQueue.Dequeue();
                    _dequeuedItems++;
                    dequeuedItem = new MainThreadDequeuedWorkItem(
                        control.WorkItem,
                        control.WorkKind,
                        control.EnqueueTimestamp);
                    return true;
                }

                if (_userQueue.Count > 0)
                {
                    var user = _userQueue.Dequeue();
                    _dequeuedItems++;
                    SignalSpaceWaiter_NoLock();

                    dequeuedItem = new MainThreadDequeuedWorkItem(
                        user.WorkItem,
                        user.WorkKind,
                        user.EnqueueTimestamp);
                    return true;
                }
            }

            dequeuedItem = default;
            return false;
        }

        public int DropUserWork(Exception exception, int maxDrop)
        {
            if (maxDrop < 1) throw new ArgumentOutOfRangeException(nameof(maxDrop));

            List<QueueEntry> dropped = null;

            lock (_gate)
            {
                var toDrop = Math.Min(maxDrop, _userQueue.Count);
                if (toDrop == 0)
                    return 0;

                dropped = new List<QueueEntry>(toDrop);

                for (var i = 0; i < toDrop; i++)
                {
                    dropped.Add(_userQueue.Dequeue());
                    _droppedItems++;
                    _lowMemoryDroppedItems++;
                    SignalSpaceWaiter_NoLock();
                }
            }

            for (var i = 0; i < dropped.Count; i++)
            {
                TryFailWorkItem(dropped[i].WorkItem, exception);
            }

            return dropped.Count;
        }

        public void FailAll(Exception exception)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));

            List<QueueEntry> pendingWork;
            List<TaskCompletionSource<bool>> waiters;

            lock (_gate)
            {
                _rejectNewWork = true;
                _rejectReason = exception.Message;

                pendingWork = new List<QueueEntry>(_controlQueue.Count + _userQueue.Count);
                while (_controlQueue.Count > 0)
                {
                    pendingWork.Add(_controlQueue.Dequeue());
                }

                while (_userQueue.Count > 0)
                {
                    pendingWork.Add(_userQueue.Dequeue());
                }

                waiters = new List<TaskCompletionSource<bool>>(_spaceWaiters.Count);
                while (_spaceWaiters.Count > 0)
                {
                    waiters.Add(_spaceWaiters.Dequeue());
                }
            }

            for (var i = 0; i < pendingWork.Count; i++)
            {
                TryFailWorkItem(pendingWork[i].WorkItem, exception);
            }

            for (var i = 0; i < waiters.Count; i++)
            {
                waiters[i].TrySetException(exception);
            }
        }

        public MainThreadWorkQueueMetrics SnapshotMetrics()
        {
            lock (_gate)
            {
                return new MainThreadWorkQueueMetrics(
                    userQueueDepth: _userQueue.Count,
                    controlQueueDepth: _controlQueue.Count,
                    waiterDepth: _spaceWaiters.Count,
                    enqueuedItems: _enqueuedItems,
                    dequeuedItems: _dequeuedItems,
                    rejectedItems: _rejectedItems,
                    droppedItems: _droppedItems,
                    waitedItems: _waitedItems,
                    lowMemoryDroppedItems: _lowMemoryDroppedItems);
            }
        }

        private async Task WaitForQueueSpaceAsync(
            TaskCompletionSource<bool> waiter,
            CancellationToken cancellationToken)
        {
            CancellationTokenRegistration cancellationRegistration = default;

            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = cancellationToken.Register(() =>
                {
                    waiter.TrySetCanceled(cancellationToken);
                });
            }

            try
            {
                if (_waitTimeout <= TimeSpan.Zero)
                {
                    await waiter.Task.ConfigureAwait(false);
                    return;
                }

                var timeoutTask = Task.Delay(_waitTimeout, CancellationToken.None);
                var completed = await Task.WhenAny(waiter.Task, timeoutTask).ConfigureAwait(false);
                if (!ReferenceEquals(completed, waiter.Task))
                {
                    waiter.TrySetException(new TimeoutException(
                        $"Timed out waiting {_waitTimeout.TotalMilliseconds:F0}ms for MainThreadDispatcher queue space."));
                }

                await waiter.Task.ConfigureAwait(false);
            }
            finally
            {
                cancellationRegistration.Dispose();
            }
        }

        private void EnqueueControl_NoLock(IMainThreadDispatchWorkItem workItem)
        {
            _controlQueue.Enqueue(new QueueEntry(
                workItem,
                MainThreadDispatcherWorkKind.Control,
                StopwatchTimestamp.Now));
            _enqueuedItems++;
        }

        private void EnqueueUser_NoLock(IMainThreadDispatchWorkItem workItem)
        {
            _userQueue.Enqueue(new QueueEntry(
                workItem,
                MainThreadDispatcherWorkKind.User,
                StopwatchTimestamp.Now));
            _enqueuedItems++;
        }

        private void EnsureAcceptingWork_NoLock()
        {
            if (!_rejectNewWork)
                return;

            throw new InvalidOperationException(_rejectReason);
        }

        private void SignalSpaceWaiter_NoLock()
        {
            while (_spaceWaiters.Count > 0)
            {
                var waiter = _spaceWaiters.Dequeue();
                if (waiter.TrySetResult(true))
                    return;
            }
        }

        private static void TryFailWorkItem(IMainThreadDispatchWorkItem workItem, Exception exception)
        {
            if (workItem == null)
                return;

            try
            {
                workItem.Fail(exception);
            }
            catch
            {
                // Intentionally ignored: queue failure paths should not throw.
            }
        }

        private readonly struct QueueEntry
        {
            public QueueEntry(
                IMainThreadDispatchWorkItem workItem,
                MainThreadDispatcherWorkKind workKind,
                long enqueueTimestamp)
            {
                WorkItem = workItem;
                WorkKind = workKind;
                EnqueueTimestamp = enqueueTimestamp;
            }

            public IMainThreadDispatchWorkItem WorkItem { get; }
            public MainThreadDispatcherWorkKind WorkKind { get; }
            public long EnqueueTimestamp { get; }
        }

        private static class StopwatchTimestamp
        {
            public static long Now => System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }
}
