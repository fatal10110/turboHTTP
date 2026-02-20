using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Unity.Decoders
{
    /// <summary>
    /// PNG/JPEG managed decoder adapter.
    /// Falls back when StbImageSharp is not available.
    /// Enable via TURBOHTTP_STBIMAGESHARP scripting define when the package is installed.
    /// </summary>
    public sealed class StbImageSharpDecoder : IImageDecoder
    {
#if TURBOHTTP_STBIMAGESHARP
        private const bool ProviderAvailable = true;
#else
        private const bool ProviderAvailable = false;
#endif

        public string Id => "stbimagesharp";

        public bool CanDecode(string contentType, string fileExtension)
        {
            if (!ProviderAvailable)
                return false;

            if (!string.IsNullOrWhiteSpace(contentType))
            {
                if (contentType.IndexOf("image/png", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    contentType.IndexOf("image/jpeg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    contentType.IndexOf("image/jpg", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            if (string.IsNullOrWhiteSpace(fileExtension))
                return false;

            return string.Equals(fileExtension, ".png", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fileExtension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fileExtension, ".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        public Task<DecodedImage> DecodeAsync(ReadOnlyMemory<byte> encodedBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ProviderAvailable)
            {
                throw new NotSupportedException(
                    "Managed image decode requires StbImageSharp provider, which is not installed in this build.");
            }

            throw new NotSupportedException(
                "Managed image decode provider adapter is not wired in this build.");
        }

        public Task WarmupAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
