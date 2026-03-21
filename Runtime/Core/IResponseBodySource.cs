using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    public interface IResponseBodySource : IAsyncDisposable
    {
        long? Length { get; }

        bool TryGetBufferedData(out ReadOnlyMemory<byte> data);

        bool TryDetachBufferedBody(out DetachedBufferedBody body);

        // Body and trailer failures after response start propagate from these methods.
        ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct);

        ValueTask DrainAsync(CancellationToken ct);

        void Abort();

        ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct);
    }
}
