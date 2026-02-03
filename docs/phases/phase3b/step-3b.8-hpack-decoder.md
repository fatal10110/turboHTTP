# Step 3B.8: HPACK Header Decoder

**File:** `Runtime/Transport/Http2/HpackDecoder.cs`
**Depends on:** Steps 3B.3 (Static Table), 3B.4 (Huffman), 3B.5 (Integer Codec), 3B.6 (Dynamic Table)
**Spec:** RFC 7541 Sections 6.1, 6.2, 6.3

## Purpose

Decode HPACK binary data (from HEADERS/CONTINUATION frame payloads) back into a list of header name-value pairs. The decoder maintains its own dynamic table, independent from the encoder's table.

## Class Design

```csharp
namespace TurboHTTP.Transport.Http2
{
    internal class HpackDecoder
    {
        private readonly HpackDynamicTable _dynamicTable;

        public HpackDecoder(int maxDynamicTableSize = 4096);

        /// <summary>
        /// Decode HPACK binary data into a list of headers.
        /// Returns pseudo-headers mixed with regular headers — the caller
        /// separates :status from the rest.
        /// </summary>
        public List<(string Name, string Value)> Decode(byte[] data, int offset, int length);

        /// <summary>
        /// Update the decoder's max dynamic table size.
        /// Called when we send SETTINGS_HEADER_TABLE_SIZE to the server.
        /// </summary>
        public void SetMaxDynamicTableSize(int newSize);
    }
}
```

## Decoding Algorithm

Read the header block byte-by-byte. The first byte of each header field representation determines its type:

```csharp
public List<(string Name, string Value)> Decode(byte[] data, int offset, int length)
{
    var headers = new List<(string, string)>();
    int end = offset + length;

    while (offset < end)
    {
        byte b = data[offset];

        if ((b & 0x80) != 0)
        {
            // 1xxxxxxx — Indexed Header Field (Section 6.1)
            DecodeIndexedHeaderField(data, ref offset, headers);
        }
        else if ((b & 0xC0) == 0x40)
        {
            // 01xxxxxx — Literal with Incremental Indexing (Section 6.2.1)
            DecodeLiteralIncrementalIndexing(data, ref offset, headers);
        }
        else if ((b & 0xF0) == 0x00)
        {
            // 0000xxxx — Literal without Indexing (Section 6.2.2)
            DecodeLiteralWithoutIndexing(data, ref offset, headers);
        }
        else if ((b & 0xF0) == 0x10)
        {
            // 0001xxxx — Literal Never Indexed (Section 6.2.3)
            DecodeLiteralNeverIndexed(data, ref offset, headers);
        }
        else if ((b & 0xE0) == 0x20)
        {
            // 001xxxxx — Dynamic Table Size Update (Section 6.3)
            DecodeDynamicTableSizeUpdate(data, ref offset);
        }
        else
        {
            throw new HpackDecodingException($"Invalid HPACK representation byte: 0x{b:X2}");
        }
    }

    return headers;
}
```

## Representation Decoding Details

### Indexed Header Field (`1xxxxxxx`)

```csharp
private void DecodeIndexedHeaderField(byte[] data, ref int offset, List<(string, string)> headers)
{
    int index = HpackIntegerCodec.Decode(data, ref offset, 7);
    if (index == 0)
        throw new HpackDecodingException("HPACK index 0 is invalid (COMPRESSION_ERROR)");

    var (name, value) = _dynamicTable.Get(index);
    headers.Add((name, value));
}
```

### Literal with Incremental Indexing (`01xxxxxx`)

```csharp
private void DecodeLiteralIncrementalIndexing(byte[] data, ref int offset, List<(string, string)> headers)
{
    int nameIndex = HpackIntegerCodec.Decode(data, ref offset, 6);
    string name;

    if (nameIndex > 0)
    {
        // Name from table
        name = _dynamicTable.Get(nameIndex).Name;
    }
    else
    {
        // New name as string literal
        name = DecodeString(data, ref offset);
    }

    string value = DecodeString(data, ref offset);

    _dynamicTable.Add(name, value);
    headers.Add((name, value));
}
```

### Literal without Indexing (`0000xxxx`)

Same as incremental indexing but does NOT add to dynamic table. Uses 4-bit prefix.

### Literal Never Indexed (`0001xxxx`)

Same as without indexing. Uses 4-bit prefix. Semantically signals the header is sensitive and MUST NOT be compressed by intermediaries. In our implementation the behavior is identical to "without indexing."

### Dynamic Table Size Update (`001xxxxx`)

```csharp
private void DecodeDynamicTableSizeUpdate(byte[] data, ref int offset)
{
    int newSize = HpackIntegerCodec.Decode(data, ref offset, 5);
    _dynamicTable.SetMaxSize(newSize);
}
```

**Constraint (RFC 7541 Section 4.2):** Dynamic table size updates MUST occur at the beginning of the first header block after a SETTINGS change. They MUST NOT appear after any header field representation has been decoded in the current block. However, for Phase 3 robustness, we accept them anywhere in the block (matching common server behavior).

## String Literal Decoding

```csharp
private string DecodeString(byte[] data, ref int offset)
{
    byte firstByte = data[offset];
    bool isHuffman = (firstByte & 0x80) != 0;
    int stringLength = HpackIntegerCodec.Decode(data, ref offset, 7);

    if (stringLength == 0)
        return "";

    if (offset + stringLength > data.Length)
        throw new HpackDecodingException("String length exceeds available data");

    string result;
    if (isHuffman)
    {
        byte[] decoded = HpackHuffman.Decode(data, offset, stringLength);
        result = Encoding.ASCII.GetString(decoded);
    }
    else
    {
        result = Encoding.ASCII.GetString(data, offset, stringLength);
    }

    offset += stringLength;
    return result;
}
```

## Error Handling

All decoding errors throw `HpackDecodingException` (a new exception type, or use `InvalidOperationException`). HTTP/2 treats HPACK errors as COMPRESSION_ERROR connection errors (RFC 7540 Section 4.3).

| Error Condition | Behavior |
|---|---|
| Index 0 referenced | COMPRESSION_ERROR |
| Index beyond static + dynamic table | COMPRESSION_ERROR |
| Huffman decode error (EOS in input) | COMPRESSION_ERROR |
| String length exceeds data | COMPRESSION_ERROR |
| Invalid representation byte | COMPRESSION_ERROR |

The `Http2Connection` read loop catches `HpackDecodingException` and sends GOAWAY with `COMPRESSION_ERROR`.

## Thread Safety

NOT thread-safe. The decoder (and its dynamic table) are accessed only from the single background read loop thread.

## Validation Criteria

- [ ] RFC 7541 Appendix C.3: Requests without Huffman encoding
  - First request: `:method GET`, `:scheme http`, `:path /`, `:authority www.example.com`
  - Second request: `:method GET`, `:scheme http`, `:path /`, `:authority www.example.com`, `cache-control: no-cache`
  - Third request: `:method GET`, `:scheme https`, `:path /index.html`, `:authority www.example.com`, `custom-key: custom-value`
- [ ] RFC 7541 Appendix C.4: Requests with Huffman encoding (same headers, Huffman-encoded strings)
- [ ] RFC 7541 Appendix C.5: Responses without Huffman encoding
- [ ] RFC 7541 Appendix C.6: Responses with Huffman encoding
- [ ] Dynamic table is populated by incremental indexing
- [ ] Dynamic table is NOT populated by without-indexing or never-indexed
- [ ] Index 0 throws COMPRESSION_ERROR
- [ ] Round-trip with encoder: `Decode(Encode(headers)) == headers`
- [ ] Dynamic table size update changes table capacity
- [ ] Huffman-encoded and raw strings both decoded correctly
