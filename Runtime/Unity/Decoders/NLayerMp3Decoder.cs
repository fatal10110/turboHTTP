using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Unity.Decoders
{
    /// <summary>
    /// MP3 managed decoder adapter.
    /// Falls back when NLayer is not available.
    /// Enable via TURBOHTTP_NLAYER scripting define when the package is installed.
    /// </summary>
    public sealed class NLayerMp3Decoder : IAudioDecoder
    {
#if TURBOHTTP_NLAYER
        private const bool ProviderAvailable = true;
#else
        private const bool ProviderAvailable = false;
#endif

        public string Id => "nlayer";

        public bool CanDecode(string contentType, string fileExtension)
        {
            if (!ProviderAvailable)
                return false;

            if (!string.IsNullOrWhiteSpace(contentType) &&
                (contentType.IndexOf("audio/mpeg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 contentType.IndexOf("audio/mp3", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            return string.Equals(fileExtension, ".mp3", StringComparison.OrdinalIgnoreCase);
        }

        public Task<DecodedAudio> DecodeAsync(ReadOnlyMemory<byte> encodedBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ProviderAvailable)
            {
                throw new NotSupportedException(
                    "Managed MP3 decode requires NLayer provider, which is not installed in this build.");
            }

            throw new NotSupportedException(
                "Managed MP3 decoder adapter is not wired in this build.");
        }

        public Task WarmupAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
