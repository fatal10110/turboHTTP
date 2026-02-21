# Phase 18a.1: Extension Framework & `permessage-deflate`

**Depends on:** Phase 18, Spikes 1 & 2
**Assembly:** `TurboHTTP.WebSocket`
**Files:** 6 new, 4 modified
**Estimated Effort:** 2 weeks
**Gated by:** Spikes 1 & 2

---

## Motivation

`permessage-deflate` (RFC 7692) is the **only** IANA-registered WebSocket extension and is negotiated by default by all major WebSocket infrastructure (nginx, Cloudflare, AWS ALB, HAProxy, IIS). Phase 18 built the extension hooks (RSV bit validation, `Sec-WebSocket-Extensions` header) but left them unused. This sub-phase fills the gaps with a general-purpose extension framework and the concrete compression implementation.

## Cross-Cutting Design Decisions (18a.1)

1. **Extension transforms operate at the message level, not the frame level.** Transforms receive the fully reassembled message payload and produce a transformed payload. Per-frame extension hooks would require a different interface design and are deferred. This aligns with RFC 7692 which specifies per-message compression (RSV1 on first fragment signals the entire reassembled message is compressed).

2. **Extension pipeline ordering.** Extensions execute in negotiation order (server-returned order). Outbound: transform left-to-right. Inbound: transform right-to-left. This matches RFC 6455 Section 9.1 semantics.

3. **RSV bit ownership.** Each registered extension declares which RSV bit(s) it uses. The framework validates no two extensions claim the same bit. `permessage-deflate` uses RSV1 (bit 6 of the first byte, mask `0x40`). The combined allowed RSV mask is passed to `WebSocketFrameReader` at connection time.

4. **RSV1 first-fragment-only semantics (RFC 7692 Section 6).** For fragmented messages, RSV1 is set only on the **first frame**. Continuation frames MUST have RSV1 = 0 (the assembler validates this). After reassembly, the extension pipeline checks RSV1 from the first fragment to determine whether to decompress.

5. **Context takeover — v1 mandates `no_context_takeover`.** Context takeover preserves the deflate sliding window across messages for better compression, but costs **300–600KB per connection** (zlib deflate state ~256KB + inflate ~44KB). Additionally, `DeflateStream.Flush()` behavior on Unity Mono may not produce the required `Z_SYNC_FLUSH` output. For v1, `client_no_context_takeover` and `server_no_context_takeover` are always sent in the offer. Full context takeover is gated behind a separate implementation phase pending Spike 1 & 2 results.

6. **Compression threshold.** Messages smaller than `CompressionThreshold` (default 128 bytes) are not compressed — the deflate overhead may increase payload size. This avoids pathological cases.

7. **Fragmentation interaction.** Compression happens before fragmentation (outbound). The `FragmentationThreshold` applies to the **compressed** payload size. Decompression happens after reassembly (inbound).

8. **Thread safety.** Compression state for outbound is protected by the existing send `SemaphoreSlim(1,1)`. Decompression state for inbound is accessed only by the single-threaded receive loop — no additional synchronization.

9. **Control frames are never compressed.** RFC 7692 Section 6.1 explicitly states that RSV1 on control frames is not modified by `permessage-deflate`. Control frames bypass the extension pipeline entirely.

10. **Graceful degradation.** If the server does not negotiate `permessage-deflate`, the connection proceeds without compression. If the server negotiates parameters the client cannot accept, the handshake fails with a descriptive error.

11. **IL2CPP compatibility.** `System.IO.Compression.DeflateStream` is available in .NET Standard 2.1 and works under IL2CPP. No `link.xml` additions required beyond existing Phase 18 entries.

12. **Unity MonoBehaviour impact.** No changes to `UnityWebSocketClient` or `UnityWebSocketBridge` — compression is transparent at the protocol layer.

13. **Extension disposal order.** Extensions are disposed in **reverse** of negotiation order (last negotiated = first disposed), consistent with resource stack unwinding.

---

## Step 1: Define Extension Interface

**File:** `Runtime/WebSocket/IWebSocketExtension.cs` (new)

Required behavior:

1. Define `IWebSocketExtension` interface:
   - `Name` (string) — extension token as registered with IANA (e.g., `"permessage-deflate"`).
   - `RsvBitMask` (byte) — RSV bits this extension uses (e.g., `0x40` for RSV1). Must be a subset of `0x70`.
   - `BuildOffers()` → `IReadOnlyList<WebSocketExtensionOffer>` — produce the client's extension offer string(s) with parameters for the `Sec-WebSocket-Extensions` request header. Multiple offers allow different parameter sets (RFC 7692 Section 7.1).
   - `AcceptNegotiation(WebSocketExtensionParameters serverParams)` → `bool` — validate and accept server-negotiated parameters. Return `false` to reject.
   - `TransformOutbound(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, out byte rsvBits)` → `IMemoryOwner<byte>` — transform outbound message before framing. Return `null` for passthrough (no transformation). Caller disposes the returned `IMemoryOwner<byte>` via `using` after the frame is written.
   - `TransformInbound(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, byte rsvBits)` → `IMemoryOwner<byte>` — transform inbound message after reassembly. Called only when relevant RSV bits are set on the first fragment. Return `null` for passthrough. Caller disposes via `using`.
   - `Reset()` — reset internal state (e.g., context takeover resets).
   - Extends `IDisposable`.

2. Define `WebSocketExtensionOffer` struct:
   - `ExtensionToken` (string) — the extension name.
   - `Parameters` (IReadOnlyDictionary<string, string>) — extension parameters (key-value, value may be null for valueless params like `server_no_context_takeover`).
   - `ToHeaderValue()` method — serializes to RFC 7692 wire format: `token; param1=value1; param2`.

3. Define `WebSocketExtensionParameters` class:
   - `ExtensionToken` (string).
   - `Parameters` (IReadOnlyDictionary<string, string>).
   - Static `Parse(string headerValue)` method — parse a single extension from the `Sec-WebSocket-Extensions` response header value. Must handle both unquoted tokens and RFC 7230 §3.2.6 quoted-string values (with backslash escapes and `DQUOTE` delimiters). Handle valueless parameters (boolean flags) by storing `null` as the value.

Implementation constraints:

1. `TransformOutbound`/`TransformInbound` use `IMemoryOwner<byte>` (from `System.Buffers`) so ownership and valid data length are explicit. The `IMemoryOwner<byte>.Memory` property provides the exact valid range. Implementations rent from `ArrayPool` and wrap with a helper `ArrayPoolMemoryOwner<byte>` that returns to pool on dispose. Return `null` for passthrough.
2. Interface must be in `TurboHTTP.WebSocket` assembly — no new assembly needed.
3. Extension name matching must be case-insensitive (RFC 6455 Section 9.1).

---

## Step 2: Add Extension Negotiation Pipeline

**File:** `Runtime/WebSocket/WebSocketExtensionNegotiator.cs` (new)

Required behavior:

1. Accept a list of `IWebSocketExtension` instances (client-configured extensions in preference order).
2. `BuildOffersHeader()` → `string` — concatenate all extension offers into a single `Sec-WebSocket-Extensions` header value. Format per RFC 6455 §9.1 ABNF: `extension-list = extension *( "," extension )` where `extension = extension-token *( ";" extension-param )`. Parameter values containing special characters must be quoted per HTTP token rules.
3. `ProcessNegotiation(string serverExtensionsHeader)` → `WebSocketExtensionNegotiationResult`:
   - Parse the server's `Sec-WebSocket-Extensions` response header into individual extension entries.
   - For each server extension entry, find a matching client extension by name and call `AcceptNegotiation`.
   - Validate RSV bit uniqueness across all accepted extensions.
   - Return the list of active (negotiated) extensions and the combined `allowedRsvMask`.
4. Reject negotiation if:
   - Server returns an extension the client did not offer.
   - Two accepted extensions claim the same RSV bit.
   - An extension's `AcceptNegotiation` returns `false`.
5. `WebSocketExtensionNegotiationResult`:
   - `ActiveExtensions` (`IReadOnlyList<IWebSocketExtension>`) — negotiated extensions in server-returned order.
   - `AllowedRsvMask` (byte) — combined RSV mask for all active extensions.
   - `IsSuccess` (bool).
   - `ErrorMessage` (string) — if negotiation failed.

Implementation constraints:

1. Server may return multiple extensions — all must be validated.
2. Negotiator is stateless — produces a result, does not hold references to active extensions.

---

## Step 3: Add RSV Bits to WebSocketFrame and Propagate Through Pipeline

**File:** `Runtime/WebSocket/WebSocketFrame.cs` (modify)

Required behavior:

1. Add `RsvBits` property (byte) to `WebSocketFrame` readonly struct — the raw RSV1/RSV2/RSV3 bits from the wire (masked to `0x70`).
2. Add convenience properties: `IsRsv1Set` (`(RsvBits & 0x40) != 0`), `IsRsv2Set`, `IsRsv3Set`.
3. Add **overloaded constructor** with `byte rsvBits = 0` default parameter to maintain backward compatibility. The existing constructor continues to work unchanged (RSV defaults to 0).
4. Validation: RSV bits must be within `0x70` mask. Invalid values throw `ArgumentOutOfRangeException`.

**File:** `Runtime/WebSocket/WebSocketFrameReader.cs` (modify — RSV propagation)

Required behavior:

1. The reader already parses RSV bits from the wire (line 74: `byte rsvBits = (byte)(first & 0x70)`). Currently these are validated but **discarded**. Propagate `rsvBits` into the `WebSocketFrame` constructor call so frames carry their RSV bits through the pipeline.

**File:** `Runtime/WebSocket/MessageAssembler.cs` (implied modification in 18a.3 pipeline integration)

Required behavior:

1. When assembling a fragmented message, preserve the RSV bits from the **first fragment** (the initiating text/binary frame). These bits determine whether the assembled message needs decompression.
2. Validate that **continuation frames have RSV1 = 0** when `permessage-deflate` is active (RFC 7692 Section 6.1). If a continuation frame has RSV1 set, reject as protocol error.
3. Expose the first-fragment RSV bits on the `WebSocketAssembledMessage` so the connection can pass them to the extension pipeline.

---

## Step 4: Implement `permessage-deflate`

**File:** `Runtime/WebSocket/PerMessageDeflateExtension.cs` (new)

Required behavior:

1. Implement `IWebSocketExtension` for RFC 7692.
2. **Extension token:** `"permessage-deflate"`.
3. **RSV bit:** RSV1 (`0x40`).
4. **Offer parameters** (client → server):
   - `server_no_context_takeover` — always sent in v1 (mandatory for v1 release).
   - `client_no_context_takeover` — always sent in v1 (mandatory for v1 release).
   - `server_max_window_bits` (8-15, default 15) — request maximum LZ77 window size for server compression.
   - `client_max_window_bits` (8-15, default 15) — declare maximum LZ77 window size for client compression.
5. **Negotiation acceptance** (`AcceptNegotiation`):
   - Parse and validate server response parameters.
   - Accept `server_no_context_takeover` and `client_no_context_takeover` — server can always require `client_no_context_takeover` even if client didn't offer it.
   - Validate `server_max_window_bits` and `client_max_window_bits` are within 8-15 range.
   - Reject unknown parameters.
   - Store negotiated values for runtime use.
6. **Outbound transform** (`TransformOutbound`):
   - Skip control frames (return null).
   - Skip messages smaller than `CompressionThreshold` (return null, RSV1 not set).
   - Compress payload using `DeflateStream` with configured compression level.
   - **Remove trailing 4 bytes** (`0x00 0x00 0xFF 0xFF`) from compressed output per RFC 7692 Section 7.2.1.
   - Set `rsvBits = 0x40` (RSV1) on success.
   - In v1 (`no_context_takeover` mandated): create a new `DeflateStream` per message, dispose after compression.
   - Return result as `IMemoryOwner<byte>`.
7. **Inbound transform** (`TransformInbound`):
   - Only process when RSV1 is set. Skip control frames.
   - **Append 4 bytes** (`0x00 0x00 0xFF 0xFF`) to the received payload before decompression (RFC 7692 Section 7.2.2).
   - Decompress using **chunk-based streaming decompression**: read from `DeflateStream` in 16KB chunks into an `ArrayBufferWriter<byte>`, checking `MaxMessageSize` after each chunk appended (zip bomb protection — reject before full allocation).
   - In v1 (`no_context_takeover` mandated): create a new `DeflateStream` per message, dispose after decompression.
   - Enforce `MaxMessageSize` on decompressed output. Throw `WebSocketException` with `DecompressedMessageTooLarge` error if exceeded.
   - Return result as `IMemoryOwner<byte>`.
8. **`Reset()`** — dispose and recreate deflate/inflate contexts (v1: no-op since contexts are per-message).
9. **`Dispose()`** — dispose any held `DeflateStream` instances and backing buffers.

Implementation constraints:

1. Use `System.IO.Compression.DeflateStream` — raw DEFLATE (RFC 1951), no gzip header, no zlib header. `DeflateStream` with `CompressionMode.Compress` produces raw deflate, which is correct.
2. **v1 = `no_context_takeover` only.** Each message gets a fresh `DeflateStream` pair (compress/decompress). No sliding window state is preserved. This avoids the 300–600KB per-connection memory overhead and the `Z_SYNC_FLUSH` vs `Z_FINISH` ambiguity. Full context takeover is deferred to a future version pending Spike 1 & 2 results.
3. **Chunk-based decompression.** Do NOT pre-allocate a buffer sized to estimated decompressed output. Instead, stream through `DeflateStream` in 16KB chunks, appending to `ArrayBufferWriter<byte>`, and check accumulated size against `MaxMessageSize` after every chunk. This prevents zip bombs from allocating unbounded memory.
4. Thread safety: outbound protected by the connection's send `SemaphoreSlim`. Inbound by single-threaded receive loop. No additional synchronization needed within the extension.
5. `DeflateStream` on Unity Mono may be significantly slower than on CoreCLR. Recommend `no_context_takeover` for mobile in documentation (which v1 already mandates).

**File:** `Runtime/WebSocket/PerMessageDeflateOptions.cs` (new)

Required behavior:

1. Configuration POCO for `permessage-deflate`:
   - `CompressionLevel` (int, 0-9, default 6) — maps to .NET `CompressionLevel` enum: `NoCompression` (0), `Fastest` (1-3), `Optimal` (4-9).
   - `ClientMaxWindowBits` (int, 8-15, default 15) — LZ77 window size for client compression.
   - `ServerMaxWindowBits` (int, 8-15, default 15) — requested LZ77 window size for server compression.
   - `ClientNoContextTakeover` (bool, default **true** for v1) — reset compression context after each message.
   - `ServerNoContextTakeover` (bool, default **true** for v1) — request server to reset compression context.
   - `CompressionThreshold` (int, default 128) — minimum payload bytes before compression is applied.
2. Provide `Default` static property with v1 defaults (both context takeover flags = true).
3. Validate at construction: window bits in range (8-15), compression level in range (0-9), threshold >= 0.

---

## Step 5: Wire Extensions into Connection Pipeline

**File:** `Runtime/WebSocket/WebSocketHandshake.cs` (modify)

Required behavior:

1. When `ExtensionFactories` are configured, instantiate extensions and use `WebSocketExtensionNegotiator.BuildOffersHeader()` for the `Sec-WebSocket-Extensions` header value.
2. After handshake success, run `WebSocketExtensionNegotiator.ProcessNegotiation()` with the server's `Sec-WebSocket-Extensions` response.
3. Store negotiated `IWebSocketExtension` instances and combined `allowedRsvMask` in the handshake result.
4. On negotiation failure, close the connection with `MandatoryExtension` close code (1010) if extensions were required, or silently proceed without extensions if compression is optional.

**File:** `Runtime/WebSocket/WebSocketConnection.cs` (modify)

Required behavior — **Receive path:**

1. Pass `allowedRsvMask` from negotiation result to `WebSocketFrameReader` constructor.
2. After `MessageAssembler` produces a complete message, check the first-fragment RSV bits exposed on `WebSocketAssembledMessage`:
   - If RSV1 is set and a `permessage-deflate` extension is active, call `TransformInbound()`.
   - The returned `IMemoryOwner<byte>` replaces the assembled message payload. Dispose the original assembled payload buffer.
   - Enforce `MaxMessageSize` on the decompressed result.
3. If decompression fails, trigger connection failure with close code 1002 (`ProtocolError`) and fire error event.

Required behavior — **Send path:**

1. Before calling `WebSocketFrameWriter.WriteMessageAsync()`, if an outbound extension is active:
   - Call `TransformOutbound()` with the message payload.
   - If transformation produced a result (non-null `IMemoryOwner<byte>`), use the compressed `Memory` region and set RSV bits on the first frame.
   - If transformation returned null (below threshold), send uncompressed as normal.
   - Dispose the `IMemoryOwner<byte>` after the frame write completes.
2. RSV bits must be set only on the first frame of a fragmented message.
3. Fragmentation threshold applies to the **compressed** payload size (fragment after compression, not before).

**File:** `Runtime/WebSocket/WebSocketFrameWriter.cs` (modify)

Required behavior:

1. Accept optional `byte rsvBits = 0` parameter in `WriteFrameAsync()` and `WriteMessageAsync()`.
2. OR the RSV bits into the first byte of the frame header alongside FIN and opcode: `first = (byte)((fin ? 0x80 : 0) | rsvBits | opcode)`.
3. For fragmented messages, apply RSV bits only to the first fragment frame, set `rsvBits = 0` for continuation frames.

**File:** `Runtime/WebSocket/WebSocketConnectionOptions.cs` (modify)

Required behavior:

1. Add `ExtensionFactories` property (`IReadOnlyList<Func<IWebSocketExtension>>`) — factory pattern so each connection gets its own extension instance (deflate context is per-connection).
2. Add convenience methods:
   - `WithCompression()` — add default `permessage-deflate` factory.
   - `WithCompression(PerMessageDeflateOptions options)` — add customized `permessage-deflate` factory.
   - `WithExtension(Func<IWebSocketExtension> factory)` — add a custom extension factory.
3. Keep backward compatibility: existing `Extensions` property (`IReadOnlyList<string>`) remains for raw header values. If both `Extensions` and `ExtensionFactories` are set, structured extensions take precedence and raw strings are appended to the offer header. Log a deprecation warning if `Extensions` is used.
4. Update `Clone()` and `Validate()` methods to handle new properties.

---

## Step 6: Add Extension Error Types

**File:** `Runtime/WebSocket/WebSocketException.cs` (modify)

Required behavior:

1. Add `WebSocketError` cases:
   - `ExtensionNegotiationFailed` — server offered unsupported extension or parameters.
   - `DecompressionFailed` — deflate decompression error (corrupt data).
   - `CompressionFailed` — deflate compression error.
   - `DecompressedMessageTooLarge` — decompressed output exceeds `MaxMessageSize` (zip bomb protection).
2. All compression/extension errors are NOT retryable (`IsRetryable = false`) — they indicate data corruption or misconfiguration.

---

## Verification Criteria

1. Extension negotiation builds correct headers and rejects invalid server responses.
2. RSV bit collision detection rejects two extensions claiming the same bit.
3. RSV bits propagate: reader → frame → assembler (first fragment) → extension transform.
4. Continuation frames with RSV1 set are rejected as protocol errors (RFC 7692 §6.1).
5. `permessage-deflate` compress/decompress round-trip equals original for text and binary.
6. RFC 7692 trailing bytes (`0x00 0x00 0xFF 0xFF`) correctly stripped on outbound, appended on inbound.
7. Context takeover behavior (v1: always resets per message).
8. Compression threshold bypass for small messages.
9. Zip bomb protection: crafted compressed payload that expands beyond `MaxMessageSize` → `DecompressedMessageTooLarge` error with chunk-based detection.
10. Control frame passthrough (never compressed, RSV1 unmodified).
11. Graceful fallback when server rejects compression.
12. `IMemoryOwner<byte>` ownership: returned owners have correct `Memory.Length` and are properly disposed.
13. Compression + fragmentation: large compressed message exceeding `FragmentationThreshold` is correctly fragmented with RSV1 only on first fragment.
14. Extension disposal in reverse negotiation order.
15. RFC 7230 §3.2.6 quoted-string parameter parsing (backslash escapes, DQUOTE delimiters).
