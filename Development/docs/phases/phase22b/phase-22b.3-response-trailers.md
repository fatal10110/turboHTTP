# Phase 22b.3: HTTP/1.1 Response Trailer Parsing

**Depends on:** Phase 22a (complete)
**Assemblies:** `TurboHTTP.Transport`, `TurboHTTP.Core`, `TurboHTTP.Middleware`, `TurboHTTP.Cache`
**Files to create:** 0 new, 6–8 modified

---

## Step 1: Prohibited Trailer Filter

**File:** `Runtime/Transport/Internal/TrailerFieldValidator.cs` (new, in `TurboHTTP.Transport`)

Create a shared `internal static` class for prohibited-trailer filtering, used by both 22b.3 (response trailers) and 22b.4 (request trailers). Both consumers are in the Transport assembly.

RFC 9110 Section 6.6.2 prohibits trailers that are "necessary for message framing, routing, request modifiers, controls, conditionals, or authentication." The set must cover all categories:

```csharp
internal static class TrailerFieldValidator
{
    /// <summary>
    /// Prohibited response trailers per RFC 9110 Section 6.6.2.
    /// Covers: framing, routing, hop-by-hop, authentication, conditionals,
    /// and content metadata that must be known before body processing.
    /// </summary>
    internal static readonly HashSet<string> ProhibitedResponseTrailers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Framing / hop-by-hop
        "Transfer-Encoding", "Content-Length", "Host", "Trailer",
        "Connection", "Keep-Alive",
        "Proxy-Connection", // Non-standard, included for defense-in-depth
        "Upgrade", "TE",
        // Content metadata (must be known before body processing)
        "Content-Encoding", "Content-Type", "Content-Range",
        // Authentication (RFC 9110 Section 11)
        "Authorization", "Proxy-Authorization",
        "WWW-Authenticate", "Proxy-Authenticate",
        // Request modifiers / conditionals
        "Cache-Control", "Expect", "Max-Forwards", "Pragma",
        "Range", "If-Match", "If-None-Match",
        "If-Modified-Since", "If-Unmodified-Since", "If-Range",
        // Response metadata that must be known at header time
        "Age", "Expires", "Date", "Location", "Retry-After", "Vary"
    };

    /// <summary>
    /// Prohibited request trailers — extends the response set with
    /// request-specific prohibitions. Used by 22b.4.
    /// </summary>
    internal static readonly HashSet<string> ProhibitedRequestTrailers =
        new HashSet<string>(ProhibitedResponseTrailers, StringComparer.OrdinalIgnoreCase);
    // Note: ProhibitedRequestTrailers is initialized as a copy of ProhibitedResponseTrailers.
    // If request-specific additions are needed beyond the shared set, add them here.

    internal static bool IsProhibitedResponseTrailer(string name)
        => ProhibitedResponseTrailers.Contains(name);

    internal static bool IsProhibitedRequestTrailer(string name)
        => ProhibitedRequestTrailers.Contains(name);
}
```

**Rationale:** The RFC's language is descriptive rather than enumerating a fixed set. This comprehensive deny list covers all categories mentioned in RFC 9110 Section 6.6.2 and aligns with the approach used by Go's `net/http` and .NET's `HttpClient`. `Proxy-Connection` is non-standard but included for defense-in-depth against legacy proxies.

---

## Step 2: Replace Trailer Discard Loop with Actual Parsing

**File:** `Runtime/Transport/Http1/Http11ResponseParser.cs` (modified) — `ReadChunkedBodyAsync` or the equivalent post-22a chunked body source

### Current State

The terminal-chunk handling currently reads and discards trailer lines:

```csharp
// Read and discard trailers (RFC 9112 §7.1.2).
while (true)
{
    var line = await reader.ReadLineAsync(ct, MaxHeaderLineLength).ConfigureAwait(false);
    if (string.IsNullOrEmpty(line))
        break;
}
```

### Change

Replace with actual parsing:

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

### Validation Rules

- `MaxHeaderLineLength` limit applies to trailer lines (same as headers)
- CRLF injection validation on trailer values (same `ValidateHeaderValue` as headers)
- Malformed trailer lines (no colon, empty name) are silently skipped (defensive parsing)
- Prohibited trailers are silently discarded (comprehensive set from Step 1 covering framing, routing, authentication, conditionals, and content metadata per RFC 9110 Section 6.6.2)
- The `Trailer` response header declaring which trailers will be sent is informational per RFC 9110 Section 6.6.2 — the parser does not use it for validation, it parses whatever trailers arrive

---

## Step 3: Trailer Storage in `Http11ResponseBodySource`

**File:** Post-22a `Http11ResponseBodySource` implementation (modified)

### Async Trailer Availability

For streaming responses, trailers are not available until the body is fully consumed.

**Chunked responses** use a `TaskCompletionSource<HttpHeaders>` for async trailer availability:

```csharp
// Only allocated for chunked responses — Content-Length responses skip this
private TaskCompletionSource<HttpHeaders> _trailersTcs;

public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
{
    ThrowIfClosed();

    // Fast path: non-chunked framing — no trailers possible
    if (_trailersTcs == null)
        return new ValueTask<HttpHeaders>(HttpHeaders.Empty);

    // If body not yet consumed, drain first (preserves existing behavior)
    if (Volatile.Read(ref _terminalState) == 0)
        return DrainThenGetTrailersAsync(ct);

    // Body already consumed, TCS should be completed
    return new ValueTask<HttpHeaders>(_trailersTcs.Task);
}

private async ValueTask<HttpHeaders> DrainThenGetTrailersAsync(CancellationToken ct)
{
    await DrainAsync(ct).ConfigureAwait(false);
    return await _trailersTcs.Task.ConfigureAwait(false);
}
```

**Optimization:** The `TaskCompletionSource<HttpHeaders>` is only allocated for chunked responses. Content-Length and read-to-end responses return `HttpHeaders.Empty` synchronously via the `ValueTask` fast path, avoiding a TCS allocation on every HTTP/1.1 response.

The TCS is completed:
- **After successful trailer parsing** (Step 2): `_trailersTcs.TrySetResult(trailers)` — trailers may be empty `HttpHeaders` if no trailer fields were present
- **After body source disposal before EOF**: `_trailersTcs.TrySetException(new ObjectDisposedException(...))` — matches `Http2ResponseBodySource` behavior in `CleanupAsync`. Returning `HttpHeaders.Empty` on abort would silently hide connection errors.
- **After body source fault**: `_trailersTcs.TrySetException(faultException)`

**Drain fallback:** When `GetTrailersAsync` is called before the body is consumed, it drains first (current behavior), then the TCS completes during drain at EOF. This preserves backward compatibility with callers that relied on `GetTrailersAsync` triggering a drain.

### Integration with `ReadAsync`

When `ReadAsync` returns 0 (EOF) on a chunked response:
1. Parse trailers from the stream (Step 2 logic)
2. Complete the trailer TCS with the parsed trailers
3. Return 0 to the caller

When `ReadAsync` returns 0 on a Content-Length or read-to-end response:
1. Complete the trailer TCS with `HttpHeaders.Empty`
2. Return 0

---

## Step 4: `UHttpResponse.Trailers` Property

**File:** `Runtime/Core/UHttpResponse.cs` (modified)

Add a `Trailers` property to `UHttpResponse` for buffered responses:

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

The `BufferedResponseCollectorHandler` (or equivalent) calls `GetTrailersAsync` after fully draining the body, passing the result to the `UHttpResponse` constructor.

**Backward compatibility:** The new `Trailers` constructor parameter must be added with a default value (`HttpHeaders trailers = null`) to avoid breaking existing constructor call sites (including user code that constructs `UHttpResponse` for `MockTransport` scenarios). Internally, `null` is normalized to `HttpHeaders.Empty` so the property is never null.

---

## Step 5: Decompression and Cache Trailer Delegation

**File:** `Runtime/Middleware/DecompressionHandler.cs` (modified)
**File:** `Runtime/Cache/CacheStoringHandler.cs` — `TeeBodySource` (modified)

Both wrapper body sources must delegate `GetTrailersAsync` to the underlying body source:

```csharp
// In DecompressionBodySource:
public ValueTask<HttpHeaders> GetTrailersAsync()
{
    return _innerBodySource.GetTrailersAsync();
}

// In TeeBodySource:
public ValueTask<HttpHeaders> GetTrailersAsync()
{
    return _innerBodySource.GetTrailersAsync();
}
```

Trailers are at the HTTP framing level, not the content-coding level. They are available after the compressed body is fully consumed. In practice, decompression EOF follows compressed EOF, so the timing is identical.

Cache trailer persistence is deferred — trailers are lost on cache replay (acceptable for 22b).

### Wrapper Audit Gate

Before completing 22b.3, verify that **all** `IResponseBodySource` wrappers in the codebase correctly delegate `GetTrailersAsync`. Known wrappers to verify:

- `DecompressionBodySource` (Middleware) — needs delegation (addressed above)
- `TeeBodySource` (Cache) — needs delegation (addressed above)
- `ObservedResponseBodySource` (Observability) — already delegates to `_inner.GetTrailersAsync()` (verified, no change needed)
- Any future wrappers added before 22b.3 implementation

This is a completion gate, not a code change — grep for `IResponseBodySource` implementations and confirm all delegate `GetTrailersAsync`.

---

## Step 6: Tests

**File:** `Tests/Runtime/Transport/Http1/Http11ResponseTrailerTests.cs` (new)
**File:** `Tests/Runtime/Integration/` (modified, add trailer integration tests)

### Unit Tests

1. **Chunked response with trailers** — verify `Content-MD5`, `Digest`, and custom trailer fields are parsed correctly
2. **Chunked response without trailers** — empty trailer section `0\r\n\r\n`, `GetTrailersAsync` returns empty `HttpHeaders`
3. **Prohibited trailer filtering** — verify representative headers from each RFC 9110 Section 6.6.2 category (framing, authentication, conditionals, content metadata) are silently discarded
4. **Malformed trailer lines** — no colon, whitespace-only, empty line → silently skipped
5. **Streaming `GetTrailersAsync` awaits until EOF** — call `GetTrailersAsync` before body is consumed, verify it completes only after `ReadAsync` returns 0
6. **`GetTrailersAsync` after body source fault** — verify fault exception is thrown
7. **Trailer parsing with CRLF injection attempt** — verify rejection
8. **`MaxHeaderLineLength` enforcement** — overlong trailer line is handled correctly
9. **Content-Length response** — `GetTrailersAsync` returns `HttpHeaders.Empty` (no trailer section possible)
10. **HTTP/2 trailer behavior unchanged** — regression test confirming existing HTTP/2 trailers still work

### Integration Tests

11. **Buffered response with trailers** — verify `UHttpResponse.Trailers` contains parsed trailers via `SendBufferedAsync`
12. **Decompression + trailers** — compressed chunked response with trailers, verify trailers pass through `DecompressionBodySource`
13. **Dispose before EOF** — dispose streaming response before consuming body, verify `GetTrailersAsync` completes with `HttpHeaders.Empty`

---

## Files Impacted (Summary)

| File | Change |
|------|--------|
| `Runtime/Transport/Internal/TrailerFieldValidator.cs` | New shared class with prohibited trailer sets for response and request trailers |
| `Runtime/Transport/Http1/Http11ResponseParser.cs` | Replace trailer discard loop with actual parsing, use `TrailerFieldValidator.IsProhibitedResponseTrailer` |
| Http11ResponseBodySource (from 22a) | Store and expose parsed trailers via `GetTrailersAsync`; `TaskCompletionSource<HttpHeaders>` for async completion |
| `Runtime/Core/UHttpResponse.cs` | Add `Trailers` property |
| `Runtime/Core/Pipeline/BufferedResponseCollectorHandler.cs` (from 22a) | Fetch trailers after body drain, pass to `UHttpResponse` constructor |
| `Runtime/Middleware/DecompressionHandler.cs` | Delegate `GetTrailersAsync` to underlying body source |
| `Runtime/Cache/CacheStoringHandler.cs` — `TeeBodySource` | Delegate `GetTrailersAsync` to underlying body source |
| `Tests/Runtime/Transport/Http1/Http11ResponseTrailerTests.cs` | New test file |

## Completion Criteria

- [ ] Chunked response trailers are parsed into `HttpHeaders` instead of being discarded
- [ ] Prohibited trailers are silently discarded (comprehensive set per RFC 9110 Section 6.6.2: framing, routing, authentication, conditionals, content metadata)
- [ ] Prohibited trailer sets live in shared `TrailerFieldValidator` class (reused by 22b.4)
- [ ] `GetTrailersAsync` on streaming HTTP/1.1 chunked response returns parsed trailers after body EOF
- [ ] `GetTrailersAsync` on streaming HTTP/1.1 Content-Length response returns `HttpHeaders.Empty`
- [ ] `GetTrailersAsync` on buffered response returns trailers immediately (body already consumed)
- [ ] `UHttpResponse.Trailers` property contains parsed trailers for buffered responses
- [ ] Trailer parsing respects `MaxHeaderLineLength` limit
- [ ] CRLF injection validation on trailer values
- [ ] Malformed trailer lines are silently skipped
- [ ] `DecompressionBodySource` delegates `GetTrailersAsync` to underlying source
- [ ] `TeeBodySource` delegates `GetTrailersAsync` to underlying source
- [ ] Body source fault propagates to `GetTrailersAsync`
- [ ] Disposing body source before EOF faults `GetTrailersAsync` with `ObjectDisposedException` (matches HTTP/2 behavior)
- [ ] `TaskCompletionSource<HttpHeaders>` only allocated for chunked responses (not Content-Length)
- [ ] `UHttpResponse.Trailers` constructor parameter has default value for backward compatibility
- [ ] All `IResponseBodySource` wrappers verified to delegate `GetTrailersAsync` (wrapper audit gate)
- [ ] Trailer line length uses `MaxHeaderLineLength` (same constant as header parsing, not `MaxChunkLineLength`)
- [ ] All unit and integration tests pass (13 tests)
- [ ] HTTP/2 trailer behavior unchanged (regression test)

## Memory and Performance Notes

- Trailers are typically 1–3 fields. The `HttpHeaders` allocation is negligible. For the common case (no trailers), use `HttpHeaders.Empty` instead of allocating a new empty instance.
- The `TaskCompletionSource<HttpHeaders>` is only allocated for chunked responses. Content-Length and read-to-end responses return `HttpHeaders.Empty` synchronously via `ValueTask` — no TCS allocation. This is an intentional optimization to avoid adding per-response allocations for the majority of HTTP/1.1 responses.
- The `HashSet<string>` for prohibited trailer names is a static readonly allocation — no per-request cost.
- Trailer line length limit uses `MaxHeaderLineLength` (from `Http11ResponseParser`) — the same constant used for header parsing. This is distinct from `MaxChunkLineLength` (256) used for chunk size lines.
