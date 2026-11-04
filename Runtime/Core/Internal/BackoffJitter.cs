using System;
using System.Threading;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// Shared source of backoff jitter for retry and reconnect policies.
    /// <para>Each thread owns its own <see cref="Random"/> (<see cref="ThreadStaticAttribute"/>), so
    /// no lock is taken per call. Seeds combine a process-wide <see cref="Interlocked"/> counter
    /// with a GUID-derived value, so threads created on the same tick (or processes started on the
    /// same tick) do not share a sequence.</para>
    /// </summary>
    internal static class BackoffJitter
    {
        private static int _seedCounter;

        [ThreadStatic]
        private static Random _threadRng;

        /// <summary>
        /// Returns a uniformly distributed value in <c>[0, 1)</c> from the calling thread's generator.
        /// </summary>
        internal static double NextDouble()
        {
            var rng = _threadRng;
            if (rng == null)
            {
                rng = new Random(CreateSeed());
                _threadRng = rng;
            }

            return rng.NextDouble();
        }

        private static int CreateSeed()
        {
            int counter = Interlocked.Increment(ref _seedCounter);
            int entropy = Guid.NewGuid().GetHashCode();
            unchecked
            {
                return entropy ^ (counter * (int)0x9E3779B9) ^ Environment.CurrentManagedThreadId;
            }
        }
    }
}
