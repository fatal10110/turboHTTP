# Phase 19c: Zero-Allocation Hot Path Completion

**Milestone:** M4 (v1.2)
**Dependencies:** Phase 19b (API Modernization), Phase 19a.2 (Buffer-Writer Serialization), Phase 19a.3 (Segmented Sequences)
**Estimated Complexity:** Medium–High
**Estimated Effort:** 1–2 weeks
**Critical:** No — performance optimization track.

## Overview

Phase 19a and 19b eliminated most per-request GC pressure: pooled connections, pooled requests, pooled HTTP/2 streams, pooled response bodies, IBufferWriter<byte> JSON serialization. What remains is a set of identified hot-path allocations concentrated in three areas:

1. **Body copy** — `UHttpRequest.Body` is typed as `byte[]`, forcing `WithLeasedBody()` to call `Memory.ToArray()` even though the source is already a pooled `IMemoryOwner<byte>`. Neither transport serializer can consume `ReadOnlyMemory<byte>` directly.

2. **HTTP/1.1 parser string allocations** — Status line and header parsing call `string.Substring()` and `string.Split()` per line, allocating 2–5 KB of strings per response.

3. **HTTP/2 per-request list and header allocations** — A fresh `List<(string, string)>` is allocated per HTTP/2 request for HPACK input, `Encode()` returns a `byte[]` copy of the reusable encoder buffer, and `HpackDecoder` allocates a new list per response.

Together these prevent reaching the Phase 6 target of <500 bytes GC per request on the hot path.

---

## Estimated Allocation Impact

| Sub-Phase | Protocol | Savings per Request/Response |
|---|---|---|
| 19c.1 — Zero-Copy Body Transport | HTTP/1.1 + HTTP/2 | Body size (100 B – 10 MB) |
| 19c.2 — HTTP/1.1 Parser String Elimination | HTTP/1.1 | 2–5 KB per response |
| 19c.3 — HTTP/2 Header List Pooling | HTTP/2 | 700 B – 5.5 KB per request/response |
| 19c.4 — PooledHeaderWriter.AppendInt | HTTP/1.1 | 20–50 B per POST request |

**Cumulative target:** <500 bytes GC per GET/POST request after all sub-phases.

---

## Sub-Phase Index

| Sub-Phase | Name | Effort |
|---|---|---|
| 19c.1 | Zero-Copy Body Transport | 2–3 days |
| 19c.2 | HTTP/1.1 Parser String Elimination | 2–3 days |
| 19c.3 | HTTP/2 Header List Pooling | 2–3 days |
| 19c.4 | PooledHeaderWriter Integer Encoding | 1 day |

---

## 19c.1: Zero-Copy Body Transport

### Problem

`UHttpRequest.WithLeasedBody(IMemoryOwner<byte>)` calls `Memory.ToArray()`, creating a full heap copy of the pooled body bytes. Both transport serializers read `request.Body` as `byte[]` and cannot consume `ReadOnlyMemory<byte>` directly. The `_bodyOwner` field exists only for pool-return lifecycle management, never for the actual write.

Additionally, `Http2Connection` calls `request.DisposeBodyOwner()` inside the DATA send `finally` block (promptly after DATA frames are sent). This is safe today only because `Body` is an independent heap copy — it would cause use-after-pool-return if `Body` were backed by `_bodyOwner.Memory`.

### Target State

```
JSON serializer → PooledArrayBufferWriter → DetachAsOwner()
  → WithLeasedBody(owner): Body = owner.Memory (zero-copy)
    → Http11RequestSerializer: stream.WriteAsync(request.Body, ct)   // Memory<byte> overload
    → Http2Connection.SendDataAsync: body.Slice(offset).CopyTo(...)  // ReadOnlyMemory<byte>
    → outer finally: request.DisposeBodyOwner()                      // sole dispose site
```

### Step 1 — `UHttpRequest.Body` → `ReadOnlyMemory<byte>`

**File:** `Runtime/Core/UHttpRequest.cs`

1. Change `public byte[] Body { get; private set; }` to `public ReadOnlyMemory<byte> Body { get; private set; }`.
2. `WithBody(byte[] body)` → `Body = body != null ? new ReadOnlyMemory<byte>(body) : ReadOnlyMemory<byte>.Empty;`
3. `WithBody(string body)` → `Body = body != null ? new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(body)) : ReadOnlyMemory<byte>.Empty;`
4. `WithLeasedBody(IMemoryOwner<byte> bodyOwner)` → remove `Memory.ToArray()`; `Body = bodyOwner?.Memory ?? ReadOnlyMemory<byte>.Empty;`
5. `ResetForPool()` → `Body = ReadOnlyMemory<byte>.Empty;`
6. All `request.Body?.Length ?? 0` → `request.Body.Length`; all `request.Body != null` → `!request.Body.IsEmpty`.

**Breaking API change:** `UHttpRequest.Body` type changes from `byte[]` to `ReadOnlyMemory<byte>`. Callers that need a `byte[]` use `.Body.ToArray()` or `.Body.Span`. Accepted per Phase 19b greenfield assumptions.

### Step 2 — `Http11RequestSerializer`

**File:** `Runtime/Transport/Http1/Http11RequestSerializer.cs`

- `int actualBodyLength = request.Body?.Length ?? 0;` → `int actualBodyLength = request.Body.Length;`
- `request.Body != null && request.Body.Length > 0` → `!request.Body.IsEmpty`
- Body write:
  ```csharp
  // Before
  await stream.WriteAsync(request.Body, 0, request.Body.Length, ct).ConfigureAwait(false);

  // After — Stream.WriteAsync(ReadOnlyMemory<byte>, CancellationToken) is .NET Standard 2.1
  await stream.WriteAsync(request.Body, ct).ConfigureAwait(false);
  ```
- Content-Length validation comparison `normalizedContentLength.Value != actualBodyLength` — unchanged, still `int`.

### Step 3 — `Http2Connection` Body Send Path

**File:** `Runtime/Transport/Http2/Http2Connection.cs`

1. `bool hasBody = request.Body != null && request.Body.Length > 0;` → `bool hasBody = !request.Body.IsEmpty;` (two occurrences).

2. `SendDataAsync` signature:
   ```csharp
   // Before
   private async Task SendDataAsync(int streamId, byte[] body, Http2Stream stream, CancellationToken ct)
   // After
   private async Task SendDataAsync(int streamId, ReadOnlyMemory<byte> body, Http2Stream stream, CancellationToken ct)
   ```

3. Inside `SendDataAsync`, replace `Buffer.BlockCopy(body, offset, payload, 0, actualAvailable)` with:
   ```csharp
   body.Slice(offset, actualAvailable).CopyTo(new Memory<byte>(payload, 0, actualAvailable));
   ```
   `body.Length` is unchanged (`ReadOnlyMemory<byte>.Length`).

4. **Remove the early `DisposeBodyOwner()` call** inside the DATA send `finally` block. When `Body` is backed by `_bodyOwner.Memory`, this early dispose would invalidate memory still being written under flow-control waits. The outer `finally` at the end of `SendRequestAsync` is the sole disposal site.

5. HTTP/2 Content-Length pseudo-header: `request.Body?.Length` → `request.Body.Length`.

### Step 4 — Audit Other `request.Body` Consumers

Grep for `request\.Body` across all assemblies. Expected callers:

| File | Change |
|---|---|
| `Tests/Runtime/Transport/Http11SerializerTests.cs` | Update test helper Body construction and assertions |
| `Tests/Runtime/Transport/Http11ResponseParserTests.cs` | Same |
| `Tests/Runtime/Core/UHttpClientTests.cs` | `.Body` assertions → `.Body.Span` or `.Body.ToArray()` |
| `Runtime/Cache/CacheMiddleware.cs` | Any cache-key or replay reads |
| Progress tracking middleware | Any `body.Length` reads |

### Step 5 — Verify `DisposeBodyOwner` Timing in HTTP/1.1 Path

Confirm `RawSocketTransport` (or caller) calls `request.DisposeBodyOwner()` after `Http11RequestSerializer.SerializeAsync` fully returns. `SerializeAsync` fully awaits `stream.WriteAsync(request.Body, ct)` before returning, so the ordering is correct. No change required — just verify by tracing the call chain.

---

## 19c.2: HTTP/1.1 Parser String Allocation Elimination

### Problem

`Http11ResponseParser` uses `string.Substring()` for every status line and header line, and `string.Split(',')` for multi-value headers. On a response with 20 headers this allocates approximately 2–5 KB of strings that are immediately discarded after parsing.

**Specific hotspots (identified in `Http11ResponseParser.cs`):**

| Location | Allocation |
|---|---|
| Status line: HTTP version extraction | `statusLine.Substring(0, firstSpace)` |
| Status line: status code extraction | `statusLine.Substring(firstSpace + 1, ...)` |
| Status line: reason phrase | `statusLine.Substring(...)` |
| Header name | `line.Substring(0, colonIndex)` per header |
| Header value | `line.Substring(colonIndex + 1).Trim()` per header |
| Connection header | `headerValue.Split(',')` array per connection response |
| Chunk size | `chunkSizeLine.Substring(...)` for chunk extension stripping |

### Step 1 — Span-Based Status Line Parsing

**File:** `Runtime/Transport/Http1/Http11ResponseParser.cs`

Parse the status line using `AsSpan()` slices instead of `Substring`. Only create a string for the HTTP version if it must be stored (for Connection keep-alive detection). The status code is a 3-digit integer — parse it directly with span arithmetic, no string needed.

```csharp
// Before
var parts = statusLine.Split(' ', 3);
var httpVersion = parts[0];
int statusCode = int.Parse(parts[1]);

// After — zero alloc for status code, one alloc for httpVersion only if stored
var span = statusLine.AsSpan();
int firstSpace = span.IndexOf(' ');
var versionSpan = span.Slice(0, firstSpace);           // no alloc
int statusCode = ParseStatusCodeSpan(span, firstSpace); // digit math, no alloc
// Only allocate httpVersion string if needed for keep-alive decision:
string httpVersion = versionSpan.ToString();            // one alloc, stored
```

### Step 2 — Span-Based Header Name and Value Parsing

The hot path: every header line. Replace `Substring` with `AsSpan` slices and only call `.ToString()` at the point of storing the value into `HttpHeaders`.

```csharp
// Before
var colonIndex = line.IndexOf(':');
var name = line.Substring(0, colonIndex).Trim();
var value = line.Substring(colonIndex + 1).Trim();

// After
var lineSpan = line.AsSpan();
var colonIndex = lineSpan.IndexOf(':');
var nameSpan = lineSpan.Slice(0, colonIndex).Trim();   // ReadOnlySpan<char>, no alloc
var valueSpan = lineSpan.Slice(colonIndex + 1).Trim(); // ReadOnlySpan<char>, no alloc
// Store only when the value is actually needed:
_headers.Set(nameSpan.ToString(), valueSpan.ToString()); // two allocs, unavoidable for storage
```

Net saving: the intermediate Trim() strings are eliminated; only the final storage strings are allocated.

### Step 3 — Span-Based Multi-Value Header Splitting

Replace `headerValue.Split(',')` with an `IndexOf(',')` scan loop that processes each token in-place on a `ReadOnlySpan<char>`:

```csharp
// Before
foreach (var token in headerValue.Split(','))
    ProcessToken(token.Trim());

// After — zero allocs for iteration
var span = headerValue.AsSpan();
while (span.Length > 0)
{
    int comma = span.IndexOf(',');
    var token = comma >= 0 ? span.Slice(0, comma).Trim() : span.Trim();
    ProcessToken(token);
    if (comma < 0) break;
    span = span.Slice(comma + 1);
}
```

`ProcessToken` compares using `SequenceEqual` on the span for known tokens (e.g., `"keep-alive"`, `"close"`, `"chunked"`) — no string allocation for the comparison itself.

### Step 4 — Span-Based Chunk Size Parsing

Chunk size lines may contain chunk extensions separated by `;`. Currently stripped via `Substring`. Use `IndexOf(';')` + span slice instead.

### Implementation Notes

- `ReadOnlySpan<char>` is fully available in .NET Standard 2.1 and safe on IL2CPP.
- `string.AsSpan()` is a zero-alloc extension available from .NET Standard 2.1 via `System.MemoryExtensions`.
- `SpanExtensions.Trim(ReadOnlySpan<char>)` is available in .NET Standard 2.1.
- Where a `string` result must be compared with a known constant, prefer `MemoryExtensions.Equals(span, "keep-alive", StringComparison.OrdinalIgnoreCase)` — no allocation.

---

## 19c.3: HTTP/2 Header List Pooling

### Problem

Three allocations per HTTP/2 request/response cycle:

1. **`List<(string, string)>` for HPACK encoder input** (`Http2Connection.SendRequestAsync`, line ~245): allocated fresh per request, capacity-pre-counted via a full header iteration loop.
2. **`HpackEncoder.Encode()` returns `byte[]`** (HpackEncoder.cs, `output.WrittenMemory.ToArray()`): the encoder already uses a reusable `_outputBuffer` (`PooledArrayBufferWriter`), but the output is `ToArray()`'d — copying the encoded block to a new heap array on every request.
3. **`List<(string, string)>` for HPACK decoder output** (`HpackDecoder.Decode`, line ~44): allocated fresh per response header block.

### Step 1 — Pool the Header List in `Http2Connection`

**File:** `Runtime/Transport/Http2/Http2Connection.cs`

Add a per-connection `List<(string, string)>` that is cleared and reused across requests (safe because HTTP/2 stream requests are serialized through the write lock):

```csharp
// Connection field
private readonly List<(string name, string value)> _headerListScratch =
    new List<(string, string)>(24); // typical request: 10-20 headers

// In SendRequestAsync — reuse instead of allocate
_headerListScratch.Clear();
// ... populate _headerListScratch ...
byte[] headerBlock = _hpackEncoder.Encode(_headerListScratch);
```

**Safety:** The HEADERS frame is sent inside `_writeLock`. HPACK encoding is synchronous. The list is only accessed under the write lock path, which is serialized per connection. No concurrent access — safe to reuse.

Remove the pre-count loop — use `_headerListScratch.Add()` directly, accepting `List<T>` growth as needed (amortized O(1)).

### Step 2 — `HpackEncoder.Encode()` Returns `ReadOnlyMemory<byte>`

**File:** `Runtime/Transport/Http2/HpackEncoder.cs`

Change:
```csharp
// Before
public byte[] Encode(IReadOnlyList<(string, string)> headers)
{
    _outputBuffer.Reset();
    // ... encode ...
    return _outputBuffer.WrittenMemory.ToArray(); // full copy
}

// After
public ReadOnlyMemory<byte> Encode(IReadOnlyList<(string, string)> headers)
{
    _outputBuffer.Reset();
    // ... encode ...
    return _outputBuffer.WrittenMemory; // zero-copy slice of the reusable buffer
}
```

**Caller update:** `Http2Connection.SendRequestAsync` passes the encoded block to `SendHeadersAsync`. Update `SendHeadersAsync` to accept `ReadOnlyMemory<byte>` instead of `byte[]`.

**Safety:** The `_outputBuffer` is a per-encoder (per-connection) reusable buffer. `Encode()` resets it at the start. The returned `ReadOnlyMemory<byte>` is valid until the next `Encode()` call. `SendHeadersAsync` consumes it synchronously inside `_writeLock` before any concurrent `Encode()` call can happen. Lifetime is safe.

**Frame codec update:** `Http2FrameCodec.WriteFrameAsync` or equivalent that writes the HEADERS payload must accept `ReadOnlyMemory<byte>`. If it currently takes `byte[]`, update to use `stream.WriteAsync(memory, ct)` (Stream Memory overload, .NET Standard 2.1).

### Step 3 — Pool the Decoder Output List in `HpackDecoder`

**File:** `Runtime/Transport/Http2/HpackDecoder.cs`

The decoder currently allocates `new List<(string, string)>()` per response header block. Two approaches:

**Option A (simpler):** Accept a pre-allocated, cleared `List<(string, string)>` as an output parameter, eliminating the alloc:
```csharp
// Before
public IReadOnlyList<(string, string)> Decode(ReadOnlySpan<byte> data)

// After
public void Decode(ReadOnlySpan<byte> data, List<(string, string)> output)
```
The caller (`Http2Connection.ReadLoop`) passes a per-connection scratch list (similar to Step 1's encoder list). The list is cleared between header blocks.

**Option B (API-preserving):** Keep the return type but add an overload that takes the output list. Use the overload internally.

Option A is preferred for the internal decoder; the decoded headers are immediately passed to `HttpHeaders` construction and the list is no longer needed.

**Safety:** The HTTP/2 read loop processes one HEADERS frame at a time per connection (`_headerBlockBuffer` accumulates across CONTINUATION frames, then decodes once). The scratch list is per-connection and accessed only on the read-loop continuation — no concurrent access.

### Step 4 — Cache HTTP/2 Scheme Header

**File:** `Runtime/Transport/Http2/Http2Connection.cs`

`:scheme` (`:scheme = "https"`) is a constant per connection (set at TLS handshake). Currently `ToLowerAsciiInvariant(request.Uri.Scheme)` is called per request. Cache as a `readonly string` field at construction time.

```csharp
// Connection field
private readonly string _schemeHeader; // "https" or "http"

// Constructor
_schemeHeader = _useTls ? "https" : "http";

// In SendRequestAsync — replace runtime call
headerList.Add((":scheme", _schemeHeader)); // no allocation
```

### Step 5 — Inline ASCII Lowercase for Header Names

**File:** `Runtime/Transport/Http2/Http2Connection.cs`

`ToLowerAsciiInvariant(string value)` currently calls `string.ToLowerInvariant()` when it finds an uppercase character. This allocates a new string for every header name that contains uppercase letters (e.g., user-provided `"Content-Type"` → `"content-type"`).

Replace with a span-based inline lowercase write directly to the header list tuple:

```csharp
private static string ToLowerAsciiHeaderName(string value)
{
    // Fast path: already lowercase (most common for pre-lowercased headers)
    for (int i = 0; i < value.Length; i++)
    {
        char c = value[i];
        if (c >= 'A' && c <= 'Z')
        {
            // Slow path: copy + lowercase. One string alloc per header that has uppercase.
            // Future: stackalloc for headers < 256 chars.
            return string.Create(value.Length, value, (span, src) =>
            {
                for (int j = 0; j < src.Length; j++)
                    span[j] = src[j] >= 'A' && src[j] <= 'Z' ? (char)(src[j] | 0x20) : src[j];
            });
        }
    }
    return value; // already lowercase, no alloc
}
```

This eliminates `string.ToLowerInvariant()` (which creates a full copy even for single-case strings). The fast path (already lowercase) is zero-alloc. `string.Create` is available in .NET Standard 2.1.

Further optimization: pre-lowercase `HttpHeaders` keys at `Set`/`Add` time so that the HTTP/2 send path sees pre-lowercased names. This requires updating `HttpHeaders` to normalize on input — broader change, can be deferred.

---

## 19c.4: PooledHeaderWriter Integer Encoding

### Problem

`PooledHeaderWriter.AppendInt(int value)` in `Http11RequestSerializer` calls `value.ToString(CultureInfo.InvariantCulture)`, which allocates a string for the Content-Length value on every POST/PUT/PATCH request with a body.

**File:** `Runtime/Transport/Http1/Http11RequestSerializer.cs`, line ~302.

### Fix

Replace with direct digit-to-Latin-1 encoding into the `PooledArrayBufferWriter`:

```csharp
public void AppendInt(int value)
{
    // Maximum digits for int: 10 (2,147,483,647). Write directly to span.
    Span<byte> digits = stackalloc byte[11]; // 10 digits + possible sign
    int pos = digits.Length;

    uint uval = (uint)value;
    do
    {
        digits[--pos] = (byte)('0' + uval % 10);
        uval /= 10;
    } while (uval > 0);

    var dst = _writer.GetSpan(digits.Length - pos);
    digits.Slice(pos).CopyTo(dst);
    _writer.Advance(digits.Length - pos);
}
```

`stackalloc` is available in .NET Standard 2.1 and is IL2CPP-safe for small fixed sizes. No heap allocation. No string. Saves ~40 bytes per POST request.

The same approach applies to `AppendInt(long value)` if large content lengths are ever needed.

---

## Verification

### Compile & Build
- No `request\.Body\b` occurrences typed as `byte[]` remain in transport code after 19c.1.
- No `string\.Substring` calls on parser hot paths in `Http11ResponseParser` after 19c.2.
- No `new List<` allocations per HTTP/2 request in `Http2Connection` after 19c.3.
- No `\.ToString(` calls for integer encoding in `PooledHeaderWriter` after 19c.4.

### Allocation Gate Tests
- Add `GC.GetTotalAllocatedBytes()` delta gate test for a GET request end-to-end via `MockTransport`: must be <500 bytes.
- Add gate test for a POST request with a JSON body via `WithJsonBody<T>()`: delta must be <500 bytes (excluding the JSON string input itself).
- Add gate test for a mock HTTP/2 response decode: header decode path must not allocate new `List<T>` objects.

### Regression Tests
- All existing `Http11SerializerTests`, `Http11ResponseParserTests`, `Http2` tests must pass.
- Status line parsing tests must cover: valid 200, 404, 101 (Switching Protocols), edge cases with no reason phrase.
- Chunk size parsing tests must cover: chunk extensions (`;` suffix), multi-chunk responses.

### Protocol Correctness
- `Http11ResponseParser` span-based parsing must produce byte-for-byte identical header `name` and `value` strings to the current `Substring` version. Add a property-based test that cross-checks both implementations on random header lines.

---

## Interaction Map

```
Phase 19a.2 (IBufferWriter<byte> JSON) ──► 19c.1 (Body zero-copy transport)
                                           (removes the ToArray() that 19a.2 left behind)

Phase 19a.3 (SegmentedBuffer response)    No interaction — response path is complete.

Phase 19a.5 (Http2StreamPool,             19c.3 augments Http2Connection with reusable
             HpackEncoder._outputBuffer)   lists; works alongside existing pools.

Phase 19b (UHttpRequest pooling,          19c.1 changes UHttpRequest.Body type;
           UHttpResponse ReadOnlySequence) tests must be updated for new type.
```

---

## Notes

**`ReadOnlyMemory<byte>` in .NET Standard 2.1:** All target platforms (Unity 2021.3 LTS, IL2CPP iOS/Android) fully support `ReadOnlyMemory<byte>` and the `Stream.WriteAsync(ReadOnlyMemory<byte>, CancellationToken)` overload. No compatibility concerns.

**`string.Create` in .NET Standard 2.1:** Available. IL2CPP-safe with concrete `TState = string`. No AOT generic concerns.

**`stackalloc` on IL2CPP:** Safe for small fixed-size allocations (<1 KB). The 11-byte digit buffer in 19c.4 is well within safe limits on all IL2CPP platforms.

**HTTP/2 encoder scratch list thread safety:** The `_headerListScratch` field is written only inside the `_writeLock`. HTTP/2 connections do not multiplex writes — only one HEADERS frame is being encoded at a time. Safe without additional synchronization.

**HpackDecoder scratch list thread safety:** The read loop is a single `Task` per connection. The decoder scratch list is read-loop-local. Safe.
