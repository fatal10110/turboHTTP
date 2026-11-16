using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Pull-based source of a response body for <see cref="UHttpStreamingResponse"/>. Transports implement it; callers normally use
    /// <see cref="UHttpStreamingResponse.Body"/>. The source is owned by the response and disposed with it.
    /// </summary>
    public interface IResponseBodySource : IAsyncDisposable
    {
        /// <summary>
        /// Gets the body length in bytes when known up front (for example from <c>Content-Length</c>), otherwise null.
        /// </summary>
        long? Length { get; }

        /// <summary>
        /// Returns the whole body without copying when it is already fully received and held in one contiguous buffer.
        /// Returns <c>false</c> when the body is incomplete or segmented.
        /// </summary>
        /// <param name="data">The buffered body when this returns <c>true</c>.</param>
        bool TryGetBufferedData(out ReadOnlyMemory<byte> data);

        /// <summary>
        /// Transfers a fully received body to the caller without copying. A detached body carries no
        /// trailers, so implementations must return <c>false</c> while the body is incomplete or when
        /// the message has a non-empty trailer section; the caller then reads the body and
        /// <see cref="GetTrailersAsync"/>.
        /// </summary>
        bool TryDetachBufferedBody(out DetachedBufferedBody body);

        // Body and trailer failures after response start propagate from these methods.
        /// <summary>
        /// Reads body bytes into <paramref name="destination"/>. Returns 0 at the end of the body.
        /// Body failures after response start propagate from this method.
        /// </summary>
        /// <param name="destination">Receives the bytes.</param>
        /// <param name="ct">Cancels the read.</param>
        /// <returns>The number of bytes read, or 0 at the end of the body.</returns>
        ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct);

        /// <summary>
        /// Reads and discards the rest of the body so the connection can be reused.
        /// </summary>
        /// <param name="ct">Cancels the drain.</param>
        ValueTask DrainAsync(CancellationToken ct);

        /// <summary>
        /// Abandons the body without reading it; the underlying connection or stream is not reused.
        /// </summary>
        void Abort();

        /// <summary>
        /// Gets the trailer fields (RFC 9110 section 6.5) once the body has been fully read; empty when the message has none.
        /// </summary>
        /// <param name="ct">Cancels the wait.</param>
        ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct);
    }
}
