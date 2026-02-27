using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Periodically logs pool health diagnostics for <see cref="ByteArrayPool"/> and
    /// registered <see cref="ObjectPool{T}"/> instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reporting runs entirely off the request hot path on a background <see cref="ThreadPool"/>
    /// thread. The timer is self-scheduling (fires the next tick only after the current
    /// callback completes), so slow log sinks never cause timer re-entrancy.
    /// </para>
    /// <para>
    /// <b>Mobile / background note:</b> On iOS, the OS suspends processes that enter the
    /// background without a declared background execution mode. Timer callbacks do not fire
    /// during suspension; the reporter simply misses those ticks. On resume, the next
    /// scheduled tick fires normally. No corrective action is needed for diagnostics use.
    /// </para>
    /// <para>
    /// <b>Thread safety of the log sink:</b> The <c>log</c> callback is invoked from a
    /// <see cref="ThreadPool"/> thread, not the Unity main thread. <c>UnityEngine.Debug.Log</c>
    /// is thread-safe in Unity 2021.3+ and can be passed directly. Callbacks that touch
    /// UI or Unity objects must marshal to the main thread themselves.
    /// </para>
    /// <para>
    /// <b>Diagnostic counter scope:</b> <see cref="ByteArrayPool"/> counters track rentals
    /// via the raw-array API (<c>ByteArrayPool.Rent/Return</c>). Rentals made through
    /// <c>MemoryPool&lt;byte&gt;.Shared</c> (backing <see cref="PooledBuffer{T}"/>) are not
    /// reflected in those counters — the two pools are separate diagnostic surfaces even
    /// though both draw from <c>ArrayPool&lt;byte&gt;.Shared</c> under the hood.
    /// </para>
    /// <para>
    /// After <see cref="Dispose"/> returns, an in-flight timer callback may still be
    /// executing. The reporter holds no disposable resources beyond the timer itself,
    /// so this is safe — but callers must not treat <see cref="Dispose"/> as a
    /// synchronization point.
    /// </para>
    /// </remarks>
    public sealed class PoolHealthReporter : IDisposable
    {
        private readonly Action<string> _log;
        private readonly TimeSpan _interval;
        private readonly List<IPoolSource> _sources = new List<IPoolSource>();
        private Timer _timer;
        private bool _disposed;
        private readonly object _lock = new object();

        // Previous-tick snapshot for delta computation. Only accessed from the
        // timer callback, which is serialized by the self-scheduling pattern.
        private ByteArrayPoolDiagnostics _prevByteArray;

        /// <summary>
        /// Creates a new reporter.
        /// </summary>
        /// <param name="log">
        /// Log sink invoked on a background thread. Must not be null.
        /// </param>
        /// <param name="interval">Reporting interval. Must be positive.</param>
        public PoolHealthReporter(Action<string> log, TimeSpan interval)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");

            _interval = interval;
            _prevByteArray = ByteArrayPool.GetDiagnostics();
        }

        /// <summary>
        /// Registers an <see cref="ObjectPool{T}"/> to be included in health reports.
        /// </summary>
        /// <param name="name">Human-readable pool name shown in log output.</param>
        /// <param name="pool">The pool to monitor.</param>
        public void Register<T>(string name, ObjectPool<T> pool) where T : class
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (pool == null) throw new ArgumentNullException(nameof(pool));

            lock (_lock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(PoolHealthReporter));

                _sources.Add(new ObjectPoolSource<T>(name, pool));
            }
        }

        /// <summary>
        /// Starts the periodic reporting timer.
        /// Safe to call multiple times — subsequent calls are no-ops.
        /// </summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(PoolHealthReporter));

                if (_timer != null)
                    return;

                // Use Timeout.Infinite period: the callback reschedules itself after
                // completing, preventing re-entrant timer callbacks if Report() is slow.
                _timer = new Timer(OnTick, null, _interval, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// Stops and disposes the reporting timer. Safe to call multiple times.
        /// </summary>
        public void Dispose()
        {
            Timer t;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                t = _timer;
                _timer = null;
            }

            t?.Dispose();
        }

        // ── Timer callback ───────────────────────────────────────────────────────

        private void OnTick(object _)
        {
            try
            {
                Report();
            }
            catch
            {
                // Never let a reporting failure propagate to the thread pool.
            }
            finally
            {
                // Reschedule only if not disposed. This makes the timer self-scheduling
                // and prevents re-entrant callbacks from a slow log sink.
                lock (_lock)
                {
                    if (!_disposed)
                        _timer?.Change(_interval, Timeout.InfiniteTimeSpan);
                }
            }
        }

        private void Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[TurboHTTP][Pools]");

            // ByteArrayPool deltas (snapshots only accessed from this callback).
            var cur = ByteArrayPool.GetDiagnostics();
            var prev = _prevByteArray;
            _prevByteArray = cur;

            long rentDelta = cur.RentCount - prev.RentCount;
            long returnDelta = cur.ReturnCount - prev.ReturnCount;
            sb.Append("  ByteArrayPool");
            sb.Append($" | Active={cur.ActiveCount}");
            sb.Append($" | +Rent={rentDelta}");
            sb.Append($" | +Return={returnDelta}");
            sb.Append($" | ClearOnReturn={cur.ClearOnReturnCount}");
            sb.AppendLine();

            // Registered ObjectPool<T> sources.
            List<IPoolSource> snapshot;
            lock (_lock)
            {
                snapshot = new List<IPoolSource>(_sources);
            }

            foreach (var source in snapshot)
                source.AppendReport(sb);

            _log(sb.ToString().TrimEnd());
        }

        // ── Inner types ──────────────────────────────────────────────────────────

        private interface IPoolSource
        {
            void AppendReport(StringBuilder sb);
        }

        private sealed class ObjectPoolSource<T> : IPoolSource where T : class
        {
            private readonly string _name;
            private readonly ObjectPool<T> _pool;
            // _prev is only read/written from the timer callback, which is serialized
            // by the self-scheduling pattern. No synchronization needed here.
            private ObjectPoolDiagnostics _prev;

            internal ObjectPoolSource(string name, ObjectPool<T> pool)
            {
                _name = name;
                _pool = pool;
                _prev = pool.GetDiagnostics();
            }

            public void AppendReport(StringBuilder sb)
            {
                var cur = _pool.GetDiagnostics();
                var prev = _prev;
                _prev = cur;

                long rentDelta = cur.RentCount - prev.RentCount;
                long returnDelta = cur.ReturnCount - prev.ReturnCount;
                long missDelta = cur.MissCount - prev.MissCount;

                sb.Append($"  {_name}");
                sb.Append($" | Active={cur.ActiveCount}/{_pool.Capacity}");
                sb.Append($" | +Rent={rentDelta}");
                sb.Append($" | +Return={returnDelta}");
                sb.Append($" | +Miss={missDelta}");
                sb.AppendLine();
            }
        }
    }
}
