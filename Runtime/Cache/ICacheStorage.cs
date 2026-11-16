using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Cache
{
    /// <summary>
    /// Storage backend for <see cref="CacheInterceptor"/> (see <see cref="CachePolicy.Storage"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Thread safety:</b> the interceptor calls the methods concurrently from several requests, so
    /// implementations must be thread-safe.</para>
    /// <para><b>Ownership:</b> a <see cref="CacheEntry"/> passed to <see cref="SetAsync"/> or returned from
    /// <see cref="GetAsync"/> must not be shared mutably with the other side. Clone with
    /// <see cref="CacheEntry.Clone"/> on the way in and out (<see cref="MemoryCacheStorage"/> does both), or
    /// store immutable data. A storage the caller passes in is owned by the caller; the interceptor disposes
    /// only a storage it created.</para>
    /// <para><b>Expiry:</b> the storage does not evict by freshness. A stale entry that has a validator is kept for
    /// conditional revalidation (RFC 9111 section 4.3); the interceptor decides freshness and removes entries it can
    /// no longer use. Storages evict only to honour their own bounds.</para>
    /// </remarks>
    public interface ICacheStorage
    {
        /// <summary>
        /// Gets an entry by key. Returns null when key is missing.
        /// Freshness and revalidation decisions belong to <see cref="CacheInterceptor"/>, so an expired entry is
        /// still returned.
        /// </summary>
        Task<CacheEntry> GetAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Stores or replaces an entry for the specified key.
        /// </summary>
        Task SetAsync(string key, CacheEntry entry, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes an entry for the specified key.
        /// </summary>
        Task RemoveAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Clears all cache entries.
        /// </summary>
        Task ClearAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the current entry count.
        /// </summary>
        Task<int> GetCountAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets total estimated cache size in bytes.
        /// </summary>
        Task<long> GetSizeAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Forward-compatibility note: stream-oriented caches can be introduced later via
    /// an adapter interface without breaking this v1 byte[] snapshot contract.
    /// </summary>
    internal static class CacheStorageCompatibilityNotes
    {
    }
}
