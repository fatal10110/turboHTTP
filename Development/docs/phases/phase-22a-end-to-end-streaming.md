# Phase 22a: End-to-End Request/Response Streaming

**Milestone:** M4 (v2.0 follow-up)  
**Dependencies:** Phase 19a-19c, Phase 22.1-22.4  
**Estimated Complexity:** Very High  
**Critical:** Yes — this is the phase that removes the remaining buffered-body constraints from the client/transport stack.  
**Compatibility:** Clean break. No backward compatibility is required.

## Overview

Phase 22 introduced the interceptor boundary and removed the old buffered transport bridge, but the body model is still mixed:

1. request bodies are still fundamentally in-memory payloads
2. HTTP/1.1 response parsing still buffers the full body before the public API sees it
3. `ResponseCollectorHandler` remains the default public API path
4. decompression, file download, cache store, and monitor capture still have buffered assumptions
5. the current synchronous `IHttpHandler.OnResponseData(...)` callback model has no natural backpressure contract

Phase 22a is the clean-break streaming phase. It makes both upload and download streaming first-class, while preserving a separate optimized buffered fast path for small payloads and explicitly bounded memory behavior for large payloads.

The key design goal is not "everything becomes streaming by default". The key design goal is:

- small buffered request/response paths stay fast and allocation-light
- large payloads stop scaling peak memory with payload size
- streaming request/response paths become explicit, replay-safe, and backpressure-aware

---

## Goals

1. Support true request-body streaming to the server for HTTP/1.1 and HTTP/2.
2. Support true response-body streaming from the server for HTTP/1.1 and HTTP/2.
3. Keep buffered request/response handling as an explicit, optimized first-class path for small payloads.
4. Remove all transport-mandated full-body buffering from the hot path.
5. Make replayability explicit so retry/redirect behavior is correct for buffered, seekable, factory-backed, and one-shot bodies.
6. Bound managed-memory growth on all large-payload paths.
7. Preserve IL2CPP/AOT safety and existing assembly-boundary rules.

## Non-Goals

1. WebGL transport redesign. Browser-specific fetch/streaming remains a separate strategy.
2. Brotli support in this phase. Gzip/deflate are the only required compressed-response codings.
3. OS-specific zero-copy syscalls such as `sendfile`. Unity/.NET Standard 2.1 portability takes priority.
4. HTTP/3/QUIC or gRPC implementation. This phase only establishes the transport/body substrate they would consume later.
5. Full backward compatibility with the current buffered-only public API shapes.
6. `Expect: 100-continue` handling for streaming request bodies. When sending a large streaming body, the client could use `Expect: 100-continue` to avoid sending a body the server will reject. This is orthogonal to streaming and deferred to post-22a. Documented as a known gap — especially important for non-replayable bodies.
7. Streaming through proxy connections. `DispatchViaProxyAsync` in `RawSocketTransport.cs` currently uses the full-buffered push-based path. Updating the proxy tunnel for streaming is deferred to post-22a. The streaming API will not silently fall back to buffered behavior through proxy connections — it will use the existing buffered proxy path explicitly.
8. HTTP/1.1 trailer parsing. `GetTrailersAsync` returns `HttpHeaders.Empty` for HTTP/1.1 responses. The current parser already discards trailers (known limitation). Full HTTP/1.1 trailer support is deferred.
9. HTTP/1.1 request trailers. Chunked request encoding sends the empty trailer section terminator (`0\r\n\r\n`) but does not support sending actual trailer fields.

---

## Why Phase 22 Is Not Sufficient

The current 22.x architecture established the right dispatch/interceptor direction, but it intentionally stopped short of full streaming:

- [`Runtime/Core/UHttpRequest.cs`](../../Runtime/Core/UHttpRequest.cs) still models request bodies as `ReadOnlyMemory<byte>`.
- [`Runtime/Transport/Http1/Http11RequestSerializer.cs`](../../Runtime/Transport/Http1/Http11RequestSerializer.cs) still rejects `Transfer-Encoding: chunked` with a request body.
- [`Runtime/Transport/Http1/Http11ResponseParser.cs`](../../Runtime/Transport/Http1/Http11ResponseParser.cs) still parses/drains the full HTTP/1.1 response body before handlers see it.
- [`Runtime/Core/Pipeline/ResponseCollectorHandler.cs`](../../Runtime/Core/Pipeline/ResponseCollectorHandler.cs) still buffers the public response path into a `UHttpResponse`.
- [`Runtime/Middleware/DecompressionHandler.cs`](../../Runtime/Middleware/DecompressionHandler.cs) still buffers the compressed response body before emitting decompressed bytes.
- [`Runtime/Files/FileDownloader.cs`](../../Runtime/Files/FileDownloader.cs) still buffers the entire response before writing to disk.
- [`Runtime/Retry/RetryDetectorHandler.cs`](../../Runtime/Retry/RetryDetectorHandler.cs) still depends on the current HTTP/1.1 "already drained before callback" assumption.

Those constraints are acceptable for 22.x, but they block the real upload/download streaming feature set.

---

## Core Design Principles

### 1. Dual Path, Not One Path

The client must keep two explicit body modes:

- **Buffered mode** — optimized for small known-length payloads and ergonomic APIs.
- **Streaming mode** — optimized for large or unknown-length payloads and bounded memory.

The streaming design must not make the buffered small-body path slower by default.

### 2. Pull-Based Body Consumption

Per-chunk push callbacks (`OnResponseData`) are not sufficient for real streaming because they do not model backpressure well. Phase 22a moves body transfer to pull-based request/response body sources while preserving the Phase 22 interceptor/dispatch model.

### 3. Replayability Is Explicit

Automatic retry and redirect can only be correct when the request body declares whether it can be replayed:

- buffered in-memory body: replayable
- pooled owned memory body: replayable
- seekable stream body: replayable only if reset is supported and verified
- factory-backed stream body: replayable
- one-shot stream body: not replayable

No hidden buffering is allowed just to preserve retries.

### 4. Bounded Memory Beats "Best Effort"

For large payloads, memory limits must be explicit:

- HTTP/1.1 request streaming uses one pooled transfer buffer per active send path
- HTTP/1.1 response streaming holds only framing state plus a bounded transfer buffer
- HTTP/2 response streaming uses a bounded per-stream queue/ring buffer tied to flow control
- observability/caching/decompression must either stream, tee, or bound retained bytes

### 5. Buffered Large Payloads Still Avoid Extra Copies

If the caller explicitly requests buffered behavior for a large payload, the client may still use `O(body size)` memory because the user asked for buffering. The transport must not add a second full-body copy on top of that.

---

## Public API Shape (Clean Break)

The current single buffered response path is replaced with two explicit entry points:

```csharp
Task<UHttpResponse> SendBufferedAsync(UHttpRequest request, CancellationToken ct = default);

Task<UHttpStreamingResponse> SendStreamingAsync(
    UHttpRequest request,
    CancellationToken ct = default);
```

`SendAsync(...)` is removed to avoid ambiguity.

### `UHttpRequest`

`UHttpRequest.Body` is removed and replaced with a body abstraction:

```csharp
public sealed class UHttpRequest
{
    public UHttpRequestBody Content { get; private set; }
}
```

Required builder helpers (Core):

- `WithBody(byte[] body)`
- `WithBody(ReadOnlyMemory<byte> body)`
- `WithLeasedBody(IMemoryOwner<byte> owner, int length)`
- `WithStreamBody(Stream stream, long? contentLength = null, bool leaveOpen = false)`
- `WithBodyFactory(Func<CancellationToken, ValueTask<Stream>> factory, long? contentLength = null)`

Required builder helpers (`TurboHTTP.Files`):

- `WithFileBody(string path, int bufferSize = 32768)` — extension method in `FileRequestBuilderExtensions`

### `UHttpStreamingResponse`

```csharp
public sealed class UHttpStreamingResponse : IAsyncDisposable, IDisposable
{
    public HttpStatusCode StatusCode { get; }
    public HttpHeaders Headers { get; }
    public ResponseBodyStream Body { get; }
    public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct = default);
}
```

#### `IAsyncDisposable` and IL2CPP

`UHttpStreamingResponse` implements both `IAsyncDisposable` and `IDisposable`:

- `IAsyncDisposable` is part of .NET Standard 2.1 and Unity 2021.3 does include it, but `await using` under IL2CPP has known issues on some Unity versions. **22a.1 must validate `IAsyncDisposable` + `await using` on iOS and Android IL2CPP before the API shape is finalized.** If validation fails, the fallback is `IDisposable`-only with an explicit `public ValueTask DisposeAsync()` method (no interface).
- `IDisposable.Dispose()` is provided as a synchronous fallback that calls `Abort()` on the body source and releases the connection lease. It does not attempt drain.
- `ValueTask<HttpHeaders>` on `GetTrailersAsync` must also be validated on IL2CPP — `ValueTask<T>` with generic type arguments on interface methods have been a known AOT issue in past Unity versions.

#### Connection Lease Ownership

Rules:

- the connection/stream lease remains owned by the response until the body is fully consumed or the response is disposed
- disposing early aborts or drains according to protocol and policy (see HTTP/1.1 and HTTP/2 drain rules below)
- trailers are available only after end-of-body
- **leak detection:** if the consumer abandons the `UHttpStreamingResponse` without disposing, the connection lease, semaphore permit, and connection all leak. A `Debug.LogWarning` in the finalizer detects this in development builds. Production builds rely on the `IDisposable` / `IAsyncDisposable` contract.
- for HTTP/1.1, the `ConnectionLease.TransferOwnership()` mechanism (already used for `Http2ConnectionManager`) transfers the lease out of the `using` scope in `DispatchCoreAsync` to the streaming response
- for HTTP/2, the stream's bounded buffer holds a reference to the `Http2Stream`; disposal triggers `RST_STREAM(CANCEL)` and buffer release

### Migration from `SendAsync`

`SendAsync(...)` on `UHttpClient` is removed to avoid ambiguity. The self-send pattern `request.SendAsync(ct)` on `UHttpRequest` is also removed. Migration:

- `await client.SendAsync(request, ct)` → `await client.SendBufferedAsync(request, ct)` (same semantics, same return type)
- `await request.SendAsync(ct)` → `await client.SendBufferedAsync(request, ct)` (caller must hold client reference)
- new streaming path: `await using var response = await client.SendStreamingAsync(request, ct);`

Mandatory compile-surface migration sweep during 22a.1:

- `Runtime/UniTask/UHttpClientUniTaskExtensions.cs`
- `Runtime/JSON/JsonExtensions.cs`
- `Runtime/Auth/OAuthClient.cs`
- `Runtime/Files/FileDownloader.cs` (stays buffered until the later streaming rewrite)
- `Runtime/Unity/UnityExtensions.cs`
- `Runtime/Unity/AudioClipHandler.cs`
- `Runtime/Unity/Texture2DHandler.cs`
- `Runtime/Unity/CoroutineWrapper.cs`

This is a clean-break API change as stated in the Compatibility section.

### Buffered Helpers Stay Layered Above Streaming

`SendBufferedAsync(...)` is not a separate transport implementation. It is a collector layered on the same streaming-capable substrate:

- response headers arrive
- body source is drained into pooled segmented storage
- final `UHttpResponse` is created without an extra full-body copy

That guarantees one behavior source of truth.

---

## Core Body Abstractions

### Request Body Model

The request body contract must support both fast buffered access and streaming access:

```csharp
public abstract class UHttpRequestBody : IDisposable
{
    public abstract bool IsEmpty { get; }
    public abstract long? Length { get; }
    public abstract RequestBodyReplayability Replayability { get; }

    public abstract bool TryGetBufferedData(out ReadOnlyMemory<byte> data);
    internal abstract ValueTask<RequestBodyReadSession> OpenReadSessionAsync(CancellationToken ct);
}
```

Required concrete implementations (all in `TurboHTTP.Core`):

1. `EmptyRequestBody`
2. `BufferedRequestBody`
3. `OwnedMemoryRequestBody`
4. `StreamRequestBody`
5. `FactoryRequestBody`

**`FileRequestBody` lives in `TurboHTTP.Files`**, not Core. This follows the same pattern as `WithJsonBody` → JSON assembly and `WithBearerToken` → Auth assembly. The builder helper `WithFileBody(...)` becomes `TurboHTTP.Files.FileRequestBuilderExtensions`. Core remains free of file system concerns; `TurboHTTP.Files` is the single place for all file I/O abstractions.

#### `RequestBodyReadSession` Lifecycle

`RequestBodyReadSession` is an `internal` class in `TurboHTTP.Core.Internal`. It is a pure coordination object (no transport I/O) that guarantees:

- **one active reader per dispatch attempt** — the session is opened via `OpenReadSessionAsync` and represents exclusive access to the body stream for one send attempt
- **deterministic owner disposal** — `RequestBodyReadSession` implements `IDisposable`. The transport always disposes the session in `finally`, even on connection failure or cancellation
- **explicit reset/reopen semantics for retries:**
  - for replayable bodies (`Replayable`, `ReplayableViaFactory`): `OpenReadSessionAsync` may be called again after the previous session is disposed. The body resets to the beginning.
  - for non-replayable bodies (`NonReplayable`): a second call to `OpenReadSessionAsync` throws `InvalidOperationException`
- **single-reader invariant** — calling `OpenReadSessionAsync` while a previous session is still active (not disposed) throws `InvalidOperationException`. For `FactoryRequestBody` used across concurrent dispatches, each dispatch gets an independent factory-created stream — the invariant is per-body-instance, not per-factory.

#### IL2CPP / AOT Notes

- `ValueTask<RequestBodyReadSession>` triggers AOT generic instantiation. Add `RequestBodyReadSession` to Core's `link.xml` before 22a.6 IL2CPP validation.
- All six concrete `UHttpRequestBody` subclasses use `abstract class` (not generic virtual methods on value types) — safe for IL2CPP.
- `TryGetBufferedData(...)` is public so optional modules can inspect already-buffered request bodies without new Core-internal coupling. `OpenReadSessionAsync(...)` remains `internal abstract`; `FileRequestBody` in `TurboHTTP.Files` uses `InternalsVisibleTo` from Core for that member.

### Replayability Enum

```csharp
public enum RequestBodyReplayability
{
    Replayable,
    ReplayableViaFactory,
    NonReplayable
}
```

`StreamRequestBody` captures `_startPosition = stream.Position` at construction time. Replay seeks to this captured position, not to position 0. This prevents incorrect replay for partial uploads where the stream's initial position is non-zero. `StreamRequestBody` is only `Replayable` when both conditions hold:

1. the stream is seekable
2. the body wrapper owns reset semantics and can restore the original position safely

Otherwise it is `NonReplayable`.

### Detached Clone Semantics

Replayability and detached cloneability are not identical:

- `UHttpRequest.Clone()` remains the **detached clone** API. The returned request must have an independent lifetime from the original request.
- `EmptyRequestBody` clones trivially.
- `BufferedRequestBody` and `OwnedMemoryRequestBody` clone by copying bytes into a new `BufferedRequestBody`.
- `FactoryRequestBody` clones by creating a new wrapper over the same factory delegate and metadata.
- `FileRequestBody` clones by creating a new wrapper over the same path + options.
- `StreamRequestBody` does **not** support detached clone, even when it is sequentially replayable. A single `Stream` instance may be rewound for retries, but it still cannot safely back two detached request objects.

Core also adds an internal shared-content copy helper (`CopyWithSharedContent(...)` or equivalent) for same-dispatch copy-on-write mutations:

- shares the `Content` reference without cloning or opening a second reader
- used by `AdaptiveInterceptor`, `AuthInterceptor`, and similar "clone just to tweak headers/timeout/metadata" flows
- must never be used for queued/background requests, persisted requests, or concurrent dispatches

`BackgroundNetworkingInterceptor` and any queued/persistent replay path use detached clone semantics and reject bodies that cannot produce a detached clone.

### Response Body Model

The transport exposes a pull-based source. This interface is **public in `TurboHTTP.Core`** because it must be implemented by Transport (`Http11ResponseBodySource`, `Http2ResponseBodySource`) and wrapped by optional modules (Decompression, Cache, Monitor) that only reference Core. This parallels `IHttpTransport` — the interface is public; implementation types remain `internal` to their respective assemblies.

```csharp
public interface IResponseBodySource : IAsyncDisposable
{
    long? Length { get; }
    bool TryGetBufferedData(out ReadOnlyMemory<byte> data);
    ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct);
    ValueTask DrainAsync(CancellationToken ct);
    void Abort();
    ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct);
}
```

**Interface shape is frozen in 22a.1** — `TryGetBufferedData` is included from day one to avoid breaking implementations in later sub-phases. `Fault(Exception)` is **removed from the public interface** — it is a transport-internal signal exposed via `internal interface IFaultableResponseBodySource` (see below).

22a.4 extends this with an ownership-transfer fast path for already-buffered responses. It is intentionally a detach/transfer contract rather than `ReadOnlyMemory<byte>` because `UHttpResponse` must safely retain either single-segment or segmented pooled storage.

#### `ReadAsync` Contract

The `ReadAsync` method follows these rules:

1. **Returns 0 only on EOF.** A return value of 0 means the body is fully consumed. There is no "no data available yet" return — the call blocks asynchronously until data arrives or EOF is reached.
2. **Partial reads are permitted.** A call may return fewer bytes than `destination.Length` even when more body bytes remain. Consumers must loop until 0 is returned.
3. **Post-EOF behavior is undefined.** Callers must not call `ReadAsync` after it returns 0.
4. **Cancellation behavior is implementation-dependent:**
   - **HTTP/2 body sources:** Cancellation does not corrupt source state — the bounded queue is separate from the network read. Subsequent `ReadAsync` with a non-cancelled token may succeed.
   - **HTTP/1.1 body sources:** Cancellation transitions the source to a faulted state. Mid-read cancellation may consume partial data from the network stream, making the byte stream position unrecoverable. The connection cannot be reused. Subsequent `ReadAsync` throws `UHttpException`.
   - Consumers must not rely on post-cancellation recovery for connection-scoped sources.
5. **Zero-length destination:** Calling `ReadAsync` with a zero-length `Memory<byte>` returns 0 immediately without affecting state. This is distinct from the EOF return of 0. Consumers must not interpret a zero-length read result as EOF.
6. **Single outstanding read.** Only one `ReadAsync` may be in flight at a time. This matches the `ValueTask<T>` contract — each returned `ValueTask<int>` must be awaited exactly once before the next call. The `Stream` adapter enforces this invariant.
7. **Transport errors throw `UHttpException`.** The body source is responsible for mapping raw `IOException` / socket errors into `UHttpException`. Consumers never see raw transport exceptions from `ReadAsync`.

#### `Fault` — Internal, Not on Public Interface

`Fault(Exception error)` is **NOT** on the public `IResponseBodySource` interface. It is a transport-to-source signal exposed via:

```csharp
internal interface IFaultableResponseBodySource
{
    void Fault(Exception error);
}
```

Both `Http11ResponseBodySource` and `Http2ResponseBodySource` implement this internal interface. After `Fault`:

- Any pending `ReadAsync` (blocked waiting for data) is woken and throws the stored exception (wrapped as `UHttpException` if not already).
- Subsequent `ReadAsync` calls throw the stored exception immediately.
- `DrainAsync` throws immediately.
- The source transitions to a terminal error state.

Module wrappers (`DecompressionBodySource`, `TeeBodySource`) that detect inner source errors call `Abort()` on their inner source, not `Fault`.

For HTTP/2, the bounded per-stream buffer has an explicit error slot that surfaces on the next `ReadAsync`. For HTTP/1.1, the body source propagates the `IOException` from the underlying socket/`SslStream` read.

#### Public `Stream` Adapter

The public `Stream` on `UHttpStreamingResponse` is a custom `ResponseBodyStream` subclass (not a plain `Stream`) over `IResponseBodySource`:

- `ResponseBodyStream.Length` returns `IResponseBodySource.Length` when known (from `Content-Length`), avoiding the `NotSupportedException` that `Stream.Length` throws on non-seekable streams.
- `ResponseBodyStream.CanSeek` returns `false`.
- `ResponseBodyStream.ReadAsync(Memory<byte>, CancellationToken)` delegates to `IResponseBodySource.ReadAsync`.
- `ResponseBodyStream.CanRead` returns `false` after `Dispose`; `ReadAsync` throws `ObjectDisposedException`.
- **No internal read-ahead buffer.** The base `ResponseBodyStream` is a thin zero-overhead adapter. Read-ahead (~8KB) lives in `DecompressionBodySource` (22a.5) which wraps the inner source in a private `BufferedStream` before passing to `GZipStream`. This avoids triple-buffering on the HTTP/1.1 path.

Rationale:

- pull-based reads model backpressure naturally
- the same source can be wrapped by decompression, tee/caching, monitor preview, or buffered collection
- `Stream` is familiar to Unity/.NET consumers and works for file writes, JSON readers, and custom parsers
- `GZipStream.ReadAsync` on .NET Standard 2.1 uses the default `Stream.ReadAsync` base implementation which allocates a `Task<int>` per call — this is a known allocation point, acceptable for decompression paths

---

## Dispatch and Interceptor Contract

Phase 22's dispatch/interceptor shape is retained, but the handler contract changes.

### Updated `IHttpHandler`

`OnResponseData(...)` and `OnResponseEnd(...)` are removed. Body transfer happens through the source object handed out at response start.

```csharp
public interface IHttpHandler
{
    void OnRequestStart(UHttpRequest request, RequestContext context);

    ValueTask OnResponseStartAsync(
        int statusCode,
        HttpHeaders headers,
        IResponseBodySource body,
        RequestContext context);

    void OnResponseError(UHttpException error, RequestContext context);
}
```

Why this shape:

- preserves the interceptor/dispatch architecture from Phase 22
- avoids per-chunk callback churn on the hot path
- keeps the fast path allocation-free when `OnResponseStartAsync(...)` completes synchronously
- gives interceptors a single place to swap or wrap the body source

### Collector Rework

`ResponseCollectorHandler` becomes `BufferedResponseCollectorHandler`.

Behavior:

1. capture status/headers/body source in `OnResponseStartAsync`
2. drain the source into pooled `SegmentedBuffer`
3. construct `UHttpResponse` only once the body is fully read
4. fetch trailers before completing the buffered result

This removes the current split between handler callbacks and a second buffered public layer.

### DispatchBridge Split

`DispatchBridge.cs` is the central orchestration point that bridges `DispatchFunc` to `Task<UHttpResponse>`. After 22a, two separate entry points (`SendBufferedAsync` and `SendStreamingAsync`) need two different orchestration paths:

1. **`BufferedDispatchBridge`** — replaces the current `DispatchBridge.CollectResponseAsync`. Uses the new `BufferedResponseCollectorHandler` to drain the body source into a `SegmentedBuffer`, construct `UHttpResponse`, and complete the task. Same lifecycle guarantees (fail/cancel/complete safety).

2. **`StreamingDispatchBridge`** — new companion. Invokes the dispatch function, captures the `IResponseBodySource` from `OnResponseStartAsync`, constructs `UHttpStreamingResponse` wrapping the body source, and transfers the connection lease ownership to the response. The task completes as soon as headers are available (not after body is fully read). **Lease safety:** the connection lease must be released in a `try/finally` on the error path — if `OnResponseStartAsync` throws or the dispatch fails before ownership transfers to `UHttpStreamingResponse`, the lease is released in `finally` to prevent connection/semaphore leaks.

Both bridges share the same error-handling patterns (`Fail`, `Cancel`, `EnsureCompleted`) and `ContinueWith` attachment logic from the current `DispatchBridge`. The split is necessary because their completion semantics are fundamentally different: buffered completes after full body drain; streaming completes after headers only.

`DispatchBridge.cs` is added to the file impact list.

---

## Transport Plan

### HTTP/1.1 Request Streaming

#### Known-Length Bodies

If `Content.Length` is known:

1. write request line + headers with `Content-Length`
2. stream request bytes directly from the body session to the socket
3. use a pooled transfer buffer only for non-buffered body types
4. bypass the transfer buffer entirely for `TryGetBufferedData(...)`

Buffered fast path:

- for bodies up to `SmallBufferedRequestThresholdBytes` (initial target: 32 KB), write directly from the stored memory
- no extra chunking layer
- no `Stream` adapter allocation

Streaming path:

- default send buffer target: 32 KB
- buffer rented once per dispatch attempt
- returned immediately after request-body completion
- **flush behavior:** for HTTP/1.1 chunked encoding, flush after each chunk by default to reduce latency. For known-length bodies, flush after the full body is written (single syscall is more efficient). A future `StreamingOptions.FlushAfterEachChunk` setting may allow tuning this tradeoff.

#### Content-Length / Transfer-Encoding Enforcement

The request serializer enforces correct framing headers per RFC 9110 Section 8.6. **Transport-set headers override any user-set values:**

- When body length is known: serializer sets `Content-Length` and strips any user-set `Transfer-Encoding`
- When body length is unknown: serializer sets `Transfer-Encoding: chunked` and strips any user-set `Content-Length`
- Empty bodies: serializer strips both `Content-Length` and `Transfer-Encoding` (unless method semantics require `Content-Length: 0`)

This prevents conflicting framing headers from reaching the wire, regardless of what the user set via `WithHeader`.

#### Unknown-Length Bodies

If `Content.Length` is unknown:

- HTTP/1.1 sends `Transfer-Encoding: chunked`
- body bytes are chunk-framed on the fly: each chunk is formatted as `<size-hex>\r\n<data>\r\n` per RFC 9112 Section 7.1
- the terminal chunk sequence is always `0\r\n\r\n` (final chunk + empty trailer section) — request trailers are out of scope for 22a but the trailer section terminator is mandatory
- the chunked encoder treats `ReadAsync` returning 0 as terminal EOF per the `Stream` contract. The terminal chunk is sent exactly once, and no subsequent reads are attempted
- `Expect: 100-continue` handling for streaming request bodies is deferred to post-22a (documented in Non-Goals)

#### Retry / Failure Semantics

Automatic retry on HTTP/1.1 request failure is allowed only when:

1. the method is retryable by policy
2. the body is replayable
3. no **request body bytes** were committed to the wire, or replay is explicitly supported

"No bytes committed" means **no request body bytes committed** — request headers may have already been sent. This aligns with RFC 9110 Section 9.2.2: the server received headers but never saw a complete request, so no action was taken. For idempotent methods, retry is always safe regardless of how many body bytes were sent (provided the body is replayable).

If a one-shot body has started sending and the connection fails:

- the connection is discarded
- the request fails immediately with a dedicated non-replayable-body transport error

No silent full-body buffering is permitted as a fallback.

### HTTP/1.1 Response Streaming

`Http11ResponseParser` must split into two stages:

1. **header parse stage**
2. **body reader stage**

New flow:

1. parse status line + headers only (consuming any `1xx` informational responses — discard them before delivering the final status/headers)
2. determine framing strategy — the body reader factory receives **the request method** in addition to response headers. Framing rules:
   - **No-body responses:** HEAD requests, 1xx, 204, 304 → always produce `EmptyResponseBodySource` regardless of `Content-Length` or `Transfer-Encoding` headers (RFC 9110 Section 9.3.2, Section 15.4.5)
   - **Transfer-Encoding present:** chunked body reader. Per RFC 9112 Section 6.1, `Transfer-Encoding` takes precedence over `Content-Length` when both are present. Stacked transfer codings beyond bare `chunked` are rejected with a clear error (not silently ignored).
   - **Content-Length present (no Transfer-Encoding):** fixed-length body reader
   - **Neither:** read-to-end (connection-close framing)
3. create `Http11ResponseBodySource`
4. call `handler.OnResponseStartAsync(...)`
5. buffered or streaming consumer pulls bytes from the source

#### `BufferedStreamReader` Transfer

The header parse stage uses a `BufferedStreamReader` that may have pre-fetched body bytes into its internal buffer during header parsing. **The `Http11ResponseBodySource` must take ownership of the `BufferedStreamReader` instance** (or an equivalent buffered wrapper), not the raw network stream. Otherwise, pre-fetched body bytes are lost. The `BufferedStreamReader` is transferred from the header-parse stage to the body source at construction time.

#### HTTP/1.1 Trailer Support

HTTP/1.1 trailers are only possible with chunked transfer encoding. The current parser discards trailers (known limitation). For 22a, `GetTrailersAsync` returns `HttpHeaders.Empty` for HTTP/1.1 responses. Full HTTP/1.1 trailer parsing is deferred to a future phase. This is documented as a known limitation, not a regression — the current implementation also discards them.

#### Connection Reuse Rules

The HTTP/1.1 connection lease remains attached to the body source until one of these happens:

1. body fully consumed → connection returned to pool
2. `DrainAsync(...)` finishes successfully → connection returned to pool
3. body is aborted and the connection is closed → connection discarded

#### Early-Dispose Drain-or-Close Policy

If a consumer disposes early, the drain-or-close decision uses a three-condition gate:

1. **Framing is deterministic** — `Content-Length` or chunked (not read-to-end, where remaining bytes are unknown)
2. **Remaining unread bytes <= `BufferedDrainReuseThresholdBytes` (64 KB)** — for `Content-Length` bodies, remaining = `Content-Length` minus bytes already consumed. For chunked bodies, drain proceeds until either EOF is reached within the 64 KB budget or the budget is exceeded (at which point close immediately). This avoids always closing chunked connections while keeping memory bounded.
3. **Response does NOT have `Connection: close`** — if `Connection: close` is present, always close immediately (no drain)

Only if all three conditions are met, `DrainAsync` is attempted with a **linked cancellation token** (`CreateLinkedTokenSource(callerCt, 2secondTimeout)` via `CancellationTokenSource.CancelAfter`). Both the caller's cancellation and the 2-second drain timeout are honored. If either fires, the connection is closed.

No unread-bytes ambiguity is allowed when returning a connection to the pool.

#### Timeout Scope for Streaming Responses

The existing request-level timeout (`CancellationTokenSource.CancelAfter(request.Timeout)`) applies to header receipt only for streaming responses. Once `OnResponseStartAsync` is called and the body source is handed to the consumer, body reads are governed by the consumer's own `CancellationToken`. This means:

- streaming large files does not hit the request timeout
- the consumer is responsible for providing per-read or overall-download timeouts via their cancellation token
- if no cancellation token is provided, body reads may block indefinitely (matches standard `Stream.ReadAsync` behavior)
- half-open TCP connections (where the remote side has closed but no RST/FIN was received) are detected by the consumer's cancellation or by TCP keep-alive socket options

### HTTP/2 Request Streaming

`Http2Connection.SendDataAsync(...)` must read from the request body session incrementally instead of slicing one pre-buffered body.

Required behavior:

- request DATA production is paced by stream window + connection window
- a per-stream send buffer is rented lazily on first non-buffered read
- buffered request bodies can still write DATA frames directly from their stored memory
- end-stream is sent only when the body session reaches EOF

#### Replay Semantics

HTTP/2 retry rules match HTTP/1.1 replay rules:

- replayable bodies may be resent on eligible retries
- non-replayable bodies may not
- a stream reset after DATA frames were sent is not retryable unless the body is replayable and policy permits it
- "no bytes committed" = "no DATA frames sent" (HEADERS frame may have been sent). The boundary is clearer than HTTP/1.1 because HEADERS and DATA are separate frame types.

### HTTP/2 Response Streaming

`Http2Stream` already receives DATA frames incrementally, but today it forwards them through callbacks with no pull contract. 22a replaces that with a bounded per-stream body source.

#### Required Design

1. DATA frames are appended into a pooled bounded queue owned by the `Http2Stream`
2. `ReadAsync(...)` pulls from that queue
3. **Decoupled WINDOW_UPDATE model:**
   - **Connection-level WINDOW_UPDATE** is sent on DATA frame receipt using the existing half-window threshold batching (not literally on every frame). "Immediately" means at receipt time (not deferred to consumption), using the existing threshold to avoid excessive WINDOW_UPDATE frames. Subject to `MaxConnectionBufferedBytes` aggregate limit — when exceeded, connection-level WINDOW_UPDATE is deferred until buffered bytes drop below the limit.
   - **Per-stream WINDOW_UPDATE** is sent when bytes are consumed by the reader (deferred). This provides true per-stream backpressure — the server cannot overshoot the per-stream buffer.
   - This decoupled model is the single most important correctness decision for HTTP/2 streaming. Without it, one slow consumer blocks DATA delivery for all multiplexed streams.
4. Early dispose triggers `RST_STREAM(CANCEL)` and releases all queued buffers (see abort protocol below)
5. Trailers are completed into `GetTrailersAsync(...)`

#### Zero-Body Responses

When HEADERS arrives with END_STREAM set (e.g., status 200 with no body), `Http2ResponseBodySource` is created in a **pre-completed state**. `ReadAsync` returns 0 immediately. `GetTrailersAsync` returns `HttpHeaders.Empty` immediately; a later trailing HEADERS frame is not legal once the initial response HEADERS has already carried END_STREAM.

#### Per-Stream Bounded Queue

**Synchronization model:** `System.Threading.Channels` is not available in Unity 2021.3 without NuGet. The bounded queue is a purpose-built `SingleReaderChannel<T>` in `TurboHTTP.Transport`, using `ManualResetValueTaskSourceCore<int>` for async reader notification. This matches the existing `IValueTaskSource` patterns already used in `Http2Stream`.

Properties:
- **SPSC (single-producer, single-consumer):** producer = `ReadLoopAsync` thread, consumer = caller's async continuation thread
- **Non-blocking enqueue:** the read loop must NEVER block on a full per-stream buffer. If a DATA frame would cause the per-stream buffer to exceed capacity, the stream is reset with `RST_STREAM(FLOW_CONTROL_ERROR)`. This should be rare because per-stream backpressure (deferred WINDOW_UPDATE) prevents the server from overshooting.
- **Async dequeue:** `ReadAsync` blocks asynchronously when the queue is empty, using `ManualResetValueTaskSourceCore` for zero-allocation notification when data arrives
- **Error slot:** an atomic error field that surfaces on the next `ReadAsync` (see internal `IFaultableResponseBodySource.Fault`)
- **Cancellation-aware:** consumer cancellation wakes the pending reader with `OperationCanceledException`

#### Buffer Size and Flow Control Window Reconciliation

Initial target:

- `DefaultHttp2PerStreamReceiveBufferBytes = 256 KB`

**Critical invariant:** the per-stream buffer capacity MUST be >= the initial stream-level receive window advertised to the server in SETTINGS. If the buffer is smaller than the window, the server can legally send more DATA than the buffer can hold, forcing the read loop to reset the stream. With the default RFC 9113 initial window of 65,535 bytes, the 256 KB buffer provides comfortable headroom.

**Recommendation:** advertise a larger initial window size in SETTINGS (e.g., 256 KB to match the buffer) to reduce WINDOW_UPDATE frame overhead. The current default of 65,535 bytes means frequent per-stream WINDOW_UPDATE frames during large transfers.

**`SETTINGS_INITIAL_WINDOW_SIZE` mid-connection changes:** If the server sends a `SETTINGS` frame changing `INITIAL_WINDOW_SIZE` mid-connection, the per-stream buffer capacity must be re-evaluated. If the new window exceeds the buffer, log a warning but do not reallocate — rely on `RST_STREAM(FLOW_CONTROL_ERROR)` as the safety net.

**Aggregate memory bound:** `MaxConnectionBufferedBytes` (default 8 MB) caps the total buffered bytes across all concurrent streams on a single connection. When the aggregate exceeds the limit, connection-level `WINDOW_UPDATE` is deferred (not per-stream — per-stream updates continue as consumed). This prevents a burst of concurrent slow consumers from exhausting memory at the connection level.

**Stall detection:** A coarse-grained scan in the read loop checks `_lastConsumptionTick` on each active stream. If a stream has not been consumed for `Http2StallTimeoutSeconds` (default 60s), the stream is reset with `RST_STREAM(CANCEL)`. This uses no per-stream `Timer` — the read loop scan is sufficient since the read loop is already iterating over frames.

#### Post-RST_STREAM DATA Frame Handling

After sending `RST_STREAM`, the peer may still send DATA frames that were in-flight. Per RFC 9113 Section 5.1, the endpoint must be prepared to receive frames for a short period. The read loop handles post-RST DATA frames by:

1. Decrementing the **connection-level** receive window (mandatory — these bytes count against the connection window)
2. Sending connection-level WINDOW_UPDATE if needed (to keep the connection flowing for other streams)
3. **NOT** delivering bytes to the body source (the stream is dead)
4. Suppressing redundant RST_STREAM for recently-reset streams (optimization — the current pattern of sending a second RST_STREAM is wasteful but harmless)

#### Abort / Early-Dispose Protocol

When the consumer calls `DisposeAsync()` on the streaming response:

1. **Set aborted flag atomically** — `Volatile.Write(ref _aborted, 1)`. This must happen first.
2. **Wake pending reader** — if `ReadAsync` is blocked waiting for data, it is woken and throws `ObjectDisposedException`.
3. **Read loop discards** — on the next `AppendResponseData` call, the read loop checks the aborted flag. If set, it discards the DATA payload and does NOT enqueue. This prevents writing to released pool buffers.
4. **Release queued buffers** — all pooled segments in the bounded queue are returned to the pool.
5. **Write RST_STREAM(CANCEL)** — queued under `_writeLock`. Between setting the aborted flag and writing RST_STREAM, the server may send additional DATA frames — these are handled by step 3.
6. **Release stream resources** — the `Http2Stream` is returned to the pool.

Race condition resolution: the aborted flag is the single source of truth. Both the reader (consumer thread) and the producer (read loop thread) check it atomically before accessing the queue. The read loop never blocks — it either enqueues successfully or discards on abort.

---

## Interceptor and Module Refactor Plan

### Decompression

Current state:

- buffers the compressed response body
- decompresses only on end-of-body

22a change:

- replace with `DecompressionBodySource` (implements `IResponseBodySource`)
- **owns the ~8KB read-ahead buffer** — `ResponseBodyStream` is a thin zero-overhead adapter with no buffer; `DecompressionBodySource` wraps the inner source's `ResponseBodyStream` in a private `BufferedStream` (~8KB) before passing to `GZipStream`. This avoids triple-buffering on the HTTP/1.1 path while ensuring `GZipStream` gets efficient reads
- wrap the underlying body source (via `ResponseBodyStream` adapter + `BufferedStream`) in a persistent `GZipStream` / `DeflateStream`
- decompress incrementally on reads — `GZipStream.Read` calls the inner stream's `Read` incrementally (confirmed: works on .NET Standard 2.1 and Unity IL2CPP Mono)
- enforce decompressed-size limits during streaming: a running counter tracks total decompressed bytes across all `ReadAsync` calls. The decompression bomb limit is configurable via constructor parameter (default 256 MB). If `_maxDecompressedBodySizeBytes` is exceeded, the body source is aborted and `ReadAsync` throws `IOException`
- the `GZipStream` lifetime is tied to the response lifetime — constructed once, never re-initialized

#### GZIP Trailer Validation in Streaming Mode

The current `ValidateSingleGzipTrailer` reads the last 8 bytes of the complete compressed sequence — this is impossible in streaming mode because the sequence is never fully buffered. Resolution:

- **Rely on `GZipStream`'s internal CRC32 validation.** `GZipStream` in `System.IO.Compression` validates the GZIP trailer (CRC32 + ISIZE) internally when it reaches EOF. If validation fails, it throws `InvalidDataException`. This is sufficient for correctness.
- The separate `ValidateSingleGzipTrailer` method is removed for streaming responses. It remains available only for the buffered decompression path (which still uses the complete compressed sequence).
- **Early dispose skips CRC32 validation.** If the consumer disposes before reading to EOF, the GZIP trailer is never reached and CRC32 is never validated. This is acceptable and should be documented.

#### Decompression Bomb Mitigation

The decompressed-size limit is enforced incrementally during streaming (not after full buffering). Test requirement: a compressed response that decompresses to 200 MB must abort at the configured limit, not OOM.

### Retry

Current state:

- relies on the HTTP/1.1 body already being drained before callbacks are suppressed

22a change:

- retry decision happens at response start
- if the response is retryable and not committed downstream, use the `IResponseBodySource` abstraction only — no protocol branching: attempt `body.DrainAsync(ct)` first (lets HTTP/1.1 reuse the connection), fall back to `body.Abort()` if drain fails or times out. HTTP/2 sources can implement `DrainAsync` as a no-op (the stream is independent) and `Abort()` as `RST_STREAM`
- replay eligibility depends on request-body replayability, not on method alone

### Redirect

Current state:

- redispatches after `OnResponseEnd`

22a change:

- redirect decision happens at response start once status + headers are known
- original response body source must be explicitly drained or aborted before redispatch
- follow-up request is only permitted when the body is replayable or the redirect semantics drop the body

### Cache

Current state:

- cache-store handler buffers the response body while forwarding callbacks

22a change:

- replace with a `TeeBodySource` (implements `IResponseBodySource`)
- cache store consumes the tee incrementally — as the consumer reads from the primary source, each chunk is also written to the cache accumulator
- cache commit happens only after successful EOF + trailers
- partially consumed or aborted streaming responses do not produce cache entries

#### Tee Lifecycle: EOF vs Abandon

The `TeeBodySource` must distinguish between two terminal states:

1. **Natural EOF** — the consumer read all bytes, the underlying source reached EOF, and trailers were received. Cache entry is committed.
2. **Consumer abandon** — the consumer disposed early without reading to EOF. The tee accumulator is discarded; no cache entry is produced.

The distinction is tracked via an internal `_completedNaturally` flag set only when `ReadAsync` returns 0 from the underlying source. `DisposeAsync` checks this flag to decide commit vs discard.

#### Accumulation Size Limit

`TeeBodySource` must check against a configurable `MaxCacheableResponseBodyBytes` limit during accumulation. Without this limit, a 500 MB streaming response would grow the tee accumulator to 500 MB, negating the streaming path's bounded-memory benefit.

Two checks are needed:
1. **Pre-tee check:** The Cache interceptor checks `Content-Length` (when known) against `MaxCacheableResponseBodyBytes` and skips installing the tee entirely if the body is too large.
2. **Incremental check:** During accumulation, if the running total of accumulated bytes exceeds the limit, silently detach the accumulator and continue delivering bytes to the consumer. This catches cases where `Content-Length` is absent.

#### Cache Write Failure

If the cache write fails mid-stream (disk full, serialization error), the `TeeBodySource` silently detaches the cache accumulator and continues delivering bytes to the consumer. The response body delivery must never be affected by cache failures.

### Logging / Metrics / Monitor

22a change:

- metrics count streamed response bytes as they are consumed
- request-side metrics use `Content.Length` when known; for unknown-length streaming uploads, the transport records actual request-body bytes sent into `RequestContext` and observability consumes that value on completion
- logging defaults to headers + bounded preview only
- request-body preview uses `request.Content.TryGetBufferedData(...)` only when the body is already buffered. Logging must never open a read session or force buffering just to print a request preview
- monitor capture uses bounded preview capture by default, not full-body retention
- request-body monitor capture follows the same rule: buffered access only when already available; streaming request bodies record metadata such as length/replayability instead of forcing a copy
- any full-body observability mode must be explicitly opt-in and documented as a memory-expensive path

### File Downloader

`FileDownloader` moves entirely to `SendStreamingAsync(...)`:

- writes chunks directly to `FileStream`
- reports progress per chunk
- never buffers the full response in managed memory
- preserves checksum validation by hashing incrementally while writing
- **IL2CPP note:** `FileStream.WriteAsync` on Android/iOS IL2CPP may fall back to synchronous I/O wrapped in `Task.Run`. This is acceptable for file I/O but can cause thread pool pressure under heavy concurrent download loads. Validate in 22a.6 on physical devices.

### CapabilityEnforcedInterceptor and ObservedHandler Redesign

The current `CapabilityEnforcedInterceptor` and `ObservedHandler` in `PluginContext.cs` are deeply tied to the push-based `IHttpHandler` callback model:

1. `RequestMutationSignature` hashes `request.Body` (the `ReadOnlyMemory<byte>` field being removed). After 22a, this must hash `request.Content` (the `UHttpRequestBody` reference) instead. The `GetHashCode()` contract for `UHttpRequestBody` subclasses must be defined.
2. `ResponseEventSignature` encodes `OnResponseData(ReadOnlySpan<byte>)` via CRC32 of raw chunk bytes. After 22a, `OnResponseData` no longer exists — the single `OnResponseStartAsync` callback replaces the multi-callback sequence. The response event signature must be redesigned for the new model.
3. `ObservedHandler` wraps the inner `IHttpHandler` and records callback invocations. It must be rewritten for the new `OnResponseStartAsync` shape — specifically, it needs to observe the `IResponseBodySource` handed to the inner handler and track whether the body was consumed, how many bytes were read, and whether trailers were fetched.

This is a non-trivial redesign that must be completed in 22a.4. `PluginContext.cs` is added to the file impact list.

---

## Performance and Memory Targets

### Threshold Configurability

Thresholds are exposed via a `StreamingOptions` type (runtime-configurable, part of `UHttpClientOptions`). This avoids compile-time-only constants that require code changes for tuning:

```csharp
public sealed class StreamingOptions
{
    public int SmallBufferedRequestThresholdBytes { get; set; } = 32 * 1024;
    public int DefaultStreamingSendBufferBytes { get; set; } = 32 * 1024;
    public int DefaultStreamingReceiveBufferBytes { get; set; } = 64 * 1024;
    public int DefaultHttp2PerStreamReceiveBufferBytes { get; set; } = 256 * 1024;
    public int BufferedDrainReuseThresholdBytes { get; set; } = 64 * 1024;
    public int MaxConnectionBufferedBytes { get; set; } = 8 * 1024 * 1024;
    public int Http2StallTimeoutSeconds { get; set; } = 60;
}
```

- `SmallBufferedResponseThresholdBytes` removed — there is no implementable enforcement mechanism (response size is unknown until fully read; `Content-Length` can be absent or incorrect).
- `MaxConnectionBufferedBytes` — aggregate memory bound for all HTTP/2 concurrent streams on a single connection. Connection-level WINDOW_UPDATE is deferred when aggregate buffered bytes exceed this limit.
- `Http2StallTimeoutSeconds` — stall detection timeout for consumers that stop reading. Uses coarse-grained read loop scan with `_lastConsumptionTick`, no per-stream Timer.

### Initial Thresholds

These are starting targets, not frozen constants. Mobile profiling in 22a.6 may reduce values if per-request working set is too high under concurrent load on low-memory Android devices.

| Setting | Initial Target | Notes |
|--------|----------------|-------|
| `SmallBufferedRequestThresholdBytes` | 32 KB | |
| `DefaultStreamingSendBufferBytes` | 32 KB | |
| `DefaultStreamingReceiveBufferBytes` (HTTP/1.1) | 64 KB | |
| `DefaultHttp2PerStreamReceiveBufferBytes` | 256 KB | Must be >= advertised initial window size |
| `BufferedDrainReuseThresholdBytes` (HTTP/1.1) | 64 KB | Applies to remaining unread bytes, not total body length |
| `MaxConnectionBufferedBytes` | 8 MB | Aggregate HTTP/2 per-connection bound |
| `Http2StallTimeoutSeconds` | 60 s | Coarse-grained, no per-stream Timer |

### Path-by-Path Targets

| Path | Peak Managed Memory Target | Allocation / Perf Target |
|------|----------------------------|--------------------------|
| Small buffered request + small buffered response | body bytes only, no duplicate full-body copy | No more than 5% latency regression vs 22.3 baseline; keep hot-path GC within Phase 19 targets |
| Small buffered request + streaming response | request bytes + <= 64 KB receive buffer | Slight fixed-cost increase allowed; no `O(response size)` growth |
| Streaming request + small buffered response | <= 32 KB send buffer + buffered response bytes | No request-body copy; buffered response still single-copy |
| Streaming request + streaming response | <= 32 KB send buffer + bounded receive buffer | Largest memory win; per-chunk coordination overhead acceptable if throughput remains within 5-10% of raw socket/file baseline |
| Large buffered request + large buffered response | exactly one buffered request body + one buffered response body | No extra full-body transport copy; segmented storage allowed |

### Expected Impact vs Current Client

| Area | Small Buffered Payloads | Large Streaming Payloads |
|------|-------------------------|--------------------------|
| Request memory | Neutral to slightly better | Dramatically better; from `O(body)` to bounded buffer |
| Response memory | Neutral to slightly better | Dramatically better; from `O(body)` to bounded buffer |
| CPU overhead | Slightly lower or neutral if buffered fast path is preserved | Slightly higher per chunk, but acceptable relative to memory savings |
| Retry/redirect behavior | More explicit, no hidden buffering | Safer and more predictable; replayability is explicit |
| Decompression | Neutral for small buffered responses | Significantly better peak memory; no compressed+decompressed full-body double buffer |

### Non-Negotiable Performance Guardrails

1. The buffered small-body path must remain a first-class optimized path, not a streaming wrapper disguised as buffering.
2. The streaming path must not allocate per chunk in normal operation. Validated via streaming-path allocation-gate tests (zero managed allocations per chunk, measured via `GC.GetTotalMemory` before and after N streaming chunks divided by N).
3. HTTP/2 receive buffering must stay bounded per stream.
4. No module may reintroduce an accidental whole-body copy on the transport hot path.
5. Regression benchmarks use a **loopback `HttpListener`-based test server** (not `MockTransport`) to measure actual I/O latency. `MockTransport` has no real I/O and cannot produce meaningful latency measurements.

### Backpressure Notes

- **HTTP/2:** pull-based reads create per-stream backpressure via deferred WINDOW_UPDATE. Connection-level window stays open (WINDOW_UPDATE on receipt) so other streams are not starved.
- **HTTP/1.1:** TCP-level backpressure from not reading the socket. Note: on TLS connections, `SslStream` has its own internal read buffer (~16 KB) that partially decouples socket-level backpressure from application-level reads. The effect is minor but worth documenting.

---

## Sub-Phase Plan

**Review model:** each sub-phase (22a.1 through 22a.5) gets a dual-agent review before proceeding. 22a.6 is the final integration review with full transport benchmarks and IL2CPP validation.

| Sub-Phase | Name | Effort |
|-----------|------|--------|
| 22a.0 | IL2CPP Spike | 0.5 day |
| 22a.1 | Core Body Model and Public API Split | 3-4 days |
| 22a.2 | HTTP/1.1 Streaming Send/Receive | 4-5 days |
| 22a.3 | HTTP/2 Streaming Send/Receive | 4-5 days |
| 22a.4 | Buffered Fast Path and Performance Tuning | 3-4 days |
| 22a.5 | Interceptor and Module Streaming Rewrite | 4-6 days |
| 22a.6 | Validation, Benchmarks, Mobile/IL2CPP Pass | 3-5 days |

**Note:** 22a.0 (IL2CPP spike) is a **blocking prerequisite** — must pass before any 22a.1 implementation begins. It validates `IAsyncDisposable` + `await using`, `ValueTask<T>` with custom struct, and `ManualResetValueTaskSourceCore<int>` version tracking on physical iOS/Android IL2CPP. If `IAsyncDisposable` fails, `UHttpStreamingResponse` falls back to `IDisposable`-only with explicit `DisposeAsync()`. This decision must be locked before downstream work begins.

**Note:** 22a.4 (buffered fast path) is sequenced before 22a.5 (interceptor rewrite) so that the buffered fast-path contract (`IResponseBodySource` detach/transfer fast path, threshold rules, and body-owner semantics) is finalized before interceptors are written against it. This prevents rework if thresholds or body-source type structure change.

### 22a.1 Core Body Model and Public API Split

Deliverables:

1. `UHttpRequestBody` hierarchy (5 subclasses in Core + `FileRequestBody` in Files)
2. `RequestBodyReadSession` in `Core/Internal` with lifecycle rules
3. `IResponseBodySource` as public interface in Core
4. `ResponseBodyStream` adapter (thin zero-overhead custom `Stream` subclass with `Length` support, no read-ahead buffer)
5. `UHttpStreamingResponse` with `IAsyncDisposable` + `IDisposable`
6. `SendBufferedAsync(...)` and `SendStreamingAsync(...)` on `UHttpClient`
7. Updated `IHttpHandler` contract with `OnResponseStartAsync(..., IResponseBodySource, ...)`
8. `BufferedResponseCollectorHandler` rewrite
9. `BufferedDispatchBridge` and `StreamingDispatchBridge` (split from current `DispatchBridge`)
10. `MockResponseBodySource` in Testing assembly (in-memory queue implementation for unit testing without transport)
11. `SingleReaderChannel<T>` in Transport (SPSC async channel for HTTP/2 bounded queue — not in Core)
12. `link.xml` entries for `RequestBodyReadSession` and `IResponseBodySource`

Complete when:

- all buffered-only request-body assumptions are removed from Core
- a streaming response can be opened without buffering the body (validated via `MockResponseBodySource`)
- buffered APIs are layered above the same substrate
- **IL2CPP checkpoint passed:** `IAsyncDisposable` + `await using` and `ValueTask<HttpHeaders>` validated on iOS and Android IL2CPP builds. If validation fails, fallback to `IDisposable`-only + explicit `DisposeAsync()` method.

### 22a.2 HTTP/1.1 Streaming Send/Receive

Deliverables:

1. Known-length request streaming
2. Chunked request streaming (RFC 9112 Section 7.1 compliant, terminal `0\r\n\r\n`)
3. Split header/body response parsing with `BufferedStreamReader` transfer to body source
4. `Http11ResponseBodySource` (Content-Length, chunked, read-to-end, and `EmptyResponseBodySource` for HEAD/1xx/204/304)
5. Early-dispose drain/close policy (three-condition gate + 2s timeout)
6. Connection lease transfer via `ConnectionLease.TransferOwnership()` for streaming responses
7. Timeout scope: request timeout applies to headers only; body reads governed by consumer's cancellation token

Complete when:

- large uploads do not allocate `O(body)` memory
- large downloads can stream to disk with bounded memory
- keep-alive reuse remains correct after partial/aborted reads
- HEAD responses with `Content-Length` produce `EmptyResponseBodySource`
- `Connection: close` responses never attempt drain

### 22a.3 HTTP/2 Streaming Send/Receive

Deliverables:

1. Producer-fed DATA send path (reads from `RequestBodyReadSession` incrementally)
2. Bounded per-stream receive queue (`SingleReaderChannel<T>`, non-blocking enqueue)
3. Decoupled flow-control: connection-level WINDOW_UPDATE on receipt, per-stream on consumption
4. Per-stream buffer capacity >= advertised initial window size
5. Post-RST_STREAM DATA frame handling (connection window accounting without delivery)
6. Abort/early-dispose protocol (atomic aborted flag → discard → RST_STREAM → release)
7. Zero-body response handling (pre-completed `Http2ResponseBodySource`)
8. Streaming trailers completion via `GetTrailersAsync`
9. Stall detection: if a consumer doesn't read for a configurable timeout, the stream is reset with `RST_STREAM(CANCEL)` to prevent connection-level degradation

Complete when:

- one slow HTTP/2 consumer does not force unbounded per-stream buffering
- one slow consumer does not block DATA delivery for other streams (connection-level window stays open)
- flow control remains correct under concurrent streams
- read loop never blocks on per-stream buffer operations

### 22a.4 Buffered Fast Path and Performance Tuning

Deliverables:

1. Small-body thresholds via `StreamingOptions` type (runtime-configurable, with sensible defaults)
2. Direct buffered request-body send path (bypass `Stream` adapter for `TryGetBufferedData`)
3. Direct buffered response collector path (optimized drain into `SegmentedBuffer`)
4. `IResponseBodySource` fast-path contract: `TryDetachBufferedBody(out DetachedBufferedBody body)` for body sources that can transfer already-buffered ownership directly into `UHttpResponse` with no extra copy
5. Handler/body-source wrapper pooling where benchmarks prove it matters
6. Allocation/latency tuning passes

Complete when:

- small JSON and form workloads do not regress materially
- large streaming paths remain bounded and allocation-light
- `StreamingOptions` type is named and documented

### 22a.5 Interceptor and Module Streaming Rewrite

**Prerequisite:** 22a.4 must be complete so the buffered fast-path contract is finalized before interceptors are written against it.

Deliverables:

1. `DecompressionBodySource` with incremental decompression, `GZipStream` internal CRC32 validation, decompressed-size limit enforcement
2. Explicit retry drain/abort behavior (retry decision at response start, body source drain/close before redispatch)
3. Explicit redirect drain/abort behavior (redirect decision at response start, body replayability check)
4. `TeeBodySource` for cache store (EOF vs abandon tracking, silent detach on cache write failure)
5. Bounded observability capture (headers + bounded preview, opt-in full-body mode)
6. Streaming file downloader (`SendStreamingAsync` + incremental `FileStream` write + incremental hash)
7. `CapabilityEnforcedInterceptor` + `ObservedHandler` redesign for new `OnResponseStartAsync` model
8. `PluginContext.RequestMutationSignature` update (hash `request.Content` instead of `request.Body`)

Complete when:

- no optional module forces whole-body buffering by default
- module behavior is correct for both buffered and streaming response modes
- `CapabilityEnforcedInterceptor` correctly detects request mutation and response observation under the new model

### 22a.6 Validation, Benchmarks, Mobile/IL2CPP Pass

Deliverables:

1. Runtime and editor test coverage (see Validation section below)
2. Transport benchmarks using **loopback `HttpListener`-based test server** (not `MockTransport`) for latency regression measurement
3. IL2CPP iOS/Android validation on physical devices:
   - `IAsyncDisposable` + `await using` (if not already validated in 22a.1)
   - `ValueTask<RequestBodyReadSession>` AOT instantiation
   - `GZipStream` streaming decompression performance
   - `FileStream.WriteAsync` behavior under concurrent load
   - async state machine boxing on hot-path `ReadAsync` chain (validate synchronous completion when data is buffered)
4. Memory profiling on large upload/download scenarios
5. Streaming-path allocation-gate tests (zero managed allocations per chunk, measured via `GC.GetTotalMemory` before and after N streaming chunks)
6. Mobile-specific threshold tuning (32KB defaults may need reduction on low-memory Android under concurrent load)
7. Documentation updates

Complete when:

- both specialist rubrics sign off on each sub-phase individually, and on the final integration
- performance and memory targets are demonstrated, not inferred
- loopback benchmarks confirm <= 5% latency regression for buffered small-body paths vs 22.3 baseline

---

## Planned File Impact

### Core files:

- `Runtime/Core/UHttpRequest.cs` — `Body` field removed, replaced with `Content: UHttpRequestBody`. `SendAsync` self-send method removed. Detached clone rules plus shared-content copy helper are added for request mutation / queueing.
- `Runtime/Core/UHttpResponse.cs` — unchanged externally; internal `BodySequence` usage preserved
- `Runtime/Core/IHttpHandler.cs` — `OnResponseData`/`OnResponseEnd` removed, `OnResponseStartAsync` added with `IResponseBodySource` parameter
- `Runtime/Core/UHttpClient.cs` — `SendAsync` replaced with `SendBufferedAsync`/`SendStreamingAsync`
- `Runtime/Core/Pipeline/ResponseCollectorHandler.cs` → `BufferedResponseCollectorHandler`
- `Runtime/Core/Pipeline/DispatchBridge.cs` → split into `BufferedDispatchBridge` + `StreamingDispatchBridge`
- `Runtime/Core/PluginContext.cs` — `CapabilityEnforcedInterceptor.RequestMutationSignature` must hash `Content` instead of `Body`. `ResponseEventSignature` + `ObservedHandler` must be redesigned for new `OnResponseStartAsync` model.
- `Runtime/Core/BackgroundNetworkingInterceptor.cs` — queued replay uses detached clone semantics
- `Runtime/Core/AdaptiveInterceptor.cs` — timeout mutation uses shared-content copy helper; request-length logic moves to `Content.Length`
- `Runtime/Transport/Http2/SingleReaderChannel.cs` — new: SPSC async channel for HTTP/2 bounded queue (in Transport, not Core)
- `Runtime/Core/AssemblyInfo.cs` — verify `InternalsVisibleTo` for Transport (already exists), add for Files if needed

### New Core types:

- `Runtime/Core/IResponseBodySource.cs` — new public interface
- `Runtime/Core/UHttpRequestBody.cs` — new abstract class + 5 concrete implementations
- `Runtime/Core/RequestBodyReplayability.cs` — new enum
- `Runtime/Core/UHttpStreamingResponse.cs` — new public class
- `Runtime/Core/ResponseBodyStream.cs` — new custom `Stream` subclass
- `Runtime/Core/Internal/RequestBodyReadSession.cs` — new internal class
- `Runtime/Core/StreamingOptions.cs` — new type for runtime-configurable thresholds
- `Runtime/Core/DetachedBufferedBody.cs` — new public ownership-carrying buffered response transfer type

### Transport files:

- `Runtime/Transport/RawSocketTransport.cs` — `ConnectionLease.TransferOwnership()` for streaming HTTP/1.1 responses. Proxy tunnel path (`DispatchViaProxyAsync`) must also be updated or explicitly documented as streaming-deferred.
- `Runtime/Transport/Http1/Http11RequestSerializer.cs` — chunked request body encoding
- `Runtime/Transport/Http1/Http11ResponseParser.cs` — split into header-parse + body-reader stages. `BufferedStreamReader` ownership transfer.
- `Runtime/Transport/Http1/Http11ResponseBodySource.cs` — new: Content-Length, chunked, read-to-end, and empty variants
- `Runtime/Transport/Http2/Http2Connection.cs` — read loop changes (decoupled WINDOW_UPDATE, non-blocking enqueue, post-RST handling)
- `Runtime/Transport/Http2/Http2Connection.Send.cs` — incremental `RequestBodyReadSession` reads
- `Runtime/Transport/Http2/Http2Stream.cs` — bounded queue, abort protocol, zero-body pre-completion
- `Runtime/Transport/Http2/Http2ResponseBodySource.cs` — new: bounded queue consumer

### Module files:

- `Runtime/Middleware/DecompressionInterceptor.cs`
- `Runtime/Middleware/DecompressionHandler.cs` → replaced by `DecompressionBodySource`
- `Runtime/Middleware/RedirectInterceptor.cs`
- `Runtime/Middleware/RedirectHandler.cs`
- `Runtime/Auth/AuthInterceptor.cs`
- `Runtime/Auth/OAuthClient.cs`
- `Runtime/Retry/RetryInterceptor.cs`
- `Runtime/Retry/RetryDetectorHandler.cs`
- `Runtime/Cache/CacheInterceptor.cs`
- `Runtime/Cache/CacheStoringHandler.cs` → replaced by `TeeBodySource`
- `Runtime/JSON/JsonExtensions.cs`
- `Runtime/Observability/LoggingInterceptor.cs`
- `Runtime/Observability/LoggingHandler.cs`
- `Runtime/Observability/MetricsInterceptor.cs`
- `Runtime/Observability/MetricsHandler.cs`
- `Runtime/Observability/MonitorInterceptor.cs`
- `Runtime/Observability/MonitorHandler.cs`
- `Runtime/Files/FileDownloader.cs` — streaming rewrite
- `Runtime/Files/FileRequestBody.cs` — new: `FileRequestBody` + `FileRequestBuilderExtensions`
- `Runtime/Unity/UnityExtensions.cs`
- `Runtime/Unity/AudioClipHandler.cs`
- `Runtime/Unity/Texture2DHandler.cs`
- `Runtime/Unity/CoroutineWrapper.cs`
- `Runtime/UniTask/UHttpClientUniTaskExtensions.cs`

### Testing files:

- `Runtime/Testing/MockResponseBodySource.cs` — new: in-memory `IResponseBodySource` for unit tests

### Expected test areas:

- `Tests/Runtime/Core/` — body model, collector, dispatch bridge
- `Tests/Runtime/Core/BackgroundNetworkingTests.cs` — detached clone rules for queued/background replay
- `Tests/Runtime/Transport/AdaptiveInterceptorTests.cs` — shared-content timeout mutation
- `Tests/Runtime/Auth/` — auth and OAuth buffered API migration
- `Tests/Runtime/Transport/Http1/` — streaming send/receive, drain policy, HEAD responses
- `Tests/Runtime/Transport/Http2/` — bounded queue, flow control, abort, zero-body, post-RST
- `Tests/Runtime/Middleware/` — decompression streaming, redirect with replayability
- `Tests/Runtime/Retry/` — retry with replayable/non-replayable bodies
- `Tests/Runtime/Cache/` — tee source, EOF vs abandon, cache write failure
- `Tests/Runtime/Pipeline/` + `Tests/Runtime/Observability/` — request preview / metrics behavior for buffered vs streaming request bodies
- `Tests/Runtime/Files/` — streaming file download, FileRequestBody
- `Tests/Runtime/Unity/` — explicit buffered API migration for Unity helpers
- `Tests/Runtime/Performance/` — allocation gates, loopback benchmarks
- `Tests/Runtime/Testing/` — MockResponseBodySource

### link.xml updates:

- Core `link.xml`: add `RequestBodyReadSession`, `IResponseBodySource`, `ValueTask<RequestBodyReadSession>`, `ValueTask<HttpHeaders>` for IL2CPP AOT
- Transport `link.xml`: add `SingleReaderChannel<T>`, `ManualResetValueTaskSourceCore<int>`, `Http11ResponseBodySource`, `Http2ResponseBodySource` for IL2CPP AOT

No asmdef layering changes are expected, but every touched module must be re-checked against its existing assembly boundary before implementation begins.

---

## Validation and Benchmark Plan

### Functional Coverage

1. HTTP/1.1 known-length upload from `FileStream`
2. HTTP/1.1 chunked upload from unknown-length stream (terminal `0\r\n\r\n` verified)
3. HTTP/1.1 large download streamed to file
4. HTTP/1.1 early-dispose response: drain succeeds when remaining <= 64 KB with deterministic framing and no `Connection: close`
5. HTTP/1.1 early-dispose response: closes immediately when `Connection: close` is present
6. HTTP/1.1 early-dispose response: chunked drain succeeds when EOF reached within 64 KB budget
7. HTTP/1.1 early-dispose response: chunked close when EOF not reached within 64 KB budget
8. HTTP/1.1 HEAD response with `Content-Length` produces `EmptyResponseBodySource` (no body read attempted)
9. HTTP/1.1 204/304 responses produce `EmptyResponseBodySource`
10. HTTP/1.1 request serializer: `Content-Length` set and `Transfer-Encoding` stripped when body length is known
11. HTTP/1.1 request serializer: `Transfer-Encoding: chunked` set and `Content-Length` stripped when body length is unknown
12. HTTP/1.1 response parser: `Transfer-Encoding` takes precedence over `Content-Length` when both present (RFC 9112 Section 6.1)
13. HTTP/1.1 response parser: stacked transfer codings beyond bare `chunked` are rejected with clear error
14. HTTP/1.1 `ReadAsync` cancellation transitions body source to faulted state (connection not reusable)
15. HTTP/1.1 drain uses linked cancellation (both caller CT and 2-second timeout honored)
16. HTTP/2 large upload with flow-control stalls
17. HTTP/2 large download with slow consumer (per-stream backpressure, connection-level window stays open)
18. HTTP/2 concurrent mixed-size streams: one slow consumer does not block other streams' DATA delivery
19. HTTP/2 zero-body response (`HEADERS+END_STREAM`): `ReadAsync` returns 0 immediately
20. HTTP/2 early-dispose: `RST_STREAM(CANCEL)` sent, queued buffers released, post-RST DATA frames handled correctly
21. HTTP/2 stall detection: consumer that stops reading triggers stream reset after timeout (coarse-grained check, no per-stream Timer)
22. HTTP/2 aggregate memory bound: `MaxConnectionBufferedBytes` defers connection-level WINDOW_UPDATE when exceeded
23. HTTP/2 `ReadAsync` cancellation on bounded queue does not corrupt state (stronger guarantee than HTTP/1.1)
24. `SingleReaderChannel<T>` version wrapping: exercise 100,000+ read cycles on both Mono and IL2CPP to validate `ManualResetValueTaskSourceCore<int>` short version counter wrapping
25. Retry/redirect behavior with replayable vs non-replayable bodies
26. Retry with body where headers were sent but no body bytes committed: retry succeeds for replayable body
27. Non-replayable body failure after partial send: dedicated transport error, no silent buffering
28. Decompression while streaming (incremental `GZipStream`)
29. Decompression bomb: compressed response exceeding limit aborts during streaming, not after OOM
30. Decompression with early dispose: CRC32 not validated (acceptable, documented)
31. Cache commit only after successful full-body completion (natural EOF)
32. Cache tee with consumer abandon mid-stream: no cache entry produced
33. Cache tee with cache write failure: consumer continues receiving body, cache silently detaches
34. Cache tee with body exceeding `MaxCacheableResponseBodyBytes`: silently detaches, consumer continues reading
35. `StreamRequestBody` with seekable stream at non-zero position: re-open correctly resets to `_startPosition`
36. `CapabilityEnforcedInterceptor` correctly detects request mutation under new `Content` model
37. `RequestBodyReadSession` lifecycle: second `OpenReadSessionAsync` on non-replayable body throws
38. `RequestBodyReadSession` lifecycle: replayable body allows re-open after previous session disposed
39. `MockResponseBodySource` correctly simulates streaming for unit tests
40. `FileRequestBody` from `TurboHTTP.Files` works with `WithFileBody` extension method

### Performance Coverage

All latency benchmarks use a **loopback `HttpListener`-based test server**, not `MockTransport`.

1. 1 KB JSON GET/POST buffered roundtrip (loopback)
2. 32 KB JSON buffered roundtrip (loopback)
3. 5 MB upload: buffered vs stream body (loopback)
4. 100 MB download to file: buffered vs streaming response (loopback)
5. 10 concurrent 10 MB HTTP/2 downloads with one intentionally slow consumer
6. Allocation-gate tests on small-body buffered paths (existing Phase 19 gates)
7. **Streaming-path allocation-gate tests:** zero managed allocations per chunk (measured via `GC.GetTotalMemory` before/after N streaming chunks, divided by N)
8. Async state machine boxing validation: hot-path `ReadAsync` chain completes synchronously when data is already buffered (no boxing under IL2CPP)

### Platform Coverage

1. Unity Editor Mono
2. Standalone IL2CPP
3. iOS IL2CPP
4. Android IL2CPP

#### IL2CPP Validation Checkpoints

These are validated early (22a.1) and confirmed in 22a.6:

- `IAsyncDisposable` + `await using` on `UHttpStreamingResponse`
- `ValueTask<RequestBodyReadSession>` AOT generic instantiation
- `ValueTask<HttpHeaders>` on `GetTrailersAsync`
- `GZipStream` streaming decompression performance
- `FileStream.WriteAsync` behavior under concurrent load (thread pool pressure)
- async state machine boxing on hot-path `ReadAsync` chain

### Review Model

Both required specialist review rubrics from `AGENTS.md` are mandatory:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

**Per-sub-phase reviews:** each sub-phase (22a.1 through 22a.5) gets a dual-agent review before proceeding to the next. 22a.6 is the final integration review with full transport benchmarks and IL2CPP validation on physical devices.

---

## Risks and Open Questions

### Design Risks

1. **HTTP/2 body-source queue design:** ring buffer vs segmented queue should be chosen from benchmark data, not aesthetics. The `SingleReaderChannel<T>` design uses `ManualResetValueTaskSourceCore` — implementation must be validated for correctness under concurrent stress.
2. **Request replayability from seekable streams:** the wrapper must own reset semantics; relying on arbitrary caller-managed stream position is too fragile.
3. **Buffered fast-path tuning:** thresholds will need measurement on mobile, not only desktop. 32KB defaults may be too large for low-memory Android under concurrent load.
4. **Observability defaults:** monitor/logging must remain useful without silently reintroducing whole-body buffering.
5. **Streaming API ergonomics:** `Stream` is the required baseline API, but the internal source abstraction must remain general enough for future specialized readers.

### Platform Risks

6. **`IAsyncDisposable` on IL2CPP:** `await using` pattern under IL2CPP has known issues on some Unity versions. Validated in 22a.1 with fallback strategy defined.
7. **`ValueTask<T>` AOT instantiation:** `ValueTask<RequestBodyReadSession>` and `ValueTask<HttpHeaders>` require `link.xml` entries for IL2CPP generic instantiation.
8. **`FileStream.ReadAsync`/`WriteAsync` on Android/iOS IL2CPP:** may fall back to synchronous I/O wrapped in `Task.Run`, causing thread pool pressure under concurrent file upload/download loads. Validated in 22a.6.
9. **Async state machine boxing on IL2CPP:** the streaming model introduces deeper async call chains (`ReadAsync` through body source → decompression → tee → consumer). Each `async` method generates a state machine struct that gets boxed on the first non-synchronous `await`. Hot-path `ReadAsync` must complete synchronously when data is already buffered to avoid boxing. Validated in 22a.6.

### Protocol Risks

10. **Connection-level window starvation:** the most critical HTTP/2 correctness risk. Mitigated by the decoupled WINDOW_UPDATE model (connection-level on receipt, per-stream on consumption). Must be stress-tested with concurrent mixed-speed consumers.
11. **Post-RST_STREAM DATA frames:** server may send DATA in-flight after RST_STREAM. Connection-level window accounting for post-RST frames must be correct to avoid connection-level deadlock.
12. **Long-lived streaming connections on mobile:** app suspend/resume can break TLS sessions. NAT devices may drop idle connections (though active data transfer prevents idle timeout). Certificate expiry during long streams is not detectable (TLS does not re-validate after handshake). TCP keep-alive socket options are the only protection for HTTP/1.1.

### Implementation Risks

13. **`ValueTask` reuse safety:** `IResponseBodySource.ReadAsync` returns `ValueTask<int>`. Only one `ReadAsync` may be in flight at a time. The `ResponseBodyStream` adapter enforces this. Consumer code that violates this contract gets undefined behavior.
14. **GZIP trailer validation in streaming mode:** streaming decompression relies on `GZipStream`'s internal CRC32 check. Early dispose skips validation entirely. Documented as acceptable.
15. **Proxy tunnel path (`DispatchViaProxyAsync`):** currently uses the full-buffered push-based path. Streaming through proxy connections is deferred to post-22a. Documented explicitly in Non-Goals to prevent the streaming API from silently falling back to buffered behavior through proxy connections.
16. **TLS renegotiation during streaming:** TLS 1.3 does not support renegotiation. TLS 1.2 renegotiation is handled transparently by `SslStream` (embedded in the record layer). No action needed, but documented for awareness.

---

## Success Criteria

Phase 22a is successful when all of the following are true:

1. large uploads and downloads no longer require `O(body size)` managed memory by default
2. small buffered requests/responses remain competitive with the 22.3 baseline
3. retry/redirect behavior is explicit and correct for replayable and non-replayable bodies
4. decompression, file download, and cache store operate incrementally
5. HTTP/2 flow control is tied to bytes actually consumed, not merely received
6. HTTP/2 aggregate buffered memory is bounded by `MaxConnectionBufferedBytes`
7. no optional module quietly reintroduces full-body buffering on the hot path
8. `SingleReaderChannel<T>` version wrapping validated through 100K+ cycles on IL2CPP
9. `Content-Length`/`Transfer-Encoding` conflict resolution enforced in request serializer
10. Response framing checks `Transfer-Encoding` before `Content-Length` per RFC 9112
