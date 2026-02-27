using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace TurboHTTP.Transport.Http2
{
    internal abstract class ValueTaskSourceCoreWrapper<T> : IValueTaskSource<T>
    {
        private ManualResetValueTaskSourceCore<T> _core;

        protected ValueTaskSourceCoreWrapper()
        {
            _core = new ManualResetValueTaskSourceCore<T>
            {
                RunContinuationsAsynchronously = true
            };
        }

        protected void PrepareCoreForUse() => _core.Reset();

        public ValueTask<T> CreateValueTask() => new ValueTask<T>(this, _core.Version);

        public void SetResult(T result) => _core.SetResult(result);

        public void SetException(Exception exception)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));
            _core.SetException(exception);
        }

        public void SetCanceled(CancellationToken cancellationToken = default) =>
            _core.SetException(new OperationCanceledException(cancellationToken));

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(
            Action<object> continuation,
            object state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) =>
            _core.OnCompleted(continuation, state, token, flags);

        public virtual T GetResult(short token)
        {
            return _core.GetResult(token);
        }
    }

    internal sealed class ResettableValueTaskSource<T> : ValueTaskSourceCoreWrapper<T>
    {
        public void PrepareForUse() => PrepareCoreForUse();
    }

    internal sealed class PoolableValueTaskSource<T> : ValueTaskSourceCoreWrapper<T>
    {
        private readonly Action<PoolableValueTaskSource<T>> _returnToPool;
        private int _returned;

        public PoolableValueTaskSource(Action<PoolableValueTaskSource<T>> returnToPool)
        {
            _returnToPool = returnToPool ?? throw new ArgumentNullException(nameof(returnToPool));
        }

        public void PrepareForUse()
        {
            PrepareCoreForUse();
            Volatile.Write(ref _returned, 0);
        }

        public void ReturnWithoutConsumption() => TryReturnToPool();

        public override T GetResult(short token)
        {
            try
            {
                return base.GetResult(token);
            }
            finally
            {
                TryReturnToPool();
            }
        }

        private void TryReturnToPool()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
            {
                _returnToPool(this);
            }
        }
    }

    internal sealed class PoolableValueTaskSourcePool<T>
    {
        private readonly ConcurrentQueue<PoolableValueTaskSource<T>> _pool =
            new ConcurrentQueue<PoolableValueTaskSource<T>>();
        private readonly int _maxSize;
        private int _reservedOrQueuedCount;

        public PoolableValueTaskSourcePool(int maxSize)
        {
            if (maxSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSize), "Must be greater than 0.");

            _maxSize = maxSize;
        }

        // Exact queue snapshot; capacity reservation is tracked separately.
        public int Count => _pool.Count;

        public PoolableValueTaskSource<T> Rent()
        {
            if (_pool.TryDequeue(out var source))
            {
                if (Interlocked.Decrement(ref _reservedOrQueuedCount) < 0)
                    Interlocked.Exchange(ref _reservedOrQueuedCount, 0);
                source.PrepareForUse();
                return source;
            }

            source = new PoolableValueTaskSource<T>(Return);
            source.PrepareForUse();
            return source;
        }

        private void Return(PoolableValueTaskSource<T> source)
        {
            if (source == null)
                return;

            while (true)
            {
                var snapshot = Volatile.Read(ref _reservedOrQueuedCount);
                if (snapshot >= _maxSize)
                    return;

                if (Interlocked.CompareExchange(
                        ref _reservedOrQueuedCount,
                        snapshot + 1,
                        snapshot) != snapshot)
                {
                    continue;
                }

                _pool.Enqueue(source);
                return;
            }
        }
    }
}
