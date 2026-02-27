using TurboHTTP.Core.Internal;

namespace TurboHTTP.Transport.Http1
{
    /// <summary>
    /// Bounded pool of <see cref="ParsedResponse"/> scratch objects used to carry
    /// HTTP/1.1 parsing results from <see cref="Http11ResponseParser"/> to the transport
    /// layer. Instances are rented for the duration of a single response parse and
    /// returned immediately after the data is transferred to <see cref="TurboHTTP.Core.UHttpResponse"/>.
    /// </summary>
    /// <remarks>
    /// Pool capacity is set to 16 — enough headroom for high-concurrency HTTP/1.1 pipelines
    /// without excessive idle memory. On pool miss a fresh instance is allocated; on pool-full
    /// return the item is silently discarded and GC'd.
    /// </remarks>
    internal static class ParsedResponsePool
    {
        private static readonly ObjectPool<ParsedResponse> s_pool = new ObjectPool<ParsedResponse>(
            factory: () => new ParsedResponse(),
            capacity: 16,
            reset: r => r.Reset());

        public static ParsedResponse Rent() => s_pool.Rent();

        public static void Return(ParsedResponse response) => s_pool.Return(response);
    }
}
