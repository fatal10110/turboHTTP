using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    public interface IResponseBodySource : IAsyncDisposable
    {
        long? Length { get; }

        bool TryGetBufferedData(out ReadOnlyMemory<byte> data);

        /// <summary>
        /// Transfers a fully received body to the caller without copying. A detached body carries no
        /// trailers, so implementations must return <c>false</c> while the body is incomplete or when
        /// the message has a non-empty trailer section; the caller then reads the body and
        /// <see cref="GetTrailersAsync"/>.
        /// </summary>
        bool TryDetachBufferedBody(out DetachedBufferedBody body);

        // Body and trailer failures after response start propagate from these methods.
        ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct);

        ValueTask DrainAsync(CancellationToken ct);

        void Abort();

        ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct);
    }
}
