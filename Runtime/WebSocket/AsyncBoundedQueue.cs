using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.WebSocket
{
    internal sealed class AsyncQueueCompletedException : Exception
    {
        public AsyncQueueCompletedException(Exception innerException)
            : base("Queue is completed.", innerException)
        {
        }
    }

    /// <summary>
    /// Lightweight bounded async queue used in the WebSocket API layer. It is bounded by item count and,
    /// optionally, by the total size of the queued items (#80). An item is admitted when the queue is
    /// empty or when it fits both budgets, so a single item larger than the byte budget is still
    /// delivered and the bytes held never exceed <c>max(maxBytes, largest item)</c>.
    /// </summary>
    /// <remarks>
    /// All state is owned by <c>_gate</c>. Waiting readers and writers park on semaphores that are
    /// released under the gate only while someone waits; a stale permit costs one extra loop iteration.
    /// </remarks>
    internal sealed class BoundedAsyncQueue<T>
    {
        private readonly Queue<Entry> _queue = new Queue<Entry>();
        private readonly SemaphoreSlim _items = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _space = new SemaphoreSlim(0);
        private readonly object _gate = new object();
        private readonly int _capacity;
        private readonly long _maxBytes;
        private readonly Func<T, int> _sizeOf;

        private long _queuedBytes;
        private int _waitingReaders;
        private int _waitingWriters;
        private bool _completed;
        private Exception _completionError;

        public BoundedAsyncQueue(int capacity)
            : this(capacity, long.MaxValue, null)
        {
        }

        public BoundedAsyncQueue(int capacity, long maxBytes, Func<T, int> sizeOf)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
            if (maxBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(maxBytes), "Byte budget must be greater than zero.");
            if (maxBytes != long.MaxValue && sizeOf == null)
                throw new ArgumentNullException(nameof(sizeOf), "A byte budget needs a size accessor.");

            _capacity = capacity;
            _maxBytes = maxBytes;
            _sizeOf = sizeOf;
        }

        /// <summary>Number of queued items.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _queue.Count;
                }
            }
        }

        /// <summary>Total size of the queued items as reported by the size accessor.</summary>
        public long QueuedBytes
        {
            get
            {
                lock (_gate)
                {
                    return _queuedBytes;
                }
            }
        }

        public async ValueTask EnqueueAsync(T value, CancellationToken ct)
        {
            int size = SizeOf(value);
            while (true)
            {
                lock (_gate)
                {
                    if (_completed)
                        throw new AsyncQueueCompletedException(_completionError);

                    if (HasRoomFor(size))
                    {
                        EnqueueLocked(value, size);
                        return;
                    }

                    _waitingWriters++;
                }

                try
                {
                    await _space.WaitAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        _waitingWriters--;
                    }
                }
            }
        }

        /// <summary>
        /// Enqueues without waiting. Returns false when the item does not fit the count or byte budget;
        /// throws <see cref="AsyncQueueCompletedException"/> when the queue is completed.
        /// </summary>
        public bool TryEnqueue(T value)
        {
            int size = SizeOf(value);
            lock (_gate)
            {
                if (_completed)
                    throw new AsyncQueueCompletedException(_completionError);

                if (!HasRoomFor(size))
                    return false;

                EnqueueLocked(value, size);
                return true;
            }
        }

        /// <summary>
        /// Enqueues without waiting. While the item does not fit the count or byte budget the oldest item is
        /// removed and passed to <paramref name="onDropped"/> (which then owns it; it runs under the queue
        /// lock and must not call back into the queue). Returns the number of dropped items. Intended for a
        /// single producer.
        /// </summary>
        public int EnqueueDropOldest(T value, Action<T> onDropped)
        {
            if (onDropped == null)
                throw new ArgumentNullException(nameof(onDropped));

            int size = SizeOf(value);
            lock (_gate)
            {
                if (_completed)
                    throw new AsyncQueueCompletedException(_completionError);

                int droppedCount = 0;
                while (!HasRoomFor(size))
                {
                    var entry = _queue.Dequeue();
                    _queuedBytes -= entry.Size;
                    droppedCount++;
                    onDropped(entry.Value);
                }

                EnqueueLocked(value, size);
                return droppedCount;
            }
        }

        public async ValueTask<T> DequeueAsync(CancellationToken ct)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_queue.Count > 0)
                        return DequeueLocked();

                    if (_completed)
                        throw new AsyncQueueCompletedException(_completionError);

                    _waitingReaders++;
                }

                try
                {
                    await _items.WaitAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        _waitingReaders--;
                    }
                }
            }
        }

        public void Complete(Exception error)
        {
            lock (_gate)
            {
                if (_completed)
                    return;

                _completed = true;
                _completionError = error;

                if (_waitingReaders > 0)
                    _items.Release(_waitingReaders);
                if (_waitingWriters > 0)
                    _space.Release(_waitingWriters);
            }
        }

        public void Drain(Action<T> onItem)
        {
            if (onItem == null)
                throw new ArgumentNullException(nameof(onItem));

            lock (_gate)
            {
                while (_queue.Count > 0)
                    onItem(DequeueLocked());
            }
        }

        private int SizeOf(T value)
        {
            if (_sizeOf == null)
                return 0;

            int size = _sizeOf(value);
            return size > 0 ? size : 0;
        }

        // Caller holds _gate. An empty queue admits any single item so it can never deadlock.
        private bool HasRoomFor(int size)
        {
            int count = _queue.Count;
            if (count == 0)
                return true;

            return count < _capacity && size <= _maxBytes - _queuedBytes;
        }

        // Caller holds _gate.
        private void EnqueueLocked(T value, int size)
        {
            _queue.Enqueue(new Entry(value, size));
            _queuedBytes += size;

            if (_waitingReaders > 0)
                _items.Release();
        }

        // Caller holds _gate and has checked Count > 0.
        private T DequeueLocked()
        {
            var entry = _queue.Dequeue();
            _queuedBytes -= entry.Size;

            if (_waitingWriters > 0)
                _space.Release(_waitingWriters);

            return entry.Value;
        }

        private readonly struct Entry
        {
            public Entry(T value, int size)
            {
                Value = value;
                Size = size;
            }

            public T Value { get; }

            public int Size { get; }
        }
    }

    /// <summary>Factory for the WebSocket message delivery queues (count and byte budget, #80).</summary>
    internal static class WebSocketMessageQueue
    {
        private static readonly Func<WebSocketMessage, int> MessageSize = GetMessageSize;

        public static BoundedAsyncQueue<WebSocketMessage> Create(WebSocketConnectionOptions options)
        {
            return new BoundedAsyncQueue<WebSocketMessage>(
                options.ReceiveQueueCapacity,
                options.ReceiveQueueMaxBytes,
                MessageSize);
        }

        private static int GetMessageSize(WebSocketMessage message)
        {
            return message != null ? message.Length : 0;
        }
    }
}
