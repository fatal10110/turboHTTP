using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Unity.Decoders
{
    /// <summary>
    /// Worker-thread image decoder contract.
    /// </summary>
    public interface IImageDecoder
    {
        string Id { get; }

        bool CanDecode(string contentType, string fileExtension);

        Task<DecodedImage> DecodeAsync(ReadOnlyMemory<byte> encodedBytes, CancellationToken cancellationToken);

        Task WarmupAsync(CancellationToken cancellationToken = default);
    }
}
