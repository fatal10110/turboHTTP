# Phase 16: Platform, Protocol, and Security Expansion

**Milestone:** M4 (v1.x "differentiators")
**Dependencies:** Phase 14 prioritization
**Estimated Complexity:** Varies
**Critical:** No - Future enhancements

## Overview

This phase was split out of Phase 14 to isolate high-impact platform/protocol/security work that can be prioritized independently. Scope is focused on six extracted features.

## Extracted Features for v1.1 - v2.0

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

### 2. WebSocket Support (High Priority)

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

### 3. gRPC Support (Low Priority)

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

### 4. GraphQL Client (Medium Priority)

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

### 5. Parallel Request Helpers (Low Priority)

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

### 6. Security & Privacy Hardening (High Priority)

**Goal:** Make "safe by default" behavior explicit and configurable

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
| WebGL Support | High | 2-3w | Medium | High | v1.1 |
| Security & Privacy | High | 1-2w | Medium | High | v1.1 |
| WebSocket | High | 2-3w | Medium | High | v1.2 |
| GraphQL | Medium | 1-2w | Low | Medium | v1.3 |
| Parallel Helpers | Low | 1w | Low | Low | v1.x |
| gRPC | Low | 4-6w | Very High | Low | v2.0? |

## Recommended Roadmap

### v1.1 (Q1 after v1.0)
- WebGL support (browser `fetch()` API via `.jslib` interop)
- Security & privacy hardening (redaction + safe defaults)

### v1.2 (Q2)
- WebSocket support

### v1.3 (Q3)
- GraphQL client

### v1.x backlog
- Parallel request helpers

### v2.0 (Q4+)
- gRPC support

## Notes

- Keep this phase synchronized with Phase 14 roadmap decisions.
- Re-prioritize based on post-v1.0 adoption data.
- Keep platform limitations explicit in documentation (especially WebGL/browser constraints).
