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

Required builder helpers:

- `WithBody(byte[] body)`
- `WithBody(ReadOnlyMemory<byte> body)`
- `WithLeasedBody(IMemoryOwner<byte> owner, int length)`
- `WithStreamBody(Stream stream, long? contentLength = null, bool leaveOpen = false)`
- `WithBodyFactory(Func<CancellationToken, ValueTask<Stream>> factory, long? contentLength, bool leaveOpen = false)`
- `WithFileBody(string path, int bufferSize = 32768)`

### `UHttpStreamingResponse`

```csharp
public sealed class UHttpStreamingResponse : IAsyncDisposable
{
    public HttpStatusCode StatusCode { get; }
    public HttpHeaders Headers { get; }
    public Stream Body { get; }
    public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct = default);
}
```

Rules:

- the connection/stream lease remains owned by the response until the body is fully consumed or the response is disposed
- disposing early aborts or drains according to protocol and policy
- trailers are available only after end-of-body

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

    internal abstract bool TryGetBufferedData(out ReadOnlyMemory<byte> data);
    internal abstract ValueTask<RequestBodyReadSession> OpenReadSessionAsync(CancellationToken ct);
}
```

Required concrete implementations:

1. `EmptyRequestBody`
2. `BufferedRequestBody`
3. `OwnedMemoryRequestBody`
4. `StreamRequestBody`
5. `FactoryRequestBody`
6. `FileRequestBody`

`RequestBodyReadSession` is an internal single-send object that guarantees:

- one active reader per dispatch attempt
- deterministic owner disposal
- explicit reset/reopen semantics for retries

### Replayability Enum

```csharp
public enum RequestBodyReplayability
{
    Replayable,
    ReplayableViaFactory,
    NonReplayable
}
```

`StreamRequestBody` is only `Replayable` when both conditions hold:

1. the stream is seekable
2. the body wrapper owns reset semantics and can restore the original position safely

Otherwise it is `NonReplayable`.

### Response Body Model

The transport exposes a pull-based internal source:

```csharp
internal interface IResponseBodySource : IAsyncDisposable
{
    long? Length { get; }
    ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct);
    ValueTask DrainAsync(CancellationToken ct);
    void Abort();
    ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct);
}
```

The public `Stream` on `UHttpStreamingResponse` is a thin adapter over `IResponseBodySource`.

Rationale:

- pull-based reads model backpressure naturally
- the same source can be wrapped by decompression, tee/caching, monitor preview, or buffered collection
- `Stream` is familiar to Unity/.NET consumers and works for file writes, JSON readers, and custom parsers

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

#### Unknown-Length Bodies

If `Content.Length` is unknown:

- HTTP/1.1 sends `Transfer-Encoding: chunked`
- body bytes are chunk-framed on the fly
- request trailers are out of scope for 22a

#### Retry / Failure Semantics

Automatic retry on HTTP/1.1 request failure is allowed only when:

1. the method is retryable by policy
2. the body is replayable
3. no bytes were committed to the wire, or replay is explicitly supported

If a one-shot body has started sending and the connection fails:

- the connection is discarded
- the request fails immediately with a dedicated non-replayable-body transport error

No silent full-body buffering is permitted as a fallback.

### HTTP/1.1 Response Streaming

`Http11ResponseParser` must split into two stages:

1. **header parse stage**
2. **body reader stage**

New flow:

1. parse status line + headers only
2. determine framing strategy (`Content-Length`, chunked, read-to-end, or no-body)
3. create `Http11ResponseBodySource`
4. call `handler.OnResponseStartAsync(...)`
5. buffered or streaming consumer pulls bytes from the source

#### Connection Reuse Rules

The HTTP/1.1 connection lease remains attached to the body source until one of these happens:

1. body fully consumed
2. `DrainAsync(...)` finishes successfully
3. body is aborted and the connection is closed

If a consumer disposes early:

- if the remaining body is small and framed, `DrainAsync(...)` may preserve keep-alive
- otherwise the connection is closed

No unread-bytes ambiguity is allowed when returning a connection to the pool.

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
- a stream reset after bytes were sent is not retryable unless the body is replayable and policy permits it

### HTTP/2 Response Streaming

`Http2Stream` already receives DATA frames incrementally, but today it forwards them through callbacks with no pull contract. 22a replaces that with a bounded per-stream body source.

Required design:

1. DATA frames are appended into a pooled bounded queue/ring buffer owned by the `Http2Stream`
2. `ReadAsync(...)` pulls from that queue
3. `WINDOW_UPDATE` is sent when bytes are consumed by the reader, not merely when bytes arrive
4. early dispose triggers `RST_STREAM(CANCEL)` and releases all queued buffers
5. trailers are completed into `GetTrailersAsync(...)`

Initial target limit:

- `DefaultHttp2PerStreamReceiveBufferBytes = 256 KB`

The exact cap may later be tied to local window settings, but the first implementation must keep it explicit and bounded.

---

## Interceptor and Module Refactor Plan

### Decompression

Current state:

- buffers the compressed response body
- decompresses only on end-of-body

22a change:

- replace with `DecompressionBodySource`
- wrap the underlying body source in a persistent `GZipStream` / `DeflateStream`
- decompress incrementally on reads
- enforce decompressed-size limits during streaming, not after full buffering

### Retry

Current state:

- relies on the HTTP/1.1 body already being drained before callbacks are suppressed

22a change:

- retry decision happens at response start
- if the response is retryable and not committed downstream:
  - HTTP/1.1: drain or close explicitly
  - HTTP/2: abort the stream explicitly
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

- replace with a tee body source
- cache store consumes the tee incrementally
- cache commit happens only after successful EOF + trailers
- partially consumed or aborted streaming responses do not produce cache entries

### Logging / Metrics / Monitor

22a change:

- metrics count streamed bytes as they are consumed
- logging defaults to headers + bounded preview only
- monitor capture uses bounded preview capture by default, not full-body retention
- any full-body observability mode must be explicitly opt-in and documented as a memory-expensive path

### File Downloader

`FileDownloader` moves entirely to `SendStreamingAsync(...)`:

- writes chunks directly to `FileStream`
- reports progress per chunk
- never buffers the full response in managed memory
- preserves checksum validation by hashing incrementally while writing

---

## Performance and Memory Targets

### Initial Thresholds

These are starting targets, not frozen constants:

| Setting | Initial Target |
|--------|----------------|
| `SmallBufferedRequestThresholdBytes` | 32 KB |
| `SmallBufferedResponseThresholdBytes` | 32 KB |
| `DefaultStreamingSendBufferBytes` | 32 KB |
| `DefaultStreamingReceiveBufferBytes` (HTTP/1.1) | 64 KB |
| `DefaultHttp2PerStreamReceiveBufferBytes` | 256 KB |
| `BufferedDrainReuseThresholdBytes` (HTTP/1.1) | 64 KB |

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
2. The streaming path must not allocate per chunk in normal operation.
3. HTTP/2 receive buffering must stay bounded per stream.
4. No module may reintroduce an accidental whole-body copy on the transport hot path.

---

## Sub-Phase Plan

| Sub-Phase | Name | Effort |
|-----------|------|--------|
| 22a.1 | Core Body Model and Public API Split | 3-4 days |
| 22a.2 | HTTP/1.1 Streaming Send/Receive | 4-5 days |
| 22a.3 | HTTP/2 Streaming Send/Receive | 4-5 days |
| 22a.4 | Interceptor and Module Streaming Rewrite | 4-6 days |
| 22a.5 | Buffered Fast Path and Performance Tuning | 3-4 days |
| 22a.6 | Validation, Benchmarks, Mobile/IL2CPP Pass | 3-5 days |

### 22a.1 Core Body Model and Public API Split

Deliverables:

1. `UHttpRequestBody` hierarchy
2. `UHttpStreamingResponse`
3. `SendBufferedAsync(...)`
4. `SendStreamingAsync(...)`
5. updated `IHttpHandler` contract with `OnResponseStartAsync(..., IResponseBodySource, ...)`
6. buffered collector rewrite

Complete when:

- all buffered-only request-body assumptions are removed from Core
- a streaming response can be opened without buffering the body
- buffered APIs are layered above the same substrate

### 22a.2 HTTP/1.1 Streaming Send/Receive

Deliverables:

1. known-length request streaming
2. chunked request streaming
3. split header/body response parsing
4. `Http11ResponseBodySource`
5. early-dispose drain/close policy

Complete when:

- large uploads do not allocate `O(body)` memory
- large downloads can stream to disk with bounded memory
- keep-alive reuse remains correct after partial/aborted reads

### 22a.3 HTTP/2 Streaming Send/Receive

Deliverables:

1. producer-fed DATA send path
2. bounded per-stream receive queue
3. flow-control accounting on bytes consumed
4. streaming trailers completion
5. abort / `RST_STREAM` behavior for early disposal

Complete when:

- one slow HTTP/2 consumer does not force unbounded per-stream buffering
- flow control remains correct under concurrent streams

### 22a.4 Interceptor and Module Streaming Rewrite

Deliverables:

1. incremental decompression
2. explicit retry drain/abort behavior
3. explicit redirect drain/abort behavior
4. tee-based cache store
5. bounded observability capture
6. streaming file downloader

Complete when:

- no optional module forces whole-body buffering by default
- module behavior is correct for both buffered and streaming response modes

### 22a.5 Buffered Fast Path and Performance Tuning

Deliverables:

1. small-body thresholds
2. direct buffered request-body send path
3. direct buffered response collector path
4. handler/body-source wrapper pooling where the data proves it matters
5. allocation/latency tuning passes

Complete when:

- small JSON and form workloads do not regress materially
- large streaming paths remain bounded and allocation-light

### 22a.6 Validation, Benchmarks, Mobile/IL2CPP Pass

Deliverables:

1. runtime and editor test coverage
2. transport benchmarks
3. IL2CPP iOS/Android validation
4. memory profiling on large upload/download scenarios
5. documentation updates

Complete when:

- both specialist rubrics sign off
- performance and memory targets are demonstrated, not inferred

---

## Planned File Impact

Expected core/transport files:

- `Runtime/Core/UHttpRequest.cs`
- `Runtime/Core/UHttpResponse.cs`
- `Runtime/Core/IHttpHandler.cs`
- `Runtime/Core/UHttpClient.cs`
- `Runtime/Core/Pipeline/ResponseCollectorHandler.cs` -> buffered collector replacement
- `Runtime/Transport/RawSocketTransport.cs`
- `Runtime/Transport/Http1/Http11RequestSerializer.cs`
- `Runtime/Transport/Http1/Http11ResponseParser.cs`
- `Runtime/Transport/Http2/Http2Connection.cs`
- `Runtime/Transport/Http2/Http2Connection.Send.cs`
- `Runtime/Transport/Http2/Http2Stream.cs`

Expected module files:

- `Runtime/Middleware/DecompressionInterceptor.cs`
- `Runtime/Middleware/DecompressionHandler.cs` -> replaced by source wrapper form
- `Runtime/Middleware/RedirectInterceptor.cs`
- `Runtime/Middleware/RedirectHandler.cs`
- `Runtime/Retry/RetryInterceptor.cs`
- `Runtime/Retry/RetryDetectorHandler.cs`
- `Runtime/Cache/CacheInterceptor.cs`
- `Runtime/Cache/CacheStoringHandler.cs`
- `Runtime/Observability/LoggingHandler.cs`
- `Runtime/Observability/MetricsHandler.cs`
- `Runtime/Observability/MonitorHandler.cs`
- `Runtime/Files/FileDownloader.cs`

Expected test areas:

- `Tests/Runtime/Transport/Http1/`
- `Tests/Runtime/Transport/Http2/`
- `Tests/Runtime/Middleware/`
- `Tests/Runtime/Retry/`
- `Tests/Runtime/Cache/`
- `Tests/Runtime/Files/`
- `Tests/Runtime/Performance/`

No asmdef layering changes are expected, but every touched module must be re-checked against its existing assembly boundary before implementation begins.

---

## Validation and Benchmark Plan

### Functional Coverage

1. HTTP/1.1 known-length upload from `FileStream`
2. HTTP/1.1 chunked upload from unknown-length stream
3. HTTP/1.1 large download streamed to file
4. HTTP/1.1 early-dispose response closes or drains correctly
5. HTTP/2 large upload with flow-control stalls
6. HTTP/2 large download with slow consumer
7. HTTP/2 concurrent mixed-size streams without unbounded buffering
8. retry/redirect behavior with replayable vs non-replayable bodies
9. decompression while streaming
10. cache commit only after successful full-body completion

### Performance Coverage

1. 1 KB JSON GET/POST buffered roundtrip
2. 32 KB JSON buffered roundtrip
3. 5 MB upload: buffered vs stream body
4. 100 MB download to file: buffered vs streaming response
5. 10 concurrent 10 MB HTTP/2 downloads with one intentionally slow consumer
6. allocation-gate tests on small-body buffered paths

### Platform Coverage

1. Unity Editor Mono
2. Standalone IL2CPP
3. iOS IL2CPP
4. Android IL2CPP

Both required specialist review rubrics from `AGENTS.md` are mandatory before implementation is considered complete:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

---

## Risks and Open Questions

1. **HTTP/2 body-source queue design:** ring buffer vs segmented queue should be chosen from benchmark data, not aesthetics.
2. **Request replayability from seekable streams:** the wrapper must own reset semantics; relying on arbitrary caller-managed stream position is too fragile.
3. **Buffered fast-path tuning:** thresholds will need measurement on mobile, not only desktop.
4. **Observability defaults:** monitor/logging must remain useful without silently reintroducing whole-body buffering.
5. **Streaming API ergonomics:** `Stream` is the required baseline API, but the internal source abstraction must remain general enough for future specialized readers.

---

## Success Criteria

Phase 22a is successful when all of the following are true:

1. large uploads and downloads no longer require `O(body size)` managed memory by default
2. small buffered requests/responses remain competitive with the 22.3 baseline
3. retry/redirect behavior is explicit and correct for replayable and non-replayable bodies
4. decompression, file download, and cache store operate incrementally
5. HTTP/2 flow control is tied to bytes actually consumed, not merely received
6. no optional module quietly reintroduces full-body buffering on the hot path
