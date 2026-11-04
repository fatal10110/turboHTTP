using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.RateLimit
{
    /// <summary>
    /// Limits concurrent operations on a per-host basis using semaphores.
    /// Also enforces a global concurrency limit across all hosts.
    /// Thread-safe and disposable.
    /// <para>Host entries are reference-counted: an entry is pinned from the moment an
    /// <see cref="AcquireAsync"/> call looks it up until the matching <see cref="Release"/> (or the
    /// failed acquire) unpins it, and only unpinned entries are evicted. A release therefore always
    /// returns its permit to the same semaphore it was taken from, and two semaphores never exist
    /// for one host at the same time (#73).</para>
    /// </summary>
    public sealed class ConcurrencyLimiter : IDisposable
    {
        internal const int MaxHostSemaphoreEntries = 1024;

        // Owned by _hostLock: the dictionary and every HostEntry.RefCount.
        private readonly object _hostLock = new object();
        private readonly Dictionary<string, HostEntry> _hostSemaphores
            = new Dictionary<string, HostEntry>(StringComparer.OrdinalIgnoreCase);

        private readonly SemaphoreSlim _globalSemaphore;
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private readonly int _maxConnectionsPerHost;
        private int _disposing;
        private int _activeOperations;
        private int _disposed;

        /// <summary>
        /// Maximum concurrent connections allowed per host.
        /// </summary>
        public int MaxConnectionsPerHost => _maxConnectionsPerHost;

        /// <summary>
        /// Maximum total concurrent connections across all hosts.
        /// </summary>
        public int MaxTotalConnections { get; }

        /// <summary>
        /// Creates a new concurrency limiter.
        /// </summary>
        /// <param name="maxConnectionsPerHost">Maximum concurrent connections per host. Default 6 (matching browser behavior).</param>
        /// <param name="maxTotalConnections">Maximum total concurrent connections. Default 64.</param>
        /// <exception cref="ArgumentOutOfRangeException">Either parameter is less than 1.</exception>
        public ConcurrencyLimiter(int maxConnectionsPerHost = 6, int maxTotalConnections = 64)
        {
            if (maxConnectionsPerHost < 1)
                throw new ArgumentOutOfRangeException(nameof(maxConnectionsPerHost), maxConnectionsPerHost,
                    "Must be at least 1.");
            if (maxTotalConnections < 1)
                throw new ArgumentOutOfRangeException(nameof(maxTotalConnections), maxTotalConnections,
                    "Must be at least 1.");

            _maxConnectionsPerHost = maxConnectionsPerHost;
            MaxTotalConnections = maxTotalConnections;
            _globalSemaphore = new SemaphoreSlim(maxTotalConnections, maxTotalConnections);
        }

        /// <summary>
        /// Acquire a concurrency permit for the given host.
        /// Blocks asynchronously until both a per-host and global permit are available.
        /// The caller MUST call <see cref="Release"/> when the operation completes.
        /// </summary>
        /// <param name="host">The host key (typically hostname or host:port).</param>
        /// <param name="cancellationToken">Token to cancel the wait.</param>
        /// <exception cref="ObjectDisposedException">The limiter has been disposed.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
        /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
        public async Task AcquireAsync(string host, CancellationToken cancellationToken = default)
        {
            if (host == null)
                throw new ArgumentNullException(nameof(host));

            ThrowIfDisposed();

            Interlocked.Increment(ref _activeOperations);

            bool globalAcquired = false;
            bool hostAcquired = false;
            HostEntry hostEntry = null;

            try
            {
                if (IsDisposingOrDisposed())
                    throw new ObjectDisposedException(nameof(ConcurrencyLimiter));

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _disposeCts.Token);

                // Acquire global permit first to prevent starvation.
                await _globalSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                globalAcquired = true;

                hostEntry = PinHostEntry(host);

                await hostEntry.Semaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                hostAcquired = true;
            }
            catch (OperationCanceledException) when (IsDisposingOrDisposed() && !cancellationToken.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(ConcurrencyLimiter));
            }
            catch
            {
                // If per-host acquire fails/cancels after global acquire, release global permit.
                if (globalAcquired && !hostAcquired)
                {
                    try
                    {
                        _globalSemaphore.Release();
                    }
                    catch (ObjectDisposedException) when (IsDisposingOrDisposed())
                    {
                        // Disposer owns semaphore lifetime once shutdown starts.
                    }
                }

                throw;
            }
            finally
            {
                // Failed acquires still consume an active-operation slot that must be released.
                if (!hostAcquired)
                {
                    if (hostEntry != null)
                        UnpinHostEntry(hostEntry);
                    EndOperation();
                }
            }
        }

        /// <summary>
        /// Release a concurrency permit for the given host.
        /// Must be called exactly once per successful <see cref="AcquireAsync"/> call.
        /// The permit goes back to the host semaphore it was acquired from, even when other hosts
        /// have churned through the entry cache in between.
        /// </summary>
        /// <param name="host">The host key used in the corresponding <see cref="AcquireAsync"/> call.</param>
        /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
        public void Release(string host)
        {
            if (host == null)
                throw new ArgumentNullException(nameof(host));

            try
            {
                HostEntry entry;
                lock (_hostLock)
                {
                    // A pinned entry is never evicted, so the entry of a held permit is still here.
                    _hostSemaphores.TryGetValue(host, out entry);
                }

                if (entry != null)
                {
                    try
                    {
                        entry.Semaphore.Release();
                    }
                    finally
                    {
                        UnpinHostEntry(entry);
                    }
                }
            }
            catch (ObjectDisposedException) when (IsDisposingOrDisposed())
            {
                // Disposal race: semaphore already torn down.
            }
            finally
            {
                try
                {
                    _globalSemaphore.Release();
                }
                catch (ObjectDisposedException) when (IsDisposingOrDisposed())
                {
                    // Disposal race: semaphore already torn down.
                }

                EndOperation();
            }
        }

        /// <summary>
        /// Disposes all semaphores. Outstanding <see cref="AcquireAsync"/> calls
        /// will throw <see cref="ObjectDisposedException"/>.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposing, 1, 0) != 0)
                return;

            _disposeCts.Cancel();
            TryFinalizeDispose();
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposingOrDisposed())
                throw new ObjectDisposedException(nameof(ConcurrencyLimiter));
        }

        private bool IsDisposingOrDisposed()
        {
            return Volatile.Read(ref _disposing) != 0 || Volatile.Read(ref _disposed) != 0;
        }

        private void EndOperation()
        {
            int remaining = Interlocked.Decrement(ref _activeOperations);
            if (remaining < 0)
            {
                Interlocked.Exchange(ref _activeOperations, 0);
                remaining = 0;
            }

            if (remaining == 0)
                TryFinalizeDispose();
        }

        /// <summary>Number of cached host entries (pinned or idle), for tests.</summary>
        internal int HostEntryCount
        {
            get
            {
                lock (_hostLock)
                    return _hostSemaphores.Count;
            }
        }

        private HostEntry PinHostEntry(string host)
        {
            lock (_hostLock)
            {
                if (!_hostSemaphores.TryGetValue(host, out var entry))
                {
                    entry = new HostEntry(new SemaphoreSlim(_maxConnectionsPerHost, _maxConnectionsPerHost));
                    _hostSemaphores.Add(host, entry);
                }

                entry.RefCount++;
                return entry;
            }
        }

        private void UnpinHostEntry(HostEntry entry)
        {
            List<SemaphoreSlim> evicted = null;
            lock (_hostLock)
            {
                entry.RefCount--;
                if (_hostSemaphores.Count > MaxHostSemaphoreEntries)
                    evicted = EvictIdleHostEntries_NoLock();
            }

            if (evicted == null)
                return;

            // Unpinned entries have no waiter and no permit holder: nobody can reach them again.
            for (int i = 0; i < evicted.Count; i++)
                evicted[i].Dispose();
        }

        private List<SemaphoreSlim> EvictIdleHostEntries_NoLock()
        {
            List<string> idleHosts = null;
            foreach (var kvp in _hostSemaphores)
            {
                if (kvp.Value.RefCount == 0)
                    (idleHosts ??= new List<string>()).Add(kvp.Key);
            }

            if (idleHosts == null)
                return null;

            var evicted = new List<SemaphoreSlim>(idleHosts.Count);
            for (int i = 0; i < idleHosts.Count; i++)
            {
                if (_hostSemaphores.TryGetValue(idleHosts[i], out var entry))
                {
                    _hostSemaphores.Remove(idleHosts[i]);
                    evicted.Add(entry.Semaphore);
                }
            }

            return evicted;
        }

        private void TryFinalizeDispose()
        {
            if (Volatile.Read(ref _disposing) == 0 || Volatile.Read(ref _activeOperations) != 0)
                return;

            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _globalSemaphore.Dispose();

            lock (_hostLock)
            {
                foreach (var kvp in _hostSemaphores)
                {
                    kvp.Value.Semaphore.Dispose();
                }

                _hostSemaphores.Clear();
            }

            _disposeCts.Dispose();
        }

        private sealed class HostEntry
        {
            public HostEntry(SemaphoreSlim semaphore)
            {
                Semaphore = semaphore;
            }

            public SemaphoreSlim Semaphore { get; }

            // Acquirers between lookup and release/failed acquire. Guarded by _hostLock.
            public int RefCount;
        }
    }
}
