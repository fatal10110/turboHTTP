# Auth Module

The `TurboHTTP.Auth` module attaches credentials to requests, from a simple static token to OAuth 2.0 / OpenID Connect with automatic refresh.

## AuthInterceptor

`AuthInterceptor` asks an `IAuthTokenProvider` for a token and sets `Authorization: <scheme> <token>` on each request (`Bearer` unless you pass another scheme). A provider that returns `null` or an empty string skips authentication for that request.

<!-- snippet: with IAuthTokenProvider myTokenProvider = null; -->
```csharp
using TurboHTTP.Auth;

var options = new UHttpClientOptions();
options.Interceptors.Add(new AuthInterceptor(myTokenProvider));
var client = new UHttpClient(options);
```

The credential stays with its origin: for each call the interceptor binds to the scheme, host and port of the original request and never attaches the token to a hop for another origin, such as a cross-origin redirect target, as browsers do. Place it below `CookieInterceptor` and above `CacheInterceptor`; see [Interceptors](Interceptors.md) for the order. The token never appears in logs or exceptions.

To authenticate a single request, use `WithBearerToken`:

<!-- snippet: with UHttpClient client = null; -->
```csharp
using TurboHTTP.Auth;

using var response = await client.Get("https://api.example.com/me")
    .WithBearerToken("token123")
    .SendBufferedAsync();
```

## Token providers

`IAuthTokenProvider` has one method, `Task<string> GetTokenAsync(CancellationToken)`. Implement it to fetch or refresh tokens your own way:

```csharp
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Auth;

public sealed class MyTokenProvider : IAuthTokenProvider
{
    public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult("token-from-my-login-flow");
    }
}
```

### StaticTokenProvider

A constant token: simple API keys or long-lived personal access tokens.

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
using TurboHTTP.Auth;

options.Interceptors.Add(new AuthInterceptor(new StaticTokenProvider("your-api-key")));
```

### OAuthTokenProvider

`OAuthTokenProvider` returns the stored access token and refreshes it through `OAuthClient` when it is about to expire. The application obtains the first token itself (authorization code with PKCE, which opens a browser or a system web view), stores it in an `ITokenStore`, and hands the store key to the provider.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Auth;
using TurboHTTP.Core;

public static class OAuthSetup
{
    static readonly OAuthConfig Config = new OAuthConfig
    {
        ClientId = "my-client",
        AuthorizationEndpoint = new Uri("https://auth.example.com/oauth/authorize"),
        TokenEndpoint = new Uri("https://auth.example.com/oauth/token"),
        RedirectUri = new Uri("com.example.game://callback"),
        Scopes = new[] { "api.read", "api.write" }
    };

    static readonly InMemoryTokenStore Store = new InMemoryTokenStore();
    static readonly OAuthClient OAuth = new OAuthClient(tokenStore: Store);

    // Keep the request until the redirect arrives: it holds the state and the PKCE verifier.
    static OAuthAuthorizationRequest pending;

    // Step 1: send the user to the authorization endpoint (state and PKCE challenge are in the URI).
    public static async Task<Uri> BeginAsync(CancellationToken ct)
    {
        pending = await OAuth.CreateAuthorizationRequestAsync(Config, ct);
        return pending.AuthorizationUri; // open it in a browser or system web view
    }

    // Step 2: the redirect (com.example.game://callback?code=...&state=...) arrived.
    public static async Task<AuthInterceptor> CompleteAsync(string returnedCode, string returnedState, CancellationToken ct)
    {
        // Reject a redirect whose state does not match the request (RFC 6749 section 10.12).
        OAuthClient.ValidateState(pending.State, returnedState);

        OAuthToken token = await OAuth.ExchangeCodeAsync(new OAuthCodeExchangeRequest
        {
            Config = Config,
            AuthorizationCode = returnedCode,
            CodeVerifier = pending.CodeVerifier // proves this client started the flow (RFC 7636)
        }, ct);
        string key = OAuthClient.BuildTokenStoreKey(Config, "api");
        await Store.SetAsync(key, token, ct);

        // The provider refreshes the token from the store when it expires.
        var provider = new OAuthTokenProvider(OAuth, key, new OAuthRefreshRequest { Config = Config, TokenStoreKey = key });
        return new AuthInterceptor(provider);
    }
}
```

Set `OAuthConfig.UseOidcDiscovery` to resolve the endpoints from the issuer's discovery document. Endpoints must use HTTPS; `AllowInsecureEndpointsForDevelopment` relaxes that for local test servers only.

## Token stores

*   `InMemoryTokenStore`: keeps tokens for the lifetime of the process.
*   Your own `ITokenStore` (`GetAsync`, `SetAsync`, `RemoveAsync`): persist tokens somewhere safe, for example the iOS Keychain or the Android Keystore. TurboHTTP does not ship a persistent store.
