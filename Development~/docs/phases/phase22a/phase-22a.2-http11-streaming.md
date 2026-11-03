# Phase 22a.2: HTTP/1.1 Streaming Send/Receive

**Depends on:** 22a.1 (complete)
**Assembly:** `TurboHTTP.Transport`
**Files to create:** 1 new, 3 modified

---

## Step 1: Known-Length Request Streaming

**File:** `Runtime/Transport/Http1/Http11RequestSerializer.cs` (modified)

### Content-Length / Transfer-Encoding Conflict Resolution (RFC 9110 Section 8.6)

**Before writing any request headers**, the serializer must enforce:

1. If `Content.Length` is known (non-null):
   - Set `Content-Length: <length>` header
   - **Strip any user-set `Transfer-Encoding: chunked`** from request headers
   - Use known-length body send path
2. If `Content.Length` is null (unknown):
   - Set `Transfer-Encoding: chunked` header
   - **Strip any user-set `Content-Length`** from request headers
   - Use chunked body send path
3. **Transport-set headers override user-set values.** A sender MUST NOT send both `Content-Length` and `Transfer-Encoding` (RFC 9110 Section 8.6).

If `Content.Length` is known:

1. Write request line + headers with `Content-Length`
2. Stream request bytes directly from the body session to the socket
3. Use a pooled transfer buffer only for non-buffered body types
4. Bypass the transfer buffer entirely for `TryGetBufferedData(...)`

### Buffered Fast Path

For bodies up to `SmallBufferedRequestThresholdBytes` (initial target: 32 KB):
- Write directly from the stored memory via `TryGetBufferedData`
- No extra chunking layer
- No `Stream` adapter allocation

### Streaming Path

- Default send buffer target: 32 KB
- Buffer rented once per dispatch attempt
- Returned immediately after request-body completion
- **Flush behavior:** for known-length bodies, flush after the full body is written (single syscall more efficient)

---

## Step 2: Chunked Request Streaming (Unknown-Length Bodies)

**File:** `Runtime/Transport/Http1/Http11RequestSerializer.cs` (modified, continued)

If `Content.Length` is unknown:

- HTTP/1.1 sends `Transfer-Encoding: chunked`
- Body bytes are chunk-framed on the fly: each chunk formatted as `<size-hex>\r\n<data>\r\n` per RFC 9112 Section 7.1
- Terminal chunk sequence is always `0\r\n\r\n` (final chunk + empty trailer section) — request trailers out of scope for 22a but trailer section terminator is mandatory
- Chunked encoder treats `ReadAsync` returning 0 as terminal EOF per the `Stream` contract. Terminal chunk sent exactly once, no subsequent reads attempted
- **Flush behavior:** flush after each chunk by default to reduce latency. Future `StreamingOptions.FlushAfterEachChunk` may allow tuning.
- `Expect: 100-continue` is deferred to post-22a (documented in Non-Goals)

---

## Step 3: Retry / Failure Semantics for Request Streaming

**File:** `Runtime/Transport/RawSocketTransport.cs` (modified)

Automatic retry on HTTP/1.1 request failure is allowed only when:

1. The method is retryable by policy
2. The body is replayable
3. No **request body bytes** were committed to the wire, or replay is explicitly supported

"No bytes committed" means **no request body bytes committed** — request headers may have already been sent. This aligns with RFC 9110 Section 9.2.2.

For idempotent methods, retry is always safe regardless of how many body bytes were sent (provided body is replayable).

If a one-shot body has started sending and the connection fails:
- Connection is discarded
- Request fails immediately with a dedicated non-replayable-body transport error
- **No silent full-body buffering is permitted as a fallback**

---

## Step 4: Split Header/Body Response Parsing

**File:** `Runtime/Transport/Http1/Http11ResponseParser.cs` (modified)

`Http11ResponseParser` must split into two stages:

### Stage 1: Header Parse

1. Parse status line + headers only (consuming any `1xx` informational responses — discard before delivering final status/headers)
2. Determine framing strategy. The body reader factory receives **the request method** in addition to response headers. **Order matters — `Transfer-Encoding` takes precedence over `Content-Length`** per RFC 9112 Section 6.1:
   - **No-body responses:** HEAD requests, 1xx, 204, 304 → always produce `EmptyResponseBodySource` regardless of `Content-Length` or `Transfer-Encoding` headers (RFC 9110 Section 9.3.2, Section 15.4.5)
   - **`Transfer-Encoding: chunked` present:** chunked body reader (**checked before `Content-Length`** — per RFC 9112 Section 6.1, if both headers are present, `Transfer-Encoding` overrides `Content-Length`)
   - **`Content-Length` present (and no `Transfer-Encoding`):** fixed-length body reader
   - **Neither:** read-to-end (connection-close framing)
   - **If both `Transfer-Encoding` and `Content-Length` are present:** log a warning (potential response smuggling attempt), use chunked framing

### Stage 2: Body Reader

3. Create `Http11ResponseBodySource` (see Step 5)
4. Call `handler.OnResponseStartAsync(statusCode, headers, bodySource, context)`
5. Buffered or streaming consumer pulls bytes from the source

### `BufferedStreamReader` Transfer

The header parse stage uses a `BufferedStreamReader` that may have pre-fetched body bytes. **The `Http11ResponseBodySource` must take ownership of the `BufferedStreamReader` instance** (or equivalent buffered wrapper), not the raw network stream. Otherwise, pre-fetched body bytes are lost. Transferred at construction time.

---

## Step 5: `Http11ResponseBodySource`

**File:** `Runtime/Transport/Http1/Http11ResponseBodySource.cs` (new)

Implements `IResponseBodySource` with four framing variants:

### 5a. Content-Length Body Reader

- Tracks remaining bytes
- `ReadAsync` reads from `BufferedStreamReader`, decrements remaining
- Returns 0 when all bytes consumed
- `Length` returns Content-Length value

### 5b. Chunked Body Reader

- Parses chunk headers inline during `ReadAsync`
- Delivers chunk data directly to consumer buffer (no intermediate copy)
- Recognizes terminal `0\r\n\r\n` and returns 0
- For early-dispose drain: remaining bytes are not known up front, so the source drains only within the Step 6 decoded-byte budget and reuses the connection only if the terminal chunk is reached inside that budget. The budget is intentionally based on decoded body bytes rather than raw wire bytes.

### 5c. Read-to-End Body Reader

- Reads until EOF (connection-close framing)
- `Length` returns `null`
- Connection always discarded after (cannot be reused)

### 5d. `EmptyResponseBodySource`

- For HEAD, 1xx, 204, 304 responses
- `ReadAsync` returns 0 immediately
- `Length` returns 0
- `GetTrailersAsync` returns `HttpHeaders.Empty`

### HTTP/1.1 Trailer Support

`GetTrailersAsync` returns `HttpHeaders.Empty` for all HTTP/1.1 responses. Current parser discards trailers (known limitation). Full parsing deferred.

`RecordReplayTransport` and other buffered-only observability/testing paths still capture response bodies only when `IResponseBodySource.TryGetBufferedData(...)` succeeds. Streaming HTTP/1.1 responses therefore replay with empty bodies in 22a.2; this is a documented limitation until those paths gain streaming-body capture.

---

## Step 6: Early-Dispose Drain-or-Close Policy

**File:** `Runtime/Transport/Http1/Http11ResponseBodySource.cs` (continued)

If a consumer disposes early, the drain-or-close decision uses a **three-condition gate**:

1. **Framing is deterministic** — `Content-Length` or chunked (not read-to-end, where remaining bytes are unknown)
2. **Remaining unread bytes within drain budget:**
   - **Content-Length bodies:** remaining = `Content-Length` minus bytes already consumed. Drain if remaining <= `BufferedDrainReuseThresholdBytes` (64 KB).
   - **Chunked bodies:** drain up to `BufferedDrainReuseThresholdBytes` (64 KB) of decoded chunk data. If the final `0\r\n\r\n` chunk is reached within that budget, reuse the connection. If not, close. This preserves connection reuse for responses where most of the chunked body was already consumed.
3. **Response does NOT have `Connection: close`** — if present, always close immediately (no drain)

Only if all three conditions are met, `DrainAsync` is attempted with a **2-second timeout** using `CancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(2))` (not socket-level timeouts, which behave differently across platforms).

**Cancellation propagation:** `IAsyncDisposable.DisposeAsync()` does not accept a caller `CancellationToken`, so the drain operation links only transport-owned cancellation (when present) with the 2-second timeout. For buffered callers this still includes the request timeout token; for streaming callers the drain budget is governed by the 2-second timeout alone because request timeout scope ends at headers.

No unread-bytes ambiguity when returning a connection to the pool.

---

## Step 7: Connection Lease Transfer for Streaming Responses

**File:** `Runtime/Transport/RawSocketTransport.cs` (modified)

The HTTP/1.1 connection lease remains attached to the body source until:

1. Body fully consumed → connection returned to pool
2. `DrainAsync(...)` finishes successfully → connection returned to pool
3. Body aborted and connection closed → connection discarded

HTTP/1.1 uses lease handoff by successful completion of the direct dispatch path: once the response body source is created and delivered successfully, the outer transport send path stops owning the lease and the body source becomes responsible for either returning the connection to the pool or closing it. This differs from the HTTP/2 `ConnectionLease.TransferOwnership()` pattern because HTTP/1.1 still needs the lease wrapper to perform `ReturnToPool()` on successful end-of-body.

**Leak detection:** if the `UHttpStreamingResponse` is abandoned without disposing, the connection lease, semaphore permit, and connection all leak. `Debug.LogWarning` in finalizer detects this in development builds.

---

## Step 8: Timeout Scope for Streaming Responses

**File:** `Runtime/Transport/RawSocketTransport.cs` (modified, continued)

The existing request-level timeout (`CancellationTokenSource.CancelAfter(request.Timeout)`) applies to **header receipt only** for streaming responses. Once `OnResponseStartAsync` is called and the body source is handed to the consumer:

- Body reads are governed by the consumer's own `CancellationToken`
- Streaming large files does not hit the request timeout
- Consumer is responsible for providing per-read or overall-download timeouts
- If no cancellation token is provided, body reads may block indefinitely (matches standard `Stream.ReadAsync` behavior)
- Half-open TCP connections detected by consumer's cancellation or TCP keep-alive socket options

---

## Planned File Impact

| File | Change |
|------|--------|
| `Runtime/Transport/Http1/Http11RequestSerializer.cs` | Known-length streaming, chunked request encoding |
| `Runtime/Transport/Http1/Http11ResponseParser.cs` | Split header/body parsing, `BufferedStreamReader` transfer |
| `Runtime/Transport/Http1/Http11ResponseBodySource.cs` | **New:** Content-Length, chunked, read-to-end, empty variants |
| `Runtime/Transport/RawSocketTransport.cs` | Direct HTTP/1.1 lease handoff, streaming timeout scope, retry semantics |

---

## Completion Criteria

- Large uploads do not allocate `O(body)` memory
- Large downloads can stream to disk with bounded memory
- Keep-alive reuse remains correct after partial/aborted reads
- HEAD responses with `Content-Length` produce `EmptyResponseBodySource`
- `Connection: close` responses never attempt drain
- Chunked request terminal sequence `0\r\n\r\n` is verified in tests
- Retry/failure semantics correct for replayable and non-replayable bodies
- `ReadAsync` cancellation on HTTP/1.1 body sources transitions to faulted state (connection not reusable)
- Chunked drain with byte budget correctly reuses connection when EOF reached within budget
- `Content-Length`/`Transfer-Encoding` conflict resolution enforced in request serializer
- Response framing checks `Transfer-Encoding` before `Content-Length` (RFC 9112 Section 6.1)

## Post-Step Review

Both specialist agents must review before proceeding to 22a.4:
- `unity-infrastructure-architect`
- `unity-network-architect`
