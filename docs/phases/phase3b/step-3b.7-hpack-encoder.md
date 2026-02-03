# Step 3B.7: HPACK Header Encoder

**File:** `Runtime/Transport/Http2/HpackEncoder.cs`
**Depends on:** Steps 3B.3 (Static Table), 3B.4 (Huffman), 3B.5 (Integer Codec), 3B.6 (Dynamic Table)
**Spec:** RFC 7541 Sections 6.1, 6.2, 6.3

## Purpose

Encode a list of HTTP header name-value pairs into HPACK binary format for use in HTTP/2 HEADERS frames. The encoder uses the static table, dynamic table, and Huffman coding to compress headers efficiently.

## Class Design

```csharp
namespace TurboHTTP.Transport.Http2
{
    internal class HpackEncoder
    {
        private readonly HpackDynamicTable _dynamicTable;

        public HpackEncoder(int maxDynamicTableSize = 4096);

        /// <summary>
        /// Encode a list of headers into HPACK binary format.
        /// Headers should include pseudo-headers (e.g., :method, :path) first.
        /// </summary>
        public byte[] Encode(IReadOnlyList<(string Name, string Value)> headers);

        /// <summary>
        /// Emit a dynamic table size update instruction.
        /// Called when SETTINGS_HEADER_TABLE_SIZE changes.
        /// </summary>
        public void SetMaxDynamicTableSize(int newSize);
    }
}
```

## Encoding Strategy

For each header in the list:

### 1. Check for Indexed Header Field (RFC 7541 Section 6.1)

Search static + dynamic table for a full match (both name and value).

```
     0   1   2   3   4   5   6   7
   +---+---+---+---+---+---+---+---+
   | 1 |        Index (7+)         |
   +---+---------------------------+
```

If `FullMatch` found: emit single-byte (or multi-byte for large indices) indexed field with 7-bit prefix and high bit set (`0x80`).

### 2. Check for Literal with Incremental Indexing (RFC 7541 Section 6.2.1)

If name-only match found in static/dynamic table:

```
     0   1   2   3   4   5   6   7
   +---+---+---+---+---+---+---+---+
   | 0 | 1 |      Index (6+)       |
   +---+---+-----------------------+
   |       Value String (*)        |
   +-------------------------------+
```

Emit with 6-bit prefix and `0x40` pattern. Encode value as string literal. Add (name, value) to dynamic table.

### 3. Literal with New Name (RFC 7541 Section 6.2.1)

If no match at all:

```
     0   1   2   3   4   5   6   7
   +---+---+---+---+---+---+---+---+
   | 0 | 1 |           0           |
   +---+---+-----------------------+
   |       Name String (*)         |
   +-------------------------------+
   |       Value String (*)        |
   +-------------------------------+
```

Emit `0x40` byte, encode name as string literal, encode value as string literal. Add to dynamic table.

### 4. Sensitive Headers: Literal Never Indexed (RFC 7541 Section 6.2.3)

For headers that should never be stored in dynamic tables or compressed by intermediaries:

```
     0   1   2   3   4   5   6   7
   +---+---+---+---+---+---+---+---+
   | 0 | 0 | 0 | 1 |  Index (4+)   |
   +---+---+---+---+---------------+
   |       Value String (*)        |
   +-------------------------------+
```

Use 4-bit prefix with `0x10` pattern. Do NOT add to dynamic table.

**Sensitive header names** (use never-indexed):
- `authorization`
- `cookie`
- `set-cookie`
- `proxy-authorization`

### 5. Dynamic Table Size Update (RFC 7541 Section 6.3)

```
     0   1   2   3   4   5   6   7
   +---+---+---+---+---+---+---+---+
   | 0 | 0 | 1 |   Max Size (5+)   |
   +---+---+---+-------------------+
```

Emitted at the beginning of the next header block after a SETTINGS_HEADER_TABLE_SIZE change.

## String Literal Encoding (RFC 7541 Section 5.2)

For each string (name or value):

```csharp
private void EncodeString(string s, List<byte> output)
{
    byte[] raw = Encoding.ASCII.GetBytes(s);
    int huffmanLength = HpackHuffman.GetEncodedLength(raw, 0, raw.Length);

    if (huffmanLength < raw.Length)
    {
        // Huffman is shorter — use it
        byte[] huffmanEncoded = HpackHuffman.Encode(raw, 0, raw.Length);
        // H bit = 1 (Huffman), 7-bit prefix for length
        HpackIntegerCodec.Encode(huffmanEncoded.Length, 7, 0x80, output);
        output.AddRange(huffmanEncoded);
    }
    else
    {
        // Raw is shorter or equal — use raw
        // H bit = 0 (no Huffman), 7-bit prefix for length
        HpackIntegerCodec.Encode(raw.Length, 7, 0x00, output);
        output.AddRange(raw);
    }
}
```

The H bit (highest bit of the length byte) indicates Huffman encoding:
- `1xxxxxxx length`: Huffman-encoded string follows
- `0xxxxxxx length`: Raw ASCII string follows

## Full Encode Method

```csharp
public byte[] Encode(IReadOnlyList<(string Name, string Value)> headers)
{
    var output = new List<byte>();

    // If a table size update is pending, emit it first
    if (_pendingSizeUpdate)
    {
        HpackIntegerCodec.Encode(_dynamicTable.MaxSize, 5, 0x20, output);
        _pendingSizeUpdate = false;
    }

    foreach (var (name, value) in headers)
    {
        if (IsSensitiveHeader(name))
        {
            EncodeLiteralNeverIndexed(name, value, output);
            continue;
        }

        var (index, match) = _dynamicTable.FindMatch(name, value);

        switch (match)
        {
            case HpackMatchType.FullMatch:
                // Indexed header field
                HpackIntegerCodec.Encode(index, 7, 0x80, output);
                break;

            case HpackMatchType.NameMatch:
                // Literal with incremental indexing, indexed name
                HpackIntegerCodec.Encode(index, 6, 0x40, output);
                EncodeString(value, output);
                _dynamicTable.Add(name, value);
                break;

            case HpackMatchType.None:
                // Literal with incremental indexing, new name
                output.Add(0x40);
                EncodeString(name, output);
                EncodeString(value, output);
                _dynamicTable.Add(name, value);
                break;
        }
    }

    return output.ToArray();
}
```

## Caller Responsibilities

The encoder does NOT generate pseudo-headers. `Http2Connection.SendRequestAsync` builds the full header list:

```csharp
var headers = new List<(string, string)>();
headers.Add((":method", request.Method.ToUpperString()));
headers.Add((":scheme", request.Uri.Scheme.ToLowerInvariant()));
headers.Add((":authority", BuildAuthorityValue(request.Uri)));
headers.Add((":path", request.Uri.PathAndQuery ?? "/"));
// Regular headers (lowercased, HTTP/2 forbidden headers stripped)
foreach (var name in request.Headers.Names) { ... }
```

## Thread Safety

NOT thread-safe. The encoder (and its dynamic table) are accessed only under the `Http2Connection._writeLock` semaphore.

## Validation Criteria

- [ ] Indexed header field: `:method GET` → single byte `0x82` (index 2)
- [ ] Indexed header field: `:path /` → single byte `0x84` (index 4)
- [ ] Literal with indexed name: `:authority example.com` → `0x41` + string literal
- [ ] Literal with new name: `custom-key: custom-value` → `0x40` + two string literals
- [ ] Sensitive headers use never-indexed representation (`0x10` prefix)
- [ ] Huffman encoding used when shorter than raw
- [ ] Dynamic table populated after incremental indexing
- [ ] RFC 7541 Appendix C.3 test vectors (requests without Huffman)
- [ ] RFC 7541 Appendix C.4 test vectors (requests with Huffman)
- [ ] Dynamic table size update emitted when `SetMaxDynamicTableSize` called
- [ ] Round-trip with decoder: `Decode(Encode(headers)) == headers`
