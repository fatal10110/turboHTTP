using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>Source of a streaming response body, implemented by transports. The consumer owns it and disposes it once; reads are sequential.</summary>
    public interface IResponseBodySource : IAsyncDisposable
    {
        /// <summary>Declared body length when known, else <c>null</c>.</summary>
        long? Length { get; }

        /// <summary>Returns the whole body as one contiguous buffer when it is already fully received; <c>false</c> otherwise.</summary>
        bool TryGetBufferedData(out ReadOnlyMemory<byte> data);

        /// <summary>
        /// Transfers a fully received body to the caller without copying. A detached body carries no
        /// trailers, so implementations must return <c>false</c> while the body is incomplete or when
        /// the message has a non-empty trailer section; the caller then reads the body and
        /// <see cref="GetTrailersAsync"/>.
        /// </summary>
        bool TryDetachBufferedBody(out DetachedBufferedBody body);

        // Body and trailer failures after response start propagate from these methods.
        /// <summary>Reads up to <paramref name="destination"/>.Length bytes; returns 0 at the end of the body.</summary>
        ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct);

        /// <summary>Discards the rest of the body so the connection can be reused, when that is cheap.</summary>
        ValueTask DrainAsync(CancellationToken ct);

        /// <summary>Abandons the body and releases the transport resources without reading further. Idempotent.</summary>
        void Abort();

        /// <summary>Returns the trailer fields once the body has ended; empty when there are none.</summary>
        ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct);
    }
}
