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
    /// Lightweight bounded async queue used in the WebSocket API layer.
    /// </summary>
    internal sealed class BoundedAsyncQueue<T>
    {
        private readonly Queue<T> _queue = new Queue<T>();
        private readonly SemaphoreSlim _items = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _spaces;
        private readonly object _gate = new object();

        private int _waitingReaders;
        private bool _completed;
        private Exception _completionError;

        public BoundedAsyncQueue(int capacity)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");

            _spaces = new SemaphoreSlim(capacity, capacity);
        }

        public async ValueTask EnqueueAsync(T value, CancellationToken ct)
        {
            await _spaces.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_completed)
                        throw new AsyncQueueCompletedException(_completionError);

                    _queue.Enqueue(value);
                }

                _items.Release();
            }
            catch
            {
                _spaces.Release();
                throw;
            }
        }

        /// <summary>
        /// Enqueues without waiting. Returns false when the queue is full; throws
        /// <see cref="AsyncQueueCompletedException"/> when the queue is completed.
        /// </summary>
        public bool TryEnqueue(T value)
        {
            if (!_spaces.Wait(0))
            {
                lock (_gate)
                {
                    if (_completed)
                        throw new AsyncQueueCompletedException(_completionError);
                }

                return false;
            }

            try
            {
                lock (_gate)
                {
                    if (_completed)
                        throw new AsyncQueueCompletedException(_completionError);

                    _queue.Enqueue(value);
                }

                _items.Release();
                return true;
            }
            catch
            {
                _spaces.Release();
                throw;
            }
        }

        /// <summary>
        /// Enqueues without waiting. When the queue is full the oldest item is removed and returned in
        /// <paramref name="dropped"/> (the caller owns it). Intended for a single producer.
        /// </summary>
        public bool EnqueueDropOldest(T value, out T dropped)
        {
            lock (_gate)
            {
                if (_completed)
                    throw new AsyncQueueCompletedException(_completionError);

                if (_spaces.Wait(0))
                {
                    _queue.Enqueue(value);
                    _items.Release();
                    dropped = default;
                    return false;
                }

                if (_queue.Count == 0)
                    throw new InvalidOperationException("Queue has no free slot and no item to drop.");

                // The replacement inherits the dropped item's space and item permits.
                dropped = _queue.Dequeue();
                _queue.Enqueue(value);
                return true;
            }
        }

        public async ValueTask<T> DequeueAsync(CancellationToken ct)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_queue.Count > 0)
                    {
                        T item = _queue.Dequeue();
                        _spaces.Release();
                        return item;
                    }

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
            }
        }

        public void Drain(Action<T> onItem)
        {
            if (onItem == null)
                throw new ArgumentNullException(nameof(onItem));

            lock (_gate)
            {
                while (_queue.Count > 0)
                {
                    T item = _queue.Dequeue();
                    _spaces.Release();
                    onItem(item);
                }
            }
        }
    }
}
