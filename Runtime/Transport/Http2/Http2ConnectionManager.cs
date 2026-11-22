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
    /// Per-host HTTP/2 connection cache. Keeps up to <see cref="Http2Options.MaxConnectionsPerOrigin"/>
    /// active Http2Connections per origin (one by default, RFC 9113 Section 9.1); another opens only
    /// when every connection of the origin is saturated. Thread-safe. A cold origin is dialed once: the
    /// first request claims the dial
    /// (<see cref="ClaimOrJoinColdDial(string, int, out ColdDialClaim)"/>) and the
    /// others await it instead of each opening a TCP+TLS connection (#736; through a CONNECT proxy, one
    /// tunnel per origin and proxy, #1180); the per-key init lock only serializes the HTTP/2
    /// connection preface of connections that were dialed anyway.
    /// </summary>
    internal class Http2ConnectionManager : IDisposable
    {
        private readonly Http2Options _options;
        private readonly StreamingOptions _streamingOptions;

        private readonly int _maxConnectionsPerOrigin;

        // Per key, an immutable array replaced by compare-and-swap (TryUpdate / ICollection.Remove compare
        // the array by reference), so removal stays instance-exact and the read path takes no lock.
        private readonly ConcurrentDictionary<string, Http2Connection[]> _connections
            = new ConcurrentDictionary<string, Http2Connection[]>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _initLocks
            = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        // Per key, the claim of the one request that currently dials an extra connection for a saturated
        // origin. TryAdd is the atomic claim: only its winner gets null from GetIfExists, every other
        // request queues on the least-queued connection, so a burst opens one extra TCP+TLS handshake at
        // a time instead of one per request. Removed instance-exact by ReleaseExtraDial.
        private readonly ConcurrentDictionary<string, ExtraDialClaim> _extraDials
            = new ConcurrentDictionary<string, ExtraDialClaim>(StringComparer.OrdinalIgnoreCase);

        // Per key, the pending dial of the first request to a cold origin (no connection accepts streams,
        // #736). TryAdd is the atomic claim: its winner dials, every later request for the key awaits the
        // claim's completion instead of dialing its own TCP+TLS connection (RFC 9113 Section 9.1), then
        // looks for a connection again (HTTP/2 published) or dials itself (the dial failed or negotiated
        // another protocol). Removed instance-exact by CompleteColdDial; completed by Dispose too.
        private readonly ConcurrentDictionary<string, ColdDialClaim> _coldDials
            = new ConcurrentDictionary<string, ColdDialClaim>(StringComparer.OrdinalIgnoreCase);

        // Keys whose last cold dial negotiated another protocol than h2 (HTTP/1.1 ALPN): their requests
        // skip the cold-dial gate, so an HTTP/1.1 origin keeps dialing in parallel. A publish for the key
        // removes it; the set is cleared once it holds MaxRememberedNonHttp2Origins keys.
        private readonly ConcurrentDictionary<string, byte> _nonHttp2Origins
            = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private const int MaxRememberedNonHttp2Origins = 256;

        // Connections evicted after GOAWAY that still drain in-flight streams (RFC 9113 Section 6.8).
        // Each closes itself once drained; the manager only keeps them to dispose them on its own Dispose.
        private readonly ConcurrentDictionary<Http2Connection, byte> _draining
            = new ConcurrentDictionary<Http2Connection, byte>();
        private int _disposed;

        public Http2ConnectionManager(Http2Options options, StreamingOptions streamingOptions)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _streamingOptions = streamingOptions ?? throw new ArgumentNullException(nameof(streamingOptions));
            _maxConnectionsPerOrigin = Math.Max(1, options.MaxConnectionsPerOrigin);
        }

        /// <summary>
        /// Get an existing alive h2 connection for this host:port, or null. Never claims an extra dial:
        /// null also when every connection is saturated and fewer than
        /// <see cref="Http2Options.MaxConnectionsPerOrigin"/> are open. Fast path — no async.
        /// </summary>
        public Http2Connection GetIfExists(string host, int port)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return null;

            var connection = SelectConnection($"{host}:{port}", out bool openAnother);
            return openAnother ? null : connection;
        }

        /// <summary>
        /// The connection a new request for this host:port should use, or null when the caller has to
        /// dial. When every connection is saturated and fewer than
        /// <see cref="Http2Options.MaxConnectionsPerOrigin"/> are open, only one caller at a time gets
        /// null together with <paramref name="extraDial"/>; it owns the claim and returns it through
        /// <see cref="ReleaseExtraDial"/> once its dial ended (published, lost, failed or cancelled).
        /// The others get the least-queued connection. Fast path — no async.
        /// </summary>
        public Http2Connection GetIfExists(string host, int port, out ExtraDialClaim extraDial)
        {
            return GetForRequest($"{host}:{port}", out extraDial);
        }

        /// <summary>
        /// Get an existing alive h2 connection for this origin through a CONNECT proxy tunnel, or null
        /// (as the direct overload). Fast path — no async.
        /// </summary>
        public Http2Connection GetIfExists(
            string originHost,
            int originPort,
            string proxyHost,
            int proxyPort)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return null;

            var connection = SelectConnection(
                BuildTunnelKey(originHost, originPort, proxyHost, proxyPort),
                out bool openAnother);
            return openAnother ? null : connection;
        }

        /// <summary>
        /// As <see cref="GetIfExists(string, int, out ExtraDialClaim)"/>, for an origin reached through a
        /// CONNECT proxy tunnel.
        /// </summary>
        public Http2Connection GetIfExists(
            string originHost,
            int originPort,
            string proxyHost,
            int proxyPort,
            out ExtraDialClaim extraDial)
        {
            return GetForRequest(BuildTunnelKey(originHost, originPort, proxyHost, proxyPort), out extraDial);
        }

        private Http2Connection GetForRequest(string key, out ExtraDialClaim extraDial)
        {
            extraDial = null;
            if (Volatile.Read(ref _disposed) != 0)
                return null;

            var connection = SelectConnection(key, out bool openAnother);
            if (!openAnother || _extraDials.ContainsKey(key))
                return connection;

            var claim = new ExtraDialClaim(key);
            if (!_extraDials.TryAdd(key, claim))
                return connection;

            extraDial = claim;
            return null;
        }

        /// <summary>
        /// Returns an extra-dial claim (idempotent: <paramref name="extraDial"/> is cleared, and removal
        /// is instance-exact, so a later claim of the same key is never released by mistake).
        /// </summary>
        public void ReleaseExtraDial(ref ExtraDialClaim extraDial)
        {
            var claim = extraDial;
            extraDial = null;
            if (claim != null)
            {
                ((ICollection<KeyValuePair<string, ExtraDialClaim>>)_extraDials).Remove(
                    new KeyValuePair<string, ExtraDialClaim>(claim.Key, claim));
            }
        }

        /// <summary>
        /// After the extra dial of <paramref name="extraDial"/> failed: releases the claim and returns the
        /// connection the request would have queued on without it (the least-queued one that still
        /// accepts streams), or null when none is left.
        /// </summary>
        public Http2Connection ReleaseExtraDialAndFallBack(ref ExtraDialClaim extraDial)
        {
            var claim = extraDial;
            ReleaseExtraDial(ref extraDial);
            if (claim == null || Volatile.Read(ref _disposed) != 0)
                return null;

            return SelectConnection(claim.Key, out _);
        }

        /// <summary>True when a connection for this host:port accepts new streams (saturated or not).</summary>
        internal bool HasConnection(string host, int port)
        {
            return GetAcceptingConnection(host, port) != null;
        }

        /// <summary>
        /// The connection a request for this host:port can still queue on after its own dial failed (the
        /// least-queued one that accepts streams, saturated or not), or null when none is left.
        /// </summary>
        internal Http2Connection GetAcceptingConnection(string host, int port)
        {
            return GetAcceptingConnection($"{host}:{port}");
        }

        /// <summary>
        /// Tunnel-keyed <see cref="GetAcceptingConnection(string, int)"/>: the connection through this
        /// proxy to this origin a request can still queue on after its own tunnel failed, or null.
        /// </summary>
        internal Http2Connection GetAcceptingConnection(
            string originHost,
            int originPort,
            string proxyHost,
            int proxyPort)
        {
            return GetAcceptingConnection(BuildTunnelKey(originHost, originPort, proxyHost, proxyPort));
        }

        private Http2Connection GetAcceptingConnection(string key)
        {
            return Volatile.Read(ref _disposed) == 0 ? SelectConnection(key, out _) : null;
        }

        /// <summary>
        /// For a request that found no connection for this host:port and holds no extra-dial claim, i.e.
        /// the origin is cold (#736). Returns null with <paramref name="coldDial"/> set when the caller is
        /// the first and dials: it owns the claim and completes it through <see cref="CompleteColdDial"/>
        /// on every path. Returns null without a claim when the origin last negotiated another protocol
        /// than h2 (an HTTP/1.1 origin dials in parallel) or the manager is disposed: the caller dials.
        /// Otherwise returns the first caller's pending dial (await it with
        /// <see cref="WaitForColdDialAsync"/>): true when the caller should look for a connection again
        /// (the dial yielded HTTP/2, or its owner was cancelled), false when it should dial itself (the
        /// dial failed or negotiated another protocol). Fast path — no async.
        /// </summary>
        public Task<bool> ClaimOrJoinColdDial(string host, int port, out ColdDialClaim coldDial)
        {
            return ClaimOrJoinColdDial($"{host}:{port}", out coldDial);
        }

        /// <summary>
        /// Tunnel-keyed <see cref="ClaimOrJoinColdDial(string, int, out ColdDialClaim)"/> (#1180): the
        /// first cold request for this origin through this proxy establishes the CONNECT tunnel, the
        /// others await it. Same contract; the claim is completed through <see cref="CompleteColdDial"/>.
        /// </summary>
        public Task<bool> ClaimOrJoinColdDial(
            string originHost,
            int originPort,
            string proxyHost,
            int proxyPort,
            out ColdDialClaim coldDial)
        {
            return ClaimOrJoinColdDial(
                BuildTunnelKey(originHost, originPort, proxyHost, proxyPort),
                out coldDial);
        }

        private Task<bool> ClaimOrJoinColdDial(string key, out ColdDialClaim coldDial)
        {
            coldDial = null;
            if (Volatile.Read(ref _disposed) != 0)
                return null;

            if (_nonHttp2Origins.ContainsKey(key))
                return null;

            while (true)
            {
                if (_coldDials.TryGetValue(key, out var pending))
                    return pending.Completion;

                var claim = new ColdDialClaim(key);
                if (!_coldDials.TryAdd(key, claim))
                    continue;

                // Dispose may have completed the pending dials before this TryAdd: never leave a claim
                // that nothing completes.
                if (Volatile.Read(ref _disposed) != 0)
                {
                    CompleteColdDial(ref claim, ColdDialOutcome.Failed);
                    return null;
                }

                coldDial = claim;
                return null;
            }
        }

        /// <summary>
        /// Completes a cold-dial claim with the outcome of its dial and wakes the requests awaiting it
        /// (idempotent: <paramref name="coldDial"/> is cleared, and removal is instance-exact, so a later
        /// claim of the same key is never completed by mistake).
        /// </summary>
        public void CompleteColdDial(ref ColdDialClaim coldDial, ColdDialOutcome outcome)
        {
            var claim = coldDial;
            coldDial = null;
            if (claim == null)
                return;

            ((ICollection<KeyValuePair<string, ColdDialClaim>>)_coldDials).Remove(
                new KeyValuePair<string, ColdDialClaim>(claim.Key, claim));

            if (outcome == ColdDialOutcome.NotHttp2 && Volatile.Read(ref _disposed) == 0)
            {
                if (_nonHttp2Origins.Count >= MaxRememberedNonHttp2Origins)
                    _nonHttp2Origins.Clear();
                _nonHttp2Origins[claim.Key] = 0;
            }

            claim.Complete(outcome == ColdDialOutcome.Http2 || outcome == ColdDialOutcome.Abandoned);
        }

        /// <summary>
        /// Awaits a pending cold dial returned by
        /// <see cref="ClaimOrJoinColdDial(string, int, out ColdDialClaim)"/>; throws
        /// <see cref="OperationCanceledException"/> as soon as <paramref name="ct"/> fires (the request
        /// timeout is enforced by transport cancellation). The cancellation registration is released on
        /// every path; the pending dial itself never faults.
        /// </summary>
        internal static async Task<bool> WaitForColdDialAsync(Task<bool> pendingDial, CancellationToken ct)
        {
            if (!ct.CanBeCanceled || pendingDial.IsCompleted)
                return await pendingDial.ConfigureAwait(false);

            var cancelSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(static state => ((TaskCompletionSource<bool>)state).TrySetResult(true), cancelSignal))
            {
                var completed = await Task.WhenAny(pendingDial, cancelSignal.Task).ConfigureAwait(false);
                if (!ReferenceEquals(completed, pendingDial))
                    throw new OperationCanceledException(ct);
            }

            return await pendingDial.ConfigureAwait(false);
        }

        internal bool HasConnection(
            string originHost,
            int originPort,
            string proxyHost,
            int proxyPort)
        {
            return GetAcceptingConnection(originHost, originPort, proxyHost, proxyPort) != null;
        }

        /// <summary>
        /// The connection a new request for <paramref name="key"/> should use, or null when none accepts
        /// new streams. With one connection allowed, the first that accepts streams. Otherwise the first
        /// with a free stream slot; when all are saturated, the least-queued one, with
        /// <paramref name="openAnother"/> set while fewer than the maximum are open. Connections that no
        /// longer accept streams are evicted on the way, so stale entries do not accumulate in long-idle
        /// sessions (a failed one is disposed, a GOAWAY'd one keeps draining its in-flight streams).
        /// </summary>
        private Http2Connection SelectConnection(string key, out bool openAnother)
        {
            openAnother = false;
            if (!_connections.TryGetValue(key, out var snapshot))
                return null;

            Http2Connection leastQueued = null;
            int leastQueuedCount = 0;
            int accepting = 0;
            for (int i = 0; i < snapshot.Length; i++)
            {
                var connection = snapshot[i];
                if (!connection.AcceptsNewStreams)
                {
                    RemoveExactAndRetire(key, connection);
                    continue;
                }

                if (_maxConnectionsPerOrigin == 1 || connection.HasFreeStreamSlot)
                    return connection;

                accepting++;
                int queued = connection.QueuedAdmissionCount;
                if (leastQueued == null || queued < leastQueuedCount)
                {
                    leastQueued = connection;
                    leastQueuedCount = queued;
                }
            }

            openAnother = leastQueued != null && accepting < _maxConnectionsPerOrigin;
            return leastQueued;
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

            // Fast path: an existing connection that can take the request
            var existing = SelectConnection(key, out bool openAnother);
            if (existing != null && !openAnother)
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

            // Fast path: an existing connection that can take the request
            var existing = SelectConnection(key, out bool openAnother);
            if (existing != null && !openAnother)
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
                    // Double-check after acquiring lock (stale entries are evicted instance-exact; a
                    // draining one finishes its streams).
                    existing = SelectConnection(key, out bool openAnother);
                    if (existing != null && !openAnother)
                    {
                        tlsStream.Dispose();
                        tlsStream = null;
                        return existing;
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
                    newConnection.ManagerKey = key;

                    // The init lock is dropped from _initLocks after a publish, so a caller still queued
                    // on the old semaphore and one on a new semaphore can both get here: the cap is
                    // enforced by the publish itself, and the loser uses an existing connection.
                    while (!TryPublish(key, newConnection))
                    {
                        existing = SelectConnection(key, out _);
                        if (existing != null)
                        {
                            newConnection.Dispose();
                            newConnection = null;
                            return existing;
                        }
                    }

                    published = true;
                    _nonHttp2Origins.TryRemove(key, out _);
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
        /// Appends <paramref name="connection"/> to the entry for <paramref name="key"/> unless that would
        /// exceed <see cref="Http2Options.MaxConnectionsPerOrigin"/> connections accepting new streams.
        /// </summary>
        private bool TryPublish(string key, Http2Connection connection)
        {
            while (true)
            {
                if (!_connections.TryGetValue(key, out var current))
                {
                    if (_connections.TryAdd(key, new[] { connection }))
                        return true;

                    continue;
                }

                int accepting = 0;
                for (int i = 0; i < current.Length; i++)
                {
                    if (current[i].AcceptsNewStreams)
                        accepting++;
                }

                if (accepting >= _maxConnectionsPerOrigin)
                    return false;

                var next = new Http2Connection[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = connection;
                if (_connections.TryUpdate(key, next, current))
                    return true;
            }
        }

        /// <summary>
        /// Evicts <paramref name="connection"/> after a failed dispatch when it no longer accepts new
        /// streams. Instance-exact: a newer connection published under the same key is never touched.
        /// A connection that still works (the failure was stream-scoped, RFC 9113 Section 5.4.2) stays.
        /// </summary>
        internal void EvictIfUnusable(Http2Connection connection)
        {
            if (connection == null || connection.AcceptsNewStreams)
                return;

            string key = connection.ManagerKey;
            if (key != null)
                RemoveExactAndRetire(key, connection);
            else if (!connection.IsAlive)
                connection.Dispose();
        }

        /// <summary>
        /// Remove and dispose whatever connections are registered for this host:port (key-based; tests and
        /// diagnostics only, dispatch failures use <see cref="EvictIfUnusable"/>).
        /// </summary>
        public void Remove(string host, int port)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            string key = $"{host}:{port}";
            if (_connections.TryRemove(key, out var connections))
            {
                foreach (var conn in connections)
                    conn.Dispose();
            }
        }

        /// <summary>
        /// Remove and dispose whatever tunneled connections are registered for this key (key-based; tests
        /// and diagnostics only, dispatch failures use <see cref="EvictIfUnusable"/>).
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
            if (_connections.TryRemove(key, out var connections))
            {
                foreach (var conn in connections)
                    conn.Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            foreach (var kvp in _connections)
            {
                foreach (var connection in kvp.Value)
                    connection.Dispose();
            }
            _connections.Clear();

            foreach (var kvp in _draining)
            {
                kvp.Key.Dispose();
            }
            _draining.Clear();

            foreach (var kvp in _initLocks)
            {
                kvp.Value.Dispose();
            }
            _initLocks.Clear();
            _extraDials.Clear();

            // Wake the requests awaiting a cold dial: they look again, find the manager disposed and fail
            // on their own dial. The claim owners still complete their (removed) claims, idempotently.
            foreach (var kvp in _coldDials)
                kvp.Value.Complete(true);
            _coldDials.Clear();
            _nonHttp2Origins.Clear();
        }

        private static string BuildTunnelKey(
            string originHost,
            int originPort,
            string proxyHost,
            int proxyPort)
        {
            return $"{originHost}:{originPort}|via|{proxyHost}:{proxyPort}";
        }

        /// <summary>
        /// Removes <paramref name="connection"/> only while it is still in the entry for
        /// <paramref name="key"/>, then disposes it, or, when it is draining after GOAWAY, leaves it to
        /// close itself once its in-flight streams are done.
        /// </summary>
        private void RemoveExactAndRetire(string key, Http2Connection connection)
        {
            if (!TryRemoveExact(key, connection))
                return;

            if (!connection.IsAlive || Volatile.Read(ref _disposed) != 0)
            {
                connection.Dispose();
                return;
            }

            // Prune drained or failed connections, so the set only holds connections still draining. A
            // pruned connection is disposed (idempotent): it may have failed without closing its
            // transport stream yet (#694).
            foreach (var kvp in _draining)
            {
                if (!kvp.Key.IsAlive && _draining.TryRemove(kvp.Key, out _))
                    kvp.Key.Dispose();
            }

            _draining[connection] = 0;

            // Raced with Dispose(): it may have cleared the set before the add above.
            if (Volatile.Read(ref _disposed) != 0 && _draining.TryRemove(connection, out _))
                connection.Dispose();
        }

        /// <summary>
        /// Removes exactly <paramref name="connection"/> from the entry for <paramref name="key"/>; false
        /// when another caller removed it first. Only one caller can win, so it is retired once.
        /// </summary>
        private bool TryRemoveExact(string key, Http2Connection connection)
        {
            while (true)
            {
                if (!_connections.TryGetValue(key, out var current))
                    return false;

                int index = -1;
                for (int i = 0; i < current.Length; i++)
                {
                    if (ReferenceEquals(current[i], connection))
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                    return false;

                if (current.Length == 1)
                {
                    var entry = new KeyValuePair<string, Http2Connection[]>(key, current);
                    if (((ICollection<KeyValuePair<string, Http2Connection[]>>)_connections).Remove(entry))
                        return true;

                    continue;
                }

                var next = new Http2Connection[current.Length - 1];
                Array.Copy(current, 0, next, 0, index);
                Array.Copy(current, index + 1, next, index, current.Length - index - 1);
                if (_connections.TryUpdate(key, next, current))
                    return true;
            }
        }

        /// <summary>
        /// The right to dial one extra connection for a saturated origin; see
        /// <see cref="GetIfExists(string, int, out ExtraDialClaim)"/>.
        /// </summary>
        internal sealed class ExtraDialClaim
        {
            internal ExtraDialClaim(string key)
            {
                Key = key;
            }

            internal string Key { get; }
        }

        /// <summary>How the dial of a <see cref="ColdDialClaim"/> ended.</summary>
        internal enum ColdDialOutcome
        {
            /// <summary>An HTTP/2 connection was published (or found): waiters look again.</summary>
            Http2,

            /// <summary>The dial negotiated another protocol (HTTP/1.1): waiters dial themselves.</summary>
            NotHttp2,

            /// <summary>The dial failed: waiters dial themselves (RFC 9113 Section 9.1 still applies).</summary>
            Failed,

            /// <summary>The owner was cancelled or timed out: waiters look again, one claims a new dial.</summary>
            Abandoned
        }

        /// <summary>
        /// The right to dial the first connection of a cold origin (or tunnel key); see
        /// <see cref="ClaimOrJoinColdDial(string, int, out ColdDialClaim)"/>.
        /// </summary>
        internal sealed class ColdDialClaim
        {
            private readonly TaskCompletionSource<bool> _completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal ColdDialClaim(string key)
            {
                Key = key;
            }

            internal string Key { get; }

            /// <summary>True: look for a connection again; false: dial yourself.</summary>
            internal Task<bool> Completion => _completion.Task;

            internal void Complete(bool lookAgain)
            {
                _completion.TrySetResult(lookAgain);
            }
        }
    }
}
