# Phase 14: Post-v1.0 Roadmap

**Milestone:** M4 (v1.x "differentiators")
**Dependencies:** Phase 12 (Editor Tooling)
**Estimated Complexity:** Varies
**Critical:** No - Future enhancements

## Overview

This phase outlines the core roadmap for TurboHTTP beyond v1.0. These features prioritize transport robustness, mobile reliability, and extensibility. This roadmap can be prepared before the deferred release phase.

Detailed sub-phase breakdown: [Phase 14 Implementation Plan - Overview](phase14/overview.md)

## Scope Split

The following items were extracted from Phase 14 into [Phase 16](phase-16-platform-protocol-security.md):
- WebGL Support (High Priority)
- WebSocket Support (High Priority)
- gRPC Support (Low Priority)
- GraphQL Client (Medium Priority)
- Parallel Request Helpers (Low Priority)
- Security & Privacy Hardening (High Priority)

## Potential Features for v1.1 - v2.0 (Phase 14 Scope)

### ~~1. HTTP/2 Support~~ ✅ Implemented in Phase 3B

HTTP/2 support is now part of the core v1.0 implementation. See [Phase 3B](phase-03b-http2.md) for details.

- Binary framing layer, HPACK header compression, stream multiplexing, flow control
- ALPN negotiation during TLS handshake to select h2 vs http/1.1
- Automatic fallback to HTTP/1.1 if server doesn't support h2

---

### 2. Happy Eyeballs (RFC 8305) (Medium Priority)

Detailed plan: [Phase 14.1 Happy Eyeballs (RFC 8305)](phase14/phase-14.1-happy-eyeballs-rfc8305.md)

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

### 3. Proxy Support (Medium Priority)

Detailed plan: [Phase 14.2 Proxy Support](phase14/phase-14.2-proxy-support.md)

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

### 4. Background Networking on Mobile (High Priority)

Detailed plan: [Phase 14.3 Background Networking on Mobile](phase14/phase-14.3-background-networking-mobile.md)

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

### 5. Adaptive Network Policies (Medium Priority)

Detailed plan: [Phase 14.4 Adaptive Network Policies](phase14/phase-14.4-adaptive-network-policies.md)

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

### 6. OAuth 2.0 / OpenID Connect (High Priority)

Detailed plan: [Phase 14.5 OAuth 2.0 / OpenID Connect](phase14/phase-14.5-oauth2-openid-connect.md)

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

### 7. Request/Response Interceptors (Medium Priority)

Detailed plan: [Phase 14.6 Request/Response Interceptors](phase14/phase-14.6-request-response-interceptors.md)

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

### 8. Mock Server for Testing (Medium Priority)

Detailed plan: [Phase 14.7 Mock Server for Testing](phase14/phase-14.7-mock-server-testing.md)

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

### 9. Plugin System (Low Priority)

Detailed plan: [Phase 14.8 Plugin System](phase14/phase-14.8-plugin-system.md)

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

## Prioritization Matrix

| Feature | Priority | Effort | Complexity | Value | Version |
|---------|----------|--------|------------|-------|---------|
| ~~HTTP/2~~ | ~~High~~ | — | — | — | **v1.0** (Phase 3B) |
| Happy Eyeballs (RFC 8305) | Medium | 1w | Medium | High | v1.1 |
| Background Networking | High | 2-3w | High | High | v1.1 |
| Proxy Support | Medium | 2-3w | Medium-High | Medium | v1.1 |
| Adaptive Network | Medium | 2w | Medium | High | v1.1 |
| Request Interceptors | Medium | 1w | Low | Medium | v1.1 |
| OAuth 2.0 | High | 3-4w | High | High | v1.2 |
| Unity Runtime Hardening (Phase 15) | High | 3-5w | High | High | v1.2 |
| Mock Server | Medium | 2w | Medium | Medium | v1.x |
| Plugin System | Low | 2w | Medium | Low | v2.0? |

## Recommended Roadmap

### v1.1 (Q1 after v1.0)
- Happy Eyeballs (RFC 8305) — dual-stack connection racing
- Background networking on mobile (iOS/Android background task support)
- Proxy support (CONNECT tunneling, environment variable detection)
- Adaptive network policies
- Request/response interceptors
- Bug fixes from v1.0 feedback

### v1.2 (Q2)
- OAuth 2.0 / OpenID Connect
- Unity runtime hardening (Phase 15)
- Performance improvements

### v1.x follow-ups
- Mock server for testing

### v2.0 (Q4+)
- Major architectural improvements based on feedback
- Breaking changes if needed
- Plugin system

### Parallel Phase 16 Track

Cross-platform and protocol/security features split from this phase are tracked in [Phase 16](phase-16-platform-protocol-security.md).

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

Phase 14 (and linked follow-up Phases 15-16) is open-ended and depends on:
1. User feedback after v1.0 launch
2. Market demands
3. Competitive landscape
4. Team capacity

**Focus on v1.0 first!** This roadmap is a guide, not a commitment.
