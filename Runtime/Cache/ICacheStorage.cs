using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Cache
{
    /// <summary>
    /// Cache storage abstraction used by cache middleware.
    /// Implementations must be thread-safe.
    /// </summary>
    public interface ICacheStorage
    {
        /// <summary>
        /// Gets an entry by key. Returns null when key is missing.
        /// Expiry/revalidation decisions are handled by cache middleware.
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
