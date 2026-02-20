# Phase 18.1: WebSocket Framing & Protocol Layer

**Depends on:** None (foundational)
**Assembly:** `TurboHTTP.WebSocket`
**Files:** 4 new

---

## Step 1: Define WebSocket Frame Structure and Opcodes

**File:** `Runtime/WebSocket/WebSocketFrame.cs` (new)

Required behavior:

1. Define `WebSocketOpcode` enum: `Continuation` (0x0), `Text` (0x1), `Binary` (0x2), `Close` (0x8), `Ping` (0x9), `Pong` (0xA).
2. Define `WebSocketFrame` struct containing: `Opcode`, `IsFinal` (FIN bit), `IsMasked`, `MaskKey` (4 bytes), `Payload` (`ReadOnlyMemory<byte>`), `PayloadLength`.
3. Add helper properties: `IsControlFrame` (opcode >= 0x8), `IsDataFrame` (opcode <= 0x2).
4. Validate RFC 6455 Section 5.5 constraints: control frames must not exceed 125 bytes payload and must not be fragmented.
5. Define `WebSocketCloseCode` enum covering RFC 6455 Section 7.4.1 status codes: `NormalClosure` (1000), `GoingAway` (1001), `ProtocolError` (1002), `UnsupportedData` (1003), `NoStatusReceived` (1005), `AbnormalClosure` (1006), `InvalidPayload` (1007), `PolicyViolation` (1008), `MessageTooBig` (1009), `MandatoryExtension` (1010), `InternalServerError` (1011).
6. Define `WebSocketCloseStatus` struct containing: `Code` (`WebSocketCloseCode`), `Reason` (string, max 123 bytes UTF-8 per RFC 6455 Section 5.5).

Implementation constraints:

1. Frame struct must be allocation-free for the common path (use `ReadOnlyMemory<byte>` for payload, not `byte[]`).
2. Opcode and close code enums must use explicit integer values matching RFC wire format.
3. Close reason string must validate UTF-8 encoding and enforce the 123-byte limit.

---

## Step 2: Implement Frame Reader (Deserializer)

**File:** `Runtime/WebSocket/WebSocketFrameReader.cs` (new)

Required behavior:

1. Read frames from a `Stream` asynchronously with `CancellationToken` support.
2. Parse the 2-byte frame header: FIN, RSV1-3, opcode, MASK, payload length.
3. Handle extended payload lengths: 16-bit (126) and 64-bit (127) variants per RFC 6455 Section 5.2.
4. Read and apply mask key (4 bytes) when MASK bit is set (server → client frames are typically unmasked).
5. Unmask payload data using XOR with rotating mask key.
6. Enforce maximum frame payload size limit (configurable, default 16MB) to prevent memory exhaustion.
7. Reassemble fragmented messages: accumulate continuation frames until FIN bit is set.
8. Handle interleaved control frames during fragmentation (RFC 6455 Section 5.4 — control frames may appear between fragments).
9. Validate RSV bits are zero unless negotiated extensions set them.

Implementation constraints:

1. Use `ByteArrayPool` / `ArrayPool<byte>.Shared` for payload buffers — return buffers after message consumption.
2. Read operations must be cancellation-safe — partial reads must not corrupt stream state.
3. Frame header parsing must handle partial reads (stream may deliver bytes incrementally).
4. Fragmented message reassembly must bound total accumulated size to prevent decompression-bomb-style attacks.
5. Network byte order (big-endian) for extended payload lengths — use `BinaryPrimitives.ReadUInt16BigEndian` / `ReadUInt64BigEndian`.

---

## Step 3: Implement Frame Writer (Serializer)

**File:** `Runtime/WebSocket/WebSocketFrameWriter.cs` (new)

Required behavior:

1. Write frames to a `Stream` asynchronously with `CancellationToken` support.
2. Construct the 2-byte frame header from opcode, FIN bit, and payload length.
3. Client frames must always be masked (RFC 6455 Section 5.3) — generate 4-byte mask key using `RandomNumberGenerator`.
4. Apply mask to payload data using XOR with rotating mask key before writing.
5. Select correct payload length encoding: 7-bit (0-125), 16-bit (126), 64-bit (127).
6. Support writing text frames (UTF-8 encoded string → binary payload).
7. Support writing binary frames (raw `ReadOnlyMemory<byte>` payload).
8. Support writing control frames: ping (with optional payload), pong (echo payload), close (status code + reason).
9. Support message fragmentation: split large messages into configurable-size fragments with continuation frames.

Implementation constraints:

1. Mask key generation must use cryptographically secure RNG (not `System.Random`) per RFC 6455 Section 10.3.
2. Frame header + mask key should be written in a single buffer to minimize stream write calls.
3. Use pooled buffers for masked payload construction — avoid allocating a new `byte[]` per frame.
4. Write operations must be serialized (only one frame write at a time) — caller is responsible for synchronization or the connection layer handles it.
5. Fragmentation threshold should be configurable (default: no fragmentation for messages under 64KB).

---

## Step 4: Add Protocol Constants and Utilities

**File:** `Runtime/WebSocket/WebSocketConstants.cs` (new)

Required behavior:

1. Define protocol constants: WebSocket GUID (`258EAFA5-E914-47DA-95CA-5AB53DC52D51`), supported version (`13`).
2. Define default configuration values: max frame size (16MB), max message size (64MB), fragmentation threshold (64KB), close handshake timeout (5s), ping interval (30s), pong timeout (10s).
3. Add static utility methods:
   - `ComputeAcceptKey(string clientKey)` — SHA-1 hash of client key + GUID, base64 encoded (RFC 6455 Section 4.2.2).
   - `GenerateClientKey()` — 16 random bytes, base64 encoded (RFC 6455 Section 4.1).
   - `ValidateCloseCode(int code)` — check code is in valid range per RFC 6455 Section 7.4.
4. Add `WebSocketError` enum extending error taxonomy: `InvalidFrame`, `FrameTooLarge`, `InvalidCloseCode`, `InvalidUtf8`, `MaskRequired`, `UnexpectedContinuation`, `ProtocolViolation`.

Implementation constraints:

1. `ComputeAcceptKey` must use `System.Security.Cryptography.SHA1` (available in .NET Standard 2.1).
2. `GenerateClientKey` must use `RandomNumberGenerator` for the 16 random bytes.
3. Constants class must be static with no instance state.

---

## Verification Criteria

1. Frame reader correctly parses single-frame text, binary, and control frames with various payload sizes (0, 125, 126, 65535, 65536 bytes).
2. Frame writer produces wire-format-correct masked frames that can be round-tripped through the reader.
3. Fragmented message reassembly works for 2-fragment and N-fragment sequences with interleaved control frames.
4. Masking/unmasking produces correct output for known test vectors.
5. Payload size limits are enforced — oversized frames/messages are rejected with appropriate error.
6. `ComputeAcceptKey` produces correct output for RFC 6455 Section 4.2.2 example values.
7. All buffer allocations use pooled arrays — no per-frame heap allocations for payload data.
