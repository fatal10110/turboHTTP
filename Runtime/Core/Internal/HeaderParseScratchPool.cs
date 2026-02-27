using System.Collections.Generic;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// Scratch accumulator for HTTP/1.1 header parsing. Holds the raw
    /// name-value pairs collected during a single response parse before they are
    /// transferred into a permanent <see cref="TurboHTTP.Core.HttpHeaders"/> instance.
    /// </summary>
    /// <remarks>
    /// Using a pooled scratch list instead of building <see cref="TurboHTTP.Core.HttpHeaders"/>
    /// directly reduces per-request list allocations: the <c>List&lt;(string, string)&gt;</c>
    /// itself is reused across requests. The backing array inside the list grows to fit the
    /// largest header set seen and is retained; typical HTTP responses have 8-20 headers.
    /// Pre-allocated capacity of 16 covers most responses without a resize.
    /// </remarks>
    internal sealed class HeaderParseScratch
    {
        /// <summary>
        /// Raw header pairs accumulated during parsing (insertion order preserved).
        /// Cleared by <see cref="Reset"/> before each reuse.
        /// </summary>
        public List<(string Name, string Value)> RawHeaders { get; }
            = new List<(string Name, string Value)>(16);

        /// <summary>Clears accumulated headers. Called by <see cref="HeaderParseScratchPool"/> before storage.</summary>
        public void Reset() => RawHeaders.Clear();
    }

    /// <summary>
    /// Bounded pool of <see cref="HeaderParseScratch"/> instances.
    /// </summary>
    internal static class HeaderParseScratchPool
    {
        private static readonly ObjectPool<HeaderParseScratch> s_pool = new ObjectPool<HeaderParseScratch>(
            factory: () => new HeaderParseScratch(),
            capacity: 16,
            reset: s => s.Reset());

        public static HeaderParseScratch Rent() => s_pool.Rent();

        public static void Return(HeaderParseScratch scratch) => s_pool.Return(scratch);
    }
}
