using System;
using System.Collections.Concurrent;
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
        private readonly int _maxDecodedHeaderBytes;

        private readonly ConcurrentDictionary<string, Http2Connection> _connections
            = new ConcurrentDictionary<string, Http2Connection>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _initLocks
            = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private int _disposed;

        public Http2ConnectionManager(
            int maxDecodedHeaderBytes = UHttpClientOptions.DefaultHttp2MaxDecodedHeaderBytes)
        {
            if (maxDecodedHeaderBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxDecodedHeaderBytes),
                    maxDecodedHeaderBytes,
                    "Must be greater than 0.");
            }

            _maxDecodedHeaderBytes = maxDecodedHeaderBytes;
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
            return null;
        }

        internal bool HasConnection(string host, int port)
        {
            return GetIfExists(host, port) != null;
        }

        /// <summary>
        /// Get or create an h2 connection for this host:port.
        /// If one exists and is alive, return it (disposes the caller's tlsStream).
        /// Otherwise, create a new one using the provided tlsStream.
        /// Prevents thundering herd via per-key locking.
        /// </summary>
        public async Task<Http2Connection> GetOrCreateAsync(
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
                return existing;
            }

            // Slow path: create with per-key lock
            var initLock = _initLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await initLock.WaitAsync(ct);
            try
            {
                // Double-check after acquiring lock
                if (_connections.TryGetValue(key, out existing) && existing.IsAlive)
                {
                    tlsStream.Dispose();
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
                var conn = new Http2Connection(
                    tlsStream,
                    host,
                    port,
                    maxDecodedHeaderBytes: _maxDecodedHeaderBytes);
                try
                {
                    await conn.InitializeAsync(ct);
                }
                catch
                {
                    conn.Dispose();
                    throw;
                }
                _connections[key] = conn;
                return conn;
            }
            finally
            {
                initLock.Release();
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
    }
}
