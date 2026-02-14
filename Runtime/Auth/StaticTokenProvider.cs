using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Token provider that returns a fixed token.
    /// Suitable for API keys and tokens that don't expire during the client's lifetime.
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
