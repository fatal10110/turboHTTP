# Phase 22a.5: Interceptor and Module Streaming Rewrite

**Depends on:** 22a.4 (complete) — buffered fast-path contract must be finalized before interceptors are written against it
**Assemblies:** `TurboHTTP.Middleware`, `TurboHTTP.Retry`, `TurboHTTP.Cache`, `TurboHTTP.Observability`, `TurboHTTP.Files`, `TurboHTTP.Core`
**Files to create:** 2 new, 10+ modified

---

## Step 1: `DecompressionBodySource`

**File:** `Runtime/Middleware/DecompressionHandler.cs` → replaced by `DecompressionBodySource`

### Current State

- Buffers the compressed response body
- Decompresses only on end-of-body

### 22a Change

Replace with `DecompressionBodySource` (implements `IResponseBodySource`):

1. Wrap the underlying body source (via `ResponseBodyStream` adapter) in a persistent `GZipStream` / `DeflateStream`
2. Decompress incrementally on reads — `GZipStream.Read` calls the inner stream's `Read` incrementally (confirmed: works on .NET Standard 2.1 and Unity IL2CPP Mono)
3. Enforce decompressed-size limits during streaming: a running counter tracks total decompressed bytes across all `ReadAsync` calls. If `_maxDecompressedBodySizeBytes` is exceeded, the body source is aborted and `ReadAsync` throws `IOException`
4. The `GZipStream` lifetime is tied to the response lifetime — constructed once, never re-initialized

### GZIP Trailer Validation in Streaming Mode

- **Rely on `GZipStream`'s internal CRC32 validation.** `GZipStream` validates the GZIP trailer (CRC32 + ISIZE) internally at EOF. If validation fails, throws `InvalidDataException`.
- Separate `ValidateSingleGzipTrailer` method removed for streaming responses. Remains for buffered decompression path only.
- **Early dispose skips CRC32 validation.** Acceptable and documented.

### Decompression Bomb Mitigation

Decompressed-size limit enforced incrementally during streaming (not after full buffering). Test requirement: a compressed response that decompresses to 200 MB must abort at the configured limit, not OOM.

**The limit is configurable** via `DecompressionInterceptor` constructor parameter (`maxDecompressedBodySizeBytes`), not a magic constant. A video streaming app and a JSON API client have very different reasonable limits. Default: 256 MB (reasonable for most workloads; the HPACK 128 KB limit from Phase 3B is a separate concern).

### `DecompressionBodySource` ~8KB Read-Ahead Buffer

**The read-ahead buffer lives here, not in `ResponseBodyStream`.** `ResponseBodyStream` (22a.1) is a thin zero-overhead adapter with no internal buffering. `DecompressionBodySource` wraps the inner `IResponseBodySource` in a private `BufferedStream(~8KB)` before passing it to `GZipStream`. This:
- Amortizes `GZipStream`'s many small inner reads
- Avoids triple-buffering on the HTTP/1.1 path where `BufferedStreamReader` already provides its own buffer
- Keeps the non-decompression path zero-overhead

The `DecompressionBodySource.ReadAsync` delegates to `GZipStream.ReadAsync`, which reads from the buffered adapter.

---

## Step 2: Retry Interceptor Updates

**Files:** `Runtime/Retry/RetryInterceptor.cs`, `Runtime/Retry/RetryDetectorHandler.cs` (modified)

### Current State

- Relies on the HTTP/1.1 body already being drained before callbacks are suppressed

### 22a Change

- **Retry decision happens at response start** — when `OnResponseStartAsync` fires, the retry detector checks status code and policy
- If response is retryable and not committed downstream, discard the response body **using `IResponseBodySource` abstraction only** — the retry module must not branch on protocol:
  - Call `body.DrainAsync(linkedCt)` — HTTP/1.1 implementations drain and return to pool; HTTP/2 implementations abort the stream via `RST_STREAM(CANCEL)`. The protocol-specific behavior is encapsulated inside each transport's `IResponseBodySource` implementation.
  - If drain fails or times out, call `body.Abort()` as fallback
- **Replay eligibility depends on request-body replayability**, not on method alone:
  - `Replayable` or `ReplayableViaFactory` → eligible for retry
  - `NonReplayable` → not eligible, even if method is idempotent
- If retry is not possible (non-replayable body, or retries exhausted):
  - Response path: return last failed response to consumer
  - Exception path: re-throw to consumer

---

## Step 3: Redirect Interceptor Updates

**Files:** `Runtime/Middleware/RedirectInterceptor.cs`, `Runtime/Middleware/RedirectHandler.cs` (modified)

### Current State

- Redispatches after `OnResponseEnd`

### 22a Change

- **Redirect decision happens at response start** once status + headers are known
- Original response body source must be explicitly drained or aborted before redispatch
- Follow-up request is only permitted when:
  - Body is replayable, OR
  - Redirect semantics drop the body (e.g., 303 → GET, which has no body)
- Non-replayable body on a redirect that requires the body → fail with descriptive error

---

## Step 4: `TeeBodySource` for Cache Store

**File:** `Runtime/Cache/CacheStoringHandler.cs` → replaced by `TeeBodySource`

### Design

`TeeBodySource` implements `IResponseBodySource` and wraps the underlying body source:

- As the consumer reads from the primary source, each chunk is also written to the cache accumulator
- Cache commit happens only after successful EOF + trailers
- Partially consumed or aborted streaming responses do not produce cache entries

### EOF vs Abandon Lifecycle

1. **Natural EOF** — consumer read all bytes, underlying source reached EOF, trailers received. Cache entry is committed. Tracked via internal `_completedNaturally` flag set when `ReadAsync` returns 0 from underlying source.
2. **Consumer abandon** — consumer disposed early without reading to EOF. Tee accumulator is discarded; no cache entry produced.

`DisposeAsync` checks `_completedNaturally` flag to decide commit vs discard.

### Accumulation Size Limit

`TeeBodySource` must check against a configurable `MaxCacheableResponseBodyBytes` limit during accumulation. When the limit is exceeded, it **silently detaches** (same behavior as cache write failure) and continues delivering bytes to the consumer. Without this limit, a 500 MB streaming response would grow the tee accumulator to 500 MB, negating the streaming path's bounded-memory benefit.

Two checks are needed:
1. **Pre-tee check:** The Cache interceptor checks `Content-Length` (when known) against `MaxCacheableResponseBodyBytes` and skips installing the tee entirely if the body is too large.
2. **Incremental check:** During accumulation, if the running total of accumulated bytes exceeds the limit, detach. This catches cases where `Content-Length` is absent.

### Cache Write Failure

If cache write fails mid-stream (disk full, serialization error), `TeeBodySource` silently detaches the cache accumulator and continues delivering bytes to the consumer. Response body delivery must never be affected by cache failures.

---

## Step 5: Bounded Observability Capture

**Files:** `Runtime/Observability/LoggingInterceptor.cs`, `Runtime/Observability/LoggingHandler.cs`, `Runtime/Observability/MetricsInterceptor.cs`, `Runtime/Observability/MetricsHandler.cs`, `Runtime/Observability/MonitorInterceptor.cs`, `Runtime/Observability/MonitorHandler.cs` (modified)

### Metrics

- Count streamed response bytes as they are consumed (incrementally, not at end)
- Track total response bytes via `Interlocked.Add` on each `ReadAsync` completion
- Request-side bytes are no longer inferred from `request.Body.Length`. For buffered request bodies, metrics may use `request.Content.Length`; for unknown-length streaming uploads, the transport records actual request-body bytes sent into `RequestContext` and metrics consume that value on completion

### Logging

- Default to headers + bounded preview only
- Preview captures first N bytes (configurable) from `OnResponseStartAsync`, not full body
- Request-body preview uses `request.Content.TryGetBufferedData(...)` only when the body is already buffered. Logging must never open a read session or force buffering just to print a request preview
- For streaming request bodies, log known length/replayability when available and otherwise mark request body as streaming/unknown
- Full-body logging mode must be explicitly opt-in and documented as memory-expensive

### Monitor

- Bounded preview capture by default (not full-body retention)
- Request-body capture uses buffered access only when `TryGetBufferedData(...)` succeeds. Streaming request bodies record length/replayability metadata and an "unavailable without buffering" marker instead of forcing a copy
- Full-body capture mode is opt-in
- Any full-body observability mode documented as memory-expensive path

---

## Step 6: Streaming File Downloader

**File:** `Runtime/Files/FileDownloader.cs` (modified)

`FileDownloader` moves entirely to `SendStreamingAsync(...)`:

1. Open streaming response
2. Write chunks directly to `FileStream` as they arrive
3. Report progress per chunk (incremental bytes written / total if known)
4. Never buffer the full response in managed memory
5. Preserve checksum validation by hashing incrementally while writing (e.g., `IncrementalHash.CreateHash(HashAlgorithmName.SHA256)`)

### IL2CPP Note

`FileStream.WriteAsync` on Android/iOS IL2CPP may fall back to synchronous I/O wrapped in `Task.Run`. Acceptable for file I/O but can cause thread pool pressure under heavy concurrent download loads. Validate in 22a.6 on physical devices.

### `Expect: 100-continue` Limitation Documentation

Without `Expect: 100-continue` (deferred to post-22a), the client sends the entire request body before discovering the server will reject it. For `NonReplayable` request bodies, this means:
- The body is consumed and cannot be resent
- Server rejections (e.g., 413 Payload Too Large) result in permanent failure

**Add explicit XML doc warnings** on `StreamRequestBody` and `FactoryRequestBody` noting this limitation. Recommend users prefer `ReplayableViaFactory` for bodies that may be rejected by the server.

---

## Step 7: `CapabilityEnforcedInterceptor` + `ObservedHandler` Full Implementation

**File:** `Runtime/Core/PluginContext.cs` (modified)

**Note:** Stub implementations of these types that compile against the new `IHttpHandler` contract are created in 22a.1 (see 22a.1 Step 7 → "CapabilityEnforcedInterceptor and ObservedHandler Stub Migration"). This step completes the full functionality.

The current implementations are deeply tied to the push-based `IHttpHandler` callback model. Three areas need redesign:

### 7a. `RequestMutationSignature`

Current: hashes `request.Body` (the `ReadOnlyMemory<byte>` field being removed).
22a: must hash `request.Content` (the `UHttpRequestBody` reference).

Define `GetHashCode()` contract for `UHttpRequestBody` subclasses:
- `BufferedRequestBody`: hash of underlying memory
- `OwnedMemoryRequestBody`: hash of underlying memory
- `EmptyRequestBody`: constant hash
- `StreamRequestBody`, `FactoryRequestBody`, `FileRequestBody`: reference identity hash (stream content cannot be hashed without consuming it)

### 7b. `ResponseEventSignature`

Current: encodes `OnResponseData(ReadOnlySpan<byte>)` via CRC32 of raw chunk bytes.
22a: `OnResponseData` no longer exists. The single `OnResponseStartAsync` callback replaces the multi-callback sequence.

New signature must capture:
- Status code
- Headers hash
- Body source type (to detect wrapping)

### 7c. `ObservedHandler`

Current: wraps inner `IHttpHandler` and records callback invocations.
22a: must be rewritten for new `OnResponseStartAsync` shape:
- Observe the `IResponseBodySource` handed to the inner handler
- Track whether the body was consumed, how many bytes were read, and whether trailers were fetched
- Wrap the body source in an `ObservedBodySource` proxy that records reads

---

## Planned File Impact

| File | Change |
|------|--------|
| `Runtime/Middleware/DecompressionInterceptor.cs` | Update to use `DecompressionBodySource` |
| `Runtime/Middleware/DecompressionHandler.cs` | Replace with `DecompressionBodySource` (new body source wrapper) |
| `Runtime/Middleware/RedirectInterceptor.cs` | Redirect at response start, body drain/abort |
| `Runtime/Middleware/RedirectHandler.cs` | Updated for `OnResponseStartAsync` |
| `Runtime/Retry/RetryInterceptor.cs` | Replay eligibility based on body replayability |
| `Runtime/Retry/RetryDetectorHandler.cs` | Retry decision at response start, explicit drain/abort |
| `Runtime/Cache/CacheInterceptor.cs` | Updated for streaming cache |
| `Runtime/Cache/CacheStoringHandler.cs` | Replaced by `TeeBodySource` |
| `Runtime/Observability/LoggingInterceptor.cs` | Request-body preview migrates from `request.Body` to `request.Content` without forcing buffering |
| `Runtime/Observability/LoggingHandler.cs` | Bounded preview, incremental metrics |
| `Runtime/Observability/MetricsInterceptor.cs` | Request-byte accounting uses `Content.Length` or transport-populated byte counters |
| `Runtime/Observability/MetricsHandler.cs` | Incremental byte counting |
| `Runtime/Observability/MonitorInterceptor.cs` | Request snapshot uses buffered access only when available |
| `Runtime/Observability/MonitorHandler.cs` | Bounded preview capture |
| `Runtime/Files/FileDownloader.cs` | Streaming rewrite with incremental hash |
| `Runtime/Core/PluginContext.cs` | `RequestMutationSignature`, `ResponseEventSignature`, `ObservedHandler` redesign |

---

## Completion Criteria

- No optional module forces whole-body buffering by default
- Module behavior is correct for both buffered and streaming response modes
- `CapabilityEnforcedInterceptor` correctly detects request mutation and response observation under the new model
- Decompression streaming works incrementally (no full-body buffer)
- Decompression bomb test: abort at limit, not OOM
- Cache tee: commit only on natural EOF, discard on abandon, silent detach on write failure or size limit exceeded
- `TeeBodySource` accumulation bounded by `MaxCacheableResponseBodyBytes`
- Decompression bomb limit is configurable via `DecompressionInterceptor` constructor parameter
- `DecompressionBodySource` owns the ~8KB read-ahead buffer, not `ResponseBodyStream`
- Retry module uses `IResponseBodySource` interface abstraction, no protocol-specific branching
- Retry: correct behavior for replayable and non-replayable bodies
- Redirect: correct behavior for body-required and body-dropped redirects
- Request-side logging / monitor capture do not force buffering for streaming request bodies
- Metrics report actual bytes sent for unknown-length streaming uploads
- File download: bounded memory, incremental progress, incremental hash

## Post-Step Review

Both specialist agents must review before proceeding to 22a.6:
- `unity-infrastructure-architect`
- `unity-network-architect`
