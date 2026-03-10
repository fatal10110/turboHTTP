using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Represents a storage mechanism for OAuth tokens.
    /// </summary>
    public interface ITokenStore
    {
        /// <summary> Retrieve a token by key. </summary>
        Task<OAuthToken> GetAsync(string key, CancellationToken ct);
        /// <summary> Store or update a token. </summary>
        Task SetAsync(string key, OAuthToken token, CancellationToken ct);
        /// <summary> Remove a token by key. </summary>
        Task RemoveAsync(string key, CancellationToken ct);
    }

    /// <summary>
    /// An in-memory implementation of <see cref="ITokenStore"/>.
    /// </summary>
    public sealed class InMemoryTokenStore : ITokenStore, System.IDisposable
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

        public void Clear()
        {
            _tokens.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
