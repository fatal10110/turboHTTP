using System;
using System.Buffers;
using System.Threading;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// Diagnostics snapshot for <see cref="ByteArrayPool"/>.
    /// </summary>
    public readonly struct ByteArrayPoolDiagnostics
    {
        /// <summary>Total number of <see cref="ByteArrayPool.Rent"/> calls since last reset.</summary>
        public readonly long RentCount;
        /// <summary>Total number of <see cref="ByteArrayPool.Return"/> calls since last reset.</summary>
        public readonly long ReturnCount;
        /// <summary>Total number of returns with <c>clearArray = true</c> since last reset.</summary>
        public readonly long ClearOnReturnCount;
        /// <summary>Estimated number of arrays currently outstanding (RentCount - ReturnCount).</summary>
        public readonly long ActiveCount;

        internal ByteArrayPoolDiagnostics(long rent, long ret, long clear)
        {
            RentCount = rent;
            ReturnCount = ret;
            ClearOnReturnCount = clear;
            ActiveCount = rent - ret;
        }

        public override string ToString() =>
            $"ByteArrayPool — Rent={RentCount}, Return={ReturnCount}, Active={ActiveCount}, ClearOnReturn={ClearOnReturnCount}";
    }

    /// <summary>
    /// Static facade over <see cref="ArrayPool{T}"/> for byte arrays.
    /// Provides bounded, thread-safe byte array pooling with optional
    /// clear-on-return for security-sensitive buffers.
    /// </summary>
    public static class ByteArrayPool
    {
        private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

        private static long _rentCount;
        private static long _returnCount;
        private static long _clearOnReturnCount;

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

            Interlocked.Increment(ref _rentCount);
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

            Interlocked.Increment(ref _returnCount);
            if (clearArray)
                Interlocked.Increment(ref _clearOnReturnCount);

            Pool.Return(array, clearArray);
        }

        /// <summary>
        /// Returns a diagnostics snapshot of this pool's activity counters.
        /// All counters are read atomically (individual fields, not as a group).
        /// </summary>
        public static ByteArrayPoolDiagnostics GetDiagnostics() =>
            new ByteArrayPoolDiagnostics(
                Interlocked.Read(ref _rentCount),
                Interlocked.Read(ref _returnCount),
                Interlocked.Read(ref _clearOnReturnCount));

        /// <summary>
        /// Resets all diagnostic counters to zero.
        /// Use for benchmark or test isolation only.
        /// </summary>
        public static void ResetDiagnostics()
        {
            Interlocked.Exchange(ref _rentCount, 0);
            Interlocked.Exchange(ref _returnCount, 0);
            Interlocked.Exchange(ref _clearOnReturnCount, 0);
        }
    }
}
