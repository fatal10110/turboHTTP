using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// Per-host HTTP/2 connection cache. Ensures one active Http2Connection per origin.
    /// Thread-safe with thundering herd prevention via per-key locking.
    /// </summary>
    internal class Http2ConnectionManager : IDisposable
    {
        private readonly Http2Options _options;
        private readonly StreamingOptions _streamingOptions;

        private readonly ConcurrentDictionary<string, Http2Connection> _connections
            = new ConcurrentDictionary<string, Http2Connection>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _initLocks
            = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private int _disposed;

        public Http2ConnectionManager(Http2Options options, StreamingOptions streamingOptions)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _streamingOptions = streamingOptions ?? throw new ArgumentNullException(nameof(streamingOptions));
        }

        /// <summary>
        /// Get an existing alive h2 connection for this host:port, or null.
        /// Fast path — no locking, no async.
        /// </summary>
        public Http2Connection GetIfExists(string host, int port)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return null;

            string key = $"{host}:{port}";
            if (_connections.TryGetValue(key, out var conn) && conn.IsAlive)
                return conn;

            // Proactively evict stale entries so they do not accumulate in long-idle sessions.
            // A GOAWAY'd connection sets IsAlive = false but the slow-path cleanup may never
            // run if no new request arrives. Eviction here prevents unbounded dead-object retention.
            if (conn != null)
                RemoveExactAndDispose(key, conn);

            return null;
        }

        /// <summary>
        /// Get an existing alive h2 connection for this origin through a CONNECT proxy tunnel, or null.
        /// Fast path — no locking, no async.
        /// </summary>
        public Http2Connection GetIfExists(
            string originHost,
            int originPort,
            string proxyHost,
            int proxyPort)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return null;

            string key = BuildTunnelKey(originHost, originPort, proxyHost, proxyPort);
            if (_connections.TryGetValue(key, out var conn) && conn.IsAlive)
                return conn;

            // Same proactive eviction as the direct overload.
            if (conn != null)
                RemoveExactAndDispose(key, conn);

            return null;
        }

        internal bool HasConnection(string host, int port)
        {
            return GetIfExists(host, port) != null;
        }

        internal bool HasConnection(
            string originHost,
            int originPort,
            string proxyHost,
            int proxyPort)
        {
            return GetIfExists(originHost, originPort, proxyHost, proxyPort) != null;
        }

        /// <summary>
        /// Get or create an h2 connection for this host:port.
        /// If one exists and is alive, return it (disposes the caller's tlsStream).
        /// Otherwise, create a new one using the provided tlsStream.
        /// Prevents thundering herd via per-key locking.
        /// </summary>
        public ValueTask<Http2Connection> GetOrCreateAsync(
            string host, int port, Stream tlsStream, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                tlsStream?.Dispose();
                throw new ObjectDisposedException(nameof(Http2ConnectionManager));
            }

            string key = $"{host}:{port}";

            // Fast path: existing alive connection
            if (_connections.TryGetValue(key, out var existing) && existing.IsAlive)
            {
                tlsStream.Dispose();
                return new ValueTask<Http2Connection>(existing);
            }

            return GetOrCreateCoreAsync(key, host, port, tlsStream, ct);
        }

        /// <summary>
        /// Get or create an h2 connection for this origin through a CONNECT proxy tunnel.
        /// The provided stream must already be TLS-wrapped with h2 ALPN negotiated.
        /// </summary>
        public ValueTask<Http2Connection> GetOrCreateAsync(
            string originHost,
            int originPort,
            string proxyHost,
            int proxyPort,
            Stream tlsStream,
            CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                tlsStream?.Dispose();
                throw new ObjectDisposedException(nameof(Http2ConnectionManager));
            }

            string key = BuildTunnelKey(originHost, originPort, proxyHost, proxyPort);

            // Fast path: existing alive connection
            if (_connections.TryGetValue(key, out var existing) && existing.IsAlive)
            {
                tlsStream.Dispose();
                return new ValueTask<Http2Connection>(existing);
            }

            return GetOrCreateCoreAsync(key, originHost, originPort, tlsStream, ct);
        }

        private ValueTask<Http2Connection> GetOrCreateCoreAsync(
            string key,
            string host,
            int port,
            Stream tlsStream,
            CancellationToken ct)
        {
            return new ValueTask<Http2Connection>(GetOrCreateSlowAsync(
                key,
                host,
                port,
                tlsStream,
                ct));
        }

        private async Task<Http2Connection> GetOrCreateSlowAsync(
            string key,
            string host,
            int port,
            Stream tlsStream,
            CancellationToken ct)
        {
            Http2Connection existing;
            Http2Connection newConnection = null;
            bool published = false;

            try
            {
                // Slow path: create with per-key lock
                var initLock = _initLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
                await initLock.WaitAsync(ct);

                try
                {
                    // Double-check after acquiring lock
                    if (_connections.TryGetValue(key, out existing) && existing.IsAlive)
                    {
                        tlsStream.Dispose();
                        tlsStream = null;
                        return existing;
                    }

                    // Remove stale connection
                    if (existing != null)
                    {
                        _connections.TryRemove(key, out _);
                        existing.Dispose();
                    }

                    // Create and initialize new connection (consumes tlsStream).
                    // If InitializeAsync fails (timeout, protocol error, cancellation),
                    // dispose the connection to clean up the stream + read loop.
                    // The lease was already transferred, so we own the stream.
                    newConnection = new Http2Connection(
                        tlsStream,
                        host,
                        port,
                        _options,
                        _streamingOptions);
                    tlsStream = null;

                    await newConnection.InitializeAsync(ct);
                    _connections[key] = newConnection;
                    published = true;
                    return newConnection;
                }
                finally
                {
                    initLock.Release();
                    // Remove init lock after a successful publish: future callers hit the fast-path
                    // TryGetValue and never need the semaphore again. Removal prevents unbounded
                    // _initLocks growth for long-lived managers with many distinct tunnel destinations.
                    // On failure paths the semaphore is left so a retry can re-enter the slow path.
                    if (published)
                        _initLocks.TryRemove(key, out _);
                }
            }
            catch
            {
                if (!published)
                {
                    newConnection?.Dispose();
                    tlsStream?.Dispose();
                }

                throw;
            }
        }

        /// <summary>
        /// Remove and dispose a stale connection.
        /// </summary>
        public void Remove(string host, int port)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            string key = $"{host}:{port}";
            if (_connections.TryRemove(key, out var conn))
            {
                conn.Dispose();
            }
        }

        /// <summary>
        /// Remove and dispose a stale tunneled connection.
        /// </summary>
        public void Remove(
            string originHost,
            int originPort,
            string proxyHost,
            int proxyPort)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            string key = BuildTunnelKey(originHost, originPort, proxyHost, proxyPort);
            if (_connections.TryRemove(key, out var conn))
            {
                conn.Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            foreach (var kvp in _connections)
            {
                kvp.Value.Dispose();
            }
            _connections.Clear();

            foreach (var kvp in _initLocks)
            {
                kvp.Value.Dispose();
            }
            _initLocks.Clear();
        }

        private static string BuildTunnelKey(
            string originHost,
            int originPort,
            string proxyHost,
            int proxyPort)
        {
            return $"{originHost}:{originPort}|via|{proxyHost}:{proxyPort}";
        }

        private void RemoveExactAndDispose(string key, Http2Connection connection)
        {
            var entry = new KeyValuePair<string, Http2Connection>(key, connection);
            if (((ICollection<KeyValuePair<string, Http2Connection>>)_connections).Remove(entry))
                connection.Dispose();
        }
    }
}
