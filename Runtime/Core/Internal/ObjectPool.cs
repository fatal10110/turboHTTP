using System;
using System.Threading;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// Diagnostics snapshot for <see cref="ObjectPool{T}"/>.
    /// </summary>
    /// <remarks>
    /// Individual counters are each read atomically via <see cref="Interlocked.Read(ref long)"/>,
    /// but the snapshot as a whole is not globally consistent: a rent or return occurring
    /// between two counter reads may cause <see cref="ActiveCount"/> to be transiently off by one.
    /// Use these values for trend detection and health monitoring, not exact accounting.
    /// </remarks>
    public readonly struct ObjectPoolDiagnostics
    {
        /// <summary>Total <see cref="ObjectPool{T}.Rent"/> calls (non-null items returned) since last reset.</summary>
        public readonly long RentCount;
        /// <summary>
        /// Total <see cref="ObjectPool{T}.Return"/> calls where the item was successfully stored
        /// back in the pool since last reset. Full-pool discards and reset-thrown items are not counted.
        /// </summary>
        public readonly long ReturnCount;
        /// <summary>
        /// Number of times <see cref="ObjectPool{T}.Rent"/> called the factory because
        /// the pool was empty (cache miss).
        /// </summary>
        public readonly long MissCount;
        /// <summary>Estimated number of objects currently outstanding (RentCount - ReturnCount).</summary>
        public readonly long ActiveCount;

        internal ObjectPoolDiagnostics(long rent, long ret, long miss)
        {
            RentCount = rent;
            ReturnCount = ret;
            MissCount = miss;
            ActiveCount = rent - ret;
        }

        public override string ToString() =>
            $"ObjectPool — Rent={RentCount}, Return={ReturnCount}, Active={ActiveCount}, Miss={MissCount}";
    }

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

        private long _rentCount;
        private long _returnCount;
        private long _missCount;

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
                    if (item != null)
                    {
                        _items[index] = null;
                        _count--;
                        Interlocked.Increment(ref _rentCount);
                        return item;
                    }

                    // Defensive: null slot should not occur, but if it does, decrement
                    // to avoid getting stuck on a persistently null entry.
                    _items[index] = null;
                    _count--;
                }
            }

            // Pool was empty or hit a null slot — create a new instance.
            Interlocked.Increment(ref _rentCount);
            Interlocked.Increment(ref _missCount);
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

            // Run reset outside the lock and before touching counters.
            // If reset throws the item is not stored and ReturnCount is not incremented,
            // keeping ActiveCount accurate.
            _reset?.Invoke(item);

            lock (_lock)
            {
                if (_count >= _capacity)
                    return; // Pool is full — discard item (not counted as returned).

                _items[_count] = item;
                _count++;
            }

            // Only increment after item is confirmed stored.
            Interlocked.Increment(ref _returnCount);
        }

        /// <summary>
        /// Returns a diagnostics snapshot of this pool's activity counters.
        /// Individual counters are each read atomically but the snapshot as a whole is
        /// not consistent — see <see cref="ObjectPoolDiagnostics"/> remarks.
        /// </summary>
        public ObjectPoolDiagnostics GetDiagnostics() =>
            new ObjectPoolDiagnostics(
                Interlocked.Read(ref _rentCount),
                Interlocked.Read(ref _returnCount),
                Interlocked.Read(ref _missCount));

        /// <summary>
        /// Resets all diagnostic counters to zero.
        /// Use for benchmark or test isolation only.
        /// </summary>
        public void ResetDiagnostics()
        {
            Interlocked.Exchange(ref _rentCount, 0);
            Interlocked.Exchange(ref _returnCount, 0);
            Interlocked.Exchange(ref _missCount, 0);
        }
    }
}
