using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Cache
{
    /// <summary>
    /// Thread-safe in-memory cache with bounded size and LRU eviction.
    /// </summary>
    public sealed class MemoryCacheStorage : ICacheStorage, IDisposable
    {
        // Deterministic fixed overhead estimate per entry for object graph metadata.
        internal const int FixedMetadataBytesPerEntry = 1024;

        private readonly Dictionary<string, CacheSlot> _entries = new Dictionary<string, CacheSlot>(StringComparer.Ordinal);
        private readonly LinkedList<string> _lruKeys = new LinkedList<string>();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private readonly int _maxEntries;
        private readonly long _maxSizeBytes;
        private long _currentSizeBytes;
        private int _disposed;

        private sealed class CacheSlot
        {
            public CacheEntry Entry;
            public LinkedListNode<string> LruNode;
            public long SizeBytes;
        }

        public MemoryCacheStorage(int maxEntries = 100, long maxSizeBytes = 10 * 1024 * 1024)
        {
            if (maxEntries <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "Must be greater than 0.");
            if (maxSizeBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSizeBytes), maxSizeBytes, "Must be greater than 0.");

            _maxEntries = maxEntries;
            _maxSizeBytes = maxSizeBytes;
        }

        public async Task<CacheEntry> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Cache key cannot be null or empty.", nameof(key));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_entries.TryGetValue(key, out var slot))
                    return null;

                if (slot.Entry.IsExpired(DateTime.UtcNow))
                {
                    RemoveSlotUnsafe(key, slot);
                    return null;
                }

                TouchUnsafe(slot);
                return slot.Entry.Clone();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SetAsync(string key, CacheEntry entry, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Cache key cannot be null or empty.", nameof(key));
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            var normalized = EnsureKey(entry, key);
            var entrySizeBytes = EstimateEntrySizeBytes(normalized);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_entries.TryGetValue(key, out var existing))
                    RemoveSlotUnsafe(key, existing);

                RemoveExpiredEntriesUnsafe(DateTime.UtcNow);

                // Cannot fit this entry under current constraints.
                if (entrySizeBytes > _maxSizeBytes)
                    return;

                while (_entries.Count >= _maxEntries || _currentSizeBytes + entrySizeBytes > _maxSizeBytes)
                {
                    if (!EvictOneUnsafe())
                        break;
                }

                var node = new LinkedListNode<string>(key);
                _lruKeys.AddFirst(node);

                _entries[key] = new CacheSlot
                {
                    Entry = normalized,
                    LruNode = node,
                    SizeBytes = entrySizeBytes
                };

                _currentSizeBytes += entrySizeBytes;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Cache key cannot be null or empty.", nameof(key));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_entries.TryGetValue(key, out var slot))
                    RemoveSlotUnsafe(key, slot);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task ClearAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _entries.Clear();
                _lruKeys.Clear();
                _currentSizeBytes = 0;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<int> GetCountAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                RemoveExpiredEntriesUnsafe(DateTime.UtcNow);
                return _entries.Count;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                RemoveExpiredEntriesUnsafe(DateTime.UtcNow);
                return _currentSizeBytes;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _gate.Dispose();
        }

        private static CacheEntry EnsureKey(CacheEntry entry, string key)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                return entry.Clone();

            return new CacheEntry(
                key: key,
                statusCode: entry.StatusCode,
                headers: entry.Headers,
                body: entry.Body,
                cachedAtUtc: entry.CachedAtUtc,
                expiresAtUtc: entry.ExpiresAtUtc,
                eTag: entry.ETag,
                lastModified: entry.LastModified,
                responseUrl: entry.ResponseUrl,
                varyHeaders: entry.VaryHeaders,
                varyKey: entry.VaryKey,
                mustRevalidate: entry.MustRevalidate);
        }

        internal static long EstimateEntrySizeBytes(CacheEntry entry)
        {
            long bodyBytes = entry.BodyLength;
            long headerBytes = EstimateHeaderBytes(entry.Headers);
            return bodyBytes + headerBytes + FixedMetadataBytesPerEntry;
        }

        private static long EstimateHeaderBytes(HttpHeaders headers)
        {
            if (headers == null || headers.Count == 0)
                return 0;

            long bytes = 0;
            foreach (var name in headers.Names)
            {
                bytes += Encoding.UTF8.GetByteCount(name);
                var values = headers.GetValues(name);
                for (int i = 0; i < values.Count; i++)
                {
                    bytes += 2; // ": "
                    bytes += Encoding.UTF8.GetByteCount(values[i] ?? string.Empty);
                    bytes += 2; // CRLF
                }
            }

            return bytes;
        }

        private bool EvictOneUnsafe()
        {
            var lruNode = _lruKeys.Last;
            if (lruNode == null)
                return false;

            var key = lruNode.Value;
            if (_entries.TryGetValue(key, out var slot))
                RemoveSlotUnsafe(key, slot);
            else
                _lruKeys.RemoveLast();

            return true;
        }

        private void RemoveExpiredEntriesUnsafe(DateTime utcNow)
        {
            var node = _lruKeys.Last;
            while (node != null)
            {
                var previous = node.Previous;
                var key = node.Value;
                if (_entries.TryGetValue(key, out var slot) && slot.Entry.IsExpired(utcNow))
                    RemoveSlotUnsafe(key, slot);
                node = previous;
            }
        }

        private void RemoveSlotUnsafe(string key, CacheSlot slot)
        {
            _entries.Remove(key);
            if (slot?.LruNode != null)
                _lruKeys.Remove(slot.LruNode);

            _currentSizeBytes -= slot?.SizeBytes ?? 0;
            if (_currentSizeBytes < 0)
                _currentSizeBytes = 0;
        }

        private void TouchUnsafe(CacheSlot slot)
        {
            if (slot?.LruNode == null || slot.LruNode.List == null)
                return;

            _lruKeys.Remove(slot.LruNode);
            _lruKeys.AddFirst(slot.LruNode);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(MemoryCacheStorage));
        }
    }
}
