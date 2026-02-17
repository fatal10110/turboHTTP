using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Performance
{
    /// <summary>
    /// Limits concurrent operations on a per-host basis using semaphores.
    /// Also enforces a global concurrency limit across all hosts.
    /// Thread-safe and disposable.
    /// </summary>
    public sealed class ConcurrencyLimiter : IDisposable
    {
        private const int MaxHostSemaphoreEntries = 1024;

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostSemaphores
            = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

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

            try
            {
                if (IsDisposingOrDisposed())
                    throw new ObjectDisposedException(nameof(ConcurrencyLimiter));

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _disposeCts.Token);

                // Acquire global permit first to prevent starvation.
                await _globalSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                globalAcquired = true;

                var hostSemaphore = _hostSemaphores.GetOrAdd(host,
                    _ => new SemaphoreSlim(_maxConnectionsPerHost, _maxConnectionsPerHost));

                await hostSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                hostAcquired = true;
                EvictHostSemaphoresIfNeeded(host);
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
                    EndOperation();
            }
        }

        /// <summary>
        /// Release a concurrency permit for the given host.
        /// Must be called exactly once per successful <see cref="AcquireAsync"/> call.
        /// </summary>
        /// <param name="host">The host key used in the corresponding <see cref="AcquireAsync"/> call.</param>
        /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
        public void Release(string host)
        {
            if (host == null)
                throw new ArgumentNullException(nameof(host));

            try
            {
                if (_hostSemaphores.TryGetValue(host, out var hostSemaphore))
                {
                    hostSemaphore.Release();
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

        private void EvictHostSemaphoresIfNeeded(string currentHost)
        {
            if (_hostSemaphores.Count <= MaxHostSemaphoreEntries)
                return;

            foreach (var kvp in _hostSemaphores)
            {
                if (string.Equals(kvp.Key, currentHost, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isIdle;
                try
                {
                    isIdle = kvp.Value.CurrentCount == _maxConnectionsPerHost;
                }
                catch (ObjectDisposedException)
                {
                    isIdle = true;
                }

                if (!isIdle)
                    continue;

                // Remove only from dictionary. Do not dispose removed semaphores because
                // racing acquires/releases may still hold references.
                _hostSemaphores.TryRemove(kvp.Key, out _);

                if (_hostSemaphores.Count <= MaxHostSemaphoreEntries)
                    break;
            }
        }

        private void TryFinalizeDispose()
        {
            if (Volatile.Read(ref _disposing) == 0 || Volatile.Read(ref _activeOperations) != 0)
                return;

            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _globalSemaphore.Dispose();

            foreach (var kvp in _hostSemaphores)
            {
                kvp.Value.Dispose();
            }

            _hostSemaphores.Clear();
            _disposeCts.Dispose();
        }
    }
}
