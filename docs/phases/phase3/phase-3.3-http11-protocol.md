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
   - **PathAndQuery guard:** `var path = request.Uri.PathAndQuery; if (string.IsNullOrEmpty(path)) path = "/";` — defensive fallback for URIs like `https://example.com` (should return `"/"` but guard against edge cases)

2. **Host header** (required by HTTP/1.1):
   - If user did not set `Host`: auto-add `Host: hostname` (with `:port` for non-default ports)
   - If user set `Host`: validated via the general header validation below

3. **Header injection validation (ALL headers, not just Host):**
   Per RFC 9110 Section 5.5, field values must not contain CR or LF. Validate all header names and values before serialization to prevent HTTP response splitting / header injection attacks:
   ```csharp
   // TODO Phase 10: Validate header names against full RFC 9110 token grammar (1*tchar).
   // Currently only checks for CRLF, colon, and empty/whitespace (sufficient for security — prevents injection).
   private static void ValidateHeader(string name, string value)
   {
       if (string.IsNullOrWhiteSpace(name))
           throw new ArgumentException("Header name cannot be null or empty");
       if (name.AsSpan().IndexOfAny('\r', '\n', ':') >= 0)
           throw new ArgumentException($"Header name contains invalid characters: {name}");
       if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
           throw new ArgumentException($"Header value for '{name}' contains CRLF characters");
   }
   ```

4. **User headers — multi-value fix:**
   ```csharp
   foreach (var name in request.Headers.Names)
   {
       var values = request.Headers.GetValues(name);
       foreach (var value in values)
       {
           ValidateHeader(name, value);
           sb.Append(name);
           sb.Append(": ");
           sb.Append(value);
           sb.Append("\r\n");
       }
   }
   ```
   This correctly emits one header line per value (critical for Set-Cookie, Via, etc.) and validates each for injection.

5. **Transfer-Encoding / Content-Length mutual exclusion (RFC 9110 §8.6):** If user set ANY `Transfer-Encoding` header (not just `chunked` — includes `gzip`, `deflate`, `identity`, etc.), do NOT auto-add `Content-Length` (a message MUST NOT contain both). Additionally, if the user set ANY `Transfer-Encoding` value and provides a `byte[]` body, throw `ArgumentException("Transfer-Encoding is set but the client does not support transfer-coded request bodies in Phase 3. Remove the Transfer-Encoding header or pre-encode the body.")`. This is a blanket rejection — Phase 3 only supports raw `byte[]` bodies with `Content-Length`.

6. **Auto-add `Content-Length`** for bodies (if not already set by user, and `Transfer-Encoding` is NOT present). **If user manually set `Content-Length`, validate it matches `request.Body.Length`** — throw `ArgumentException` on mismatch to prevent server hangs:
   ```csharp
   if (request.Body != null && request.Body.Length > 0)
   {
       var userCL = request.Headers.Get("Content-Length");
       if (userCL != null)
       {
           if (!long.TryParse(userCL, out var clValue) || clValue != request.Body.Length)
               throw new ArgumentException(
                   $"Content-Length header ({userCL}) does not match body size ({request.Body.Length})");
       }
       else
       {
           sb.Append("Content-Length: ");
           sb.Append(request.Body.Length);
           sb.Append("\r\n");
       }
   }
   ```

7. **Auto-add `User-Agent: TurboHTTP/1.0`** (unless user explicitly set a `User-Agent` header). Many servers and CDNs block or behave differently for requests without a User-Agent.

8. **Auto-add `Connection: keep-alive`** (unless user explicitly set a `Connection` header)

9. **End of headers:** `\r\n`

10. **Write to stream:**
   ```csharp
   // Use Latin-1 (ISO-8859-1), NOT ASCII. ASCII silently replaces non-ASCII bytes with '?'.
   // Most HTTP clients (including .NET HttpClient) use Latin-1 internally for header encoding.
   // For truly non-ASCII filenames, users should use RFC 5987 encoding (filename*=UTF-8''...).
   //
   // IMPORTANT: Encoding.Latin1 static property is .NET 5+ only, NOT .NET Standard 2.1.
   // Use Encoding.GetEncoding(28591) cached in a static field. The numeric codepage 28591
   // avoids string-based lookup issues. If this fails under IL2CPP stripping, fall back
   // to a custom Latin1Encoding class (see below).
   var headerBytes = Latin1.GetBytes(sb.ToString());
   await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct).ConfigureAwait(false);
   ```

   **Latin-1 encoding resolution (shared static field):**

   **IMPORTANT:** Both the serializer and parser MUST use the same `Latin1` static field. Extract to a shared internal static class `EncodingHelper` in `Runtime/Transport/Internal/EncodingHelper.cs` to avoid duplicating the initialization logic and the `Latin1Encoding` fallback class:
   ```csharp
   // File: Runtime/Transport/Internal/EncodingHelper.cs
   namespace TurboHTTP.Transport.Internal
   {
       internal static class EncodingHelper
       {
           internal static readonly Encoding Latin1 = InitLatin1();
           // ... InitLatin1() and Latin1Encoding fallback class here
       }
   }
   ```
   Both `Http11RequestSerializer` and `Http11ResponseParser` reference `EncodingHelper.Latin1`.

   ```csharp
   // Try Encoding.GetEncoding(28591) first. If IL2CPP strips codepage data,
   // fall back to a minimal custom implementation.
   private static readonly Encoding Latin1 = InitLatin1();

   private static Encoding InitLatin1()
   {
       try
       {
           return Encoding.GetEncoding(28591);
       }
       catch
       {
           return new Latin1Encoding();
       }
   }

   /// <summary>
   /// Minimal Latin-1 (ISO-8859-1) encoder/decoder. Maps bytes 0-255 directly to chars 0-255.
   /// Used as IL2CPP-safe fallback when Encoding.GetEncoding(28591) is stripped.
   /// Overrides GetString and GetBytes(string) to ensure the parser's byte→string path works.
   /// </summary>
   private sealed class Latin1Encoding : Encoding
   {
       public override int GetByteCount(char[] chars, int index, int count) => count;
       public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
       {
           for (int i = 0; i < charCount; i++)
           {
               char c = chars[charIndex + i];
               bytes[byteIndex + i] = (byte)(c < 256 ? c : (byte)'?');
           }
           return charCount;
       }
       public override int GetCharCount(byte[] bytes, int index, int count) => count;
       public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
       {
           for (int i = 0; i < byteCount; i++)
               chars[charIndex + i] = (char)bytes[byteIndex + i];
           return byteCount;
       }
       public override int GetMaxByteCount(int charCount) => charCount;
       public override int GetMaxCharCount(int byteCount) => byteCount;

       // Required by response parser (byte[] → string for header lines)
       public override string GetString(byte[] bytes, int index, int count)
       {
           var chars = new char[count];
           for (int i = 0; i < count; i++)
               chars[i] = (char)bytes[index + i];
           return new string(chars);
       }

       // Required by request serializer (string → byte[] for header block)
       public override byte[] GetBytes(string s)
       {
           var bytes = new byte[s.Length];
           for (int i = 0; i < s.Length; i++)
           {
               char c = s[i];
               bytes[i] = (byte)(c < 256 ? c : (byte)'?');
           }
           return bytes;
       }
   }
   ```

11. **Write body** (if present):
   ```csharp
   if (request.Body != null && request.Body.Length > 0)
       await stream.WriteAsync(request.Body, 0, request.Body.Length, ct).ConfigureAwait(false);
   ```

12. `await stream.FlushAsync(ct).ConfigureAwait(false);`

**Performance note:** StringBuilder + `Encoding.Latin1.GetBytes` allocates ~600-700 bytes per request. Document as GC hotspot for Phase 10 rewrite with `ArrayPool<byte>`. Combined with parser allocations (byte-by-byte ReadAsync ~29KB), Phase 3 targets ~50KB GC per request (see overview.md GC Target section).

---

## Step 2: `Http11ResponseParser` + `ParsedResponse`

**File:** `Runtime/Transport/Http1/Http11ResponseParser.cs`

### `ParsedResponse` (internal class)

```csharp
internal class ParsedResponse
{
    /// <summary>
    /// HTTP status code. May be cast from a non-standard int (e.g., 451, 425).
    /// Consumers should use integer range checks (>= 200 && < 300) rather than
    /// enum comparisons for robustness. ToString() returns the numeric string
    /// for non-standard codes.
    /// </summary>
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
   const int Max1xxResponses = 10; // Cap to prevent infinite loop from malicious server
   int interim1xxCount = 0;
   do {
       parse status line + headers
       if (statusCode >= 100 && statusCode < 200) {
           interim1xxCount++;
           if (interim1xxCount > Max1xxResponses)
               throw new FormatException("Too many 1xx interim responses");
           // 1xx informational — skip and read next response
           continue;
       }
   } while (statusCode >= 100 && statusCode < 200)
   ```

2. **Status line:** `HTTP/1.1 200 OK`
   - Parse using `IndexOf(' ')`, NOT `Split(' ')` — reason phrase may contain spaces (e.g., "Not Found"), be empty, or be absent entirely per RFC 9112 Section 4:
     ```csharp
     int firstSpace = line.IndexOf(' ');
     if (firstSpace < 0) throw new FormatException("Invalid HTTP status line");
     string httpVersion = line.Substring(0, firstSpace);
     int secondSpace = line.IndexOf(' ', firstSpace + 1);
     string statusStr = secondSpace > 0
         ? line.Substring(firstSpace + 1, secondSpace - firstSpace - 1)
         : line.Substring(firstSpace + 1);
     // reasonPhrase is optional — not stored, only statusCode matters
     ```
   - Validate: valid HTTP version prefix, valid integer status code (100-999)

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
   - If `Transfer-Encoding` is present and **ends with `chunked`** (case-insensitive), use chunked decoding. The body bytes returned may be compressed if `Transfer-Encoding` includes other codings (e.g., `gzip, chunked`). Callers can detect this via the `Transfer-Encoding` header and decompress manually. Decompression support deferred to Phase 5/6.
   - If `Transfer-Encoding` value is `identity` (case-insensitive) → treat as absent (no-op per RFC 9112). Fall through to Content-Length or read-to-end logic.
   - If `Transfer-Encoding` is present but does **NOT** end with `chunked` and is **NOT** `identity`, throw `NotSupportedException("Unsupported Transfer-Encoding: <value>. Only 'chunked' and 'identity' are supported.")`.

5. **Body reading — RFC 9112 Section 6.1 compliance (precedence rules):**

   **Skip body entirely** for:
   - `requestMethod == HttpMethod.HEAD`
   - Status code 204 (No Content)
   - Status code 304 (Not Modified)

   Otherwise, **Transfer-Encoding takes precedence over Content-Length** per RFC 9112 Section 6.1. If both are present, `Content-Length` is ignored:
   - `Transfer-Encoding` ends with `chunked` → `ReadChunkedBodyAsync`
   - No `Transfer-Encoding`, but `Content-Length` header present → parse with `long.TryParse` (Content-Length can exceed `int.MaxValue` per RFC 9110 Section 8.6), validate no conflicting values (if multiple `Content-Length` values differ, throw `FormatException`), check `contentLength > MaxResponseBodySize` before narrowing: `int length = (int)contentLength` (safe because `MaxResponseBodySize` is 100MB which fits in `int`), then `ReadFixedBodyAsync(stream, length, ct)`
   - Neither → `ReadToEndAsync` (read until connection closes)

6. **Keep-alive detection:**
   - Check `Connection` header: if `"close"` → not keep-alive
   - Default: HTTP/1.1 = keep-alive, HTTP/1.0 = close
   - **Critical:** If body was read via `ReadToEndAsync` (no Content-Length, no chunked), **force `KeepAlive = false`** regardless of headers. Reading to EOF means the server closed the connection — the socket is dead and cannot be reused. Without this, a dead connection would be returned to the pool and the next request on it would fail immediately.
     ```csharp
     bool keepAlive = IsKeepAlive(httpVersion, headers);
     if (usedReadToEnd)
         keepAlive = false;  // Connection read to EOF, not reusable
     ```

### Helper Methods

#### `ReadLineAsync(Stream stream, CancellationToken ct, int maxLength = 8192)`

Byte-by-byte CRLF reader with **max length limit** and **explicit Latin-1 encoding**:
- Reads one byte at a time via `stream.ReadAsync(singleByteBuf, 0, 1, ct)` into a `MemoryStream` buffer (NOT directly into StringBuilder, NOT `byte[]` — MemoryStream handles dynamic growth without manual resizing)
- Tracks `lastWasCR` (`bool`) for CRLF detection
- If `buffer.Length > maxLength` → throw `FormatException("HTTP header line exceeds maximum length")`
- **Converts raw bytes to string using the same `Latin1` encoding used by the serializer** (NOT implicit char cast, NOT UTF-8). HTTP/1.1 status lines and headers are Latin-1 encoded (ISO 8859-1). Using UTF-8 would corrupt non-ASCII header values.
  ```csharp
  return Latin1.GetString(buffer, 0, length); // Same static Latin1 field as serializer
  ```
- Returns line content (without CRLF)

**Phase 10 optimization:** Replace with buffered reader (4KB+ chunks from stream).

#### `ReadChunkedBodyAsync(Stream stream, CancellationToken ct)`

```
MaxResponseBodySize = 100 * 1024 * 1024  // 100MB configurable limit
totalBodyBytes = 0

loop:
  read chunk size line (use maxLength: 256 — valid hex chunk sizes are <16 chars; prevents multi-KB junk)
  // Strip chunk extensions per RFC 9112 Section 7.1.1: "1A; ext=value\r\n"
  strip everything after first ';' or space before parsing hex
  parse hex with long.TryParse(..., NumberStyles.HexNumber, ...) — int would overflow for chunks > 2GB
  if parse fails: throw FormatException("Invalid chunk size")
  if size == 0: break (last chunk)
  // Guard: individual chunk size must fit in int for MemoryStream/array operations.
  // MaxResponseBodySize (100MB) already fits in int, so this also serves as a sanity check.
  if size > MaxResponseBodySize:
      throw IOException("Response body exceeds maximum size")
  int chunkSize = (int)size;  // Safe: validated <= MaxResponseBodySize (100MB) which fits in int
  totalBodyBytes += chunkSize
  if totalBodyBytes > MaxResponseBodySize:
      throw IOException("Response body exceeds maximum size")
  read exactly `chunkSize` bytes into MemoryStream
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

Read into `MemoryStream` with 8KB buffer from `ArrayPool<byte>.Shared` until `read == 0`. **Enforce the same `MaxResponseBodySize` limit** as chunked reading — throw `IOException("Response body exceeds maximum size")` if exceeded. Without this limit, a server could send an infinite stream causing unbounded memory growth.

```csharp
var buffer = ArrayPool<byte>.Shared.Rent(8192);
try
{
    // ... read loop with MemoryStream ...
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer);
}
```

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
11. CRLF injection in ANY header name or value throws `ArgumentException`
12. Header names containing `:` throw `ArgumentException`
13. KeepAlive forced false when ReadToEnd is used (no Content-Length, no chunked)
