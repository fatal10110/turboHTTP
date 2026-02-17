# Step 3B.4: HPACK Huffman Encoding/Decoding

**File:** `Runtime/Transport/Http2/HpackHuffman.cs`
**Depends on:** Nothing
**Spec:** RFC 7541 Section 5.2 (String Literal Representation), Appendix B (Huffman Code)

## Purpose

Implement Huffman encoding and decoding for HPACK string literals. Huffman coding reduces header sizes by ~30% for typical HTTP headers by assigning shorter bit sequences to more common bytes.

## Class Design

```csharp
namespace TurboHTTP.Transport.Http2
{
    internal static class HpackHuffman
    {
        public static byte[] Encode(byte[] data);
        public static byte[] Encode(byte[] data, int offset, int length);
        public static int GetEncodedLength(byte[] data, int offset, int length);
        public static byte[] Decode(byte[] data, int offset, int length);
    }
}
```

## Huffman Code Table (RFC 7541 Appendix B)

256 entries mapping byte values to variable-length bit sequences (5–30 bits). Plus the EOS (End-of-String) symbol (index 256) with a 30-bit code.

Store as `static readonly (uint Code, byte BitLength)[]`:

```csharp
private static readonly (uint Code, byte BitLength)[] s_encodeTable = new (uint, byte)[]
{
    (0x1ff8,    13), // ( 0) '\0'
    (0x7fffd8,  23), // ( 1)
    (0xfffffe2, 28), // ( 2)
    (0xfffffe3, 28), // ( 3)
    // ... all 256 entries from RFC 7541 Appendix B ...
    (0x7fffd9,  23), // (253)
    (0x7fffda,  23), // (254)
    (0x7fffdb,  23), // (255)
};

// EOS symbol (index 256): 0x3fffffff, 30 bits
// EOS is never encoded, but its presence in decode input is an error.
```

**Critical entries for common HTTP characters:**
| Byte | Char | Code | Bits |
|------|------|------|------|
| 48   | '0'  | 0x0  | 5    |
| 49   | '1'  | 0x1  | 5    |
| 50   | '2'  | 0x2  | 5    |
| 97   | 'a'  | 0x3  | 5    |
| 101  | 'e'  | 0x5  | 5    |
| 105  | 'i'  | 0x7  | 5    |
| 111  | 'o'  | 0x8  | 5    |
| 115  | 's'  | 0x9  | 5    |
| 116  | 't'  | 0xa  | 5    |
| 32   | ' '  | 0x14 | 6    |
| 37   | '%'  | 0x15 | 6    |

## Encoding Algorithm

### `Encode(byte[] data, int offset, int length)`

1. Initialize: `long bitBuffer = 0`, `int bitCount = 0`, output `List<byte>` (or pre-allocated array based on `GetEncodedLength`).
2. For each byte in `data[offset..offset+length]`:
   a. Look up `(code, bitLength)` from `s_encodeTable[data[i]]`.
   b. Accumulate: `bitBuffer = (bitBuffer << bitLength) | code; bitCount += bitLength;`
   c. While `bitCount >= 8`: emit `(byte)(bitBuffer >> (bitCount - 8))`, decrement `bitCount -= 8`.
3. **Pad the last byte:** If `bitCount > 0`, pad with 1-bits: `bitBuffer = (bitBuffer << (8 - bitCount)) | ((1 << (8 - bitCount)) - 1)`. Emit as final byte.

**Padding rule (RFC 7541 Section 5.2):** The final byte is padded with the most significant bits of the EOS symbol (all 1s). The padding MUST be at most 7 bits, all 1s. Decoders validate this.

### `GetEncodedLength(byte[] data, int offset, int length)`

Sum up `s_encodeTable[data[i]].BitLength` for all bytes, then `(totalBits + 7) / 8`. Used by the encoder to decide whether Huffman is shorter than raw.

## Decoding Algorithm

### Decode Tree Construction (static initialization)

Build a binary tree at class load time:

```csharp
private class HuffmanNode
{
    public int Symbol = -1;  // -1 = internal node, 0-255 = leaf, 256 = EOS
    public HuffmanNode Zero;  // Left child (bit 0)
    public HuffmanNode One;   // Right child (bit 1)
}

private static readonly HuffmanNode s_root;
```

Build by iterating the encoding table. For each symbol 0–256:
1. Start at root.
2. For each bit in the code (MSB first, `bitLength` bits):
   - bit = 0 → go to `Zero` child (create if null)
   - bit = 1 → go to `One` child (create if null)
3. At the leaf, set `Symbol = symbolIndex`.

### `Decode(byte[] data, int offset, int length)`

1. Initialize: current node = root, output `List<byte>`.
2. For each byte in `data[offset..offset+length]`:
   a. For each bit (MSB first, 8 bits per byte):
      - Navigate: `node = (bit == 0) ? node.Zero : node.One`.
      - If `node == null`: throw compression error (invalid Huffman sequence).
      - If `node.Symbol >= 0`:
        - If `node.Symbol == 256` (EOS): throw compression error per RFC 7541 Section 5.2.
        - Emit `(byte)node.Symbol`.
        - Reset: `node = root`.
3. **Validate padding:** After processing all bits, `node` should be an internal node where only 1-bit paths were followed (i.e., the remaining bits are all 1s padding). If `node.Symbol >= 0` and we're mid-symbol, that's an error. The number of remaining padding bits must be ≤ 7 and must all be 1s.
   - Practical check: if `node != root`, verify we're on a path reachable by all-1s padding and haven't landed on a complete symbol.

## Alternative: Flat Lookup Table Decoding (Optional Optimization)

For Phase 10 performance, decode can use a 256-entry flat lookup table per node level (process 8 bits at a time instead of 1). For Phase 3 (correctness focus), the bit-by-bit tree walk is preferred because:
- Simpler to verify against RFC
- Easier to debug
- Lower memory footprint

## IL2CPP/AOT Safety

- No generics, no reflection
- Static arrays and simple class nodes are fully AOT-compatible
- `allowUnsafeCode` is available but not needed for Phase 3 Huffman implementation
- Static constructor builds the decode tree once; no lazy initialization issues

## Performance Notes (Phase 3)

- `List<byte>` for output accumulation (GC allocations). Phase 10: ArrayPool.
- Tree walk is O(totalBits) per decode — fine for typical header sizes (< 1KB compressed).
- Encoding table lookup is O(1) per byte.

## Validation Criteria

- [ ] Encode/decode round-trip: `Decode(Encode(data)) == data` for arbitrary byte sequences
- [ ] RFC 7541 Appendix C test vectors pass (specific header values)
- [ ] Empty input encodes to empty output
- [ ] Padding is all 1-bits and ≤ 7 bits
- [ ] EOS symbol (256) in input triggers compression error on decode
- [ ] Common strings ("www.example.com", "no-cache", "custom-key", "custom-value") encode to expected byte sequences from RFC examples
- [ ] `GetEncodedLength` matches actual encoded output length
- [ ] Huffman encoding of "www.example.com" = `f1e3 c2e5 f23a 6ba0 ab90 f4ff` (from RFC 7541 C.4.1)
