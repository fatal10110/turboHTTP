# Phase 22b.1: `Expect: 100-continue` Handling

**Depends on:** Phase 22a (complete)
**Assemblies:** `TurboHTTP.Core`, `TurboHTTP.Transport`
**Files to create:** 0 new, 5–7 modified

---

## Step 1: `StreamingOptions` Extension

**File:** `Runtime/Core/StreamingOptions.cs` (modified, from 22a)

Add two new properties:

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

**Default 1000ms rationale:** RFC 9110 Section 10.1.1 says "a reasonable time to wait" without specifying a duration. 1 second is the common choice per curl, Go stdlib, .NET HttpClient.

---

## Step 2: Builder API — `WithExpectContinue`

**File:** Streaming request builder from 22a (modified)

```csharp
/// <summary>
/// Sets the "Expect: 100-continue" header on the request.
/// Only meaningful for requests with a body. Ignored for bodyless requests.
/// </summary>
builder.WithExpectContinue(bool enable = true)
```

### Behavior

1. When `enable` is `true`, sets `Expect: 100-continue` header on the request
2. When `enable` is `false`, removes the `Expect` header if present
3. Bodyless requests: header is allowed per RFC but meaningless — no wait occurs

### Auto-Injection

If `StreamingOptions.AutoExpectContinueThresholdBytes` is set and the body's `Length` (when known) exceeds the threshold, the `Expect: 100-continue` header is injected automatically by the transport before serialization. Unknown-length bodies (chunked) do not trigger the automatic path. A threshold of `0` means all known-length non-empty bodies opt into the wait.

**Immutability constraint:** `UHttpRequest` is immutable. The transport must create a modified copy via `request.WithHeader("Expect", "100-continue")` and use this copy consistently throughout the entire dispatch path — serialization, `handler.OnResponseStartAsync(...)`, logging, and observability. The original request must not be passed to any handler after auto-injection.

**Why opt-in, not automatic by default:** Many servers do not implement 100-continue correctly (some ignore it, some always respond 100 regardless). Automatic injection would add latency for all streaming uploads. The caller knows their server's behavior.

---

## Step 3: Split `Http11RequestSerializer` into Two-Stage API

**File:** `Runtime/Transport/Http1/Http11RequestSerializer.cs` (modified)

### Current State

Single method serializes the full request:

```csharp
internal static async Task SerializeAsync(Stream stream, UHttpRequest request, CancellationToken ct)
```

### Change

Split into header-only and body-only methods:

```csharp
/// <summary>
/// Writes request line + all headers + header terminator (\r\n).
/// Does NOT write any body bytes.
/// </summary>
internal static async Task SerializeHeadersAsync(Stream stream, UHttpRequest request, CancellationToken ct)

/// <summary>
/// Writes the body using either direct memory write (for buffered bodies via TryGetBufferedData)
/// or incremental streaming via the RequestBodyReadSession.
/// </summary>
internal static async Task SerializeBodyAsync(Stream stream, UHttpRequestBody body, RequestBodyReadSession session, CancellationToken ct)
```

### Non-100-Continue Path

For requests WITHOUT `Expect: 100-continue`, both methods are called in immediate succession — **no behavioral change, no added latency, no extra flush**:

```csharp
await SerializeHeadersAsync(stream, request, ct);
await SerializeBodyAsync(stream, body, session, ct);
```

### 100-Continue Path

For requests WITH `Expect: 100-continue`, a wait is inserted between the two calls (see Step 4).

### Performance Constraint

The split must not introduce measurable overhead for the non-100-continue path. The headers+body are still flushed together when no wait is needed.

---

## Step 4: HTTP/1.1 100-Continue Wait Logic

**File:** `Runtime/Transport/RawSocketTransport.cs` (modified) — `DispatchOnStreamAsync` or 22a equivalent

### Three-Stage Flow

**Stage 1 — Send headers:**
1. `SerializeHeadersAsync` writes request line + all headers (including `Expect: 100-continue`) + `\r\n`
2. Flush header bytes to the socket
3. Do NOT begin body transmission

**Stage 2 — Wait for server response:**
1. Start a timer: `Task.Delay(ExpectContinueTimeoutMs, ct)`
2. Attempt to read a response from the server using the `BufferedStreamReader`:
   - **`100 Continue` received:** Proceed to Stage 3 (body send). Discard the 100 response.
   - **Final response (2xx-5xx) received:** Abort body send. Dispose `RequestBodyReadSession` without reading. Return/surface the response with its body source normally.
   - **Timeout with no response:** Proceed to Stage 3 (body send). Log at `Debug` level: `"100-continue timeout, proceeding with body send"`.
3. Error handling: socket errors during the wait → normal connection failure (`UHttpException` path).

**Stage 3 — Send body:**
1. Open the `RequestBodyReadSession` and stream the body normally via `SerializeBodyAsync`
2. After body send completes, read the final response (or continue reading if already received in Stage 2)
3. If body production or socket write fails after the wait has committed to body transmission, surface that send failure even if a final response head also arrives. Partial uploads are treated as transport failures rather than silent success.

### Implementation Detail — `Task.WhenAny`

```csharp
// Dedicated CTS for the delay timer — ensures cleanup when response arrives first
using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
var timeoutTask = Task.Delay(options.ExpectContinueTimeoutMs, delayCts.Token);
var responseReadTask = ReadInterimResponseAsync(reader, ct);

var winner = await Task.WhenAny(timeoutTask, responseReadTask).ConfigureAwait(false);

if (winner == responseReadTask)
{
    delayCts.Cancel(); // Clean up the timer registration immediately
    var interimResult = await responseReadTask.ConfigureAwait(false);
    if (interimResult.StatusCode == 100)
    {
        // Proceed to body send (Stage 3)
    }
    else
    {
        // Final response received — abort body send
        // Drain error response body if present before returning lease to pool
        // Return the response
    }
}
else
{
    // Timeout — proceed with body send (Stage 3)
    // CRITICAL: Do NOT issue any new reads on the BufferedStreamReader here.
    // The responseReadTask is still in flight, reading from the same reader.
    // After body send completes (Stage 3), we MUST await responseReadTask
    // before starting the final response read. See "Timeout Path Sequencing" below.
}
```

**Timer cleanup:** The dedicated `CancellationTokenSource` is cancelled when the response arrives first, immediately disposing the timer registration. The `using` statement ensures cleanup on all paths.

### Timeout Path Sequencing (Critical)

When the timeout fires, `responseReadTask` may still be reading from the `BufferedStreamReader`. Two concurrent reads on the same reader is **not safe** — the reader has internal buffer state and position tracking.

**Required sequencing after timeout:**

```csharp
// Stage 3 — body send proceeds after timeout
await SerializeBodyAsync(stream, body, session, ct);

// AFTER body send completes, await the interim read task.
// This ensures no concurrent reads on the BufferedStreamReader.
var interimResult = await responseReadTask.ConfigureAwait(false);

if (interimResult != null && interimResult.StatusCode != 100)
{
    // Server sent a final response (e.g., 4xx/5xx) during/after body send.
    // Body was already sent — deliver this response normally.
    // The reader state from responseReadTask is preserved for body source parsing.
    return BuildResponse(interimResult, reader);
}

// interimResult is null (no response yet) or was 100 (proceed normally)
// Read the final response using the same reader — no byte loss
var finalResponse = await ReadFinalResponseAsync(reader, ct).ConfigureAwait(false);
```

The `BufferedStreamReader` from `responseReadTask` is **transferred** to the final response read. Any bytes pre-fetched during the interim read are preserved in the reader's internal buffer. This is the same transfer pattern used in 22a for header-parse-to-body-source handoff.

### Half-Duplex Constraint

HTTP/1.1 is half-duplex at the application level. The 100-continue mechanism is the only standard exception where the client reads mid-send.

- TCP sockets are full-duplex at the OS level — concurrent read + write is safe
- `SslStream` supports concurrent read and write (documented in .NET)
- The concurrent read-during-send is bounded to the 100-continue wait phase only
- **Platform validation required:** SslStream concurrent read/write must be tested on physical iOS (Apple Security framework) and Android (OpenSSL via Mono) devices under IL2CPP, as these use different native TLS backends than desktop .NET

---

## Step 5: HTTP/2 100-Continue Wait Logic

**File:** `Runtime/Transport/Http2/Http2Connection.cs` (modified) — `SendRequestAsync`

### Design

HTTP/2 does not use `Expect: 100-continue` the same way at the protocol level, but RFC 9113 Section 8.1 acknowledges that a server MAY send a `100 Continue` status in a HEADERS frame before the client sends DATA frames.

### Flow

1. Send HEADERS frame (includes `Expect: 100-continue` in the header list — it is NOT a hop-by-hop header in HTTP/2)
2. Do NOT send DATA frames yet
3. Wait for one of:
   - **HEADERS frame with status `100`:** Proceed to send DATA frames
   - **HEADERS frame with final status (2xx-5xx):** Abort DATA send, surface the response
   - **Timeout (`ExpectContinueTimeoutMs`):** Proceed to send DATA frames
4. The `Http2Stream` already has a `TaskCompletionSource` for response headers — the 100-continue wait can reuse this mechanism with a short-circuit for 100 status

### `Http2Stream` Modification

**File:** `Runtime/Transport/Http2/Http2Stream.cs` (modified)

Support early HEADERS response (100 status) before DATA send completion:

- When a HEADERS frame with `:status: 100` arrives, signal the 100-continue wait without completing the final response TCS
- The stream must distinguish between interim 100 headers and the final response headers
- Implementation: a separate `TaskCompletionSource<bool>` (with `TaskCreationOptions.RunContinuationsAsynchronously`) for the 100-continue signal, alongside the existing response-header TCS
- **Do NOT use `ManualResetEventSlim`** — it is a blocking primitive incompatible with async code and can cause deadlocks on Unity's main thread or under IL2CPP

### Read Loop Integration

**File:** `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs` (modified) — `HandleHeadersFrame`

The read loop's `HandleHeadersFrame` method currently treats the first HEADERS frame with a `:status` pseudo-header as the final response. For 100-continue support:

1. When `HandleHeadersFrame` receives a HEADERS frame for a stream, extract `:status`
2. If `:status` is in the 1xx range (100–199):
   - If the stream has a pending 100-continue TCS, complete it with `true`
   - Do NOT complete the stream's final response-header TCS
   - Do NOT transition the stream to "headers received" state
   - Discard the interim headers (per RFC 9113 Section 8.1, informational responses do not carry entity headers)
3. If `:status` is in the 2xx–5xx range:
   - If the stream has a pending 100-continue TCS, complete it with `false` (signal: final response arrived, abort DATA send)
   - Complete the stream's final response-header TCS as normal
4. **RST_STREAM during wait:** If `HandleRstStreamFrame` fires while the 100-continue TCS is pending, the TCS must be cancelled/faulted. The existing `Http2Stream.Abort()` path should propagate to the 100-continue TCS.

This is the most implementation-complex piece of 22b.1 and must be carefully tested for state machine correctness.

---

## Step 6: Edge Cases

### 6a. Server Sends 100 Then Final Error Before Body Completes

The body send may have already started when the server sends 4xx/5xx. After body send completes, the response MUST be read before the connection is declared failed. If the server closes the connection before the client reads the response, the client may get a broken pipe error rather than the error response. The implementation must read the response promptly after body send, even if a write error occurred.

### 6b. Server Sends Multiple 100 Responses

The existing `Max1xxResponses` guard (10) prevents infinite loops. Each 100 is discarded.

### 6c. Bodyless Request with `Expect: 100-continue`

Header is allowed per RFC but meaningless. Client sends headers, then immediately reads the response (no body to wait for). No error thrown.

### 6d. Non-Replayable Body + Server Rejects

Body source is disposed without reading. Caller receives the rejection response. No retry is attempted.

### 6e. Retry with `Expect: 100-continue`

On retry, the 100-continue flow repeats. The body is reopened via `OpenReadSessionAsync` (only if replayable).

### 6f. Connection Reuse After 100-Continue Rejection

When the server sends a final response (e.g., 403, 417) instead of 100, the body was NOT sent. The connection is clean from the client side. However, if the rejection response has a body (e.g., an error page), the client must drain the response body before returning the connection to the pool. Apply the standard drain-or-close policy (`BufferedDrainReuseThresholdBytes`).

### 6g. Proxy Strips `Expect` Header

Some proxies silently strip or ignore `Expect: 100-continue`. In this case, the server never sees the header and never sends 100. The timeout fires and body is sent — correct fallback behavior per RFC 9110 Section 10.1.1. This adds `ExpectContinueTimeoutMs` of latency. Known limitation, documented.

### 6h. Server Sends 100 After Timeout But Before Body Send Completes

The `responseReadTask` may complete with a 100 response during body send. This is harmless — the body was already being sent. After body send, `responseReadTask` is awaited (see "Timeout Path Sequencing"), the 100 is consumed, and the final response is read normally. The `Max1xxResponses` guard in the final response read path handles any additional 100s.

### 6i. HTTP/2 RST_STREAM During 100-Continue Wait

If the server sends RST_STREAM while the client is waiting for 100-continue, the 100-continue TCS on the `Http2Stream` must be faulted immediately. The existing `Http2Stream.Abort()` path should propagate to the 100-continue TCS, causing the wait to throw and the stream to be cleaned up normally.

---

## Step 7: Tests

**File:** `Tests/Runtime/Transport/Http1/Http11ExpectContinueTests.cs` (new)
**File:** `Tests/Runtime/Transport/Http2/Http2ExpectContinueTests.cs` (new)

### HTTP/1.1 Unit Tests

1. **100 received → body sent** — server responds 100, body is transmitted, final response received
2. **Final response received → body aborted** — server responds 403 before 100, body source disposed without reading
3. **Timeout → body sent** — no response within `ExpectContinueTimeoutMs`, body sent anyway
4. **Multiple 100 responses** — verify `Max1xxResponses` guard prevents infinite loop
5. **`BufferedStreamReader` transfer** — bytes pre-fetched during 100-continue wait are preserved for final response parsing
6. **Non-replayable body + server rejects** — body source not consumed, rejection response delivered
7. **Replayable body + retry** — body reopened on retry, 100-continue flow repeats
8. **Bodyless request with `Expect: 100-continue`** — no wait, no error

### HTTP/2 Unit Tests

9. **100 HEADERS → DATA frames** — server responds 100, DATA frames sent
10. **Final HEADERS → DATA aborted** — server responds 4xx, no DATA frames sent
11. **Timeout → DATA frames** — no HEADERS response in time, DATA frames sent anyway
12. **RST_STREAM during 100-continue wait** — verify stream faulted, TCS cancelled

### Integration Tests

13. **`AutoExpectContinueThresholdBytes` threshold** — verify automatic header injection for body exceeding threshold
14. **`AutoExpectContinueThresholdBytes` + unknown-length** — verify unknown-length body does NOT trigger auto-injection
15. **`AutoExpectContinueThresholdBytes` modified request consistency** — verify the auto-injected `Expect` header is visible to observability/logging layers (not just serialization)
16. **MockTransport scenarios** — delayed 100, immediate rejection, timeout
17. **Connection reuse after rejection** — server sends 403 with body, verify connection drained and returned to pool
18. **Timeout path sequencing** — verify `responseReadTask` is awaited after body send, no concurrent reader access

### Performance Test

19. **No latency regression** — requests without `Expect: 100-continue` must not have measurable overhead from the serialization split

---

## Files Impacted (Summary)

| File | Change |
|------|--------|
| `Runtime/Core/StreamingOptions.cs` | Add `ExpectContinueTimeoutMs`, `AutoExpectContinueThresholdBytes` |
| Streaming request builder (from 22a) | `WithExpectContinue()` API |
| `Runtime/Transport/Http1/Http11RequestSerializer.cs` | Split into `SerializeHeadersAsync` + `SerializeBodyAsync` |
| `Runtime/Transport/RawSocketTransport.cs` | Insert 100-continue wait between header send and body send |
| `Runtime/Transport/Http2/Http2Connection.cs` | Insert 100-continue wait between HEADERS and DATA frames |
| `Runtime/Transport/Http2/Http2Stream.cs` | Support early HEADERS response (100 status) before DATA send |
| `Runtime/Core/Internal/BufferedStreamReader.cs` | Verify reader transfer from 100-continue wait to response-parse stage |
| `Tests/Runtime/Transport/Http1/Http11ExpectContinueTests.cs` | New test file |
| `Tests/Runtime/Transport/Http2/Http2ExpectContinueTests.cs` | New test file |

## Completion Criteria

- [ ] `WithExpectContinue()` builder method available on the streaming request builder
- [ ] HTTP/1.1: headers sent → wait for 100/final/timeout → body sent or aborted
- [ ] HTTP/1.1: final response before 100 aborts body send and returns response without consuming body source
- [ ] HTTP/1.1: timeout fallback proceeds with body send after `ExpectContinueTimeoutMs`
- [ ] HTTP/1.1: `BufferedStreamReader` from 100-continue wait phase transferred without byte loss
- [ ] HTTP/2: HEADERS frame sent → wait for 100 HEADERS/final HEADERS/timeout → DATA frames sent or aborted
- [ ] Non-replayable body source is not consumed when server rejects early
- [ ] Replayable body + retry correctly reopens the body session with 100-continue on retry
- [ ] `AutoExpectContinueThresholdBytes` threshold triggers automatic header injection
- [ ] Bodyless requests with `Expect: 100-continue` handled without error
- [ ] Timeout path awaits `responseReadTask` after body send before final response read (no concurrent reader access)
- [ ] Timer cleanup via dedicated `CancellationTokenSource` (no leaked timer registrations)
- [ ] Connection drained and reused after server rejection with response body
- [ ] Auto-injected `Expect` header uses modified request copy consistently through entire dispatch path
- [ ] HTTP/2 read loop distinguishes 1xx interim HEADERS from final response HEADERS
- [ ] HTTP/2 RST_STREAM during 100-continue wait faults the stream correctly
- [ ] SslStream concurrent read/write validated on physical iOS and Android devices
- [ ] All unit and integration tests pass (19 tests)
- [ ] No latency regression for requests without `Expect: 100-continue`

## Performance Notes

- The `SerializeHeadersAsync` / `SerializeBodyAsync` split introduces one additional `FlushAsync` call for 100-continue requests. For non-100-continue requests, the two calls are in immediate succession with identical flush behavior.
- The 100-continue wait uses `Task.WhenAny` with `Task.Delay`. Timer cleaned up via dedicated `CancellationTokenSource` — cancelled and disposed when the response arrives first. The `using` on the CTS ensures cleanup on all paths including exceptions.
- `Task.WhenAny` allocates a `WhenAnyPromise` task. Acceptable because it only fires for requests with the `Expect` header (opt-in). `ConfigureAwait(false)` on the `WhenAny` result is critical to avoid posting continuations back to Unity's main thread `SynchronizationContext`.
- On HTTP/2, the wait is cheaper because HEADERS and DATA are already separate frame sends — no serialization split needed.
