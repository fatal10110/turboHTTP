using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Auth
{
    public interface ITokenStore
    {
        Task<OAuthToken> GetAsync(string key, CancellationToken ct);
        Task SetAsync(string key, OAuthToken token, CancellationToken ct);
        Task RemoveAsync(string key, CancellationToken ct);
    }

    public sealed class InMemoryTokenStore : ITokenStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OAuthToken> _tokens
            = new System.Collections.Concurrent.ConcurrentDictionary<string, OAuthToken>(System.StringComparer.Ordinal);

        public Task<OAuthToken> GetAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _tokens.TryGetValue(key ?? string.Empty, out var token);
            return Task.FromResult(token);
        }

        public Task SetAsync(string key, OAuthToken token, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (key == null) key = string.Empty;
            if (token == null)
            {
                _tokens.TryRemove(key, out _);
            }
            else
            {
                _tokens[key] = token;
            }

            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _tokens.TryRemove(key ?? string.Empty, out _);
            return Task.CompletedTask;
        }
    }
}
