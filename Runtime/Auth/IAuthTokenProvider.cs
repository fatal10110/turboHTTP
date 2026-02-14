using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Provides authentication tokens for HTTP requests.
    /// Implementations may return static tokens, refresh OAuth tokens, etc.
    /// </summary>
    public interface IAuthTokenProvider
    {
        /// <summary>
        /// Get the current authentication token.
        /// Returns null or empty string to skip authentication for this request.
        /// </summary>
        Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
    }
}
