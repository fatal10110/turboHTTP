using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Unity.Decoders
{
    /// <summary>
    /// Worker-thread audio decoder contract.
    /// </summary>
    public interface IAudioDecoder
    {
        string Id { get; }

        bool CanDecode(string contentType, string fileExtension);

        Task<DecodedAudio> DecodeAsync(ReadOnlyMemory<byte> encodedBytes, CancellationToken cancellationToken);

        Task WarmupAsync(CancellationToken cancellationToken = default);
    }
}
