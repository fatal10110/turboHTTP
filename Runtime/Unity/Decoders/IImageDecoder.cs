using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Unity.Decoders
{
    /// <summary>
    /// Worker-thread image decoder contract.
    /// </summary>
    /// <remarks>
    /// <see cref="TextureOptions.MaxPixels"/> (on by default) reads the pixel count from a PNG or
    /// JPEG header before any decoder runs and rejects payloads whose header it cannot read. A
    /// decoder for another format therefore only receives payloads when the caller sets
    /// <c>MaxPixels = null</c>; it should then bound the decoded size itself.
    /// </remarks>
    public interface IImageDecoder
    {
        string Id { get; }

        bool CanDecode(string contentType, string fileExtension);

        Task<DecodedImage> DecodeAsync(ReadOnlyMemory<byte> encodedBytes, CancellationToken cancellationToken);

        Task WarmupAsync(CancellationToken cancellationToken = default);
    }
}
