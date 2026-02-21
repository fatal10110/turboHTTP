# Phase 18.1: WebSocket Framing & Protocol Layer

**Depends on:** None (foundational)
**Assembly:** `TurboHTTP.WebSocket`
**Files:** 6 new

---

## Step 0: Add WebSocket Assembly Definition

**File:** `Runtime/WebSocket/TurboHTTP.WebSocket.asmdef` (new)

Required behavior:

1. Create `TurboHTTP.WebSocket` assembly definition.
2. References: `TurboHTTP.Core` only (no `TurboHTTP.Transport` reference).
3. Set `autoReferenced` to `false` to match modular package behavior.
4. Keep assembly WebGL-compatible (`excludePlatforms` empty).

Implementation constraints:

1. Set `noEngineReferences` to `true` (protocol layer must not depend on UnityEngine).
2. Keep `overrideReferences` as `false` and avoid precompiled reference overrides unless required later.

---

## Step 1: Define WebSocket Frame Structure and Opcodes

**File:** `Runtime/WebSocket/WebSocketFrame.cs` (new)

Required behavior:

1. Define `WebSocketOpcode` enum: `Continuation` (0x0), `Text` (0x1), `Binary` (0x2), `Close` (0x8), `Ping` (0x9), `Pong` (0xA).
2. Define `WebSocketFrame` as a **`readonly struct`** containing: `Opcode`, `IsFinal` (FIN bit), `IsMasked`, `MaskKey` (`uint` for XOR performance), `Payload` (`ReadOnlyMemory<byte>`), `PayloadLength`.
3. Add helper properties: `IsControlFrame` (opcode >= 0x8), `IsDataFrame` (opcode <= 0x2).
4. Validate RFC 6455 Section 5.5 constraints: control frames must not exceed 125 bytes payload and must not be fragmented.
5. Define `WebSocketCloseCode` enum covering RFC 6455 Section 7.4.1 status codes: `NormalClosure` (1000), `GoingAway` (1001), `ProtocolError` (1002), `UnsupportedData` (1003), `NoStatusReceived` (1005), `AbnormalClosure` (1006), `InvalidPayload` (1007), `PolicyViolation` (1008), `MessageTooBig` (1009), `MandatoryExtension` (1010), `InternalServerError` (1011).
6. Define `WebSocketCloseStatus` struct containing: `Code` (`WebSocketCloseCode`), `Reason` (string, max 123 bytes UTF-8 per RFC 6455 Section 5.5).
7. Close codes 1005 (`NoStatusReceived`) and 1006 (`AbnormalClosure`) must NEVER be sent on the wire — these are reserved for local signaling only (RFC 6455 Section 7.4.1). `ValidateCloseCode` must reject them in the send path.

Implementation constraints:

1. Frame is a `readonly struct` — use `ReadOnlyMemory<byte>` for payload, prevent accidental mutation. Do not use `WebSocketFrame` as a generic type argument in collections (avoids IL2CPP boxing concerns with `Memory<T>` in structs).
2. Opcode and close code enums must use explicit integer values matching RFC wire format.
3. Close reason string must validate UTF-8 encoding and enforce the 123-byte limit. Truncation at byte level during frame write using `Encoding.UTF8.GetByteCount()` then `GetBytes()` with a max 123-byte buffer — no substring allocation.
4. Reserved opcodes (0x3-0x7 for data, 0xB-0xF for control) must be treated as protocol errors by the reader — reject frames with reserved opcodes unless extensions are negotiated.

---

## Step 2: Implement Frame Reader (Deserializer)

**File:** `Runtime/WebSocket/WebSocketFrameReader.cs` (new)

Required behavior:

1. Read frames from a `Stream` asynchronously with `CancellationToken` support.
2. Parse the 2-byte frame header: FIN, RSV1-3, opcode, MASK, payload length.
3. Handle extended payload lengths: 16-bit (126) and 64-bit (127) variants per RFC 6455 Section 5.2.
4. **64-bit payload length validation:** the most significant bit (bit 63) MUST be zero (RFC 6455 Section 5.2). Reject frames where the high bit is set — a negative `long` would overflow buffer allocation.
5. **Server-to-client masking validation:** RFC 6455 Section 5.1 states a server MUST NOT mask frames sent to the client. If a server frame arrives with the MASK bit set, the client MUST fail the connection with a protocol error (close code 1002). Do not silently unmask server frames.
6. Unmask payload data using XOR with rotating mask key (only applicable if masking is present and valid).
7. Enforce maximum frame payload size limit (configurable, default 16MB) to prevent memory exhaustion.
8. Validate RSV bits are zero unless negotiated extensions set them.
9. Reject frames with reserved opcodes (0x3-0x7, 0xB-0xF) as protocol errors.
10. Validate fragmentation rules inline:
    - Reject unexpected continuation frame (no preceding non-final data frame in progress).
    - Reject new data frame (text/binary) while a fragmented message is in progress (RFC 6455 Section 5.4).
    - Allow interleaved control frames during fragmentation.

Implementation constraints:

1. Use `ByteArrayPool` / `ArrayPool<byte>.Shared` for payload buffers — buffer ownership is transferred to the caller (connection layer / message assembler).
2. Read operations must be cancellation-safe. For cancellation of blocked `Stream.ReadAsync` calls where the token is not respected, use `ct.Register(() => stream.Dispose())` pattern (same as existing HTTP/1.1 timeout enforcement).
3. **Partial read strategy:** implement `ReadExactAsync(stream, buffer, offset, count, ct)` helper that loops `ReadAsync` until `count` bytes are read or the stream ends (returns false on EOF). Frame header can be 2, 4, 8, or 14 bytes — the helper must handle byte-at-a-time delivery.
4. Network byte order (big-endian) for extended payload lengths — use `BinaryPrimitives.ReadUInt16BigEndian` / `ReadUInt64BigEndian`.
5. **`WebSocketFrameReader` is stateless** — it parses individual frames only. Fragmentation reassembly is handled by `MessageAssembler` (Step 5). The reader validates fragmentation rules (item 10 above) by accepting current fragmentation state as a parameter.

---

## Step 3: Implement Frame Writer (Serializer)

**File:** `Runtime/WebSocket/WebSocketFrameWriter.cs` (new)

Required behavior:

1. Write frames to a `Stream` asynchronously with `CancellationToken` support.
2. Construct the 2-byte frame header from opcode, FIN bit, and payload length.
3. Client frames must always be masked (RFC 6455 Section 5.3).
4. **Batch mask key generation:** pre-generate mask keys by filling a 256-byte buffer (64 keys × 4 bytes) from `RandomNumberGenerator`. Consume keys sequentially, refill when exhausted. Avoids per-frame crypto syscall overhead at high throughput (e.g., 60fps game state updates).
5. **Chunked masking strategy:** do NOT copy the entire payload into a masked buffer. Instead, write the frame header (2-14 bytes + 4-byte mask key) first, then stream the payload through a small fixed-size working buffer (8KB), masking each chunk via XOR before writing to the stream. This avoids doubling memory for large payloads (up to `FragmentationThreshold` = 64KB default).
6. Select correct payload length encoding: 7-bit (0-125), 16-bit (126), 64-bit (127).
7. Support writing text frames (UTF-8 encoded string → binary payload). Use `Encoding.UTF8.GetByteCount(str)` first to determine exact buffer size, then rent exact-fit buffer from pool (avoids `GetMaxByteCount` 3x overestimation for ASCII-heavy strings).
8. Support writing binary frames (raw `ReadOnlyMemory<byte>` payload).
9. Support writing control frames: ping (with optional payload), pong (echo payload), close (status code + reason).
10. Support message fragmentation: split large messages into configurable-size fragments with continuation frames.

Implementation constraints:

1. Mask key batch buffer must use `RandomNumberGenerator.Create()` (not `RNGCryptoServiceProvider` directly — better cross-platform compatibility, especially Android IL2CPP).
2. Frame header + mask key should be written in a single buffer to minimize stream write calls.
3. Chunked masking working buffer (8KB) is allocated once per writer instance, not per frame.
4. Write operations must be serialized (only one frame write at a time) — the connection layer enforces this via `SemaphoreSlim(1,1)` with try/finally.
5. Fragmentation threshold should be configurable (default: no fragmentation for messages under 64KB).

---

## Step 4: Add Protocol Constants and Utilities

**File:** `Runtime/WebSocket/WebSocketConstants.cs` (new)

Required behavior:

1. Define protocol constants: WebSocket GUID (`258EAFA5-E914-47DA-95CA-5AB53DC52D51`), supported version (`13`).
2. Define default configuration values: max frame size (16MB), max message size (4MB), max fragment count (64), fragmentation threshold (64KB), close handshake timeout (5s), ping interval (25s), pong timeout (10s), receive queue capacity (100).
3. Add static utility methods:
   - `ComputeAcceptKey(string clientKey)` — SHA-1 hash of client key + GUID, base64 encoded (RFC 6455 Section 4.2.2).
   - `GenerateClientKey()` — 16 random bytes, base64 encoded (RFC 6455 Section 4.1).
   - `ValidateCloseCode(int code)` — check code is in valid range per RFC 6455 Section 7.4. Reject 1005 and 1006 for wire transmission.
4. Add `WebSocketError` enum extending error taxonomy: `InvalidFrame`, `FrameTooLarge`, `InvalidCloseCode`, `InvalidUtf8`, `MaskedServerFrame`, `UnexpectedContinuation`, `ReservedOpcode`, `ProtocolViolation`, `PayloadLengthOverflow`.

Implementation constraints:

1. `ComputeAcceptKey` must use `System.Security.Cryptography.SHA1` (available in .NET Standard 2.1). Add `link.xml` preservation for `SHA1` and `SHA1Managed` to prevent IL2CPP code stripping.
2. `GenerateClientKey` must use `RandomNumberGenerator.Create()`.
3. Constants class must be static with no instance state.
4. Add platform detection: test SHA-1 availability in a static method, throw descriptive `PlatformNotSupportedException` if missing.

---

## Step 5: Implement Message Assembler

**File:** `Runtime/WebSocket/MessageAssembler.cs` (new)

Required behavior:

1. Accumulate continuation frames until FIN bit is set, producing a complete `WebSocketMessage`.
2. Handle interleaved control frames during fragmentation — control frames are returned immediately, fragmentation state is preserved.
3. Enforce `MaxMessageSize` during accumulation — reject **before** allocating the next fragment buffer when accumulated size would exceed the limit.
4. Enforce `MaxFragmentCount` — reject after the configured maximum number of fragments (default 64) to bound metadata overhead.
5. Track fragmentation state: whether a fragmented message is in progress, the original opcode (text/binary), accumulated fragments.

Implementation constraints:

1. `MessageAssembler` is a separate class owned by `WebSocketConnection`, keeping `WebSocketFrameReader` stateless and independently testable.
2. Fragment buffers from `ArrayPool` must be tracked for return after assembly or on error/cancellation.
3. Assembled message payload is copied into a single contiguous buffer (rented from pool) — individual fragment buffers are returned immediately after copy.
4. Thread safety: `MessageAssembler` is accessed only by the receive loop (single-threaded access) — no synchronization required.
5. `Reset()` method to clear fragmentation state on connection close or error, returning any held buffers to the pool.

---

## Verification Criteria

1. Frame reader correctly parses single-frame text, binary, and control frames with various payload sizes (0, 125, 126, 65535, 65536 bytes).
2. Frame writer produces wire-format-correct masked frames that can be round-tripped through the reader.
3. Fragmented message reassembly works for 2-fragment and N-fragment sequences with interleaved control frames.
4. Masking/unmasking produces correct output for known test vectors.
5. Payload size limits are enforced — oversized frames/messages are rejected with appropriate error.
6. `ComputeAcceptKey` produces correct output for RFC 6455 Section 4.2.2 example values.
7. 64-bit payload length with bit 63 set is rejected.
8. Masked server-to-client frames are rejected as protocol errors.
9. Reserved opcodes (0x3-0x7, 0xB-0xF) are rejected as protocol errors.
10. `ReadExactAsync` handles byte-at-a-time delivery without corruption.
11. Chunked masking produces identical output to full-buffer masking.
12. Batch mask key generation produces cryptographically random keys without per-frame syscall.
13. `MaxMessageSize` and `MaxFragmentCount` are enforced before allocation during reassembly.
