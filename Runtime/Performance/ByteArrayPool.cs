using System;
using System.Buffers;

namespace TurboHTTP.Performance
{
    /// <summary>
    /// Static facade over <see cref="ArrayPool{T}"/> for byte arrays.
    /// Provides bounded, thread-safe byte array pooling with optional
    /// clear-on-return for security-sensitive buffers.
    /// </summary>
    public static class ByteArrayPool
    {
        private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

        /// <summary>
        /// Rent a byte array of at least the specified length.
        /// The returned array may be larger than requested.
        /// Contents are NOT guaranteed to be zeroed.
        /// A request of length 0 returns <see cref="Array.Empty{T}"/> and should not
        /// be returned to the pool.
        /// </summary>
        /// <param name="minimumLength">Minimum required length. Must be non-negative.</param>
        /// <returns>A pooled or newly allocated byte array.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumLength"/> is negative.</exception>
        public static byte[] Rent(int minimumLength)
        {
            if (minimumLength < 0)
                throw new ArgumentOutOfRangeException(nameof(minimumLength), minimumLength, "Must be non-negative.");

            if (minimumLength == 0)
                return Array.Empty<byte>();

            return Pool.Rent(minimumLength);
        }

        /// <summary>
        /// Return a byte array to the pool.
        /// </summary>
        /// <param name="array">The array to return. Null arrays are silently ignored.</param>
        /// <param name="clearArray">If true, the array is zeroed before returning to the pool.
        /// Use this for buffers that contained sensitive data (tokens, credentials, etc.).</param>
        public static void Return(byte[] array, bool clearArray = false)
        {
            if (array == null || array.Length == 0)
                return;

            Pool.Return(array, clearArray);
        }
    }
}
