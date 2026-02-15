using System;
using System.Threading;

namespace TurboHTTP.Performance
{
    /// <summary>
    /// Generic bounded object pool with configurable capacity.
    /// Thread-safe. Items exceeding the capacity limit are discarded on return.
    /// </summary>
    /// <typeparam name="T">The type of objects to pool. Must be a reference type.</typeparam>
    public sealed class ObjectPool<T> where T : class
    {
        private readonly T[] _items;
        private readonly Func<T> _factory;
        private readonly Action<T> _reset;
        private readonly int _capacity;
        private int _count;

        /// <summary>
        /// Approximate number of items currently available in the pool.
        /// This value is inherently racy under concurrent access and should be
        /// used only for diagnostics, not for control flow.
        /// </summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>
        /// Maximum number of items this pool will hold.
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Creates a new object pool.
        /// </summary>
        /// <param name="factory">Factory function to create new instances when the pool is empty.</param>
        /// <param name="capacity">Maximum number of items to store. Must be at least 1.</param>
        /// <param name="reset">Optional callback invoked on each item before it is returned to the pool.
        /// Use this to clear mutable state and prevent cross-request data leakage.</param>
        /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is less than 1.</exception>
        public ObjectPool(Func<T> factory, int capacity, Action<T> reset = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");

            _capacity = capacity;
            _items = new T[capacity];
            _reset = reset;
            _count = 0;
        }

        /// <summary>
        /// Rent an object from the pool. If the pool is empty, a new instance is
        /// created via the factory. The returned object may contain state from a
        /// previous use if no reset callback was provided.
        /// </summary>
        /// <returns>A pooled or newly created instance.</returns>
        public T Rent()
        {
            // Try to take an item from the pool using lock-free decrement.
            // We speculatively decrement, then check if the slot had an item.
            while (true)
            {
                int currentCount = Volatile.Read(ref _count);
                if (currentCount == 0)
                    break;

                if (Interlocked.CompareExchange(ref _count, currentCount - 1, currentCount) == currentCount)
                {
                    int index = currentCount - 1;
                    T item = Interlocked.Exchange(ref _items[index], null);
                    if (item != null)
                        return item;

                    // Slot was null (race with another Rent); the count is already
                    // decremented, which is correct — the item was logically consumed.
                    // Fall through to create a new one.
                    break;
                }
                // CAS failed — another thread modified _count, retry.
            }

            return _factory();
        }

        /// <summary>
        /// Return an object to the pool. If the pool is full, the item is discarded.
        /// The reset callback (if provided) is invoked before storing the item.
        /// </summary>
        /// <param name="item">The item to return. Null items are silently ignored.</param>
        public void Return(T item)
        {
            if (item == null)
                return;

            _reset?.Invoke(item);

            // Try to insert into the pool using lock-free increment.
            while (true)
            {
                int currentCount = Volatile.Read(ref _count);
                if (currentCount >= _capacity)
                    return; // Pool is full — discard item.

                if (Interlocked.CompareExchange(ref _count, currentCount + 1, currentCount) == currentCount)
                {
                    _items[currentCount] = item;
                    return;
                }
                // CAS failed — retry.
            }
        }
    }
}
