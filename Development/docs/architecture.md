# TurboHTTP Architecture Guide

Welcome to the TurboHTTP architecture guide. This document serves as the primary technical onboarding resource for contributors. It details the core architectural constraints, subsystem boundaries, and performance patterns that define the project.

## 1. High-Level Design

TurboHTTP is a zero-dependency, allocation-optimized HTTP client designed for Unity games and C# applications. It implements HTTP/1.1 and HTTP/2 over raw TCP sockets (`System.Net.Sockets.Socket`) and bypasses `UnityWebRequest` entirely.

The architecture is strictly layered:
**Public API → Middleware Pipeline → Core Engine → Transport → Serialization**

## 2. Assembly Boundaries & Constraints

To ensure maximum portability and to avoid monolithic bloat, TurboHTTP is split into 13 runtime assemblies. Respecting these boundaries is the most critical rule for contributors.

### `TurboHTTP.Core`
- **Role:** Defines the public client API (`UHttpClient`), immutable request/response models (`UHttpRequest`, `UHttpResponse`), headers, errors, and pipeline contracts (`IHttpMiddleware`, `IHttpTransport`).
- **Dependencies:** **Zero external assembly references.**
- **Unity Constraints:** `noEngineReferences` is intentionally `false`. `UnityEngine` references are permitted **only** in `PlatformInfo.cs` and `PlatformConfig.cs` to supply safe platform-specific defaults (e.g., mobile concurrency limits). The rest of Core must remain engine-agnostic.

### `TurboHTTP.Transport`
- **Role:** The engine-agnostic networking backend. Handles raw TCP sockets, TLS handshakes (`SslStream` + ALPN, or BouncyCastle fallback), connection pooling, and HTTP/1.1 / HTTP/2 protocol parsing & serialization.
- **Dependencies:** Only references `TurboHTTP.Core`.
- **Unity Constraints:** `noEngineReferences` must remain `true`. Transport code must **never** reference `UnityEngine`. This ensures the core networking logic can be tested in pure .NET environments.
- **Permissions:** This is the only assembly with `allowUnsafeCode: true`.

### Optional Modules (Middleware, JSON, Auth, etc.)
- **Role:** Provide opt-in functionality without dragging down the base package size. 
- **Dependencies:** Optional modules may **only** depend on `TurboHTTP.Core`. They must **never** depend on `TurboHTTP.Transport` or each other. If you need JSON support, you include `TurboHTTP.JSON`; if you need authentication, you include `TurboHTTP.Auth`.

## 3. Middleware Pipeline

TurboHTTP uses an ASP.NET Core-style middleware pipeline.
- The pipeline delegate chain is compiled **once** during `UHttpClient` construction to ensure zero per-request overhead.
- Request flow: `Middleware[0] → Middleware[1] → ... → Transport`
- Response flow: `Transport → ... → Middleware[1] → Middleware[0]`
- If the middleware list is empty, `SendAsync` routes directly to the Transport layer.

## 4. Memory Management & Zero-Allocation Patterns

TurboHTTP targets `<500 bytes` GC allocation per request. To achieve this, several strict memory management rules apply:

1. **UHttpResponse Disposal:** `UHttpResponse` implements `IDisposable`. The body is exposed as a `ReadOnlyMemory<byte>`. Transports allocate body byte arrays from `ArrayPool<byte>.Shared`. **Contributors must ensure `UHttpResponse` is always disposed** by the consumer or within middleware to return the array to the pool.
2. **Serialization Pooling:** `Http11RequestSerializer` avoids `StringBuilder` and `Encoding.GetBytes()`. Instead, it uses a custom `PooledHeaderWriter` that writes directly into rented `ArrayPool` buffers.
3. **No Linq on Hot Paths:** LINQ extensions are prohibited in Transport parsing logic and `HttpHeaders` lookups to avoid closure and enumerator allocations.
4. **Immutable Requests:** `UHttpRequest` is immutable. To modify a request in a middleware (e.g., adding an Auth header), you must use the `.WithHeader()` builder pattern which creates a new instance wrapping the cloned header dictionary.

## 5. Threading & Concurrency

1. **No `async void`:** All asynchronous methods must return `Task` or `Task<T>`. `async void` is strictly prohibited as it crashes the process on unhandled exceptions.
2. **Synchronization:** `TurboHTTP` favors standard `lock` statements for thread-safe state (e.g., `ObjectPool`, `RequestContext` timelines) because `ConcurrentBag` and other concurrent collections have unpredictable thread-local overhead on IL2CPP platforms.
3. **Connection Cleanup Deadlocks:** The `Dispose()` methods on long-running objects like `UHttpClient` or `TcpConnectionPool` use `.Wait()` with a strict timeout to ensure deterministic cleanup.
4. **Timeouts:** Timeouts are controlled exclusively by the Transport layer via `CancellationTokenSource.CancelAfter()`. There is no `TimeoutMiddleware`.
5. **Semaphore Leaks:** Connection leases (`ConnectionLease`) are wrapped in `IDisposable` classes (not structs) to guarantee that process-wide connection semaphores are safely released even if a thread aborts or a connection breaks.
