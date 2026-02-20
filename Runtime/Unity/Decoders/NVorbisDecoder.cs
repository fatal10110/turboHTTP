using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Unity.Decoders
{
    /// <summary>
    /// OGG/Vorbis managed decoder adapter.
    /// Falls back when NVorbis is not available.
    /// Enable via TURBOHTTP_NVORBIS scripting define when the package is installed.
    /// </summary>
    public sealed class NVorbisDecoder : IAudioDecoder
    {
#if TURBOHTTP_NVORBIS
        private const bool ProviderAvailable = true;
#else
        private const bool ProviderAvailable = false;
#endif

        public string Id => "nvorbis";

        public bool CanDecode(string contentType, string fileExtension)
        {
            if (!ProviderAvailable)
                return false;

            if (!string.IsNullOrWhiteSpace(contentType) &&
                (contentType.IndexOf("audio/ogg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 contentType.IndexOf("application/ogg", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            return string.Equals(fileExtension, ".ogg", StringComparison.OrdinalIgnoreCase);
        }

        public Task<DecodedAudio> DecodeAsync(ReadOnlyMemory<byte> encodedBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ProviderAvailable)
            {
                throw new NotSupportedException(
                    "Managed OGG decode requires NVorbis provider, which is not installed in this build.");
            }

            throw new NotSupportedException(
                "Managed OGG decoder adapter is not wired in this build.");
        }

        public Task WarmupAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
