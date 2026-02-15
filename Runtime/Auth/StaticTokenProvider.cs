using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Token provider that returns a fixed token.
    /// Suitable for API keys and non-sensitive tokens that don't expire during the client's lifetime.
    /// <para><b>Security Note:</b> The token is stored as a plain <see cref="string"/> in managed memory.
    /// It will persist in memory until garbage collected and may be visible in memory dumps or crash reports.
    /// For sensitive tokens (OAuth access tokens, session tokens), prefer implementing
    /// <see cref="IAuthTokenProvider"/> with on-demand token fetching to minimize credential retention.</para>
    /// </summary>
    public class StaticTokenProvider : IAuthTokenProvider
    {
        private readonly string _token;

        public StaticTokenProvider(string token)
        {
            _token = token;
        }

        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_token);
        }
    }
}
