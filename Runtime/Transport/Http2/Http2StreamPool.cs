using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// Bounded pool of <see cref="Http2Stream"/> instances. Pooled streams hold no header-block
    /// buffer: encoded field blocks accumulate in a connection-level buffer owned by the read loop
    /// (rented per block and returned at END_HEADERS), so an idle slot never retains a grown buffer.
    /// </summary>
    /// <remarks>
    /// Pool capacity (128) provides headroom above the HTTP/2 default
    /// <c>SETTINGS_MAX_CONCURRENT_STREAMS</c> limit (100). Memory per idle slot is
    /// minimal: the stream object fields (~200 bytes).
    /// </remarks>
    internal static class Http2StreamPool
    {
        private static readonly ObjectPool<Http2Stream> s_pool = new ObjectPool<Http2Stream>(
            factory: () => new Http2Stream(),
            capacity: 128,
            reset: s => s.PrepareForPool());

        /// <summary>
        /// Rents a stream from the pool and initialises it with the supplied per-request state.
        /// </summary>
        public static Http2Stream Rent(
            int streamId,
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            int initialSendWindowSize,
            int initialRecvWindowSize,
            Http2Connection connection)
        {
            var stream = s_pool.Rent();
            stream.Initialize(streamId, request, handler, context,
                initialSendWindowSize, initialRecvWindowSize, connection);
            return stream;
        }

        /// <summary>
        /// Returns a stream to the pool. The pool's reset delegate calls
        /// <see cref="Http2Stream.PrepareForPool"/> which clears per-request references
        /// and returns the pooled body buffer (if any) to <see cref="System.Buffers.ArrayPool{T}"/>.
        /// </summary>
        public static void Return(Http2Stream stream) => s_pool.Return(stream);
    }
}
