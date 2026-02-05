using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Transport.Tcp
{
    /// <summary>
    /// Represents a single pooled TCP connection with optional TLS.
    /// </summary>
    public sealed class PooledConnection : IDisposable
    {
        private long _lastUsedTicks;
        private int _disposeFlag;

        public Socket Socket { get; }
        public Stream Stream { get; }
        public string Host { get; }
        public int Port { get; }
        public bool IsSecure { get; }

        /// <summary>
        /// Set to true when a connection is dequeued from the idle pool (previously used and returned).
        /// Used by RawSocketTransport retry-on-stale logic to distinguish fresh connections from pooled reused ones.
        /// </summary>
        public bool IsReused { get; internal set; }

        /// <summary>
        /// Set after TLS handshake from sslStream.SslProtocol. Null for non-TLS connections.
        /// </summary>
        public SslProtocols? NegotiatedTlsVersion { get; internal set; }

        /// <summary>
        /// The ALPN-negotiated protocol ("h2", "http/1.1", or null if no ALPN).
        /// Set after TLS handshake completes.
        /// </summary>
        public string NegotiatedAlpnProtocol { get; internal set; }

        /// <summary>
        /// Last time this connection was used. Stored as ticks for atomic access via Interlocked.
        /// </summary>
        public DateTime LastUsed
        {
            get => new DateTime(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);
            internal set => Interlocked.Exchange(ref _lastUsedTicks, value.Ticks);
        }

        /// <summary>
        /// Best-effort detection of server-closed connections.
        /// MUST NOT be relied upon for correctness — the retry-on-stale mechanism
        /// in RawSocketTransport (Phase 3.4) is the true safety net.
        /// </summary>
        public bool IsAlive
        {
            get
            {
                if (Volatile.Read(ref _disposeFlag) != 0) return false;
                try
                {
                    if (Socket == null || !Socket.Connected) return false;
                    if (Stream != null && !Stream.CanRead) return false;
                    return !(Socket.Poll(0, SelectMode.SelectRead) && Socket.Available == 0);
                }
                catch (ObjectDisposedException) { return false; }
                catch (SocketException) { return false; }
            }
        }

        public PooledConnection(Socket socket, Stream stream, string host, int port, bool isSecure)
        {
            Socket = socket ?? throw new ArgumentNullException(nameof(socket));
            Stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Host = host ?? throw new ArgumentNullException(nameof(host));
            Port = port;
            IsSecure = isSecure;
            LastUsed = DateTime.UtcNow;
        }

        /// <summary>
        /// Dispose Stream only. Since we use NetworkStream(socket, ownsSocket: true) and
        /// SslStream(innerStream, leaveInnerStreamOpen: false), the disposal chain handles
        /// everything: SslStream -> NetworkStream -> Socket.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposeFlag, 1, 0) != 0) return;
            try { Stream?.Dispose(); }
            catch { /* Best effort cleanup */ }
        }
    }

    /// <summary>
    /// Wraps a PooledConnection + semaphore reference to guarantee the per-host semaphore
    /// permit is always released, regardless of success, failure, or keep-alive status.
    /// A class (not struct) to avoid copy semantics issues with mutable IDisposable.
    /// </summary>
    public sealed class ConnectionLease : IDisposable
    {
        private readonly TcpConnectionPool _pool;
        private readonly SemaphoreSlim _semaphore;
        private readonly object _lock = new object();
        private bool _released;
        private bool _disposed;

        public PooledConnection Connection { get; }

        internal ConnectionLease(TcpConnectionPool pool, SemaphoreSlim semaphore, PooledConnection connection)
        {
            _pool = pool;
            _semaphore = semaphore;
            Connection = connection;
        }

        /// <summary>
        /// Transfer ownership of the underlying connection to another owner (e.g., Http2ConnectionManager).
        /// The connection will NOT be disposed or returned to the idle pool when this lease is disposed.
        /// The semaphore permit IS still released.
        /// </summary>
        public void TransferOwnership()
        {
            lock (_lock)
            {
                if (_released)
                    return;
                _released = true;
            }
            try
            {
                _semaphore.Release();
            }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }

        /// <summary>
        /// Return the connection to the pool for keep-alive reuse.
        /// Must be called BEFORE Dispose() for the connection to be reused.
        /// If not called, Dispose() will destroy the connection (but still release the semaphore).
        /// Thread-safe: synchronized with Dispose() to prevent races on async continuations.
        /// IsAlive check is performed OUTSIDE the lock to avoid holding the lock during Socket.Poll() syscall.
        /// </summary>
        public void ReturnToPool()
        {
            bool shouldReturn = false;
            lock (_lock)
            {
                if (!_released && !_disposed)
                {
                    shouldReturn = true;
                    _released = true;
                }
            }

            if (shouldReturn)
            {
                try
                {
                    if (Connection.IsAlive)
                        _pool.EnqueueConnection(Connection);
                    else
                        Connection.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Connection was disposed by racing Dispose() call on another thread.
                    // This can occur if Dispose() is called between setting _released = true
                    // and the IsAlive check. Safe to ignore — connection is already cleaned up.
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                if (!_released)
                {
                    Connection?.Dispose();
                }
            }
            // Release semaphore OUTSIDE the lock to avoid holding lock during Release().
            // ALWAYS release semaphore — this is the critical invariant.
            try
            {
                _semaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // Pool was disposed while connection was in flight — safe to ignore.
            }
            catch (SemaphoreFullException)
            {
                // Defensive: should never happen if permit tracking is correct.
                // Swallow to prevent Dispose() from throwing (violates .NET guidelines).
            }
        }
    }

    /// <summary>
    /// Thread-safe TCP connection pool keyed by host:port:secure.
    /// Manages idle connection reuse, per-host concurrency limits, TLS handshake delegation,
    /// and DNS resolution with timeout.
    /// </summary>
    public sealed class TcpConnectionPool : IDisposable
    {
        private const int DnsTimeoutMs = 5000;
        private const int MaxSemaphoreEntries = 1000;

        private readonly int _maxConnectionsPerHost;
        private readonly TimeSpan _connectionIdleTimeout;
        private volatile bool _disposed;

        private readonly ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>> _idleConnections
            = new ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores
            = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        /// <param name="maxConnectionsPerHost">Maximum concurrent connections per host (default 6, matches browser convention).</param>
        /// <param name="connectionIdleTimeout">How long idle connections remain pooled (default 2 minutes).</param>
        public TcpConnectionPool(int maxConnectionsPerHost = 6, TimeSpan? connectionIdleTimeout = null)
        {
            if (maxConnectionsPerHost <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxConnectionsPerHost), "Must be greater than 0");
            if (connectionIdleTimeout.HasValue && connectionIdleTimeout.Value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(connectionIdleTimeout), "Must be positive");

            _maxConnectionsPerHost = maxConnectionsPerHost;
            _connectionIdleTimeout = connectionIdleTimeout ?? TimeSpan.FromMinutes(2);
        }

        /// <summary>
        /// Acquire a connection (pooled or new) to the specified host.
        /// Returns a ConnectionLease that guarantees semaphore release on disposal.
        /// </summary>
        public async Task<ConnectionLease> GetConnectionAsync(
            string host, int port, bool secure, CancellationToken ct)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(TcpConnectionPool));

            var key = $"{host}:{port}:{(secure ? "s" : "")}";

            var semaphore = _semaphores.GetOrAdd(key, _ => new SemaphoreSlim(_maxConnectionsPerHost, _maxConnectionsPerHost));

            EvictSemaphoresIfNeeded(key);

            await semaphore.WaitAsync(ct).ConfigureAwait(false);

            // From this point, the semaphore permit is owned. ALL paths must release it.
            try
            {
                // Try to dequeue an idle connection
                if (_idleConnections.TryGetValue(key, out var queue))
                {
                    while (queue.TryDequeue(out var candidate))
                    {
                        if (candidate.IsAlive && (DateTime.UtcNow - candidate.LastUsed) < _connectionIdleTimeout)
                        {
                            candidate.IsReused = true;
                            candidate.LastUsed = DateTime.UtcNow;
                            return new ConnectionLease(this, semaphore, candidate);
                        }
                        candidate.Dispose();
                    }
                }

                // No reusable connection — create new
                var connection = await CreateConnectionAsync(host, port, secure, ct).ConfigureAwait(false);
                return new ConnectionLease(this, semaphore, connection);
            }
            catch
            {
                semaphore.Release();
                throw;
            }
        }

        /// <summary>
        /// Enqueue a live connection for reuse. Called by ConnectionLease.ReturnToPool().
        /// Does NOT release the semaphore — that is handled by ConnectionLease.Dispose().
        /// </summary>
        internal void EnqueueConnection(PooledConnection connection)
        {
            if (_disposed || connection == null || !connection.IsAlive)
            {
                connection?.Dispose();
                return;
            }

            connection.LastUsed = DateTime.UtcNow;
            var key = $"{connection.Host}:{connection.Port}:{(connection.IsSecure ? "s" : "")}";
            var queue = _idleConnections.GetOrAdd(key, _ => new ConcurrentQueue<PooledConnection>());
            queue.Enqueue(connection);
        }

        private async Task<PooledConnection> CreateConnectionAsync(
            string host, int port, bool secure, CancellationToken ct)
        {
            // DNS resolution with timeout
            var addresses = await ResolveDnsAsync(host, ct).ConfigureAwait(false);

            // Socket connection with address fallback (IPv6-safe)
            var socket = await ConnectSocketAsync(addresses, port, ct).ConfigureAwait(false);

            Stream stream = new NetworkStream(socket, ownsSocket: true);
            SslProtocols? negotiatedTls = null;

            if (secure)
            {
                try
                {
                    var tlsResult = await TlsStreamWrapper.WrapAsync(stream, host, ct,
                        new[] { "h2", "http/1.1" }).ConfigureAwait(false);
                    stream = tlsResult.Stream;
                    negotiatedTls = tlsResult.NegotiatedProtocol;
                }
                catch
                {
                    stream.Dispose(); // Cascades to NetworkStream -> Socket
                    throw;
                }
            }

            var connection = new PooledConnection(socket, stream, host, port, secure);
            if (negotiatedTls.HasValue)
                connection.NegotiatedTlsVersion = negotiatedTls.Value;

            // Store ALPN result
            if (secure && stream is System.Net.Security.SslStream sslStream)
            {
                connection.NegotiatedAlpnProtocol = TlsStreamWrapper.GetNegotiatedProtocol(sslStream);
            }

            return connection;
        }

        private async Task<IPAddress[]> ResolveDnsAsync(string host, CancellationToken ct)
        {
            // Dns.GetHostAddressesAsync has no CancellationToken in .NET Standard 2.1.
            // Wrap with a timeout to prevent 30+ second hangs on mobile networks.
            var dnsTask = Dns.GetHostAddressesAsync(host);
            var timeoutTask = Task.Delay(DnsTimeoutMs, ct);
            var completed = await Task.WhenAny(dnsTask, timeoutTask).ConfigureAwait(false);

            if (completed == timeoutTask)
            {
                // Check cancellation FIRST — distinguish user cancellation from DNS timeout.
                ct.ThrowIfCancellationRequested();
                throw new UHttpException(new UHttpError(UHttpErrorType.Timeout,
                    $"DNS resolution for '{host}' timed out after {DnsTimeoutMs}ms"));
            }

            return await dnsTask.ConfigureAwait(false);
        }

        private static async Task<Socket> ConnectSocketAsync(
            IPAddress[] addresses, int port, CancellationToken ct)
        {
            Socket socket = null;
            Exception lastException = null;

            foreach (var address in addresses)
            {
                try
                {
                    socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    socket.NoDelay = true;

                    // ConnectAsync has no CancellationToken in .NET Std 2.1.
                    // Use dispose-on-cancel pattern for best-effort cancellation.
                    using (ct.Register(() => socket.Dispose()))
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, port)).ConfigureAwait(false);
                    }

                    // Guard against race: if ct fired just after ConnectAsync completed
                    // but before the Register callback was unsubscribed, the socket may
                    // have been disposed. Check and throw to avoid returning a dead socket.
                    if (ct.IsCancellationRequested)
                    {
                        socket.Dispose();
                        throw new OperationCanceledException("Connection cancelled", ct);
                    }

                    lastException = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    socket?.Dispose();
                    socket = null;

                    if (ct.IsCancellationRequested)
                        throw new OperationCanceledException("Connection cancelled", ex, ct);
                }
            }

            if (socket == null)
                throw lastException ?? new SocketException((int)SocketError.HostNotFound);

            return socket;
        }

        private void EvictSemaphoresIfNeeded(string currentKey)
        {
            if (_semaphores.Count <= MaxSemaphoreEntries)
                return;

            foreach (var kvp in _semaphores)
            {
                if (string.Equals(kvp.Key, currentKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (kvp.Value.CurrentCount == _maxConnectionsPerHost)
                {
                    // Remove idle connections first
                    if (_idleConnections.TryRemove(kvp.Key, out var removedQueue))
                    {
                        while (removedQueue.TryDequeue(out var conn))
                        {
                            conn.Dispose();
                        }
                    }

                    // Remove semaphore from dictionary but do NOT dispose it — a racing thread
                    // may still hold a reference from GetOrAdd. The orphaned semaphore is valid
                    // and will be GC'd when that thread completes.
                    //
                    // KNOWN RACE: If another thread grabbed this semaphore reference just before
                    // TryRemove, future calls for the same host will create a NEW semaphore via
                    // GetOrAdd, temporarily allowing 2x the per-host limit until the old semaphore's
                    // waiters drain. This is an accepted best-effort tradeoff — the cap is advisory,
                    // not a hard guarantee. Proper LRU with quiescence checks deferred to Phase 10.
                    _semaphores.TryRemove(kvp.Key, out _);

                    if (_semaphores.Count <= MaxSemaphoreEntries)
                        break;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Drain all queues, dispose each connection
            foreach (var kvp in _idleConnections)
            {
                while (kvp.Value.TryDequeue(out var conn))
                {
                    conn.Dispose();
                }
            }
            _idleConnections.Clear();

            // Dispose all semaphores
            foreach (var kvp in _semaphores)
            {
                kvp.Value.Dispose();
            }
            _semaphores.Clear();
        }
    }
}
