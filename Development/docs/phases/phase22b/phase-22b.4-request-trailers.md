# Phase 22b.4: HTTP/1.1 Request Trailer Support

**Depends on:** 22b.3 (shared prohibited-trailer filtering logic)
**Assemblies:** `TurboHTTP.Core`, `TurboHTTP.Transport`
**Files to create:** 0 new, 5 modified

---

## Step 1: Trailer Provider on `UHttpRequestBody`

**File:** `Runtime/Core/UHttpRequestBody.cs` (modified)

Add trailer provider properties to `UHttpRequestBody`:

```csharp
public abstract class UHttpRequestBody : IDisposable
{
    // ... existing from 22a ...

    /// <summary>
    /// Optional provider called after body EOF to produce request trailers.
    /// Null if no trailers are to be sent.
    /// </summary>
    internal Func<HttpHeaders> TrailerProvider { get; }

    /// <summary>
    /// Trailer field names declared upfront for the Trailer header.
    /// Null if no trailers are to be sent.
    /// </summary>
    internal IReadOnlyList<string> DeclaredTrailerNames { get; }
}
```

### Why `Func<HttpHeaders>` (Not Async)

Request trailers are computed from data available at body-EOF time (e.g., hash accumulators, counters). An async provider adds complexity for a case that is extremely rare. Can be added as an overload later without breaking changes.

### Why Provider Pattern (Not Direct `HttpHeaders`)

Trailer values are typically not known at request construction time — they depend on body content (e.g., hash of the streamed body). The provider pattern allows lazy computation.

### API Design Note — User Subclassability

`UHttpRequestBody` is `public abstract`. The `TrailerProvider` and `DeclaredTrailerNames` properties are `internal`, meaning user-created subclasses of `UHttpRequestBody` in external assemblies cannot provide trailers. This is an intentional limitation for 22b: trailers are set via the builder API (`WithRequestTrailers`), not by subclass override. If user-subclassable trailer support is needed later, a `protected virtual` method can be added without breaking changes. Document this limitation in the API surface.

---

## Step 2: Builder API — `WithRequestTrailers`

**File:** Streaming request builder from 22a (modified)

```csharp
/// <summary>
/// Attach request trailers to be sent after the request body.
/// The declared names are used to set the Trailer header on the request.
/// The provider is invoked after body EOF to produce the actual trailer values.
/// </summary>
builder.WithRequestTrailers(IReadOnlyList<string> declaredNames, Func<HttpHeaders> provider)
```

### Behavior

1. Stores `declaredNames` and `provider` on the resulting `UHttpRequestBody`
2. At request build time, sets the `Trailer` header on request headers listing the declared trailer field names:
   ```
   Trailer: Digest, Content-MD5
   ```

### Example Usage

```csharp
var sha256 = SHA256.Create();
builder
    .WithStreamBody(new CryptoStream(fileStream, sha256, CryptoStreamMode.Read))
    .WithRequestTrailers(new[] { "Digest" }, () =>
    {
        var headers = new HttpHeaders();
        headers.Set("Digest", "sha-256=" + Convert.ToBase64String(sha256.Hash));
        return headers;
    });
```

**Usage pitfall:** The trailer provider is invoked after body EOF. `SHA256.Hash` is only available after the `CryptoStream` has been fully read and `FlushFinalBlock()` has been called. The `RequestBodyReadSession` reads the stream to EOF and then disposes it, which calls `FlushFinalBlock()` on the `CryptoStream`. However, if the stream is disposed *before* `FlushFinalBlock()` (e.g., on early abort), `sha256.Hash` will throw. Document this timing dependency in the API documentation and recommend wrapping with a `try`/`catch` in the provider lambda for robustness.

---

## Step 3: Use Shared Prohibited Trailer Set

**File:** `Runtime/Transport/Internal/TrailerFieldValidator.cs` (from 22b.3)

The request trailer prohibited set is `TrailerFieldValidator.ProhibitedRequestTrailers`, established in 22b.3 Step 1. It is initialized as a copy of `ProhibitedResponseTrailers` and already includes `Content-Type`, `Content-Encoding`, `Authorization`, and all other RFC 9110 Section 6.6.2 categories.

The serializer calls `TrailerFieldValidator.IsProhibitedRequestTrailer(name)` — no local `HashSet` needed in `Http11RequestSerializer`.

If request-specific additions beyond the shared set are ever needed, they can be added to `ProhibitedRequestTrailers` in the `TrailerFieldValidator` static constructor.

---

## Step 4: HTTP/1.1 Chunked Trailer Serialization

**File:** `Runtime/Transport/Http1/Http11RequestSerializer.cs` (modified) — `SerializeBodyAsync`

### Chunked-Only Constraint Validation

Before body serialization, check for the invalid combination:

```csharp
// HTTP/1.1 with known-length body + trailer provider → throw
if (body.TrailerProvider != null && body.Length.HasValue)
{
    throw new InvalidOperationException(
        "Request trailers require chunked transfer encoding. " +
        "Use an unknown-length body or remove the trailer provider.");
}
```

**Note:** HTTP/2 always supports trailing HEADERS regardless of body length, so this check only applies to HTTP/1.1. Since the protocol is not known at build time, validation happens at serialization time.

### Terminal Chunk + Trailers

Replace the current `0\r\n\r\n` terminal chunk with trailer serialization using the existing `PooledHeaderWriter` pattern (same as header serialization — no string interpolation, no ad-hoc `WriteAsync(stream, string)` calls):

```csharp
// After body EOF:
// 1. Write terminal chunk size via the PooledHeaderWriter
writer.Append("0\r\n");

// 2. If trailer provider is set, invoke and serialize using PooledHeaderWriter
if (body.TrailerProvider != null)
{
    var trailers = body.TrailerProvider();
    if (trailers != null && trailers.Count > 0)
    {
        foreach (var name in trailers.Names)
        {
            if (TrailerFieldValidator.IsProhibitedRequestTrailer(name))
                continue;

            foreach (var value in trailers.GetValues(name))
            {
                ValidateHeaderValue(name, value); // CRLF injection protection
                writer.Append(name);
                writer.Append(": ");
                writer.Append(value);
                writer.Append("\r\n");
            }
        }
    }
}

// 3. Write empty line (trailer section terminator)
writer.Append("\r\n");

// 4. Flush the writer to the stream
await writer.FlushAsync(stream, ct).ConfigureAwait(false);
```

**Important:** The trailer serialization MUST use the same `PooledHeaderWriter` + `EncodingHelper.GetLatin1Bytes` path already established in `SerializeAsync` for header serialization. Do NOT use string interpolation (`$"{name}: {value}\r\n"`) or a non-existent `WriteAsync(Stream, string, CancellationToken)` overload — neither exists on `Stream` and string interpolation allocates per trailer field.

### Edge Cases

- **Provider returns `null`:** Same behavior as no provider — empty trailer section (`0\r\n\r\n`)
- **Provider returns empty `HttpHeaders`:** Same behavior as no provider
- **Provider returns prohibited trailers only:** All filtered, result is empty trailer section

---

## Step 5: HTTP/2 Request Trailers

**File:** `Runtime/Transport/Http2/Http2Connection.cs` (modified) — `SendRequestAsync`

### Change

After the last DATA frame, if a trailer provider is set:

```csharp
if (body.TrailerProvider != null)
{
    // Invoke provider and filter before deciding END_STREAM placement
    var trailers = body.TrailerProvider();
    var filteredTrailers = FilterProhibitedRequestTrailers(trailers);

    if (filteredTrailers != null && filteredTrailers.Count > 0)
    {
        // Has actual trailers: last DATA frame does NOT have END_STREAM
        await SendDataFrameAsync(streamId, lastChunk, endStream: false, ct);
        // HPACK-encode, send trailing HEADERS with END_STREAM
        await SendTrailingHeadersAsync(streamId, filteredTrailers, ct);
    }
    else
    {
        // Empty/null trailers after filtering — set END_STREAM on the last DATA frame
        // (same as no-provider path, avoids sending an unnecessary extra frame)
        await SendDataFrameAsync(streamId, lastChunk, endStream: true, ct);
    }
}
else
{
    // Last DATA frame has END_STREAM (current behavior)
    await SendDataFrameAsync(streamId, lastChunk, endStream: true, ct);
}
```

### `SendTrailingHeadersAsync`

New internal method:

1. Filter prohibited request trailers (same set as Step 3)
2. HPACK-encode the trailer headers using the existing encoder
3. Send a HEADERS frame with END_STREAM flag

The HPACK encoder is already available on the connection. Trailing HEADERS use the same encoding as initial HEADERS.

### HTTP/2 + Known-Length Body + Trailers

Unlike HTTP/1.1, this combination is valid in HTTP/2. No `InvalidOperationException` — HTTP/2 always supports trailing HEADERS regardless of body length.

---

## Step 6: Tests

**File:** `Tests/Runtime/Transport/Http1/Http11RequestTrailerTests.cs` (new)
**File:** `Tests/Runtime/Transport/Http2/Http2RequestTrailerTests.cs` (new)

### Unit Tests

1. **HTTP/1.1 chunked request with trailers** — wire format validation (`0\r\nDigest: sha-256=...\r\n\r\n`)
2. **HTTP/1.1 Content-Length + trailer provider** → `InvalidOperationException`
3. **HTTP/2 request with trailing HEADERS frame** — verify END_STREAM on HEADERS, not on last DATA
4. **HTTP/2 known-length body + trailer provider** → valid (no exception)
5. **Prohibited trailer filtering** — verify representative headers from each RFC 9110 Section 6.6.2 category are filtered (uses shared `TrailerFieldValidator.ProhibitedRequestTrailers`)
6. **CRLF injection rejection in request trailers** — verify `ValidateHeaderValue` is called
7. **Empty/null trailer provider** — verify no trailers serialized, normal terminal chunk
8. **`Trailer` header declaration** — verify `Trailer` request header matches declared names
9. **Provider returns extra headers not in declaration** — extra non-prohibited trailers are sent (RFC 9110 `Trailer` header is advisory, not a filter). The `Trailer` declaration is for the receiver's benefit, not a send-side constraint.
10. **Provider returns fewer headers than declared** — allowed per RFC 9110 Section 6.6.2
11. **Trailer provider throws exception** — verify exception propagates cleanly, connection closed (not returned to pool)

### Integration Tests

12. **Round-trip with request trailers** — mock server validates receipt of trailer fields
13. **Retry with request trailers** — replayable body + trailer provider, verify trailers re-sent on retry

---

## Files Impacted (Summary)

| File | Change |
|------|--------|
| `Runtime/Core/UHttpRequestBody.cs` | Add `TrailerProvider` and `DeclaredTrailerNames` properties |
| Streaming request builder (from 22a) | `WithRequestTrailers(IReadOnlyList<string>, Func<HttpHeaders>)` method |
| `Runtime/Transport/Http1/Http11RequestSerializer.cs` | `SerializeBodyAsync`: serialize trailers after terminal chunk using `PooledHeaderWriter`; validate chunked-only constraint; use `TrailerFieldValidator.IsProhibitedRequestTrailer` |
| `Runtime/Transport/Http2/Http2Connection.cs` | `SendRequestAsync`: send trailing HEADERS frame after DATA frames when provider is set; invoke provider before deciding END_STREAM placement |
| `Runtime/Transport/Internal/TrailerFieldValidator.cs` | From 22b.3 — shared prohibited trailer sets, no changes needed |
| `Tests/Runtime/Transport/Http1/Http11RequestTrailerTests.cs` | New test file |
| `Tests/Runtime/Transport/Http2/Http2RequestTrailerTests.cs` | New test file |

## Completion Criteria

- [ ] `WithRequestTrailers(declaredNames, provider)` builder method available
- [ ] `Trailer` header automatically set on request headers from declared names
- [ ] HTTP/1.1 chunked: terminal chunk followed by serialized trailer fields and empty line
- [ ] HTTP/1.1 Content-Length + trailer provider → `InvalidOperationException` at serialization time
- [ ] HTTP/2: trailing HEADERS frame with END_STREAM sent after DATA frames when provider is set
- [ ] HTTP/2: last DATA frame does NOT have END_STREAM when trailer provider is set
- [ ] Prohibited request trailers are silently filtered (shared `TrailerFieldValidator.ProhibitedRequestTrailers` set)
- [ ] CRLF injection validation on trailer values
- [ ] Trailer provider returning empty `HttpHeaders` → same behavior as no provider
- [ ] Trailer provider returning `null` → same behavior as no provider
- [ ] Trailer serialization uses `PooledHeaderWriter` pattern (no string interpolation, no ad-hoc `WriteAsync(stream, string)`)
- [ ] HTTP/2 empty-trailer path sets END_STREAM on last DATA frame (no unnecessary empty DATA frame)
- [ ] Provider returning extra undeclared trailers: sent if not prohibited (RFC `Trailer` header is advisory)
- [ ] Trailer provider exception propagates cleanly, connection not returned to pool
- [ ] All unit and integration tests pass (13 tests)
- [ ] `Trailer` header declaration matches declared names from builder API

## Security Notes

- Request trailer values are validated with the same CRLF injection check used for headers. Prevents HTTP response splitting.
- `Authorization` is prohibited as a trailer because servers process authentication before reading the body. Allowing it as a trailer could bypass auth checks.
