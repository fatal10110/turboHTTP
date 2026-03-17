# Phase 22b: Streaming Extensions — 100-Continue, Proxy Streaming, HTTP/1.1 Trailers

**Milestone:** M4 (v2.0 follow-up)
**Dependencies:** Phase 22a (end-to-end streaming must be complete)
**Estimated Complexity:** Medium-High
**Critical:** No — these are enhancements to the streaming substrate established in 22a, not blockers for core streaming functionality.
**Compatibility:** Additive. No breaking changes to the Phase 22a streaming API surface.

## Overview

Phase 22a established the end-to-end streaming substrate: pull-based body sources (`IResponseBodySource`, `UHttpRequestBody`), dual buffered/streaming paths (`SendBufferedAsync`/`SendStreamingAsync`), bounded memory via per-stream queues and pooled transfer buffers, and the updated interceptor contract (`OnResponseStartAsync`). Four capabilities were explicitly deferred as non-goals of 22a because they are orthogonal to the core streaming contract:

1. **`Expect: 100-continue`** — avoid sending non-replayable streaming bodies to servers that will reject them
2. **Streaming through proxy connections** — `DispatchViaProxyAsync` still uses the full-buffered push-based path
3. **HTTP/1.1 response trailer parsing** — `GetTrailersAsync` returns `HttpHeaders.Empty` for HTTP/1.1
4. **HTTP/1.1 request trailers** — chunked encoding sends only `0\r\n\r\n` without actual trailer fields

Phase 22b picks up all four as a focused follow-on. Each is a self-contained sub-phase with its own deliverables and completion criteria.

---

## Goals

1. Implement `Expect: 100-continue` negotiation so that non-replayable streaming request bodies are not needlessly transmitted to servers that will reject them.
2. Extend `DispatchViaProxyAsync` in `RawSocketTransport.cs` to use the 22a streaming body-source path for both request upload and response download, eliminating the memory disparity between direct and proxy-tunneled connections.
3. Parse HTTP/1.1 chunked-encoding trailers and expose them via `GetTrailersAsync` on `IResponseBodySource`, matching the HTTP/2 trailer path that already works.
4. Support sending request trailers in HTTP/1.1 chunked encoding for callers that provide them (e.g., content integrity digests computed during streaming).

## Non-Goals

1. **HTTP/2 trailer changes.** HTTP/2 trailers are already handled by HEADERS frames with END_STREAM in `Http2Connection.ReadLoop.cs` → `stream.AppendTrailers(responseHeaders)`. No work needed here.
2. **SOCKS proxy support.** Only HTTP/HTTPS CONNECT proxy tunneling is in scope. SOCKS belongs in a separate Phase.
3. **Automatic trailer-based integrity verification.** Trailers like `Digest` or `Content-MD5` are parsed and exposed; the client does not auto-validate them. Validation is the caller's responsibility.
4. **HTTP/2 `Expect: 100-continue` server push optimization.** The HTTP/2 path honors `Expect: 100-continue` semantics but does not require server-push-aware scheduling — this is deferred.
5. **Proxy ALPN negotiation for HTTP/2.** CONNECT tunnels currently force HTTP/1.1 ALPN on the inner TLS handshake, suppressing HTTP/2 negotiation with the origin server through the tunnel. Enabling HTTP/2 through CONNECT tunnels is covered by **Phase 22c** (`phase-22c-proxy-http2-alpn.md`).

---

## Sub-Phase 22b.1: `Expect: 100-continue` Handling

### Motivation

When sending a large or non-replayable streaming request body, the client currently begins transmitting immediately after headers. If the server intends to reject the request (e.g., 413 Payload Too Large, 401 Unauthorized, 403 Forbidden), the client has already committed bandwidth and potentially exhausted a one-shot body source (`RequestBodyReplayability.NonReplayable`). RFC 9110 Section 10.1.1 defines the `Expect: 100-continue` mechanism to avoid exactly this scenario.

This is especially important for `StreamRequestBody` and `FactoryRequestBody` with `NonReplayable` replayability — if the server rejects before 100, the body source is disposed without reading, preserving the caller's ability to handle the rejection without data loss.

### Current State

- `Http11RequestSerializer` currently processes `Transfer-Encoding` and `Content-Length` but has no concept of header-body split timing. Serialization writes headers and body in a single continuous flow.
- `Http11ResponseParser.ParseAsync` already handles 1xx interim responses — it loops in a `do { ... } while (statusCode >= 100 && statusCode < 200)` block (lines 131–175), skipping up to `Max1xxResponses` (10) interim responses. However, this loop runs during response parsing, not during the request-body-send phase. The 100-continue flow requires reading interim responses *between* header send and body send.
- `Http2Connection.SendRequestAsync` sends HEADERS and DATA frames sequentially. No wait-for-100 gap exists.

### Design

#### Opt-In API

The client does NOT automatically add `Expect: 100-continue`. Instead:

```csharp
// On the streaming request builder (introduced in 22a):
builder.WithExpectContinue(bool enable = true)

// Sets the "Expect: 100-continue" header on the request.
// Only meaningful for requests with a body. Ignored for bodyless requests.
```

**Why opt-in, not automatic:** Many servers do not implement 100-continue correctly (some ignore it, some always respond 100 regardless). Making it automatic would add latency for all streaming uploads. The caller knows their server's behavior and can opt in when it matters.

**Automatic opt-in consideration:** A `StreamingOptions.AutoExpectContinueThresholdBytes` property (default: disabled / `null`) can be added to automatically inject `Expect: 100-continue` for request bodies exceeding a size threshold. This is a convenience — the explicit `WithExpectContinue()` remains the primary API. If `AutoExpectContinueThresholdBytes` is set and the body's `Length` (when known) exceeds the threshold, the header is injected automatically. Unknown-length bodies (chunked) do not trigger the automatic path because their size is unknowable at header-send time.

#### HTTP/1.1 Flow

The 100-continue flow requires splitting the request-send sequence into three stages:

**Stage 1 — Send headers:**
1. `Http11RequestSerializer` serializes request line + all headers (including `Expect: 100-continue`) + header terminator (`\r\n`)
2. Flush the header bytes to the socket
3. Do NOT begin body transmission

**Stage 2 — Wait for server response:**
1. Start a timer with `ExpectContinueTimeoutMs` (configurable via `StreamingOptions`, default: 1000ms — RFC 9110 Section 10.1.1 says "a reasonable time to wait" without specifying a duration; 1 second is the common implementation choice per curl, Go stdlib, .NET HttpClient)
2. Attempt to read a response from the server using the existing `BufferedStreamReader`:
   - **`100 Continue` received:** proceed to Stage 3 (body send). Discard the 100 response. The existing 1xx-skipping loop in `Http11ResponseParser` handles this, but it must be invoked *during the body-send phase*, not after the full response is expected.
   - **Final response (2xx-5xx) received:** abort body send, return/surface the response. The body source's `RequestBodyReadSession` is disposed without reading. The `IResponseBodySource` on the response (if any) is delivered normally.
   - **Timeout with no response:** proceed to Stage 3 (body send). The server may not support 100-continue, and RFC 9110 Section 10.1.1 says the client SHOULD proceed after a reasonable delay. Log at `Debug` level: "100-continue timeout, proceeding with body send".
3. Error handling: if the socket errors during the wait, treat as a normal connection failure (the same `UHttpException` path).

**Stage 3 — Send body:**
1. Open the `RequestBodyReadSession` and stream the body normally
2. After body send completes, read the final response (or continue reading if a final response was already received in Stage 2)

#### Implementation Split in `Http11RequestSerializer`

The current `SerializeAsync` method must be split:

```csharp
// Current: single method
internal static async Task SerializeAsync(Stream stream, UHttpRequest request, CancellationToken ct)

// After 22b.1: two-stage API
internal static async Task SerializeHeadersAsync(Stream stream, UHttpRequest request, CancellationToken ct)
internal static async Task SerializeBodyAsync(Stream stream, UHttpRequestBody body, RequestBodyReadSession session, CancellationToken ct)
```

`SerializeHeadersAsync` writes the request line, headers, and the `\r\n` terminator. It does NOT write any body bytes. `SerializeBodyAsync` writes the body using either direct memory write (for buffered bodies via `TryGetBufferedData`) or incremental streaming via the `RequestBodyReadSession`.

For requests WITHOUT `Expect: 100-continue`, both stages are called in immediate succession — no behavioral change, no added latency.

#### Response Reading During Body Send

A critical subtlety: while waiting for 100-continue, the client must be prepared to read a response from the server. This means the `BufferedStreamReader` used for response parsing must be created *before* the body is sent, and any bytes consumed during the 100-continue wait must be preserved for final response parsing.

The `BufferedStreamReader` created in Stage 2 is transferred to the response-parse stage (same pattern as 22a's `BufferedStreamReader` transfer from header parse to body source). This avoids creating a second reader and losing pre-fetched bytes.

#### Half-Duplex Constraint

HTTP/1.1 is half-duplex at the application level — the client sends the full request, then reads the response. The 100-continue mechanism is the only standard exception where the client reads mid-send. This means:

- The socket must support concurrent read and write. TCP sockets are full-duplex at the OS level, so this is safe. `SslStream` also supports concurrent read and write (documented in .NET).
- The concurrent read-during-send is bounded to the 100-continue wait phase only. After the wait, the client returns to normal half-duplex behavior.

#### HTTP/2 Flow

HTTP/2 does not use `Expect: 100-continue` in the same way at the protocol level, but RFC 9113 Section 8.1 acknowledges that a server MAY send a `100 Continue` status in a HEADERS frame before the client sends DATA frames.

Required behavior:

1. If `Expect: 100-continue` is set on the request, the HEADERS frame is sent without DATA frames
2. The client waits for either:
   - A HEADERS frame with status `100` → proceed to send DATA frames
   - A HEADERS frame with final status (2xx-5xx) → abort DATA send, surface the response
   - Timeout (`ExpectContinueTimeoutMs`) → proceed to send DATA frames
3. The `Expect` header is included in the HEADERS frame's header list (it is not a connection-level or hop-by-hop header in HTTP/2)

Implementation: the wait logic lives in `Http2Connection.SendRequestAsync` between sending the HEADERS frame and the DATA-send loop. The `Http2Stream` already has a `TaskCompletionSource` for response headers — the 100-continue wait can reuse this mechanism with a short-circuit for 100 status.

#### `StreamingOptions` Extension

```csharp
public sealed class StreamingOptions
{
    // ... existing properties from 22a ...

    /// <summary>
    /// Timeout in milliseconds to wait for a 100 Continue response before
    /// proceeding with body transmission. Default: 1000ms.
    /// Only applies when Expect: 100-continue is set on the request.
    /// </summary>
    public int ExpectContinueTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// When non-null, automatically injects Expect: 100-continue for request
    /// bodies whose Length exceeds this threshold. Default: null (disabled).
    /// Unknown-length bodies (chunked) are not affected.
    /// </summary>
    public long? AutoExpectContinueThresholdBytes { get; set; }
}
```

#### Edge Cases

1. **Server sends 100 then a final error before body completes:** The body send may have already started when the server sends 4xx/5xx. The client detects this on the next response read after body send completes. No special handling — the response is delivered normally.
2. **Server sends multiple 100 responses:** The existing `Max1xxResponses` guard (10) prevents infinite loops. Each 100 is discarded.
3. **Bodyless request with `Expect: 100-continue`:** The header is allowed (per RFC) but meaningless. The client sends headers, then immediately reads the response (no body to wait for). No error thrown — just ignored.
4. **Non-replayable body + server rejects:** The body source is disposed without reading. The caller receives the rejection response. No retry is attempted (body is non-replayable).
5. **Retry with `Expect: 100-continue`:** On retry, the 100-continue flow repeats. The body is reopened via `OpenReadSessionAsync` (only if replayable).

### Files Impacted

| File | Change |
|------|--------|
| `Runtime/Transport/Http1/Http11RequestSerializer.cs` | Split into `SerializeHeadersAsync` + `SerializeBodyAsync`; preserve backward compat for non-100-continue callers via an overload or internal flag |
| `Runtime/Transport/RawSocketTransport.cs` | `DispatchOnStreamAsync` (or 22a equivalent): insert 100-continue wait between header send and body send |
| `Runtime/Transport/Http2/Http2Connection.cs` | `SendRequestAsync`: insert 100-continue wait between HEADERS and DATA frames |
| `Runtime/Transport/Http2/Http2Stream.cs` | Support early HEADERS response (100 status) before DATA send completion |
| `Runtime/Core/StreamingOptions.cs` (from 22a) | Add `ExpectContinueTimeoutMs`, `AutoExpectContinueThresholdBytes` |
| Streaming request builder (from 22a) | `WithExpectContinue()` API |
| `Runtime/Core/Internal/BufferedStreamReader.cs` | Verify that reader created during 100-continue wait can be transferred to response-parse stage without byte loss |

### Completion Criteria

- [ ] `WithExpectContinue()` builder method available on the streaming request builder
- [ ] HTTP/1.1: headers sent → wait for 100/final/timeout → body sent or aborted
- [ ] HTTP/1.1: final response before 100 aborts body send and returns response without consuming body source
- [ ] HTTP/1.1: timeout fallback proceeds with body send after `ExpectContinueTimeoutMs`
- [ ] HTTP/1.1: `BufferedStreamReader` from 100-continue wait phase is transferred to response parse without byte loss
- [ ] HTTP/2: HEADERS frame sent → wait for 100 HEADERS/final HEADERS/timeout → DATA frames sent or aborted
- [ ] Non-replayable body source is not consumed when server rejects early
- [ ] Replayable body + retry correctly reopens the body session with 100-continue on the retry attempt
- [ ] `AutoExpectContinueThresholdBytes` threshold triggers automatic header injection when body length is known and exceeds threshold
- [ ] Bodyless requests with `Expect: 100-continue` are handled without error (header passes through, no wait)
- [ ] Unit tests for all three HTTP/1.1 outcomes (100 received → body sent, final response received → body aborted, timeout → body sent)
- [ ] Unit tests for HTTP/2 100-continue flow
- [ ] Unit test for multiple 100 responses (confirm `Max1xxResponses` guard)
- [ ] Unit test for `AutoExpectContinueThresholdBytes` auto-injection
- [ ] Integration test with `MockTransport` simulating 100-continue scenarios (delayed 100, immediate rejection, timeout)
- [ ] No latency regression for requests without `Expect: 100-continue` (serialization split must not add measurable overhead)

### Performance Notes

- The `SerializeHeadersAsync` / `SerializeBodyAsync` split introduces one additional `FlushAsync` call for 100-continue requests. For non-100-continue requests, the two calls are made in immediate succession and the flush point is identical to the current single-method path.
- The 100-continue wait uses `Task.WhenAny` with a `Task.Delay(ExpectContinueTimeoutMs)` timer. The timer is cancelled if a response arrives first, avoiding orphaned timer allocations.
- On HTTP/2, the wait is cheaper because HEADERS and DATA are already separate frame sends — no serialization split is needed.

---

## Sub-Phase 22b.2: Streaming Through Proxy Connections

### Motivation

`DispatchViaProxyAsync` in `RawSocketTransport.cs` currently uses the same `DispatchOnStreamAsync` that direct connections use, but the request preparation methods (`PrepareHttpProxyForwardRequest`, `PrepareHttpsProxyTunnelRequest`) still use `request.Body.ToArray()` to copy the body into the new `UHttpRequest` (lines 468 and 483). After Phase 22a changes the body model to `UHttpRequestBody`, these methods must transfer the body source without copying.

Additionally, after Phase 22a establishes streaming dispatch paths, the proxy code must use the same streaming paths to avoid memory regression when downloading large payloads through a proxy.

### Current State

The proxy code path in `RawSocketTransport.cs` (lines 372–427):

1. **HTTP forward proxy (non-CONNECT):** Creates a connection to the proxy, prepares a forwarded request with absolute-form URI, dispatches via `DispatchOnStreamAsync`. The body is copied with `request.Body.ToArray()`.
2. **HTTPS CONNECT tunnel:** Creates a connection to the proxy, sends a CONNECT request, reads the tunnel response, performs TLS handshake through the tunnel, then dispatches the original request via `DispatchOnStreamAsync`. The body is copied with `request.Body.ToArray()`.

Post-22a, `DispatchOnStreamAsync` will be replaced by the streaming dispatch path. The proxy code must:
- Transfer `UHttpRequestBody` instead of copying `byte[]`
- Use the streaming dispatch path for both forward proxy and CONNECT tunnel
- Correctly handle `ConnectionLease.TransferOwnership()` for streaming responses through tunnels

### Design

#### HTTP Forward Proxy Streaming

For non-CONNECT forward proxy requests (plain HTTP through proxy):

1. `PrepareHttpProxyForwardRequest` must transfer `request.Content` (the `UHttpRequestBody`) to the forwarded request instead of calling `request.Body.ToArray()`. Since the proxy just changes the request-line format (absolute-form URI) and adds `Proxy-Authorization`, the body source is the same.

```csharp
// Before (current):
return new UHttpRequest(request.Method, request.Uri, headers,
    request.Body.IsEmpty ? null : request.Body.ToArray(), request.Timeout, metadata);

// After (22b.2):
return new UHttpRequest(request.Method, request.Uri, headers,
    request.Content, request.Timeout, metadata);
```

2. The streaming dispatch path handles the rest — body source streaming, bounded memory, etc.

3. Request-line serialization must support absolute-form URIs (`GET http://example.com/path HTTP/1.1`). The current `Http11RequestSerializer` checks `RequestMetadataKeys.ProxyAbsoluteForm` to format the request line. This must continue working with the 22a serialization split.

#### HTTPS CONNECT Tunnel Streaming

After the CONNECT handshake establishes a tunnel and TLS is negotiated:

1. The tunneled stream is functionally identical to a direct TLS connection. The streaming dispatch path works unchanged on this stream.

2. `PrepareHttpsProxyTunnelRequest` must transfer `request.Content` instead of copying the body:

```csharp
// Before (current):
return new UHttpRequest(request.Method, request.Uri, headers,
    request.Body.IsEmpty ? null : request.Body.ToArray(), request.Timeout, metadata);

// After (22b.2):
return new UHttpRequest(request.Method, request.Uri, headers,
    request.Content, request.Timeout, metadata);
```

3. `ConnectionLease` ownership for streaming responses through CONNECT tunnels:
   - The lease is for the proxy connection (proxy host:port), not the origin server
   - After TLS handshake, the TLS-wrapped stream is handed to the streaming dispatch
   - `ConnectionLease.TransferOwnership()` transfers the proxy connection lease to the streaming response
   - When the streaming response is disposed, the proxy connection (with its TLS wrapper) is returned to the pool or closed

4. Connection pool key: the existing pool uses `(host, port)` as the connection key. For CONNECT tunnels, the connection is to the proxy but carries traffic to the origin. The pool key must remain `(proxyHost, proxyPort)` for the outer connection — the inner TLS session is tied to the origin but the socket-level connection is to the proxy. This means proxy connections to different origins through the same proxy cannot be reused (each CONNECT tunnel is origin-specific). This is the current behavior and is correct.

#### Connection Reuse Through Tunnels

After a streaming response through a CONNECT tunnel is fully consumed:
- HTTP/1.1 on the inner connection follows the same drain-or-close rules as direct HTTP/1.1
- If the inner HTTP/1.1 connection can be reused (keep-alive, body fully drained), the tunnel remains open and the lease is returned to the pool. The next request to the same origin through the same proxy can reuse the tunnel.
- If the inner connection cannot be reused, the tunnel is closed and the lease is discarded.

For HTTP/2 through CONNECT tunnels: the current code forces HTTP/1.1 on the tunnel framing ("Advertising h2 ALPN here would negotiate HTTP/2 that this tunnel code path cannot yet service safely"). The *inner* TLS connection to the origin server can negotiate HTTP/2 via ALPN. This is the correct and expected behavior — the proxy tunnel is HTTP/1.1 but the end-to-end protocol can be HTTP/2. No changes needed for this in 22b.

#### CONNECT Tunnel Body Handling

The CONNECT handshake itself is always bodyless — it's a tunneling mechanism, not a content request. The body of the actual request is sent after the tunnel is established. The current `DrainProxyConnectBodyAsync` handles any unexpected body in the CONNECT response (e.g., error pages). This remains unchanged.

### Files Impacted

| File | Change |
|------|--------|
| `Runtime/Transport/RawSocketTransport.cs` | `DispatchViaProxyAsync`: use streaming dispatch after tunnel establishment; `PrepareHttpProxyForwardRequest`: transfer `request.Content` instead of `request.Body.ToArray()`; `PrepareHttpsProxyTunnelRequest`: transfer `request.Content` instead of `request.Body.ToArray()` |
| `Runtime/Transport/Http1/Http11RequestSerializer.cs` | Verify absolute-form URI serialization works with `SerializeHeadersAsync` / `SerializeBodyAsync` split from 22b.1 |

### Completion Criteria

- [ ] HTTP forward proxy request uses streaming body source (no `Body.ToArray()` copy)
- [ ] HTTPS CONNECT tunnel request uses streaming body source (no `Body.ToArray()` copy)
- [ ] Streaming response through CONNECT tunnel correctly transfers `ConnectionLease` ownership
- [ ] Connection reuse after fully-consumed streaming response through tunnel works correctly
- [ ] Early-dispose of streaming response through tunnel follows drain-or-close policy
- [ ] Memory behavior through proxy matches direct connection (bounded, not proportional to payload size)
- [ ] Large upload through HTTP forward proxy does not allocate `O(body)` extra memory
- [ ] Large download through HTTPS CONNECT tunnel streams to consumer with bounded memory
- [ ] `Expect: 100-continue` works correctly through proxy connections (if 22b.1 is complete)
- [ ] Absolute-form URI serialization works with the 22a/22b.1 serialization split
- [ ] Unit tests with `MockTransport` for forward proxy streaming upload/download
- [ ] Unit tests with `MockTransport` for CONNECT tunnel streaming upload/download
- [ ] Unit test for connection reuse through tunnel after full body consumption
- [ ] Unit test for early-dispose through tunnel (drain-or-close)

### IL2CPP / Platform Notes

- Proxy connections go through the same `TcpConnectionPool` and `TlsProviderSelector` as direct connections. No new platform-specific concerns.
- `SslStream` through a CONNECT tunnel is the same as direct `SslStream` — the inner TLS handshake is to the origin server, not the proxy.

---

## Sub-Phase 22b.3: HTTP/1.1 Response Trailer Parsing

### Motivation

HTTP/1.1 chunked transfer encoding supports trailers after the final chunk (RFC 9112 Section 7.1.2). The current parser (`Http11ResponseParser.ReadChunkedBodyAsync`, lines 520–529) reads and discards trailer lines with a documented TODO:

```csharp
// Read and discard trailers (RFC 9112 §7.1.2).
// Known limitation: trailer fields declared via the Trailer header
// (e.g. Content-MD5, Digest) are consumed but not merged into the
// response headers. Future work: expose trailers on ParsedResponse.
while (true)
{
    var line = await reader.ReadLineAsync(ct, MaxHeaderLineLength).ConfigureAwait(false);
    if (string.IsNullOrEmpty(line))
        break;
}
```

Phase 22a's `IResponseBodySource` defines `GetTrailersAsync` but returns `HttpHeaders.Empty` for HTTP/1.1 responses. This sub-phase implements actual trailer parsing and exposure, making HTTP/1.1 and HTTP/2 trailer behavior consistent.

### Current State

- **HTTP/2 trailers work:** `Http2Connection.ReadLoop.cs` line 409 calls `stream.AppendTrailers(responseHeaders)` when trailing HEADERS are received. `Http2Stream._trailers` stores them and `OnResponseEnd` passes them to the handler.
- **HTTP/1.1 trailers are discarded:** The `ReadChunkedBodyAsync` loop at lines 524–529 reads trailer lines and throws them away.
- **`ParsedResponse`** does not have a trailer field.
- **`GetTrailersAsync`** on the 22a `IResponseBodySource` for HTTP/1.1 returns `HttpHeaders.Empty`.
- **Trailer-capable framing:** Only chunked transfer encoding supports trailers. Content-Length and read-to-end framing do not have a trailer section.

### Design

#### Parsing Location

Trailers are parsed in the same location where they are currently discarded — inside `ReadChunkedBodyAsync` (or, post-22a, inside the equivalent chunked body source's read loop). The existing `while (true) { ReadLineAsync ... }` loop is replaced with actual header parsing:

```csharp
// After terminal chunk (size == 0):
var trailers = new HttpHeaders();
while (true)
{
    var line = await reader.ReadLineAsync(ct, MaxHeaderLineLength).ConfigureAwait(false);
    if (string.IsNullOrEmpty(line))
        break;

    // Parse "Name: Value" — same logic as header parsing
    int colonIndex = line.IndexOf(':');
    if (colonIndex <= 0)
        continue; // Malformed trailer — skip silently (defensive)

    var name = line.Substring(0, colonIndex).Trim();
    var value = line.Substring(colonIndex + 1).Trim();

    // CRLF injection validation (same as headers)
    ValidateHeaderValue(name, value);

    // Hop-by-hop filtering (RFC 9110 §6.6.2)
    if (IsProhibitedTrailer(name))
        continue; // Silently discard

    trailers.Add(name, value);
}
```

#### Prohibited Trailers (RFC 9110 Section 6.6.2)

The following headers MUST NOT appear as trailers and are silently discarded if present:

- `Transfer-Encoding`
- `Content-Length`
- `Host`
- `Trailer` (the declaration header itself)
- `Connection`
- `Keep-Alive`
- `Proxy-Connection`
- `Upgrade`
- `TE`

These are hop-by-hop or framing headers that have no meaning as trailers. The filter is implemented as a static `HashSet<string>` with `StringComparer.OrdinalIgnoreCase`.

```csharp
private static readonly HashSet<string> s_prohibitedTrailers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Transfer-Encoding", "Content-Length", "Host", "Trailer",
    "Connection", "Keep-Alive", "Proxy-Connection", "Upgrade", "TE"
};

private static bool IsProhibitedTrailer(string name)
{
    return s_prohibitedTrailers.Contains(name);
}
```

#### Storage and Exposure

**Buffered path (22a `BufferedResponseCollectorHandler`):**

For buffered responses, the body is fully consumed during `SendBufferedAsync`. Trailers are parsed at the end of chunked body reading and stored on the response. Since the body is already fully consumed when the caller receives `UHttpResponse`, trailers are available immediately.

Post-22a, trailers flow through `IResponseBodySource.GetTrailersAsync`. For the buffered path:
1. `Http11ResponseBodySource` reads the chunked body incrementally
2. When `ReadAsync` returns 0 (EOF), the chunked body source reads trailers from the stream
3. `GetTrailersAsync` returns the parsed trailers
4. `BufferedResponseCollectorHandler` calls `GetTrailersAsync` after draining the body, includes trailers in `UHttpResponse`

Adding a `Trailers` property to `UHttpResponse`:

```csharp
public sealed class UHttpResponse
{
    // ... existing properties ...

    /// <summary>
    /// Trailer headers received after the response body (HTTP/1.1 chunked or HTTP/2).
    /// Empty if no trailers were present or if the response used Content-Length framing.
    /// </summary>
    public HttpHeaders Trailers { get; }
}
```

**Streaming path (`SendStreamingAsync`):**

For streaming responses, trailers are not available until the body is fully consumed:
1. The chunked body source reads and parses trailers after the terminal chunk
2. `GetTrailersAsync` blocks (returns a pending `ValueTask<HttpHeaders>`) until EOF + trailer parsing completes
3. After `ReadAsync` returns 0, `GetTrailersAsync` completes synchronously with the parsed trailers

Implementation: the `Http11ResponseBodySource` stores trailers in a `TaskCompletionSource<HttpHeaders>` (or `ManualResetValueTaskSourceCore<HttpHeaders>` for zero-alloc on the hot path). The trailer TCS is completed:
- After successful trailer parsing: completed with parsed `HttpHeaders`
- After EOF with no trailers (Content-Length framing, read-to-end): completed with `HttpHeaders.Empty`
- After body source disposal before EOF: completed with `HttpHeaders.Empty` (best effort)
- After body source fault: completed with the fault exception

#### `Trailer` Header Declaration

The response `Trailer` header (RFC 9110 Section 6.6.2) declares which trailer fields will be sent. Per the RFC, the declaration is informational — a server may send trailers not declared in the `Trailer` header, and may not send all declared trailers. The parser does not use the `Trailer` header for validation — it parses whatever trailers arrive after the terminal chunk. The `Trailer` header is treated as a normal response header.

#### Interaction with Decompression

If the response is compressed (`Content-Encoding: gzip/deflate`) and the `DecompressionBodySource` wraps the `Http11ResponseBodySource`:
- The decompression wrapper delegates `GetTrailersAsync` to the underlying body source
- Trailers are at the HTTP framing level, not the content-coding level — they are available after the compressed body is fully consumed, not after decompression is complete
- In practice, these are the same moment (decompression EOF follows compressed EOF)

#### Interaction with Cache

The `TeeBodySource` (from 22a.5) must also delegate `GetTrailersAsync` to the underlying body source. Trailers should be included in cached responses if the cache format supports them. If the cache format does not support trailers, they are lost on cache replay — this is acceptable for 22b and can be enhanced later.

### Files Impacted

| File | Change |
|------|--------|
| `Runtime/Transport/Http1/Http11ResponseParser.cs` | `ReadChunkedBodyAsync`: replace trailer discard loop with actual parsing + prohibited-trailer filtering |
| `Runtime/Core/IResponseBodySource.cs` (from 22a) | No interface change needed — `GetTrailersAsync` is already defined |
| Http11ResponseBodySource implementation (from 22a) | Store and expose parsed trailers via `GetTrailersAsync`; use `TaskCompletionSource<HttpHeaders>` for async completion |
| `Runtime/Core/UHttpResponse.cs` | Add `Trailers` property |
| `Runtime/Core/Pipeline/BufferedResponseCollectorHandler.cs` (from 22a) | Fetch trailers after body drain, pass to `UHttpResponse` constructor |
| `Runtime/Core/HttpHeaders.cs` | Add `IsProhibitedTrailer` helper (or keep it private in parser) |
| `Runtime/Middleware/DecompressionHandler.cs` (or 22a equivalent) | Delegate `GetTrailersAsync` to underlying body source |
| `Runtime/Cache/CacheStoringHandler.cs` (or 22a equivalent) | Delegate `GetTrailersAsync` to underlying body source |

### Completion Criteria

- [ ] Chunked response trailers are parsed into `HttpHeaders` instead of being discarded
- [ ] Prohibited hop-by-hop headers in trailers are silently discarded (list of 9 headers)
- [ ] `GetTrailersAsync` on streaming HTTP/1.1 chunked response returns parsed trailers after body EOF
- [ ] `GetTrailersAsync` on streaming HTTP/1.1 Content-Length response returns `HttpHeaders.Empty` (no trailer section)
- [ ] `GetTrailersAsync` on buffered response returns trailers immediately (body already consumed)
- [ ] `UHttpResponse.Trailers` property contains parsed trailers for buffered responses
- [ ] Trailer parsing respects `MaxHeaderLineLength` limit (same as headers)
- [ ] CRLF injection validation on trailer values (same as headers)
- [ ] Malformed trailer lines (no colon, empty name) are silently skipped (defensive parsing)
- [ ] `DecompressionBodySource` delegates `GetTrailersAsync` to underlying source
- [ ] `TeeBodySource` delegates `GetTrailersAsync` to underlying source
- [ ] Body source fault propagates to `GetTrailersAsync` (throws the fault exception)
- [ ] Disposing body source before EOF completes `GetTrailersAsync` with `HttpHeaders.Empty`
- [ ] Unit test: chunked response with trailers (`Content-MD5`, `Digest`, custom)
- [ ] Unit test: chunked response without trailers (empty trailer section `0\r\n\r\n`)
- [ ] Unit test: prohibited trailer filtering (each of the 9 prohibited headers)
- [ ] Unit test: malformed trailer lines (no colon, whitespace-only, empty)
- [ ] Unit test: streaming `GetTrailersAsync` awaits until EOF, then returns trailers
- [ ] Unit test: `GetTrailersAsync` after body source fault throws fault exception
- [ ] Unit test: trailer parsing with CRLF injection attempt (rejected)
- [ ] Integration test: buffered response with trailers via `UHttpResponse.Trailers`
- [ ] HTTP/2 trailer behavior unchanged (regression test)

### Memory and Performance Notes

- Trailers are typically 1–3 fields (e.g., `Digest`, `Content-MD5`, `Server-Timing`). The `HttpHeaders` allocation is negligible.
- The `TaskCompletionSource<HttpHeaders>` for async trailer availability is allocated once per response. For the buffered path, this is a synchronous completion (no async overhead). For the streaming path, the TCS bridges the producer (body source EOF) and consumer (`GetTrailersAsync` caller) threads.
- The `HashSet<string>` for prohibited trailer names is a static readonly allocation — no per-request cost.

---

## Sub-Phase 22b.4: HTTP/1.1 Request Trailer Support

### Motivation

The current chunked request encoder (post-22a) sends `0\r\n\r\n` as the terminal chunk — an empty trailer section. RFC 9112 Section 7.1.2 allows sending actual trailer fields after the terminal chunk. This is useful for:

1. **Content integrity:** Sending `Content-MD5` or `Digest` trailer computed during streaming — the hash is only known after the full body has been read.
2. **Server timing:** Sending `Server-Timing` or custom metrics trailers computed during upload.
3. **gRPC-Web:** gRPC-Web over HTTP/1.1 uses trailers for `grpc-status` and `grpc-message`.

### Current State

- Post-22a, the chunked encoder in `Http11RequestSerializer.SerializeBodyAsync` reads from the `RequestBodyReadSession` until EOF, then writes `0\r\n\r\n` (terminal chunk + empty trailer section).
- There is no API to provide trailer fields.
- HTTP/2 request trailers are already structurally supported: a HEADERS frame with END_STREAM after the DATA frames. However, the current `Http2Connection.SendRequestAsync` does not expose an API for callers to provide trailing headers. This is addressed in 22b.4 for both protocols.

### Design

#### Trailer Provider API

The streaming request builder gains a trailer provider — a callback invoked after the request body source reaches EOF:

```csharp
public sealed class UHttpRequestBody
{
    // ... existing from 22a ...

    /// <summary>
    /// Optional provider called after body EOF to produce request trailers.
    /// Null if no trailers are to be sent.
    /// </summary>
    internal Func<HttpHeaders> TrailerProvider { get; }
}
```

Builder API:

```csharp
// On the streaming request builder:
builder.WithRequestTrailers(Func<HttpHeaders> trailerProvider)

// Example usage — compute digest during streaming:
var sha256 = SHA256.Create();
builder
    .WithStreamBody(new CryptoStream(fileStream, sha256, CryptoStreamMode.Read))
    .WithRequestTrailers(() =>
    {
        var headers = new HttpHeaders();
        headers.Set("Digest", "sha-256=" + Convert.ToBase64String(sha256.Hash));
        return headers;
    });
```

**Why `Func<HttpHeaders>` and not `Func<Task<HttpHeaders>>`:** Request trailers are typically computed from data available at body-EOF time (e.g., hash accumulators, counters). An async provider would add complexity for a case that is extremely rare in practice. If an async trailer provider is needed in the future, it can be added as an overload without breaking changes.

**Why not `HttpHeaders` directly on the builder:** The trailer values are typically not known at request construction time — they depend on body content (e.g., hash of the streamed body). The provider pattern allows lazy computation.

#### `Trailer` Header Declaration

When a trailer provider is set, the `Trailer` header is automatically declared in the request headers:

1. The trailer provider is invoked at EOF time, producing an `HttpHeaders` instance
2. Before the body is sent, the `Trailer` header is set on the request headers listing the trailer field names:
   ```
   Trailer: Digest, Content-MD5
   ```
3. This is a problem: the `Trailer` header must be sent with the request headers (before the body), but the trailer field names are only known when the provider is invoked (after the body). Two solutions:

   **Option A — Explicit declaration:** The builder requires the caller to declare trailer names upfront:
   ```csharp
   builder.WithRequestTrailers(new[] { "Digest" }, () => { ... });
   ```
   The `Trailer` header is set from the declared names. The provider must return headers matching the declaration (extra headers are silently dropped; missing headers mean the server sees fewer trailers than declared — this is allowed by RFC 9110 Section 6.6.2).

   **Option B — No automatic `Trailer` header:** The `Trailer` header is informational (RFC 9110 Section 6.6.2 says "A sender SHOULD generate a Trailer header field ... to indicate which fields will be present"). It is not mandatory. The client sends trailers without declaring them. Servers that require the `Trailer` header for trailer processing are rare. The caller can manually set the `Trailer` header if needed.

   **Decision: Option A.** Explicit declaration is safer and more correct per the RFC. The builder API is:
   ```csharp
   builder.WithRequestTrailers(IReadOnlyList<string> declaredNames, Func<HttpHeaders> provider)
   ```

#### HTTP/1.1 Encoding

After the terminal chunk, trailer fields are serialized:

```
0\r\n
Digest: sha-256=...\r\n
Content-MD5: ...\r\n
\r\n
```

Each trailer field follows the same format as a header line: `name: value\r\n`. The final `\r\n` terminates the trailer section.

Implementation in `Http11RequestSerializer.SerializeBodyAsync`:

```csharp
// After body EOF:
// 1. Write terminal chunk
await WriteAsync(stream, "0\r\n", ct);

// 2. If trailer provider is set, invoke and serialize
if (body.TrailerProvider != null)
{
    var trailers = body.TrailerProvider();
    foreach (var name in trailers.Names)
    {
        // Prohibited trailer check
        if (IsProhibitedTrailer(name))
            continue;

        foreach (var value in trailers.GetValues(name))
        {
            ValidateHeaderValue(name, value); // CRLF injection protection
            await WriteAsync(stream, $"{name}: {value}\r\n", ct);
        }
    }
}

// 3. Write empty line (trailer section terminator)
await WriteAsync(stream, "\r\n", ct);
```

#### HTTP/2 Request Trailers

HTTP/2 request trailers are sent as a HEADERS frame with END_STREAM after all DATA frames:

1. After the last DATA frame (which does NOT have END_STREAM — that flag is reserved for the trailing HEADERS):
   - Invoke the trailer provider
   - Encode trailer headers via HPACK
   - Send a HEADERS frame with END_STREAM flag

2. If no trailer provider is set, the last DATA frame carries END_STREAM (current behavior).

Implementation in `Http2Connection.SendRequestAsync`:

```csharp
// After body EOF:
if (body.TrailerProvider != null)
{
    // Last DATA frame does NOT have END_STREAM
    await SendDataFrameAsync(streamId, lastChunk, endStream: false, ct);

    var trailers = body.TrailerProvider();
    // Filter prohibited trailers + HPACK-encode
    await SendTrailingHeadersAsync(streamId, trailers, ct);
}
else
{
    // Last DATA frame has END_STREAM (current behavior)
    await SendDataFrameAsync(streamId, lastChunk, endStream: true, ct);
}
```

#### Prohibited Request Trailers

The same prohibited trailer set from 22b.3 applies to request trailers. Additionally:
- `Content-Type` and `Content-Encoding` are not allowed as trailers (they describe the message body representation and must be known before the body is processed)
- `Authorization` is not allowed as a trailer (authentication must complete before the body is processed)

The prohibited set is extended:

```csharp
private static readonly HashSet<string> s_prohibitedRequestTrailers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Transfer-Encoding", "Content-Length", "Host", "Trailer",
    "Connection", "Keep-Alive", "Proxy-Connection", "Upgrade", "TE",
    "Content-Type", "Content-Encoding", "Authorization"
};
```

#### Chunked-Only Constraint

Request trailers are only valid with chunked transfer encoding (HTTP/1.1) or with DATA + trailing HEADERS (HTTP/2). If:
- HTTP/1.1 with `Content-Length` (known-length body): trailers cannot be sent. The builder throws `InvalidOperationException` at build time if `WithRequestTrailers` is combined with a body that has known `Length` and HTTP/1.1 is the target protocol.
- However, since the protocol is not known at build time (HTTP/1.1 vs HTTP/2 is determined during connection), the validation happens at serialization time:
  - HTTP/1.1 with known-length body + trailer provider → throw `InvalidOperationException("Request trailers require chunked transfer encoding. Use an unknown-length body or remove the trailer provider.")`
  - HTTP/2 with any body + trailer provider → valid (HTTP/2 always supports trailing HEADERS)

**Alternative:** For HTTP/1.1 with known-length body + trailer provider, the serializer could force chunked encoding even though the length is known. This is valid per RFC but unusual. **Decision: throw, don't force chunked.** Forcing chunked for known-length bodies is surprising behavior and wastes bandwidth on chunk framing.

### Files Impacted

| File | Change |
|------|--------|
| `Runtime/Core/UHttpRequestBody.cs` (from 22a) | Add `TrailerProvider` property and `DeclaredTrailerNames` |
| Streaming request builder (from 22a) | `WithRequestTrailers(IReadOnlyList<string> declaredNames, Func<HttpHeaders> provider)` |
| `Runtime/Transport/Http1/Http11RequestSerializer.cs` | `SerializeBodyAsync`: serialize trailers after terminal chunk; validate chunked-only constraint |
| `Runtime/Transport/Http2/Http2Connection.cs` | `SendRequestAsync`: send trailing HEADERS frame after DATA frames when provider is set |
| `Runtime/Core/HttpHeaders.cs` | Reuse `IsProhibitedTrailer` from 22b.3 (or shared static set) |

### Completion Criteria

- [ ] `WithRequestTrailers(declaredNames, provider)` builder method available
- [ ] `Trailer` header automatically set on request headers from declared names
- [ ] HTTP/1.1 chunked: terminal chunk followed by serialized trailer fields and empty line
- [ ] HTTP/1.1 Content-Length + trailer provider → `InvalidOperationException` at serialization time
- [ ] HTTP/2: trailing HEADERS frame with END_STREAM sent after DATA frames when provider is set
- [ ] HTTP/2: last DATA frame does NOT have END_STREAM when trailer provider is set
- [ ] Prohibited request trailers are silently filtered (12 header names)
- [ ] CRLF injection validation on trailer values
- [ ] Trailer provider returning empty `HttpHeaders` → same behavior as no provider (empty trailer section / END_STREAM on last DATA)
- [ ] Trailer provider returning `null` → same behavior as no provider
- [ ] Unit test: HTTP/1.1 chunked request with trailers (wire format validation)
- [ ] Unit test: HTTP/1.1 Content-Length + trailer provider → exception
- [ ] Unit test: HTTP/2 request with trailing HEADERS frame
- [ ] Unit test: prohibited trailer filtering for request trailers
- [ ] Unit test: CRLF injection rejection in request trailers
- [ ] Unit test: empty/null trailer provider behavior
- [ ] Unit test: `Trailer` header declaration matches provider output
- [ ] Integration test: round-trip with request trailers (mock server validates receipt)

### Security Notes

- Request trailer values are validated with the same CRLF injection check used for headers. This prevents HTTP response splitting attacks where a malicious trailer value could inject extra HTTP responses.
- The `Authorization` header is prohibited as a trailer because servers process authentication before reading the body. Allowing it as a trailer could bypass auth checks on servers that don't read trailers.

---

## Ordering and Dependencies

```
22b.1 (Expect: 100-continue)  ─── independent
22b.2 (Proxy streaming)       ─── independent (but benefits from 22b.1 for 100-continue through proxies)
22b.3 (Response trailers)     ─── independent
22b.4 (Request trailers)      ─── depends on 22b.3 (shared prohibited-trailer filtering logic)
```

**Recommended order:** 22b.3 → 22b.4 → 22b.1 → 22b.2

Rationale:
1. **22b.3 first:** Response trailer parsing is the simplest and most self-contained change. It also establishes the prohibited-trailer filtering code reused by 22b.4.
2. **22b.4 second:** Request trailers build on 22b.3's filtering logic and complete the trailer story.
3. **22b.1 third:** 100-continue is the most complex sub-phase (serialization split, concurrent read/write, timer management). Having trailers done first avoids interleaving concerns.
4. **22b.2 last:** Proxy streaming depends on the 22a streaming dispatch path being stable and benefits from all other 22b features being available (100-continue through proxies, trailers through proxies).

Alternative: 22b.1 and 22b.3 can run in parallel if two developers are available, since they touch different parts of the transport code.

---

## Sub-Phase Effort Estimates

| Sub-Phase | Name | Effort |
|-----------|------|--------|
| 22b.1 | `Expect: 100-continue` Handling | 3–4 days |
| 22b.2 | Streaming Through Proxy Connections | 2–3 days |
| 22b.3 | HTTP/1.1 Response Trailer Parsing | 2–3 days |
| 22b.4 | HTTP/1.1 Request Trailer Support | 2–3 days |

Total: 9–13 days

---

## Validation

Each sub-phase must pass both specialist agent reviews (unity-infrastructure-architect + unity-network-architect) before marking complete, per project convention.

### Cross-Cutting Test Requirements

- All new tests must run under Unity Test Runner with NUnit
- All new tests go in `Tests/Runtime/` under appropriate subdirectories
- `MockTransport` (from Phase 7 / 22a) is used for unit tests, not real network I/O
- Integration tests use the `ExternalNetwork` test category for optional real-server testing
- IL2CPP-sensitive patterns (`ValueTask<T>`, `IAsyncDisposable`) must have `link.xml` entries

### RFC Compliance Matrix

| RFC | Section | Feature | Sub-Phase |
|-----|---------|---------|-----------|
| RFC 9110 | 10.1.1 | `Expect: 100-continue` semantics | 22b.1 |
| RFC 9110 | 6.6.2 | Trailer header field, prohibited trailers | 22b.3, 22b.4 |
| RFC 9112 | 7.1.2 | HTTP/1.1 chunked trailer section | 22b.3, 22b.4 |
| RFC 9113 | 8.1 | HTTP/2 `100 Continue` in HEADERS frame | 22b.1 |
| RFC 9113 | 8.1 | HTTP/2 trailing HEADERS frame | 22b.4 |

---

## Deferred Until Implementation

1. **`Func<Task<HttpHeaders>>` async trailer provider:** Synchronous `Func<HttpHeaders>` is sufficient for known use cases (hash accumulator, counters). Async provider can be added as an overload without breaking changes.
2. **Cache format trailer persistence:** Whether cached responses preserve trailers depends on the cache serialization format. Currently deferred — trailers are lost on cache replay.
3. **`AutoExpectContinueThresholdBytes` for unknown-length bodies:** Only known-length bodies trigger the automatic threshold. Unknown-length (chunked) bodies could use a heuristic, but this is deferred.
4. **HTTP/2 CONNECT tunnel ALPN upgrade:** Covered by **Phase 22c** (`phase-22c-proxy-http2-alpn.md`). Depends on 22b.2 (streaming through proxy connections).
5. **gRPC-Web trailer interop testing:** gRPC-Web uses HTTP/1.1 trailers for `grpc-status`. Formal interop testing with gRPC-Web servers is deferred to the gRPC phase (Phase 25).
