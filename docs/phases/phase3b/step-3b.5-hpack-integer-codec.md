# Step 3B.5: HPACK Integer Encoding/Decoding

**File:** `Runtime/Transport/Http2/HpackIntegerCodec.cs`
**Depends on:** Nothing
**Spec:** RFC 7541 Section 5.1 (Integer Representation)

## Purpose

Encode and decode HPACK prefix-coded integers. HPACK uses a variable-length integer encoding where the first N bits of the first byte serve as a prefix, and if the value doesn't fit, subsequent bytes encode the remainder in 7-bit chunks with a continuation bit.

## Class Design

```csharp
namespace TurboHTTP.Transport.Http2
{
    internal static class HpackIntegerCodec
    {
        /// <summary>
        /// Encode an integer with the given prefix bit width.
        /// The first byte's upper bits (above the prefix) are preserved from prefixByte.
        /// </summary>
        public static void Encode(int value, int prefixBits, byte prefixByte, List<byte> output);

        /// <summary>
        /// Decode an integer with the given prefix bit width.
        /// The offset is advanced past the decoded integer.
        /// Returns the decoded value.
        /// </summary>
        public static int Decode(byte[] data, ref int offset, int prefixBits);
    }
}
```

## Encoding Algorithm (RFC 7541 Section 5.1)

```
if value < 2^N - 1:
    encode value in the lower N bits of the prefix byte
    emit prefix byte
else:
    set lower N bits of prefix byte to all 1s
    emit prefix byte
    value = value - (2^N - 1)
    while value >= 128:
        emit (value % 128) + 128   // 7 value bits + continuation bit
        value = value / 128
    emit value                      // final byte, no continuation bit
```

### `Encode(int value, int prefixBits, byte prefixByte, List<byte> output)`

- `prefixBits`: Number of prefix bits (1–8). Common values: 7 (indexed), 6 (literal incremental), 5 (table size update), 4 (literal without/never indexed).
- `prefixByte`: The first byte with upper bits already set (e.g., `0x80` for indexed header field). The lower `prefixBits` bits will be overwritten.
- `maxPrefix = (1 << prefixBits) - 1`: Maximum value that fits in the prefix.

```csharp
public static void Encode(int value, int prefixBits, byte prefixByte, List<byte> output)
{
    int maxPrefix = (1 << prefixBits) - 1;

    if (value < maxPrefix)
    {
        output.Add((byte)(prefixByte | value));
    }
    else
    {
        output.Add((byte)(prefixByte | maxPrefix));
        value -= maxPrefix;
        while (value >= 128)
        {
            output.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        output.Add((byte)value);
    }
}
```

### `Decode(byte[] data, ref int offset, int prefixBits)`

```csharp
public static int Decode(byte[] data, ref int offset, int prefixBits)
{
    int maxPrefix = (1 << prefixBits) - 1;
    int value = data[offset] & maxPrefix;
    offset++;

    if (value < maxPrefix)
        return value;

    // Multi-byte encoding
    int m = 0;
    byte b;
    do
    {
        if (offset >= data.Length)
            throw new InvalidOperationException("Unexpected end of HPACK integer");
        b = data[offset];
        offset++;
        value += (b & 0x7F) << m;
        m += 7;

        if (m > 28)  // Prevent overflow: 4 continuation bytes max for int32
            throw new InvalidOperationException("HPACK integer overflow");
    }
    while ((b & 0x80) != 0);

    return value;
}
```

## Test Vectors (RFC 7541 Section C.1)

### Example 1: Encode 10 with 5-bit prefix
- 10 < 31 (2^5 - 1), fits in prefix
- Result: `0x0A` (lower 5 bits = 01010)

### Example 2: Encode 1337 with 5-bit prefix
- 1337 >= 31, does not fit
- First byte: prefix bits = 11111 → `0x1F`
- Remainder: 1337 - 31 = 1306
- 1306 % 128 = 26, 1306 / 128 = 10. Emit `26 | 0x80 = 0x9A`
- 10 < 128. Emit `0x0A`
- Result: `0x1F 0x9A 0x0A`

### Example 3: Encode 42 with 8-bit prefix
- 42 < 255 (2^8 - 1), fits in prefix
- Result: `0x2A`

## Usage in HPACK

Different HPACK representations use different prefix widths:

| Representation | Prefix Bits | First Byte Pattern |
|---|---|---|
| Indexed Header Field | 7 | `1xxxxxxx` |
| Literal with Incremental Indexing | 6 | `01xxxxxx` |
| Dynamic Table Size Update | 5 | `001xxxxx` |
| Literal without Indexing | 4 | `0000xxxx` |
| Literal Never Indexed | 4 | `0001xxxx` |

The `prefixByte` parameter carries the upper bits. For example:
- Indexed header: `prefixByte = 0x80`, `prefixBits = 7`
- Literal incremental: `prefixByte = 0x40`, `prefixBits = 6`
- Table size update: `prefixByte = 0x20`, `prefixBits = 5`

## Edge Cases

- **Value 0:** Encoded as a single byte with lower `prefixBits` bits = 0.
- **Maximum prefix value minus one:** Fits in prefix (e.g., 30 with 5-bit prefix = single byte).
- **Maximum prefix value:** Triggers multi-byte encoding (e.g., 31 with 5-bit prefix = `0x1F 0x00`).
- **Overflow protection:** Limit continuation bytes to prevent `int` overflow. 4 continuation bytes encode up to `2^28 - 1 + maxPrefix`, which is more than enough for any HPACK value.

## Validation Criteria

- [ ] RFC 7541 Section C.1 test vectors: encode 10 (5-bit) → `0x0A`, encode 1337 (5-bit) → `0x1F 0x9A 0x0A`, encode 42 (8-bit) → `0x2A`
- [ ] Round-trip: `Decode(Encode(value)) == value` for values 0, 1, 30, 31, 127, 128, 255, 256, 1337, 65535
- [ ] All prefix widths (1–8) work correctly
- [ ] Overflow detection throws on excessively long continuation sequences
- [ ] Decode advances `offset` correctly past all consumed bytes
