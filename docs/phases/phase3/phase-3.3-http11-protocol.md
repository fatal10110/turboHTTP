# Phase 3.3: HTTP/1.1 Serializer & Parser (Transport Assembly)

**Depends on:** Phase 2 (complete)
**Assembly:** `TurboHTTP.Transport`
**Files to create:** 2 new

---

## Step 1: `Http11RequestSerializer`

**File:** `Runtime/Transport/Http1/Http11RequestSerializer.cs`

Static class that serializes `UHttpRequest` to HTTP/1.1 wire format.

### `SerializeAsync(UHttpRequest request, Stream stream, CancellationToken ct)`

All awaits use `.ConfigureAwait(false)`.

1. **Request line:** `METHOD /path HTTP/1.1\r\n`
   - `request.Method.ToUpperString()` + space + `request.Uri.PathAndQuery` + ` HTTP/1.1\r\n`

2. **Host header** (required by HTTP/1.1):
   - If user did not set `Host`: auto-add `Host: hostname` (with `:port` for non-default ports)
   - If user set `Host`: **validate for CRLF injection** — throw `ArgumentException` if contains `\r` or `\n`

3. **User headers — multi-value fix:**
   ```csharp
   foreach (var name in request.Headers.Names)
   {
       var values = request.Headers.GetValues(name);
       foreach (var value in values)
       {
           sb.Append(name);
           sb.Append(": ");
           sb.Append(value);
           sb.Append("\r\n");
       }
   }
   ```
   This correctly emits one header line per value (critical for Set-Cookie, Via, etc.)

4. **Auto-add `Content-Length`** for bodies (if not already set by user)

5. **Auto-add `Connection: keep-alive`** (unless user explicitly set a `Connection` header)

6. **End of headers:** `\r\n`

7. **Write to stream:**
   ```csharp
   // Use Latin-1 (ISO-8859-1), NOT ASCII. ASCII silently replaces non-ASCII bytes with '?'.
   // Most HTTP clients (including .NET HttpClient) use Latin-1 internally for header encoding.
   // For truly non-ASCII filenames, users should use RFC 5987 encoding (filename*=UTF-8''...).
   var headerBytes = Encoding.GetEncoding("iso-8859-1").GetBytes(sb.ToString());
   await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct).ConfigureAwait(false);
   ```

8. **Write body** (if present):
   ```csharp
   if (request.Body != null && request.Body.Length > 0)
       await stream.WriteAsync(request.Body, 0, request.Body.Length, ct).ConfigureAwait(false);
   ```

9. `await stream.FlushAsync(ct).ConfigureAwait(false);`

**Performance note:** StringBuilder + `Encoding.GetEncoding("iso-8859-1").GetBytes` allocates ~600-700 bytes per request. Document as GC hotspot for Phase 10 rewrite with `ArrayPool<byte>`. Combined with parser allocations, Phase 3 targets <2KB GC per request (see CLAUDE.md).

---

## Step 2: `Http11ResponseParser` + `ParsedResponse`

**File:** `Runtime/Transport/Http1/Http11ResponseParser.cs`

### `ParsedResponse` (internal class)

```csharp
public class ParsedResponse
{
    public HttpStatusCode StatusCode { get; set; }
    public HttpHeaders Headers { get; set; }
    public byte[] Body { get; set; }
    public bool KeepAlive { get; set; }
}
```

### `ParseAsync(Stream stream, HttpMethod requestMethod, CancellationToken ct)`

Accepts `requestMethod` to correctly handle HEAD responses. All awaits use `.ConfigureAwait(false)`.

1. **Skip 1xx interim responses in a loop** (handles 100-Continue):
   ```
   do {
       parse status line + headers
       if (statusCode >= 100 && statusCode < 200) {
           // 1xx informational — skip and read next response
           continue;
       }
   } while (statusCode >= 100 && statusCode < 200)
   ```

2. **Status line:** `HTTP/1.1 200 OK`
   - Parse: `(httpVersion, statusCode, reasonPhrase)`
   - Validate: at least 2 parts, valid integer status code

3. **Headers:** Read lines until empty line, tracking total header bytes
   ```csharp
   var headers = new HttpHeaders();
   int totalHeaderBytes = 0;
   while (true)
   {
       var line = await ReadLineAsync(stream, ct);
       if (string.IsNullOrEmpty(line)) break;
       totalHeaderBytes += line.Length;
       if (totalHeaderBytes > 102400) // 100KB
           throw new FormatException("Response headers exceed maximum size");
       var colonIndex = line.IndexOf(':');
       if (colonIndex > 0)
       {
           var name = line.Substring(0, colonIndex).Trim();
           var value = line.Substring(colonIndex + 1).Trim();
           headers.Add(name, value);  // ← Add, NOT Set (preserves multi-value headers)
       }
   }
   ```

4. **Transfer-Encoding validation:**
   - If `Transfer-Encoding` is present and does NOT end with `chunked` (case-insensitive), throw `NotSupportedException("Only chunked transfer encoding is supported")`
   - This rejects `gzip, chunked` combinations until decompression is implemented

5. **Body reading — RFC 9110 compliance:**

   **Skip body entirely** for:
   - `requestMethod == HttpMethod.HEAD`
   - Status code 204 (No Content)
   - Status code 304 (Not Modified)

   Otherwise:
   - `Transfer-Encoding: chunked` → `ReadChunkedBodyAsync`
   - `Content-Length` header → `ReadFixedBodyAsync`
   - Neither → `ReadToEndAsync` (read until connection closes)

6. **Keep-alive detection:**
   - Check `Connection` header: if `"close"` → not keep-alive
   - Default: HTTP/1.1 = keep-alive, HTTP/1.0 = close

### Helper Methods

#### `ReadLineAsync(Stream stream, CancellationToken ct, int maxLength = 8192)`

Byte-by-byte CRLF reader with **max length limit**:
- Reads one byte at a time into `StringBuilder`
- Tracks `lastWasCR` for CRLF detection
- If `sb.Length > maxLength` → throw `FormatException("HTTP header line exceeds maximum length")`
- Returns line content (without CRLF)

**Phase 10 optimization:** Replace with buffered reader (4KB+ chunks from stream).

#### `ReadChunkedBodyAsync(Stream stream, CancellationToken ct)`

```
MaxResponseBodySize = 100 * 1024 * 1024  // 100MB configurable limit
totalBodyBytes = 0

loop:
  read chunk size line
  // Strip chunk extensions per RFC 9112 Section 7.1.1: "1A; ext=value\r\n"
  strip everything after first ';' or space before parsing hex
  parse hex with int.TryParse(..., NumberStyles.HexNumber, ...)
  if parse fails: throw FormatException("Invalid chunk size")
  if size == 0: break (last chunk)
  totalBodyBytes += size
  if totalBodyBytes > MaxResponseBodySize:
      throw IOException("Response body exceeds maximum size")
  read exactly `size` bytes into MemoryStream
  read trailing CRLF
end loop
// Read trailers (zero or more header lines until empty line)
// TODO Phase 6: Parse trailer headers and merge into response.Headers
// For now, we just consume them to reach the end of the message
loop:
  read line
  if empty: break
end loop
return ms.ToArray()
```

**Critical:** After the zero-length terminating chunk, read lines in a loop until empty line (not just one line). Servers may send trailer headers.

**Chunk size validation:** Must handle chunk extensions (strip after `;`), validate hex digits, and enforce maximum body size to prevent `OutOfMemoryException` from malicious servers.

#### `ReadFixedBodyAsync(Stream stream, int length, CancellationToken ct)`

Validate `length <= MaxResponseBodySize` before allocating — throw `IOException("Response body exceeds maximum size")` if exceeded. Then read exactly `length` bytes using `ReadExactAsync`.

#### `ReadToEndAsync(Stream stream, CancellationToken ct)`

Read into `MemoryStream` with 8KB buffer until `read == 0`. **Enforce the same `MaxResponseBodySize` limit** as chunked reading — throw `IOException("Response body exceeds maximum size")` if exceeded. Without this limit, a server could send an infinite stream causing unbounded memory growth.

#### `ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)`

Custom helper (not a .NET Standard 2.1 API):
```csharp
static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
{
    int totalRead = 0;
    while (totalRead < count)
    {
        int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, ct)
            .ConfigureAwait(false);
        if (read == 0)
            throw new IOException("Unexpected end of stream");
        totalRead += read;
    }
}
```

---

## Verification

1. Both files compile in `TurboHTTP.Transport` assembly
2. Serializer output matches expected HTTP/1.1 wire format (test with MemoryStream capture)
3. Parser handles: Content-Length bodies, chunked bodies, empty bodies
4. Parser skips body for HEAD/204/304
5. Parser skips 1xx interim responses correctly
6. Transfer-Encoding validation rejects unsupported encodings
7. Total header size limit enforced (100KB)
8. Multi-value headers: serializer emits separate lines, parser preserves via `Add()`
9. ReadLineAsync enforces max line length (8KB)
10. Chunked trailer parsing reads until empty line
11. CRLF injection in Host header throws
