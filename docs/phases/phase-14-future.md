# Phase 14: Post-v1.0 Roadmap

**Milestone:** M4 (v1.x "differentiators")
**Dependencies:** Phase 13 (v1.0 Release)
**Estimated Complexity:** Varies
**Critical:** No - Future enhancements

## Overview

This phase outlines the roadmap for TurboHTTP beyond v1.0. These features will differentiate TurboHTTP from competitors and address advanced use cases. Prioritize based on user feedback and market demand.

## Potential Features for v1.1 - v2.0

### 1. WebGL Support (High Priority)

**Goal:** Make TurboHTTP work in WebGL builds

**Approach:** Implement a `.jslib` JavaScript plugin that wraps the browser `fetch()` API, with a C# `WebGLBrowserTransport : IHttpTransport` that calls into it via `[DllImport("__Internal")]`. This is the same proven approach used by BestHTTP (which uses `XMLHttpRequest`), but using the modern `fetch()` API which additionally supports `ReadableStream` for streaming responses.

**Architecture:**
```
IHttpTransport
├── RawSocketTransport          ← Desktop/Mobile (Phase 3/3B)
└── WebGLBrowserTransport       ← WebGL: fetch() API via .jslib
```

**Implementation:**
- `Plugins/WebGL/TurboHTTP_WebFetch.jslib` — ~200 lines of JS wrapping `fetch()`
  - `WebFetch_Create(method, url)` — create a fetch request
  - `WebFetch_SetHeader(id, name, value)` — set request headers
  - `WebFetch_Send(id, bodyPtr, bodyLen)` — send request, marshal body from Emscripten heap
  - `WebFetch_GetStatus(id)` / `WebFetch_GetResponseHeaders(id)` / `WebFetch_GetResponseBody(id)` — retrieve response data
  - `WebFetch_Abort(id)` / `WebFetch_Release(id)` — cleanup
- `Runtime/Transport/WebGL/WebGLBrowserTransport.cs` — C# side calling jslib via `[DllImport("__Internal")]`

**WebGL Limitations (accepted, same as BestHTTP):**
- Cookies: browser-managed only
- Caching: browser cache only (no TurboHTTP cache middleware)
- Streaming: partial support via `ReadableStream` (improvement over BestHTTP's XHR approach)
- Proxy: unavailable
- Custom certificate validation: unavailable (browser handles TLS)
- Redirect control: unavailable (browser follows automatically)
- HTTP/2: browser decides protocol transparently (no client-side choice)
- Connection pooling: browser-managed

**Estimated Effort:** 2-3 weeks

**Complexity:** Medium

**Value:** High (expands platform support)

---

### ~~2. HTTP/2 Support~~ ✅ Implemented in Phase 3B

HTTP/2 support is now part of the core v1.0 implementation. See [Phase 3B](phase-03b-http2.md) for details.

- Binary framing layer, HPACK header compression, stream multiplexing, flow control
- ALPN negotiation during TLS handshake to select h2 vs http/1.1
- Automatic fallback to HTTP/1.1 if server doesn't support h2

---

### 3. Happy Eyeballs (RFC 8305) (Medium Priority)

**Goal:** Improve connection time on dual-stack networks with broken IPv6

**Problem:** Current `ConnectSocketAsync` tries each DNS address sequentially. If the first address is IPv6 and the route is broken, the connection is delayed by the full timeout before falling back to IPv4.

**Implementation:**
- Sort addresses: IPv6 first (iOS requirement), then IPv4
- Try IPv6 first, wait 250ms, then start IPv4 in parallel
- Use the first successful connection, cancel the other
- Per RFC 8305 Section 3

```csharp
// Sort: IPv6 first, then IPv4
var ipv6 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6);
var ipv4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork);

// Try IPv6, stagger IPv4 by 250ms
var ipv6Task = ConnectAsync(ipv6.ToArray(), port, ct);
await Task.Delay(250, ct);
var ipv4Task = ConnectAsync(ipv4.ToArray(), port, ct);
var winner = await Task.WhenAny(ipv6Task, ipv4Task);
```

**Estimated Effort:** 1 week

**Complexity:** Medium

**Value:** High (mobile user experience, iOS App Store compliance)

---

### 4. Proxy Support (Medium Priority)

**Goal:** HTTP proxy support for enterprise and corporate environments

**Features:**
- `CONNECT` tunneling for HTTPS through HTTP proxies
- `Proxy-Authorization` header support
- Environment variable detection (`HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`)
- Optional `TurboHTTP.Proxy` module (references only `TurboHTTP.Core`)

**Implementation:**
```csharp
var options = new UHttpClientOptions
{
    Proxy = new ProxySettings
    {
        Address = "http://proxy.corp.example.com:8080",
        Credentials = new NetworkCredential("user", "pass"),
        BypassList = new[] { "*.local", "10.*" }
    }
};
```

**Estimated Effort:** 2-3 weeks

**Complexity:** Medium-High

**Value:** Medium (enterprise requirement, rare for mobile games)

---

### 5. WebSocket Support (High Priority)

**Goal:** Add WebSocket client alongside HTTP

**Use Cases:**
- Real-time multiplayer
- Live chat
- Push notifications
- Game servers

**Implementation:**
```csharp
public class UWebSocketClient
{
    public async Task ConnectAsync(string url);
    public async Task SendAsync(string message);
    public event Action<string> OnMessage;
    public event Action OnConnected;
    public event Action<string> OnError;
    public async Task DisconnectAsync();
}
```

**Estimated Effort:** 2-3 weeks

**Complexity:** Medium

**Value:** High (expands use cases)

---

### 6. gRPC Support (Low Priority)

**Goal:** Support gRPC protocol

**Benefits:**
- Binary protocol (smaller payloads)
- Strongly-typed contracts
- Streaming support

**Challenges:**
- Requires protobuf compiler
- Unity IL2CPP compatibility
- Code generation complexity

**Estimated Effort:** 4-6 weeks

**Complexity:** Very High

**Value:** Low-Medium (niche use case)

---

### 7. Background Networking on Mobile (High Priority)

**Goal:** Support HTTP requests that survive app backgrounding on iOS and Android

**Problem:** On mobile platforms, the OS suspends networking when the app goes to the background. In-flight requests fail, and new requests cannot be started. This is critical for file uploads, analytics, and data sync.

**Implementation:**
- **iOS:** Use `UIApplication.BeginBackgroundTask` / `EndBackgroundTask` to request extra execution time (up to ~30s) for in-flight requests
- **Android:** Use `WorkManager` or `JobScheduler` for deferred network operations; foreground services for long transfers
- **API:** `BackgroundNetworkingMiddleware` that wraps request execution with platform-specific background task registration
- **Fallback:** Queue failed requests for retry when app resumes (via `OnApplicationPause`)

**Estimated Effort:** 2-3 weeks

**Complexity:** High (platform-specific native plugins)

**Value:** High (essential for production mobile apps)

---

### 8. Adaptive Network Policies (Medium Priority)
<!-- NOTE: Sections renumbered after inserting "7. Background Networking on Mobile" -->

**Goal:** Automatically adjust behavior based on network conditions

**Features:**
- Detect slow connections and adjust timeouts
- Reduce concurrent requests on poor networks
- Prefer cached content on mobile data
- Adjust retry behavior based on success rate

**Implementation:**
```csharp
public class AdaptiveMiddleware : IHttpMiddleware
{
    private NetworkQualityDetector _detector;

    public async Task<UHttpResponse> InvokeAsync(...)
    {
        var quality = _detector.GetCurrentQuality();

        if (quality == NetworkQuality.Poor)
        {
            // Increase timeout, reduce concurrency, prefer cache
        }

        return await next(request, context, cancellationToken);
    }
}

public enum NetworkQuality
{
    Excellent,
    Good,
    Fair,
    Poor
}
```

**Estimated Effort:** 2 weeks

**Complexity:** Medium

**Value:** High (improves mobile experience)

---

### 9. GraphQL Client (Medium Priority)

**Goal:** Add GraphQL query builder and client

**Implementation:**
```csharp
public class GraphQLClient
{
    private readonly UHttpClient _httpClient;

    public async Task<T> QueryAsync<T>(string query, object variables = null)
    {
        var request = new { query, variables };
        return await _httpClient.PostJsonAsync<object, T>(endpoint, request);
    }
}

// Usage
var query = @"
    query GetUser($id: ID!) {
        user(id: $id) {
            name
            email
        }
    }
";

var user = await graphql.QueryAsync<User>(query, new { id = "123" });
```

**Estimated Effort:** 1-2 weeks

**Complexity:** Low-Medium

**Value:** Medium (popular API format)

---

### 10. Advanced Content Handlers (Low Priority)

**Goal:** Support more Unity asset types and formats

**New Handlers:**
- AssetBundle loading
- Video (VideoClip)
- 3D models (glTF, FBX)
- Compressed formats (gzip, brotli)
- Protobuf serialization

**Implementation:**
```csharp
// AssetBundle handler
var assetBundle = await client.GetAssetBundleAsync(url);

// Video handler
var videoClip = await client.GetVideoClipAsync(url);

// Protobuf handler
var data = response.AsProtobuf<MyProtoMessage>();
```

**Estimated Effort:** 1-2 weeks per handler

**Complexity:** Low-Medium

**Value:** Medium

---

### 11. OAuth 2.0 / OpenID Connect (High Priority)

**Goal:** Built-in OAuth flow support

**Features:**
- Authorization code flow
- PKCE support
- Token refresh
- Token storage
- Multiple providers (Google, Facebook, etc.)

**Implementation:**
```csharp
public class OAuthClient
{
    public async Task<OAuthToken> AuthorizeAsync(OAuthConfig config);
    public async Task<OAuthToken> RefreshTokenAsync(string refreshToken);
    public bool IsTokenExpired(OAuthToken token);
}

// Usage
var oauth = new OAuthClient();
var token = await oauth.AuthorizeAsync(new OAuthConfig
{
    ClientId = "...",
    RedirectUri = "...",
    Scopes = new[] { "profile", "email" }
});

// Use token with requests
client.Options.Middlewares.Add(new AuthMiddleware(
    new OAuthTokenProvider(token)
));
```

**Estimated Effort:** 3-4 weeks

**Complexity:** High

**Value:** High (common requirement)

---

### 12. Request/Response Interceptors (Medium Priority)

**Goal:** Allow modifying requests/responses without middleware

**Implementation:**
```csharp
client.OnRequest += (request) =>
{
    // Modify request before sending
    Debug.Log($"Sending: {request.Uri}");
};

client.OnResponse += (response) =>
{
    // Process response
    Debug.Log($"Received: {response.StatusCode}");
};
```

**Estimated Effort:** 1 week

**Complexity:** Low

**Value:** Medium

---

### 13. Parallel Request Helpers (Low Priority)

**Goal:** Simplify common parallel request patterns

**Implementation:**
```csharp
// Batch requests
var urls = new[] { "/user/1", "/user/2", "/user/3" };
var users = await client.GetManyJsonAsync<User>(urls);

// All succeed or all fail
var results = await client.GetAllOrNoneAsync(urls);

// Race (return first success)
var fastestResult = await client.RaceAsync(urls);
```

**Estimated Effort:** 1 week

**Complexity:** Low

**Value:** Low-Medium

---

### 14. Mock Server for Testing (Medium Priority)

**Goal:** Built-in mock HTTP server for testing

**Implementation:**
```csharp
var mockServer = new MockHttpServer();

mockServer.On("GET", "/api/users", (req) =>
{
    return new MockResponse
    {
        StatusCode = 200,
        Body = JsonSerializer.Serialize(new[] { new User { Name = "Test" } })
    };
});

mockServer.Start("http://localhost:8080");

// Use in tests
var response = await client.Get("http://localhost:8080/api/users").SendAsync();
```

**Estimated Effort:** 2 weeks

**Complexity:** Medium

**Value:** Medium (testing)

---

### 15. Plugin System (Low Priority)

**Goal:** Allow third-party extensions

**Implementation:**
```csharp
public interface IHttpPlugin
{
    void Initialize(UHttpClient client);
    void Shutdown();
}

// Plugin example: Sentry error reporting
public class SentryPlugin : IHttpPlugin
{
    public void Initialize(UHttpClient client)
    {
        client.OnError += (error) => Sentry.CaptureException(error);
    }
}

// Register plugin
client.RegisterPlugin(new SentryPlugin());
```

**Estimated Effort:** 2 weeks

**Complexity:** Medium

**Value:** Low-Medium

---

### 16. Security & Privacy Hardening (High Priority)

**Goal:** Make “safe by default” behavior explicit and configurable

**Focus Areas:**
- **Record/replay redaction:** redact `Authorization`, cookies, API keys, and user identifiers by default
- **Logging controls:** ensure sensitive headers/body fields are never logged unless explicitly enabled
- **Cache safety:** partition cache correctly and avoid caching user-specific responses by default
- **TLS controls (advanced):** optional certificate pinning / custom validation hooks (platform constraints apply)

**Estimated Effort:** 1-2 weeks

**Complexity:** Medium

**Value:** High (reduces support burden and prevents real-world incidents)

---

## Prioritization Matrix

| Feature | Priority | Effort | Complexity | Value | Version |
|---------|----------|--------|------------|-------|---------|
| ~~HTTP/2~~ | ~~High~~ | — | — | — | **v1.0** (Phase 3B) |
| WebGL Support | High | 2-3w | Medium | High | v1.1 |
| Happy Eyeballs (RFC 8305) | Medium | 1w | Medium | High | v1.1 |
| Background Networking | High | 2-3w | High | High | v1.1 |
| Proxy Support | Medium | 2-3w | Medium-High | Medium | v1.1 |
| Adaptive Network | Medium | 2w | Medium | High | v1.1 |
| OAuth 2.0 | High | 3-4w | High | High | v1.2 |
| WebSocket | High | 2-3w | Medium | High | v1.2 |
| GraphQL | Medium | 1-2w | Low | Medium | v1.3 |
| Request Interceptors | Medium | 1w | Low | Medium | v1.1 |
| Security & Privacy | High | 1-2w | Medium | High | v1.1 |
| Advanced Content | Low | 1-2w each | Medium | Medium | v1.x |
| gRPC | Low | 4-6w | Very High | Low | v2.0? |
| Mock Server | Medium | 2w | Medium | Medium | v1.x |
| Parallel Helpers | Low | 1w | Low | Low | v1.x |
| Plugin System | Low | 2w | Medium | Low | v2.0? |

## Recommended Roadmap

### v1.1 (Q1 after v1.0)
- WebGL support (browser `fetch()` API via `.jslib` interop)
- Happy Eyeballs (RFC 8305) — dual-stack connection racing
- Background networking on mobile (iOS/Android background task support)
- Proxy support (CONNECT tunneling, environment variable detection)
- Adaptive network policies
- Request/response interceptors
- Security & privacy hardening (redaction + safe defaults)
- Bug fixes from v1.0 feedback

### v1.2 (Q2)
- OAuth 2.0 / OpenID Connect
- WebSocket support
- Performance improvements

### v1.3 (Q3)
- GraphQL client
- Additional content handlers

### v2.0 (Q4+)
- Major architectural improvements based on feedback
- Breaking changes if needed
- Advanced features (gRPC, plugin system)

## User Feedback Collection

After v1.0 launch, collect feedback on:

1. **Most requested features**
   - Survey users
   - Monitor support tickets
   - Check Asset Store reviews

2. **Pain points**
   - What's difficult to use?
   - What's missing?
   - What's confusing?

3. **Performance issues**
   - Any bottlenecks?
   - Platform-specific problems?

4. **Use cases**
   - How are users using TurboHTTP?
   - What patterns are common?
   - What's unexpected?

## Success Metrics

Track these metrics to guide roadmap decisions:

- **Adoption:** Number of downloads
- **Engagement:** Active users
- **Satisfaction:** Average rating, reviews
- **Support:** Number of support tickets
- **Community:** Forum activity, GitHub stars

## Notes

- Don't commit to specific features until v1.0 is released
- Let user feedback guide priorities
- Balance new features with stability
- Consider backward compatibility
- Plan for breaking changes in v2.0
- Keep documentation up to date
- Maintain high quality standards

## Conclusion

Phase 14 is open-ended and depends on:
1. User feedback after v1.0 launch
2. Market demands
3. Competitive landscape
4. Team capacity

**Focus on v1.0 first!** This roadmap is a guide, not a commitment.
