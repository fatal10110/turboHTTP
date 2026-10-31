# 2026-10 — HTTP/2 field-block reception (#11, #20, #32, #31)

## What

- **#11 (P0)**: encoded field blocks no longer accumulate on `Http2Stream`. The read loop owns one
  connection-level block (`_headerBlockBuffer`, `_continuationStreamId`, end-stream and push flags):
  HEADERS/PUSH_PROMISE + CONTINUATION are contiguous on the wire, so at most one block is open.
  Every block is HPACK-decoded at END_HEADERS whatever the state of its stream (RFC 9113 §4.3,
  RFC 7541 §2.2), and only then routed to the stream if it is still in `_activeStreams`, or
  discarded. `_continuationStreamId` is set on any HEADERS/PUSH_PROMISE without END_HEADERS and
  cleared on END_HEADERS for any stream, which fixes both secondary bugs: a stream removed mid-block
  no longer leaves `_continuationStreamId` set, and a block for an unknown stream no longer makes its
  CONTINUATION "unexpected". Single-frame blocks decode straight from the frame payload (no copy).
- **PUSH_PROMISE** (live: `EnablePush` defaults to true): padding is stripped, the promised id read,
  and the promised request's field block goes through the same path (decoded, CONTINUATION
  accepted, then discarded) before RST_STREAM(REFUSED_STREAM). PUSH_PROMISE on stream 0 or too short
  for the promised id is now a connection PROTOCOL_ERROR instead of being ignored.
- **#20**: a block is bounded before anything is buffered: at most
  `MaxContinuationFramesPerHeaderBlock` (128) CONTINUATION frames, zero-length ones included, and at
  most `max(MAX_FRAME_SIZE, min(MaxHeaderListSize, 256 KB))` encoded bytes. Either violation is a
  connection error ENHANCE_YOUR_CALM (GOAWAY, all streams failed). The buffer is rented from
  `ArrayPool<byte>.Shared` per multi-frame block and returned at END_HEADERS or when the read loop
  exits; `Http2Stream` has no header buffer any more, so the 128 pooled streams retain nothing.
- **#32**: `HpackHuffman.Decode` tracks the bits consumed since the last emitted symbol and whether
  they were all 1-bits. At end of input a partial code must be an EOS prefix (all ones) shorter than
  8 bits, else `HpackDecodingException` (connection COMPRESSION_ERROR). The unreachable lenient
  "null node in the last byte" branch is now a plain error (the HPACK code is complete).
- **#31**: decoded regular fields of response headers and trailers are checked against RFC 9113
  §8.2.1/§8.2.2 (`GetMalformedResponseFieldReason`): empty names, uppercase or other invalid name
  characters, NUL/CR/LF in values, leading/trailing SP/HTAB in values, and the connection-specific
  fields `connection`, `keep-alive`, `proxy-connection`, `transfer-encoding`, `upgrade`. A violation
  is a stream error: `stream.Fail` + `RemoveActiveStream`, as the neighbouring malformed branches do.
  The reason never echoes the value. Validation runs after the HPACK decode, so a rejected block does
  not desync the table.

## Files

- `Runtime/Transport/Http2/Http2Connection.cs`: connection-level block state, flood constants
- `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs`: `BeginHeaderBlock`, `HandleContinuationFrame`,
  `CompleteHeaderBlock`, buffer rent/grow/release, `ApplyDecodedHeaders` (was `DecodeAndSetHeaders`),
  response-field validation, PUSH_PROMISE block handling, read-loop `finally` releases the buffer
- `Runtime/Transport/Http2/Http2Stream.cs`, `Http2StreamPool.cs`: header buffer and
  `PendingEndStream` removed
- `Runtime/Transport/Http2/HpackHuffman.cs`: EOS-prefix padding check
- Tests: `Tests/Runtime/Transport/Http2/Http2ConnectionTests.HeaderBlockReception.cs`,
  `Tests/Runtime/Transport/Http2/HpackHuffmanPaddingTests.cs`

## Decisions

- Connection-level buffer rather than a per-stream discard path: one block can be open per
  connection, so this is the simplest structure that decodes every block, and it removes the pooled
  per-stream `MemoryStream` (#20) at the same time.
- Byte cap derived from the advertised MAX_HEADER_LIST_SIZE (an encoded block is in practice not
  larger than the decoded list it carries), capped at 256 KB, floored at one full frame so a block
  accepted as one frame is also accepted when split. ENHANCE_YOUR_CALM for both bounds (RFC 9113
  §10.5: abusive resource use).
- #31 goes beyond uppercase and connection-specific names to the rest of §8.2.1 for responses
  (invalid name characters, NUL/CR/LF, value edge whitespace); the RFC says MUST treat as malformed,
  and an empty name previously reached `HttpHeaders.Add`, which throws and killed the connection
  without GOAWAY.
- No RST_STREAM(PROTOCOL_ERROR) on malformed responses: #28 (h2-stream-lifecycle) owns RST on every
  malformed-response branch, including the new one. The sync shape of the header path is unchanged.
- Frames on idle (never-opened) stream ids are still treated as closed (out of scope; #19/#28/#25).

## Verification

- `'./Tools~/ci/check.sh'` and `--full`: pass.
- The Http2/Hpack NUnit fixtures (`Tests/Runtime/Transport/Http2/**`, except the
  UnityEngine-dependent `Http2FlowControlTests`) were compiled against the shadow-built
  `TurboHTTP.Core`/`Transport`/`Testing` DLLs in a scratch .NET 8 NUnit project: 223/223 pass. The
  same new tests against `origin/main` DLLs: 21 fail for the expected reasons (HPACK index 62 out
  of range, "Unexpected CONTINUATION frame", no GOAWAY, malformed fields accepted, invalid padding
  accepted, `Http2Stream` holding a `MemoryStream`).
- Not run: Unity EditMode/PlayMode (no Unity on this host; PR CI), IL2CPP or device builds.
