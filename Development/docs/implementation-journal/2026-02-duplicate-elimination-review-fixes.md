# Duplicate Elimination Review — Specialist Agent Fixes

**Date:** 2026-02-27
**Status:** Complete — 2-round specialist review + follow-up fix
**References:** `2026-02-duplicate-elimination-core-transport.md`

---

## Review Outcome

Both specialist agents reviewed all 7 changes from the deduplication pass. 6 of 7 changes were confirmed correct with no action required. 3 bugs were identified and fixed.

---

## Bug 1 — PooledHeaderWriter.WriteToAsync returned unawaited Task

**File:** `Runtime/Transport/Http1/Http11RequestSerializer.cs`
**Reviewer:** unity-infrastructure-architect

**Issue:** `WriteToAsync` returned `stream.WriteAsync(...)` directly as a `Task` rather than awaiting it. The `PooledHeaderWriter` is a `using var` — its `Dispose()` calls `_writer.Dispose()` which returns the backing `ArrayPool` buffer to the pool. While in the normal completion path the `await` in `SerializeAsync` ensures disposal only happens after the task completes, the pattern is an anti-pattern for `IDisposable` owners: any future caller that does not await could cause the backing buffer to be returned while the OS write is still in-flight.

**Fix:** Converted to `async Task` with internal `await`.

```csharp
// Before
public Task WriteToAsync(Stream stream, CancellationToken ct)
{
    ...
    return stream.WriteAsync(segment.Array, segment.Offset, segment.Count, ct);
}

// After
public async Task WriteToAsync(Stream stream, CancellationToken ct)
{
    ...
    await stream.WriteAsync(segment.Array, segment.Offset, segment.Count, ct)
        .ConfigureAwait(false);
}
```

---

## Bug 2 — HpackHuffman.Decode early-exit bypasses cross-byte padding length check

**File:** `Runtime/Transport/Http2/HpackHuffman.cs`
**Reviewer:** unity-network-architect
**RFC:** RFC 7541 Section 5.2 — padding MUST be fewer than 8 bits

**Issue:** When the inner bit loop hits `next == null` (null child in the Huffman tree) on the last byte, the code verifies that all remaining bits in that byte are 1s (via the inner `pb` loop), then returns immediately. However, it did not account for `paddingBits` — the count of consecutive 1-bits accumulated from bytes prior to the last byte. The total padding is:

```
totalPaddingBits = paddingBits + 1 (current bit) + bit (remaining bits in last byte)
```

Without checking `totalPaddingBits > 7`, a compressed string where the last valid symbol completes mid-byte N-1 followed by bytes of all-1 padding could pass validation with more than 7 total padding bits, violating RFC 7541 Section 5.2.

**Example scenario:** A symbol completes at bit 0 of byte N-1 (resetting `paddingBits = 0`). Byte N is `0xFF` (all 1-bits). At bit 7 of byte N, `next == null`, `paddingBits = 0`, the inner loop runs to validate bits 6-0. Total padding = 0 + 1 + 7 = 8. This was not detected.

**Fix:** Added total-padding check before the early return.

```csharp
int totalPaddingBits = paddingBits + 1 + bit;
if (totalPaddingBits > 7)
    throw new HpackDecodingException("Invalid Huffman padding (more than 7 bits)");
```

---

## Bug 3 — Silent non-Latin-1 replacement in header encoding paths

**Files:** `Runtime/Transport/Internal/EncodingHelper.cs`, `Runtime/Transport/Http1/Http11RequestSerializer.cs`
**Reviewer:** unity-network-architect

`EncodingHelper.GetLatin1Bytes` and the `Latin1Encoding` fallback silently replaced characters above U+00FF with `'?'` (0x3F). Any caller passing a non-Latin-1 string in an HTTP header value received silent data corruption — the serialized byte on the wire would be `'?'` instead of an error. The CRLF injection guard in `ValidateHeader` did not catch this.

**Fix:** All four encoding call sites now throw `ArgumentException` on non-Latin-1 input:

1. `EncodingHelper.GetLatin1Bytes` — hot path, used by HTTP/1.1 serializer and HPACK encoder
2. `Latin1Encoding.GetBytes(char[], int, int, byte[], int)` — IL2CPP fallback
3. `Latin1Encoding.GetBytes(string)` — IL2CPP fallback
4. `PooledHeaderWriter.Append(char)` — HTTP/1.1 serializer literal-char append path

Error message includes the Unicode code point (`U+XXXX`) and character index. The decode direction (`GetString`, `GetChars`) is unaffected — byte-to-char is always lossless for Latin-1.

---

## Files Modified

1. `Runtime/Transport/Http1/Http11RequestSerializer.cs` — Bug 1 fix + Bug 3 fix (Append(char))
2. `Runtime/Transport/Http2/HpackHuffman.cs` — Bug 2 fix
3. `Runtime/Transport/Internal/EncodingHelper.cs` — Bug 3 fix

---

## Confirmed Correct (No Changes)

| Change | Verdict |
|---|---|
| HpackEncoder._outputBuffer (reusable field) | Correct — Reset/ToArray ordering safe, IL2CPP OK |
| HpackHuffman.Decode uses PooledArrayBufferWriter | Correct (Bug 2 fixed separately) |
| ValueTaskSourceCoreWrapper base class | Correct — struct field in class OK, AOT generics safe |
| SendFrameWithPooledPayloadAsync | Correct — payload returned in finally, codec copies before return |
| HPACK literal decode consolidation | Correct — RFC 7541 Section 6.2.2/6.2.3 semantically identical at decoder level |
| AdaptiveMiddleware local variable refactor | Correct — redundant assignment is harmless |
| WINDOW_UPDATE R-bit masking | Correct — `& 0x7F` zeroes reserved bit per RFC 7540 Section 6.9 |
| GOAWAY lastStreamId=0 | Acceptable — client-initiated, server push disabled |
| RST_STREAM Flags=None | Correct — RFC 7540 Section 6.4 defines no flags |
| PING payload zeros | Correct — opaque 8-byte payload, non-ACK ping |
