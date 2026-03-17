# Phase 22a.1: Core Body Model and Public API Split

**Depends on:** Phase 22.4 (complete)
**Assemblies:** `TurboHTTP.Core`, `TurboHTTP.Files`, `TurboHTTP.Testing`
**Files to create:** 12 new, 6 modified

---

## Step 0: IL2CPP Spike — `IAsyncDisposable` + `ValueTask<T>` Validation

**BLOCKING PREREQUISITE** — must pass before any 22a.1 implementation begins.

Run a minimal spike on physical iOS and Android IL2CPP devices:

1. Create a type implementing `IAsyncDisposable` in Transport
2. Confirm `await using` works on IL2CPP (Unity 2021.3)
3. Confirm `ValueTask<T>` with custom struct `T` instantiates correctly under AOT
4. Confirm `ManualResetValueTaskSourceCore<int>` version tracking works

If `IAsyncDisposable` + `await using` fails, the API shape for `UHttpStreamingResponse` must fall back to `IDisposable`-only with an explicit `public ValueTask DisposeAsync()` method (no interface). This decision must be locked before any other 22a.1 work begins to avoid rewriting 15+ days of downstream code.

Estimated spike effort: 30 minutes.

---

## Step 1: `RequestBodyReplayability` Enum

**File:** `Runtime/Core/RequestBodyReplayability.cs`

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

---

## Step 2: `UHttpRequestBody` Abstract Class + 5 Concrete Implementations

**File:** `Runtime/Core/UHttpRequestBody.cs`

```csharp
public abstract class UHttpRequestBody : IDisposable
{
    public abstract bool IsEmpty { get; }
    public abstract long? Length { get; }
    public abstract RequestBodyReplayability Replayability { get; }

    public abstract bool TryGetBufferedData(out ReadOnlyMemory<byte> data);
    internal abstract ValueTask<RequestBodyReadSession> OpenReadSessionAsync(CancellationToken ct);

    public virtual void Dispose() { }
}
```

Concrete implementations (all in `TurboHTTP.Core`):

### 2a. `EmptyRequestBody`

- `IsEmpty` → `true`
- `Length` → `0`
- `Replayability` → `Replayable`
- `TryGetBufferedData` → `true`, returns `ReadOnlyMemory<byte>.Empty`
- `OpenReadSessionAsync` → returns session wrapping empty memory

### 2b. `BufferedRequestBody`

- Wraps `ReadOnlyMemory<byte>`
- `IsEmpty` → `memory.Length == 0`
- `Length` → `memory.Length`
- `Replayability` → `Replayable`
- `TryGetBufferedData` → `true`, returns the stored memory
- `OpenReadSessionAsync` → returns session wrapping a `ReadOnlyMemoryStream` over the stored memory

### 2c. `OwnedMemoryRequestBody`

- Wraps `IMemoryOwner<byte>` + `int length`
- `IsEmpty` → `length == 0`
- `Length` → `length`
- `Replayability` → `Replayable`
- `TryGetBufferedData` → `true`, returns `owner.Memory.Slice(0, length)`
- `OpenReadSessionAsync` → returns session wrapping a `ReadOnlyMemoryStream` over the owned memory
- `Dispose` → disposes the `IMemoryOwner<byte>`

### 2d. `StreamRequestBody`

- Wraps `Stream stream`, `long? contentLength`, `bool leaveOpen`
- **Captures `_startPosition = stream.Position` at construction time** — replay seeks to `_startPosition`, not to position 0. This prevents bugs where a caller constructs from a stream at position 500 (partial upload) and a retry incorrectly resets to 0.
- `IsEmpty` → `false` (unknown)
- `Length` → `contentLength`
- `Replayability` → `Replayable` if `stream.CanSeek`, otherwise `NonReplayable`. Note: `CanSeek == true` does not guarantee seek will succeed at runtime (e.g., some `CryptoStream` wrappers). Callers using streams where `CanSeek` returns true but seeking may fail should use `FactoryRequestBody` (`ReplayableViaFactory`) instead.
- `TryGetBufferedData` → `false`
- `OpenReadSessionAsync` → returns session wrapping the stream. For replayable streams, seeks to `_startPosition` on re-open. Throws `InvalidOperationException` on re-open if `NonReplayable`.
- `Dispose` → disposes the stream unless `leaveOpen`

### 2e. `FactoryRequestBody`

- Wraps `Func<CancellationToken, ValueTask<Stream>> factory`, `long? contentLength`
- **No `leaveOpen` parameter** — each factory-created stream is owned by the `RequestBodyReadSession` that wraps it and is disposed when the session is disposed. The factory itself is stateless and has nothing to leave open.
- `IsEmpty` → `false` (unknown)
- `Length` → `contentLength`
- `Replayability` → `ReplayableViaFactory`
- `TryGetBufferedData` → `false`
- `OpenReadSessionAsync` → invokes factory, returns session wrapping the new stream. Each call creates a fresh stream.
- `Dispose` → no-op (factory is stateless)

**Concurrency clarification:** The single-reader invariant is per-body-instance, not per-factory. A single `FactoryRequestBody` instance cannot be dispatched concurrently — `OpenReadSessionAsync` throws if a previous session is still active. To send the same factory-backed request concurrently, clone the request (which creates a new `FactoryRequestBody` instance sharing the same factory delegate). Each clone can be dispatched independently.

### IL2CPP / AOT Notes

- `ValueTask<RequestBodyReadSession>` triggers AOT generic instantiation. Add `RequestBodyReadSession` to Core's `link.xml`.
- All concrete subclasses use `abstract class` (not generic virtual methods on value types) — safe for IL2CPP.
- `TryGetBufferedData(...)` is public so optional modules can inspect already-buffered request bodies without new Core-internal coupling. `OpenReadSessionAsync(...)` remains `internal abstract`; `FileRequestBody` in `TurboHTTP.Files` uses `InternalsVisibleTo` from Core for that member.

### `InternalsVisibleTo` Prerequisite

`FileRequestBody` requires `[assembly: InternalsVisibleTo("TurboHTTP.Files")]` on Core's `AssemblyInfo.cs`. This entry almost certainly does not exist yet (it was not needed before `FileRequestBody`). **Add it as step zero of 22a.1.** Verify that `InternalsVisibleTo` entries in Unity assembly definitions work at runtime under IL2CPP — IL2CPP strips internals aggressively. Add `InternalsVisibleTo` entries to `link.xml` if required.

---

## Step 3: `RequestBodyReadSession`

**File:** `Runtime/Core/Internal/RequestBodyReadSession.cs`

`internal` class in `TurboHTTP.Core.Internal`. Pure coordination object (no transport I/O) that guarantees:

- **One active reader per dispatch attempt** — the session is opened via `OpenReadSessionAsync` and represents exclusive access to the body stream for one send attempt
- **Deterministic owner disposal** — implements `IDisposable`. Transport always disposes the session in `finally`, even on connection failure or cancellation
- **Explicit reset/reopen semantics for retries:**
  - for replayable bodies (`Replayable`, `ReplayableViaFactory`): `OpenReadSessionAsync` may be called again after the previous session is disposed. The body resets to the beginning.
  - for non-replayable bodies (`NonReplayable`): a second call to `OpenReadSessionAsync` throws `InvalidOperationException`
- **Single-reader invariant** — calling `OpenReadSessionAsync` while a previous session is still active (not disposed) throws `InvalidOperationException`. Concurrent dispatch of the same request instance is NOT supported. To send the same factory-backed request concurrently, clone the request (which creates a new `FactoryRequestBody` instance sharing the same factory delegate). Each clone can be dispatched independently.

Key members:

```csharp
internal sealed class RequestBodyReadSession : IDisposable
{
    internal Stream Stream { get; }
    internal long? ContentLength { get; }

    // Read body bytes into destination buffer. Returns bytes read, 0 on EOF.
    internal ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct);

    public void Dispose();
}
```

---

## Step 4: `IResponseBodySource` Interface

**File:** `Runtime/Core/IResponseBodySource.cs`

**Public** interface in `TurboHTTP.Core` — must be implemented by Transport (`Http11ResponseBodySource`, `Http2ResponseBodySource`) and wrapped by optional modules (Decompression, Cache, Monitor) that only reference Core. Parallels `IHttpTransport`.

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

### `TryGetBufferedData` Method

**Included in the initial interface definition** (not deferred to 22a.4) to freeze the interface shape and avoid breaking implementations later.

- Returns `true` with the complete body data when the body is already fully buffered in memory (e.g., small HTTP/2 responses where all DATA frames fit in the bounded queue before the consumer reads)
- Returns `false` for streaming body sources where the body is not yet fully available
- Default behavior for most implementations: return `false`
- Enables a zero-copy collector path in `BufferedResponseCollectorHandler` (utilized in 22a.4)

### `ReadAsync` Contract

1. **Returns 0 only on EOF.** No "no data available yet" return — blocks asynchronously until data arrives or EOF.
2. **Partial reads are permitted.** Consumers must loop until 0 is returned.
3. **Post-EOF behavior is undefined.** Callers must not call `ReadAsync` after it returns 0.
4. **Cancellation behavior is implementation-dependent:**
   - **HTTP/2 body sources:** Cancellation does not corrupt source state — the bounded queue is separate from the network read, so cancelling `ReadAsync` merely abandons the wait. Subsequent `ReadAsync` with a non-cancelled token may succeed.
   - **HTTP/1.1 body sources:** Cancellation transitions the source to a faulted state. Mid-read cancellation may have consumed partial data from the network stream, making the byte stream position unrecoverable. The connection cannot be reused. Subsequent `ReadAsync` throws `UHttpException`.
   - **Consumers must not rely on post-cancellation recovery for connection-scoped sources.**
5. **Zero-length destination:** Calling `ReadAsync` with a zero-length `Memory<byte>` returns 0 immediately without affecting state. This is distinct from the EOF return of 0 (which only occurs when the source is truly exhausted). Consumers must not interpret a zero-length read result as EOF.
6. **Single outstanding read.** Only one `ReadAsync` in flight at a time (matches `ValueTask<T>` contract).
7. **Transport errors throw `UHttpException`.** Body source maps raw `IOException` / socket errors.

### `Fault` — Internal, Not on Public Interface

`Fault(Exception error)` is **NOT** on the public `IResponseBodySource` interface. It is a transport-to-source signal that should not be callable by consumers or middleware.

Transport implementations expose faulting via an **internal** `IFaultableResponseBodySource` interface:

```csharp
internal interface IFaultableResponseBodySource
{
    void Fault(Exception error);
}
```

Both `Http11ResponseBodySource` and `Http2ResponseBodySource` implement this internal interface. When the connection drops mid-stream:
- Any pending `ReadAsync` is woken and throws the stored exception (wrapped as `UHttpException` if not already)
- Subsequent `ReadAsync` calls throw immediately
- `DrainAsync` throws immediately
- Source transitions to terminal error state

Module wrappers (`DecompressionBodySource`, `TeeBodySource`) that detect inner source errors call `Abort()` on their inner source, not `Fault`. This prevents consumers from putting a transport body source into a terminal error state via the public API.

---

## Step 5: `ResponseBodyStream` Adapter

**File:** `Runtime/Core/ResponseBodyStream.cs`

Custom `Stream` subclass over `IResponseBodySource`:

- `Length` → returns `IResponseBodySource.Length` when known (from `Content-Length`), avoiding `NotSupportedException`
- `CanSeek` → `false`
- `CanRead` → `true` (returns `false` after `Dispose`, per standard .NET `Stream` convention)
- `ReadAsync(Memory<byte>, CancellationToken)` → delegates to `IResponseBodySource.ReadAsync`
- `ReadAsync` after `Dispose` → throws `ObjectDisposedException`
- **No internal read-ahead buffer.** The base `ResponseBodyStream` is a thin zero-overhead adapter. Read-ahead buffering to amortize `GZipStream`'s many small inner reads is the responsibility of `DecompressionBodySource` (22a.5), which wraps the inner `IResponseBodySource` in a private `BufferedStream(~8KB)` before passing it to `GZipStream`. This avoids triple-buffering on the HTTP/1.1 path where `BufferedStreamReader` already provides its own buffer.
- `GZipStream.ReadAsync` on .NET Standard 2.1 uses the default `Stream.ReadAsync` base implementation which allocates a `Task<int>` per call — known allocation point, acceptable for decompression paths
- **Drain path bypasses `ResponseBodyStream`:** `DrainAsync` on `IResponseBodySource` is the correct drain entry point for the drain-or-close decision. `ResponseBodyStream` is not involved in drain logic.

---

## Step 6: `UHttpStreamingResponse`

**File:** `Runtime/Core/UHttpStreamingResponse.cs`

```csharp
public sealed class UHttpStreamingResponse : IAsyncDisposable, IDisposable
{
    public HttpStatusCode StatusCode { get; }
    public HttpHeaders Headers { get; }
    public ResponseBodyStream Body { get; }
    public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct = default);
}
```

### `IAsyncDisposable` and IL2CPP

- Implements both `IAsyncDisposable` and `IDisposable`
- **22a.1 must validate `IAsyncDisposable` + `await using` on iOS and Android IL2CPP before the API shape is finalized.** If validation fails, fallback: `IDisposable`-only with explicit `public ValueTask DisposeAsync()` method (no interface).
- `IDisposable.Dispose()` is synchronous fallback that calls `Abort()` on the body source and releases the connection lease. Does not attempt drain.
- `ValueTask<HttpHeaders>` on `GetTrailersAsync` must also be validated on IL2CPP.

### Connection Lease Ownership

- Connection/stream lease remains owned by the response until body fully consumed or response disposed
- Disposing early aborts or drains according to protocol and policy
- Trailers available only after end-of-body
- **Leak detection:** `Debug.LogWarning` in finalizer for development builds if consumer abandons without disposing
- HTTP/1.1: `ConnectionLease.TransferOwnership()` transfers lease out of `using` scope in `DispatchCoreAsync`
- HTTP/2: stream's bounded buffer holds reference to `Http2Stream`; disposal triggers `RST_STREAM(CANCEL)` and buffer release

---

## Step 7: Updated `IHttpHandler` Contract

**File:** `Runtime/Core/IHttpHandler.cs` (modified)

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

### `CapabilityEnforcedInterceptor` and `ObservedHandler` Stub Migration

**Performed in 22a.1, not deferred to 22a.5.** These types are in `Runtime/Core/PluginContext.cs` and implement `IHttpHandler`. After 22a.1 changes the `IHttpHandler` contract (`OnResponseStartAsync` replaces `OnResponseData`/`OnResponseEnd`), they will not compile.

In 22a.1, update them to compile against the new `IHttpHandler` contract with minimal stub implementations:
- `ObservedHandler`: wraps the body source in a stub `ObservedBodySource` proxy that passes through all reads. Full observation tracking (bytes read, trailer access) is filled in during 22a.5.
- `CapabilityEnforcedInterceptor`: update `ResponseEventSignature` to capture status code + headers hash (replacing the removed `OnResponseData` CRC32 of raw chunks). Full `RequestMutationSignature` update for the new `Content` model is completed in 22a.5.

This ensures all shipping runtime assemblies compile throughout 22a.1–22a.4.

---

## Step 8: `BufferedResponseCollectorHandler` Rewrite

**File:** `Runtime/Core/Pipeline/ResponseCollectorHandler.cs` → renamed to `BufferedResponseCollectorHandler`

Behavior:
1. Capture status/headers/body source in `OnResponseStartAsync`
2. Drain the source into pooled `SegmentedBuffer`
3. Construct `UHttpResponse` only once body is fully read
4. Fetch trailers before completing the buffered result

This removes the current split between handler callbacks and a second buffered public layer.

---

## Step 9: `BufferedDispatchBridge` and `StreamingDispatchBridge`

**File:** `Runtime/Core/Pipeline/DispatchBridge.cs` → split into two files

### `BufferedDispatchBridge`

Replaces current `DispatchBridge.CollectResponseAsync`. Uses `BufferedResponseCollectorHandler` to drain body source into `SegmentedBuffer`, construct `UHttpResponse`, complete the task. Same lifecycle guarantees (fail/cancel/complete safety).

### `StreamingDispatchBridge`

New companion. Invokes dispatch function, captures `IResponseBodySource` from `OnResponseStartAsync`, constructs `UHttpStreamingResponse` wrapping the body source, transfers connection lease ownership. Task completes as soon as headers are available (not after body is fully read).

**Lease safety (critical):** `StreamingDispatchBridge` must own a `try/finally` that holds the transferred connection lease until the `UHttpStreamingResponse` object is successfully constructed and its reference is captured. If the bridge fails after lease transfer but before the response object is constructed (e.g., exception in the bridge code), the `finally` block must explicitly **close the connection** (not return it to the pool, since the body state is unknown). This prevents lease orphaning. The finalizer-based leak detection on `UHttpStreamingResponse` is a secondary safety net, not the primary protection — on mobile under memory pressure, the finalizer thread may not run before the connection pool exhausts its semaphore permits.

Both bridges share error-handling patterns (`Fail`, `Cancel`, `EnsureCompleted`) and `ContinueWith` attachment logic.

---

## Step 10: `UHttpClient` API Update

**File:** `Runtime/Core/UHttpClient.cs` (modified)

Replace `SendAsync` with:

```csharp
public Task<UHttpResponse> SendBufferedAsync(UHttpRequest request, CancellationToken ct = default);
public Task<UHttpStreamingResponse> SendStreamingAsync(UHttpRequest request, CancellationToken ct = default);
```

`SendAsync(...)` is removed. `request.SendAsync(ct)` self-send pattern is also removed.

Migration:
- `await client.SendAsync(request, ct)` → `await client.SendBufferedAsync(request, ct)`
- `await request.SendAsync(ct)` → `await client.SendBufferedAsync(request, ct)`
- new streaming: `await using var response = await client.SendStreamingAsync(request, ct);`

`SendBufferedAsync` is not a separate transport implementation — it is a collector layered on the streaming-capable substrate.

### Compile-Surface Migration Sweep

22a.1 also includes a signature-only migration sweep for existing runtime callers that currently depend on `SendAsync(...)` / `request.SendAsync(...)`:

- `Runtime/UniTask/UHttpClientUniTaskExtensions.cs`
- `Runtime/JSON/JsonExtensions.cs`
- `Runtime/Auth/OAuthClient.cs`
- `Runtime/Files/FileDownloader.cs` (stays buffered in 22a.1; full streaming rewrite still happens in 22a.5)
- `Runtime/Unity/UnityExtensions.cs`
- `Runtime/Unity/AudioClipHandler.cs`
- `Runtime/Unity/Texture2DHandler.cs`
- `Runtime/Unity/CoroutineWrapper.cs`

This step does not change their behavior yet. It only keeps all shipping runtime assemblies compiling against the explicit buffered/streaming public API split before the deeper module rewrites land.

---

## Step 11: `UHttpRequest` Body Update

**File:** `Runtime/Core/UHttpRequest.cs` (modified)

`Body` field removed, replaced with:

```csharp
public sealed class UHttpRequest
{
    public UHttpRequestBody Content { get; private set; }
}
```

Builder helpers updated (Core):
- `WithBody(byte[] body)` → creates `BufferedRequestBody`
- `WithBody(ReadOnlyMemory<byte> body)` → creates `BufferedRequestBody`
- `WithLeasedBody(IMemoryOwner<byte> owner, int length)` → creates `OwnedMemoryRequestBody`
- `WithStreamBody(Stream stream, long? contentLength = null, bool leaveOpen = false)` → creates `StreamRequestBody`
- `WithBodyFactory(Func<CancellationToken, ValueTask<Stream>> factory, long? contentLength = null)` → creates `FactoryRequestBody`

Builder helper in `TurboHTTP.Files`:
- `WithFileBody(string path, int bufferSize = 32768)` → extension method in `FileRequestBuilderExtensions`, creates `FileRequestBody`

### Detached Clone vs Shared-Content Copies

Replayability and detached cloneability are related, but not identical:

- `UHttpRequest.Clone()` remains the **detached clone** API. The clone must have an independent lifetime from the original request.
- `EmptyRequestBody` → clone produces another empty body.
- `BufferedRequestBody` and `OwnedMemoryRequestBody` → clone copies bytes into a new `BufferedRequestBody`. This preserves the current detached-copy behavior and avoids sharing request-body lifetime across queued or replayed requests.
- `FactoryRequestBody` → clone creates a new wrapper over the same factory delegate and metadata.
- `FileRequestBody` → clone creates a new wrapper over the same path + options.
- `StreamRequestBody` → `Clone()` throws `InvalidOperationException` even when the body is sequentially replayable. A single `Stream` instance may support rewind for retries, but it still cannot safely back two detached request objects.

Core also adds an internal **shared-content copy** helper (`CopyWithSharedContent(...)` or equivalent) for same-dispatch copy-on-write mutations:

- shares the `Content` reference without cloning or opening a second reader
- used by `AdaptiveInterceptor`, `AuthInterceptor`, and similar "clone just to tweak headers/timeout/metadata" flows
- must never be used for queued/background requests, persisted requests, or concurrent dispatches

`BackgroundNetworkingInterceptor` and any queued/persistent replay path use detached clone semantics and reject bodies that cannot produce a detached clone.

---

## Step 12: `SingleReaderChannel<T>` (SPSC Async Channel)

**File:** `Runtime/Transport/Http2/SingleReaderChannel.cs`

**Lives in `TurboHTTP.Transport`, not Core.** `SingleReaderChannel<T>` is solely needed for HTTP/2's bounded per-stream receive queue. Core stays free of transport concerns. If a future WebSocket module needs a similar channel, it can be promoted to Core at that time.

Purpose-built SPSC channel using `ManualResetValueTaskSourceCore<int>` for HTTP/2 bounded queue. `System.Threading.Channels` is not available in Unity 2021.3.

Properties:
- **SPSC:** producer = `ReadLoopAsync` thread, consumer = caller's async continuation thread
- **Non-blocking enqueue:** read loop must NEVER block on full buffer. Overflow triggers `RST_STREAM(FLOW_CONTROL_ERROR)`.
- **Async dequeue:** `ReadAsync` blocks asynchronously when queue is empty, using `ManualResetValueTaskSourceCore` for zero-allocation notification
- **Error slot:** atomic error field that surfaces on next `ReadAsync`
- **Cancellation-aware:** consumer cancellation wakes pending reader with `OperationCanceledException`

### `ManualResetValueTaskSourceCore<int>` Reset Protocol

The reset protocol is strict and must be explicitly specified:
- **Producer** calls `SetResult()` or `SetException()` to wake the consumer
- **Consumer** calls `GetResult()` exactly once after being woken, then `Reset()` before the next wait
- A missed `Reset()` causes `InvalidOperationException` on next `SetResult()` in debug builds
- **Version counter wraps at `short.MaxValue` (32,767).** This is by design in .NET, but streaming use cases will exercise version wrapping far more aggressively than per-request uses. Add a specific test case in 22a.6 exercising 100,000+ read cycles to validate version wrapping on both Mono and IL2CPP.

---

## Step 13: `MockResponseBodySource`

**File:** `Runtime/Testing/MockResponseBodySource.cs`

In-memory `IResponseBodySource` implementation for unit testing without transport:
- Accepts a queue of byte chunks
- `ReadAsync` delivers chunks sequentially
- Supports simulating errors via a test-only `InjectFault(Exception)` method (not via the removed public `Fault` — `MockResponseBodySource` implements `IResponseBodySource`, which no longer has `Fault`)
- `TryGetBufferedData` returns `true` only when all chunks have been pre-loaded and total size fits in a single `ReadOnlyMemory<byte>`
- Supports drain, abort
- Returns configurable trailers

---

## Step 14: `FileRequestBody` + `FileRequestBuilderExtensions`

**File:** `Runtime/Files/FileRequestBody.cs`

- Extends `UHttpRequestBody`
- `Replayability` → `Replayable` (file can be re-read)
- `TryGetBufferedData` → `false`
- `OpenReadSessionAsync` → opens `FileStream` with specified buffer size
- `Length` → file length from `FileInfo`

**File:** `Runtime/Files/FileRequestBuilderExtensions.cs`

```csharp
public static class FileRequestBuilderExtensions
{
    public static UHttpRequest WithFileBody(
        this UHttpRequest request, string path, int bufferSize = 32768);
}
```

---

## Step 15: `link.xml` Updates

**File:** `Runtime/Core/link.xml` (modified)

Add entries for IL2CPP AOT:
- `RequestBodyReadSession`
- `IResponseBodySource`
- `ValueTask<IResponseBodySource>` (if any method returns this at interface boundaries)
- `Http2ResponseBodySource` (accessed through the interface — IL2CPP must preserve its methods)

**File:** `Runtime/Transport/link.xml` (modified)

Add entries for IL2CPP AOT in Transport:
- `SingleReaderChannel<ReadOnlyMemory<byte>>` (the concrete instantiation used for HTTP/2)
- `ManualResetValueTaskSourceCore<int>` (used inside `SingleReaderChannel<T>`)

A full audit of all new generic instantiations must be done as part of 22a.1 completion criteria.

---

## Planned File Impact

### New Files (12)

| File | Description |
|------|-------------|
| `Runtime/Core/RequestBodyReplayability.cs` | Replayability enum |
| `Runtime/Core/UHttpRequestBody.cs` | Abstract base + 5 concrete body implementations |
| `Runtime/Core/IResponseBodySource.cs` | Public response body source interface |
| `Runtime/Core/ResponseBodyStream.cs` | Thin `Stream` adapter (no read-ahead buffer) |
| `Runtime/Core/UHttpStreamingResponse.cs` | Streaming response with `IAsyncDisposable` |
| `Runtime/Core/Internal/RequestBodyReadSession.cs` | Internal session coordination |
| `Runtime/Transport/Http2/SingleReaderChannel.cs` | SPSC async channel for HTTP/2 (in Transport, not Core) |
| `Runtime/Core/Pipeline/BufferedDispatchBridge.cs` | Buffered dispatch bridge |
| `Runtime/Core/Pipeline/StreamingDispatchBridge.cs` | Streaming dispatch bridge |
| `Runtime/Testing/MockResponseBodySource.cs` | Test body source |
| `Runtime/Files/FileRequestBody.cs` | File body + builder extension |
| `Runtime/Files/FileRequestBuilderExtensions.cs` | `WithFileBody` extension method |

### Modified Files (15+)

| File | Change |
|------|--------|
| `Runtime/Core/UHttpRequest.cs` | `Body` → `Content: UHttpRequestBody`, builder helpers updated |
| `Runtime/Core/UHttpClient.cs` | `SendAsync` → `SendBufferedAsync`/`SendStreamingAsync` |
| `Runtime/Core/IHttpHandler.cs` | Remove `OnResponseData`/`OnResponseEnd`, add `OnResponseStartAsync` |
| `Runtime/Core/Pipeline/ResponseCollectorHandler.cs` | Renamed to `BufferedResponseCollectorHandler`, rewritten |
| `Runtime/Core/Pipeline/DispatchBridge.cs` | Split into two files (may be deleted) |
| `Runtime/Core/BackgroundNetworkingInterceptor.cs` | Detached clone rules for queued/background replay |
| `Runtime/Core/AdaptiveInterceptor.cs` | Shared-content copy helper for timeout mutation; request length uses `Content.Length` |
| `Runtime/Auth/AuthInterceptor.cs` | Shared-content copy helper for header mutation |
| `Runtime/UniTask/UHttpClientUniTaskExtensions.cs` | `SendAsync` / `request.SendAsync` migration to explicit buffered APIs |
| `Runtime/JSON/JsonExtensions.cs` | Explicit buffered send migration |
| `Runtime/Auth/OAuthClient.cs` | Explicit buffered send migration |
| `Runtime/Files/FileDownloader.cs` | Temporary buffered API migration before later streaming rewrite |
| `Runtime/Unity/UnityExtensions.cs` | Explicit buffered send migration |
| `Runtime/Unity/AudioClipHandler.cs` | Explicit buffered send migration |
| `Runtime/Unity/Texture2DHandler.cs` | Explicit buffered send migration |
| `Runtime/Unity/CoroutineWrapper.cs` | Explicit buffered send migration |
| `Runtime/Core/link.xml` | Add `RequestBodyReadSession`, `IResponseBodySource` |

---

## Completion Criteria

- **IL2CPP spike passed (Step 0):** `IAsyncDisposable` + `await using` and `ValueTask<T>` validated on physical iOS/Android IL2CPP. API shape locked.
- `InternalsVisibleTo("TurboHTTP.Files")` added to Core's `AssemblyInfo.cs` and verified on IL2CPP
- `IResponseBodySource` interface shape frozen (includes `TryGetBufferedData`; excludes `Fault`)
- All buffered-only request-body assumptions are removed from Core
- A streaming response can be opened without buffering the body (validated via `MockResponseBodySource`)
- Buffered APIs are layered above the same substrate
- All shipping runtime assemblies compile against `SendBufferedAsync(...)` / `SendStreamingAsync(...)`; no remaining HTTP runtime call sites reference the removed `SendAsync(...)` or `request.SendAsync(...)` APIs
- `CapabilityEnforcedInterceptor` and `ObservedHandler` compile against new `IHttpHandler` contract (stub implementations)
- `StreamingDispatchBridge` has `try/finally` lease safety for the error path
- `SingleReaderChannel<T>` lives in Transport, not Core
- Full audit of new generic instantiations for `link.xml` completed

## Post-Step Review

Both specialist agents must review before proceeding to 22a.2/22a.3:
- `unity-infrastructure-architect`: architecture, memory, thread safety, IL2CPP/AOT, module dependency rules, resource disposal
- `unity-network-architect`: platform compatibility, protocol correctness, zero-allocation patterns
