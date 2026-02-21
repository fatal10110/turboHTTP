# Auth Module

TurboHTTP's `Auth` module provides an extensible framework for handling authentication, ranging from simple static tokens to complex OAuth 2.0 and OIDC flows.

## AuthMiddleware

All authentication logic is encapsulated within `AuthMiddleware`. This middleware intercepts outbound requests and injects the necessary Authorization headers based on an `ITokenProvider`.

```csharp
using TurboHTTP.Auth;

// Add generic Auth Middleware configured with a specific provider
var options = new UHttpClientOptions();
options.Middlewares.Add(new AuthMiddleware(myTokenProvider));
```

## Token Providers

### StaticTokenProvider

Provides a constant Bearer token. Useful for simple API keys or long-lived personal access tokens.

```csharp
var provider = new StaticTokenProvider("your-api-key");
```

### OAuthTokenProvider

Handles complex OAuth 2.0 flows, including automated token refresh. It requires an `OAuthClient` and an `ITokenStore`.

```csharp
// 1. Configure OAuth Client
var oauthConfig = new OAuthConfig {
    TokenUrl = "https://auth.example.com/oauth/token",
    ClientId = "my-client",
    ClientSecret = "my-secret",
    Scopes = { "api.read", "api.write" }
};
var oauthClient = new OAuthClient(oauthConfig, new InMemoryTokenStore());

// 2. Wrap in Provider
var provider = new OAuthTokenProvider(oauthClient);

var options = new UHttpClientOptions();
options.Middlewares.Add(new AuthMiddleware(provider));
```

## Token Stores

TurboHTTP provides several `ITokenStore` implementations:
- `InMemoryTokenStore`: Stores tokens in memory for the session duration.
- `PlayerPrefsTokenStore`: Persists tokens across application restarts using Unity's `PlayerPrefs`.
- Custom implementation: Implement `ITokenStore` to save tokens securely (e.g., to iOS Keychain or Android Keystore natively).
