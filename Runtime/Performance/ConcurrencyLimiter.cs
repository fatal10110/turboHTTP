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
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostSemaphores
            = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        private readonly SemaphoreSlim _globalSemaphore;
        private readonly int _maxConnectionsPerHost;
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
            ThrowIfDisposed();

            if (host == null)
                throw new ArgumentNullException(nameof(host));

            // Acquire global permit first to prevent starvation
            await _globalSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var hostSemaphore = _hostSemaphores.GetOrAdd(host,
                    _ => new SemaphoreSlim(_maxConnectionsPerHost, _maxConnectionsPerHost));

                await hostSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // If per-host acquire fails (cancellation), release the global permit
                _globalSemaphore.Release();
                throw;
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

            if (_hostSemaphores.TryGetValue(host, out var hostSemaphore))
            {
                hostSemaphore.Release();
            }

            _globalSemaphore.Release();
        }

        /// <summary>
        /// Disposes all semaphores. Outstanding <see cref="AcquireAsync"/> calls
        /// will throw <see cref="ObjectDisposedException"/>.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _globalSemaphore.Dispose();

            foreach (var kvp in _hostSemaphores)
            {
                kvp.Value.Dispose();
            }

            _hostSemaphores.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ConcurrencyLimiter));
        }
    }
}
