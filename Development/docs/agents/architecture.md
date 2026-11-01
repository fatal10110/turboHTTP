# Architecture guide

Load when touching module boundaries, the request pipeline, transport wiring, pooling or a public
type. Always-on rules are in `CLAUDE.md`; this file holds the detail behind them.

## Layers

Public API (`UHttpClient`, `UHttpRequest`, `UHttpResponse`/`UHttpStreamingResponse`)
→ interceptor pipeline (`InterceptorPipeline`, `IHttpInterceptor`, `IHttpHandler`)
→ transport (`IHttpTransport`: `RawSocketTransport`, HTTP/1.1, HTTP/2, TLS, pools)
→ content handlers (JSON, files, Unity assets).

## Assemblies and allowed references

Enforced by `Tools~/ci/check_repo.py asmdef`. Expanding an exception needs an architecture decision
recorded in the journal and in this table.

| Assembly | References | Notes |
| --- | --- | --- |
| `TurboHTTP.Core` | — | `autoReferenced`; client API, request/response, pipeline, `Core.Internal` pooling/buffers |
| `TurboHTTP.Transport` | Core | only assembly with `allowUnsafeCode`; `noEngineReferences`; excludes WebGL |
| `TurboHTTP.Transport.BouncyCastle` | Core, Transport, SecureProtocol | TLS fallback provider glue |
| `TurboHTTP.SecureProtocol` | — | vendored BouncyCastle in `Transport/BouncyCastle/Lib/` (never edited); `noEngineReferences`; excludes WebGL; asmdef sits at the `Lib/` root so the re-vendor sync keeps it (#143) |
| `TurboHTTP.JSON` | Core | `noEngineReferences`; `LiteJsonSerializer` default, `TURBOHTTP_USE_SYSTEM_TEXT_JSON` optional |
| `TurboHTTP.Middleware`, `.Auth`, `.Retry`, `.Cache`, `.RateLimit`, `.Observability`, `.Files`, `.Testing`, `.Unity` | Core | independently includable feature modules |
| `TurboHTTP.WebSocket` | Core | `noEngineReferences` |
| `TurboHTTP.WebSocket.Transport` | Core, WebSocket, Transport | raw-socket WebSocket transport |
| `TurboHTTP.Unity.WebSocket` | Core, WebSocket, Unity | MonoBehaviour client/bridge |
| `TurboHTTP.UniTask` | Core, WebSocket, UniTask | compiled only with `com.cysharp.unitask` (`TURBOHTTP_UNITASK`); split tracked in #116 |
| `TurboHTTP.Editor` | Core, Observability | HTTP Monitor window |
| `TurboHTTP.Complete` | all runtime modules | aggregation asmdef, contains no scripts (#7) |

- String references in asmdefs, never GUIDs. Namespaces are `TurboHTTP.<Module>`.
- Modules reach `Core.Internal` through `InternalsVisibleTo` in `Runtime/Core/AssemblyInfo.cs`; third-party
  interceptors cannot (#76). Do not widen that list casually.
- Auth reaches JSON through reflection (`ProjectJsonBridge`), a hidden dependency (#67).

## Request pipeline

- `UHttpClientOptions.Interceptors` is a `List<IHttpInterceptor>`; each interceptor implements
  `DispatchFunc Wrap(DispatchFunc next)`. The chain is built once at client construction; an empty
  list dispatches straight to the transport. Request flow runs in list order, response callbacks in
  reverse.
- Responses flow through `IHttpHandler` callbacks (`OnRequestStart`, `OnResponseStart`, body,
  `OnResponseEnd`/`OnResponseError`). `SendBufferedAsync` collects the body
  (`BufferedDispatchBridge`); `SendStreamingAsync` returns at headers with an `IResponseBodySource`
  (`StreamingDispatchBridge`). Handlers that replace the body own disposal of the one they replaced.
- Ordering matters and is not enforced yet (#55). Known hazards: Cache outside Auth bypasses the
  authorized-request guard; Concurrency outside Retry holds permits during backoff. Auth binds its
  token to the first request's origin and Cookie stores Set-Cookie per redirect hop in either order
  (#56, #58), but Cookie outside Redirect attaches the Cookie header for the first hop only.
- Module conveniences are extension methods in the owning module (`WithJsonBody` in JSON,
  `WithBearerToken` in Auth); Core's request API keeps raw HTTP primitives.
- `UHttpClient.Dispose()` disposes `IDisposable` interceptors (ownership issue #63). Disposal is
  idempotent everywhere (`RawSocketTransport` uses `Interlocked.CompareExchange`).
- `UHttpRequest` is mutable and single-lease: `UHttpClient.CreateRequest` allocates a new instance (not
  pooled, #62), and `Dispose`/`SendBufferedAsync` end the lease. The lease completes (owned body disposed)
  exactly once, after dispose, the end of the send and every response hold (#148); a stale reference
  always throws `ObjectDisposedException`.

## Transport

- `HttpTransportFactory.Register(Func<IHttpTransport>)` with a `Lazy<T>` default. The default HTTP and
  WebSocket transports are registered by `TurboHttpBootstrap` (`Runtime/Bootstrap/`, in
  `TurboHTTP.Complete`) from `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` (plus
  `[InitializeOnLoadMethod]` in the Editor); the assembly is `[AlwaysLinkAssembly]` so stripping keeps it.
  Each run disposes the previous session's default transport (Enter Play Mode without domain reload).
  The same bootstrap registers TurboHTTP.JSON with `ProjectJsonBridge`. The `[ModuleInitializer]`s in
  Transport and WebSocket.Transport are only a secondary path; projects that exclude `TurboHTTP.Complete`
  call `RawSocketTransport.EnsureRegistered()`, `RawSocketWebSocketTransport.EnsureRegistered()` and
  `JsonModuleRegistration.EnsureRegistered()` themselves. Player-build and device checks: #100, #99.
- `TcpConnectionPool`: per-host pooling, Happy Eyeballs, DNS/connect timeouts. `ConnectionLease` is a
  class whose idempotent `Dispose` always releases the per-host permit.
- Socket I/O modes (`SocketIoMode`): `NetworkStream` (default), `Saea`, `PollSelect` (#38).
- TLS providers behind `ITlsProvider`/`TlsProviderSelector`: `SslStream` (OS-negotiated protocol,
  TLS 1.2 minimum enforced after handshake, ALPN via runtime probe) and BouncyCastle. Auto selection
  and the fallback rules are in `platform.md`.
- HTTP/1.1: `Http11RequestSerializer`, `Http11ResponseParser`, `Http11ResponseBodySource`; framing
  headers are transport-owned; CR/LF/token validation at serialization.
- HTTP/2: `Http2ConnectionManager` → `Http2Connection` (partial classes: `.ReadLoop`, `.Send`,
  `.Lifecycle`), `HpackEncoder`/`HpackDecoder`, pooled `Http2Stream`. One read loop owns inbound
  frames; `_writeLock` serializes outbound frames.
- Proxies: CONNECT tunnels keyed origin × proxy (`originHost:originPort|via|proxyHost:proxyPort`);
  direct and tunneled HTTP/2 manager entries never alias; `Proxy-Authorization` is stripped before
  origin dispatch.
- Timeouts: `RawSocketTransport` applies `CancelAfter(request.Timeout)` and maps expiry to
  `UHttpErrorType.Timeout`; retry treats it as retryable via `IsRetryable()`.

## Contracts that reviews keep catching

- **Handler callbacks.** Order is `OnRequestStart` → `OnResponseStart` → body → exactly one of
  `OnResponseEnd`/`OnResponseError`. `OnResponseError` is only valid before the response starts;
  later failures surface through the `IResponseBodySource`. Cancellation propagates as
  `OperationCanceledException`, never as an error callback. A throwing downstream callback must still
  leave every completion source resolved; handler faults are wrapped in `HandlerCallbackException`,
  never re-reported to the same handler, and never tear down a shared HTTP/2 connection.
- **Request mutation.** `UHttpRequest.With*` mutates in place. Interceptors that rewrite a request
  use `CopyWithSharedContent()` (same dispatch) or `Clone()` (detached), call
  `context.UpdateRequest(...)`, restore the original in `finally`, and dispose the copy if `next()`
  throws. Stream bodies cannot be detached-cloned.
- **Body wrappers** (decompression, tee, observed, plugins) delegate `DrainAsync`, `Abort`,
  `GetTrailersAsync` and `TryGetBufferedData`. Logging, metrics, monitor and cache completion hook on
  `DisposeAsync`, not EOF; a cache commit needs natural EOF plus trailers.
- **Replayability.** Non-replayable bodies hard-gate retry and body-preserving redirects; bodies of
  discarded responses are drained with a bounded budget, then `Abort()`.
- **Ownership transfer.** After handing off a pooled object or lease, null the local so `finally`
  does not release it again (`lease.TransferOwnership(); lease = null;`). Snapshot status/headers
  before returning a pooled `ParsedResponse`. Never return a buffer while an OS operation or another
  thread may still touch it (SAEA in-flight flags, PollSelect thread join, HTTP/2 abort vs read).
  A finalizer never calls pool callbacks.
- **Shared config.** Never mutate caller-owned or static configuration per request: clone it
  (`RetryPolicy.Default` returns copies, `HttpHeaders.Empty` is frozen, `OAuthClient` must not edit the
  caller's config).
- **Static state in Unity code** (dispatcher, decoder registry, schedulers, temp-file manager) is reset
  in a `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` method that also disposes replaced
  primitives, so Enter Play Mode without domain reload works.
- **Background work** always has an owner that observes its exception and awaits it on dispose.
- **New transports** (including test doubles) set `TransportBehaviorFlags.SelfDrainsResponseBody`
  when they drain bodies themselves; `RetryDetectorHandler` depends on it.
- Optional modules never use Core's internal dispatch bridges; use the public
  `TransportDispatchHelper`. Shared policies (e.g. `ResponseBodyDiscardHelper`) live in Core.

## Shared infrastructure

- Pooling lives in `TurboHTTP.Core.Internal`: `ObjectPool<T>`, `ByteArrayPool`, `ArrayPoolMemoryOwner`,
  `PooledArrayBufferWriter`, `SegmentedBuffer` (16 KB pooled segments; `UHttpResponse.Body` flattens
  lazily, `BodySequence` exposes the `ReadOnlySequence<byte>`). Diagnostics (`PoolHealthReporter`,
  `PooledBuffer<T>`) live in Observability.
- `HttpHeaders`: case-insensitive, multi-value (`Dictionary<string, List<string>>`); not thread-safe.
- `RequestContext`: lock-protected timeline and state; shared by HTTP/2 continuations.
- `HttpMetrics`: public fields for `Interlocked`; doubles stored as `long` bits for 32-bit atomicity.
- `ConcurrencyLimiter`: global permit before per-host permit; releases the global one if the
  per-host acquire fails.
- Latin-1: `Encoding.GetEncoding(28591)` cached, with a custom fallback when IL2CPP strips it
  (`Encoding.Latin1` is .NET 5+ only).

## Older documents

`Development/docs/architecture.md` and the phase docs before 22 still describe the pre-interceptor
`HttpPipeline`/`*Middleware` API (#107). Trust the code and this guide over them.
