# 2026-11 Decompression: zlib deflate, multi-member gzip, 32 MB default, segmented buffered body (#219, #74)

## What

- `deflate` is decoded as zlib (RFC 9110 section 8.4.1.2, RFC 1950): the 2-byte header is detected
  (CM = 8, CINFO <= 7, `(CMF*256+FLG) % 31 == 0`), the Adler-32 trailer is checked, and the trailer
  must be the end of the layer input. Without a zlib header the body is raw DEFLATE (RFC 1951), as
  before. FDICT (preset dictionary) fails, per RFC 1950 section 2.3.
- gzip bodies with several members (RFC 1952 section 2.2) decode; every member's CRC32 + ISIZE trailer
  is checked. Header: ID1/ID2/CM and reserved flags checked, FEXTRA/FNAME/FCOMMENT/FHCRC skipped
  (header CRC16 not verified, allowed by section 2.3.1.2), header bounded at 128 KiB. After a trailer
  only end of input or the next member's ID1 ID2 is accepted (trailing garbage fails, as before).
- Default `maxDecompressedBodySizeBytes` lowered from 256 MB to 32 MB (`DecompressionInterceptor`,
  XML doc and CHANGELOG). The compressed-length cap still uses the same value.
- The buffered path decodes into a `SegmentedBuffer` (pooled 16 KB segments) and hands it to the new
  `SegmentedResponseBodySource`; the response collector detaches the segments into the `UHttpResponse`
  (owner = the buffer). Old path: `MemoryStream` growth + `ToArray()` + the collector's copy.
  The limit is checked before a segment is added; at the limit a 1-byte stack probe tells a complete
  body from an oversized one.

## Files

- `Runtime/Middleware/InflateLayerStream.cs` (new): one gzip/deflate layer over a raw `DeflateStream`.
  Replaces `GzipInputTrackingStream` and `DecoderCompletionStream` (removed from `DecompressionHandler`).
- `Runtime/Middleware/SegmentedResponseBodySource.cs` (new): buffered decoded body over a `SegmentedBuffer`.
- `Runtime/Middleware/DecompressionHandler.cs`: buffered branch and `DecompressBufferedBody`, decoder factory.
- `Runtime/Middleware/DecompressionInterceptor.cs`: 32 MB default, XML docs.
- Tests: `Tests/Runtime/Middleware/DecompressionZlibAndMultiMemberTests.cs`,
  `Tests/Runtime/Middleware/DecompressionLimitAndAllocationTests.cs`.
- Docs: `CHANGELOG.md`; phase docs 22a and 22a.5 note the new default.

## Decisions

- Member end detection. .NET Standard 2.1 has no zlib/inflate API that reports consumed input
  (`ZLibStream` is .NET 6+), and `GZipStream` differs between Mono and .NET on multi-member input. The
  layer parses gzip/zlib framing itself and runs a raw `DeflateStream` per member. The inflater only
  pulls input after consuming what it had, so its data ends in the last chunks it pulled. Input is handed
  out in chunks of at most 2 KiB, the last 4 KiB handed stay buffered, and at the member end the trailer
  is the first position in that window whose 8 bytes match the computed CRC32/ISIZE and that is followed
  by end of input or `1F 8B`. If the assumption ever broke, the trailer would not be found and the body
  fails (fail-safe, never silent corruption). The window covers two chunks in case an inflater pulls one
  more chunk after its end.
- zlib strictness: Adler-32 must be the last 4 bytes (no trailing data). Raw DEFLATE keeps the lenient
  behaviour: up to 64 KiB of trailing bytes are skipped (and now drained, so a stacked layer below
  always reaches its own end).
- `SegmentedResponseBodySource.TryGetBufferedData` returns true only when the decoded body fits one
  segment; larger bodies are read or detached. Detach is refused when trailers exist (the detached body
  has none), so such bodies are read (one copy, like before).
- `_in` in `InflateLayerStream` is a plain 8 KiB array, not pooled: `Abort()` can dispose the stream
  from another thread while a read runs, and a pooled buffer would then be returned while in use.
- The allocation criterion names `GC.GetAllocatedBytesForCurrentThread`; Unity's Mono (Boehm) reports
  nothing there, so the test falls back to the profiler counter "GC Allocated In Frame" around a frame
  that runs only the measured section, and skips only when neither counts. A counter-free structural test
  (the 10 MB body arrives as <= 16 KB segments) runs everywhere.
- The bomb bound is asserted as limit + one 16 KB segment + 64 KB fixed overhead (decoder state,
  exceptions, pipeline objects), the same fixed slack as the 10 MB criterion.

## Deferred

- None for #219/#74. The number of gzip members is not capped (each costs >= 20 input bytes, one
  `DeflateStream` that is disposed at the member end, output bounded by the limit).

## Verification

- `./Tools~/ci/check.sh`: pass.
- Unity 2021.3.45f2 PlayMode (Editor, Mono), `--filter 'Decompression|InterceptorOrder'`: all pass,
  including `StreamingAllocationGateTests.DecompressionStreamingRead_KnownAllocationPoint_StaysWithinBudget`.
  Measured (frame counter, warm pool): 10 MB buffered body 8.5 MB allocated; bomb with 512 KB limit 19.5 KB.
- The new tests were run against the old runtime code: positive zlib, multi-member, optional-header,
  allocation, segment and 32 MB-default tests fail there (old allocation: 54.5 MB for 10 MB; bomb 1.58 MB).
- Not run: .NET (CoreCLR) `DeflateStream` (no non-Unity test host), IL2CPP player, devices.
