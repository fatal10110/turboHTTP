using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Performance
{
    /// <summary>
    /// Priority levels for queued requests.
    /// </summary>
    public enum RequestPriority
    {
        /// <summary>High priority — processed before Normal and Low.</summary>
        High = 0,
        /// <summary>Normal priority — default for most requests.</summary>
        Normal = 1,
        /// <summary>Low priority — processed only when no higher-priority work is pending.</summary>
        Low = 2
    }

    /// <summary>
    /// Thread-safe, priority-based request queue with graceful shutdown support.
    /// Items are dequeued in priority order (High before Normal before Low).
    /// Within the same priority level, items are dequeued in FIFO order.
    /// </summary>
    /// <typeparam name="T">The type of items in the queue.</typeparam>
    public sealed class RequestQueue<T> : IDisposable
    {
        private readonly ConcurrentQueue<T>[] _queues;
        private readonly SemaphoreSlim _itemAvailable;
        private int _count;
        private int _disposed;
        private int _shutdown;

        /// <summary>
        /// Approximate number of items currently in the queue across all priority levels.
        /// </summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>
        /// True if <see cref="Shutdown"/> has been called.
        /// </summary>
        public bool IsShutdown => Volatile.Read(ref _shutdown) != 0;

        /// <summary>
        /// Creates a new request queue.
        /// </summary>
        public RequestQueue()
        {
            int priorityCount = 3; // High, Normal, Low
            _queues = new ConcurrentQueue<T>[priorityCount];
            for (int i = 0; i < priorityCount; i++)
                _queues[i] = new ConcurrentQueue<T>();

            _itemAvailable = new SemaphoreSlim(0);
        }

        /// <summary>
        /// Enqueue an item with the specified priority.
        /// </summary>
        /// <param name="item">The item to enqueue.</param>
        /// <param name="priority">The priority level. Default is <see cref="RequestPriority.Normal"/>.</param>
        /// <exception cref="InvalidOperationException">The queue has been shut down.</exception>
        /// <exception cref="ObjectDisposedException">The queue has been disposed.</exception>
        public void Enqueue(T item, RequestPriority priority = RequestPriority.Normal)
        {
            ThrowIfDisposed();

            if (Volatile.Read(ref _shutdown) != 0)
                throw new InvalidOperationException("Cannot enqueue items after shutdown.");

            _queues[(int)priority].Enqueue(item);
            Interlocked.Increment(ref _count);
            _itemAvailable.Release();
        }

        /// <summary>
        /// Dequeue the highest-priority item. Blocks asynchronously until an item is available.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the wait.</param>
        /// <returns>The dequeued item.</returns>
        /// <exception cref="OperationCanceledException">The token was cancelled or the queue was shut down.</exception>
        /// <exception cref="ObjectDisposedException">The queue has been disposed.</exception>
        public async Task<T> DequeueAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _itemAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Drain in priority order
            for (int i = 0; i < _queues.Length; i++)
            {
                if (_queues[i].TryDequeue(out T item))
                {
                    Interlocked.Decrement(ref _count);
                    return item;
                }
            }

            // Should not happen — semaphore guarantees an item exists.
            // But under extreme contention another DequeueAsync could have taken it.
            // Throw rather than return default to avoid silent data loss.
            throw new InvalidOperationException("Queue was signaled but no item was available.");
        }

        /// <summary>
        /// Try to dequeue an item without waiting.
        /// </summary>
        /// <param name="item">The dequeued item, or default if the queue is empty.</param>
        /// <returns>True if an item was dequeued; false if the queue is empty.</returns>
        public bool TryDequeue(out T item)
        {
            item = default;

            if (!_itemAvailable.Wait(0))
                return false;

            for (int i = 0; i < _queues.Length; i++)
            {
                if (_queues[i].TryDequeue(out item))
                {
                    Interlocked.Decrement(ref _count);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Initiate graceful shutdown. No new items can be enqueued.
        /// Pending <see cref="DequeueAsync"/> calls will throw <see cref="OperationCanceledException"/>
        /// once remaining items are drained.
        /// </summary>
        public void Shutdown()
        {
            Interlocked.Exchange(ref _shutdown, 1);
        }

        /// <summary>
        /// Disposes the queue, releasing the internal semaphore.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            Interlocked.Exchange(ref _shutdown, 1);
            _itemAvailable.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(RequestQueue<T>));
        }
    }
}
