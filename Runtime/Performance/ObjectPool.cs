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
        private readonly object _lock = new object();
        private int _count;

        /// <summary>
        /// Number of items currently available in the pool at the moment of the call.
        /// The value can change immediately after this property returns under concurrency.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _count;
                }
            }
        }

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
            lock (_lock)
            {
                if (_count > 0)
                {
                    int index = _count - 1;
                    var item = _items[index];
                    _items[index] = null;
                    _count--;

                    if (item != null)
                        return item;
                }
            }

            return _factory();
        }

        /// <summary>
        /// Return an object to the pool. If the pool is full, the item is discarded.
        /// The reset callback (if provided) is invoked before storing the item.
        /// If reset throws, the exception propagates and the item is not returned.
        /// </summary>
        /// <param name="item">The item to return. Null items are silently ignored.</param>
        public void Return(T item)
        {
            if (item == null)
                return;

            _reset?.Invoke(item);

            lock (_lock)
            {
                if (_count >= _capacity)
                    return; // Pool is full — discard item.

                _items[_count] = item;
                _count++;
            }
        }
    }
}
