# Phase 18a: Advanced WebSocket Features

**Milestone:** M4 (v1.2)
**Dependencies:** Phase 18 (all sub-phases — core WebSocket fully implemented)
**Estimated Complexity:** High
**Estimated Effort:** 4-6 weeks
**Critical:** No — enhancement to Phase 18 WebSocket client

## Overview

Phase 18 delivers a complete, production-ready WebSocket client: RFC 6455 framing, handshake, lifecycle, send/receive API, reconnection, and Unity integration. However, several categories of enhancement remain that are standard in mature WebSocket libraries. Phase 18a addresses all of them.

This document covers **seven enhancement areas**, organized as sub-phases. Each can be implemented and shipped independently after Phase 18, so teams can prioritize based on their use cases.

## Why These Enhancements?

Phase 18 was intentionally scoped to the RFC 6455 baseline. The following features were explicitly deferred or were identified as gaps during the Phase 18 review and implementation:

| Enhancement | Why It Was Deferred | Why It Matters Now |
|---|---|---|
| **Extension framework + `permessage-deflate`** | Phase 18 overview §19: "deferred to a future phase" | Only standardized WS extension; most servers (nginx, Cloudflare, AWS ALB) negotiate it by default. 60-90% bandwidth savings on text payloads. |
| **`IAsyncEnumerable` streaming receive** | Phase 18.4: method name `ReceiveAllAsync` was *reserved* but not implemented | Enables `await foreach` pattern — the idiomatic C# way to consume message streams. Reduces boilerplate significantly. |
| **Connection metrics & observability** | No coverage in Phase 18 | Production debugging requires bytes sent/received, message counts, compression ratios, latency histograms. Zero-allocation counter pattern. |
| **HTTP proxy tunneling** | No coverage in Phase 18 | Enterprise and mobile networks frequently use HTTP proxies. Without CONNECT tunnel support, WebSocket connections fail behind proxies. |
| **Typed message serialization** | Phase 18 overview §19: "sub-protocol dispatch is out of scope" | JSON-RPC, game state protocols, and event systems need strongly-typed send/receive. Reduces boilerplate and prevents serialization bugs. |
| **Connection health & diagnostics** | Phase 18.3 has basic keep-alive | Latency probing, bandwidth estimation, connection quality scoring for adaptive game networking. |

## Pre-Implementation Spikes

> [!CAUTION]
> These spikes **must** be completed and passed before the corresponding sub-phase begins implementation.

### Spike 1: `DeflateStream.Flush()` Behavior (gates 18a.1)

RFC 7692 §7.2.1 requires stripping the trailing `0x00 0x00 0xFF 0xFF` produced by `Z_SYNC_FLUSH`. On Unity Mono, `DeflateStream.Flush()` may produce `Z_FINISH` instead, and `FlushMode` is not exposed in .NET Standard 2.1.

**Test:** Create a minimal Unity project that compresses a short payload via `DeflateStream`, calls `Flush()` (not `Close()`), and checks whether the output ends with `0x00 0x00 0xFF 0xFF`. Run on:
- Unity Editor (Mono)
- iOS IL2CPP build
- Android IL2CPP build

**If fails:** Evaluate native zlib P/Invoke with explicit `Z_SYNC_FLUSH` control as fallback.

### Spike 2: `DeflateStream` Memory Usage (gates 18a.1)

The actual memory per `DeflateStream` instance varies significantly by runtime. zlib deflate state is ~256KB for compression at default level, ~44KB for decompression — **300–600KB total per connection with context takeover**, not 64KB.

**Test:** Measure `GC.GetTotalMemory(true)` before/after creating 10 `DeflateStream` pairs (compress + decompress). Run on Unity Mono and IL2CPP. Document per-pair memory cost.

### Spike 3: `IAsyncEnumerable` IL2CPP Compatibility (gates 18a.2)

`IAsyncEnumerable<T>` is natively included in .NET Standard 2.1 (no NuGet dependency required). However, IL2CPP code stripping may affect async enumerable state machine infrastructure.

**Test:** Create a minimal `async IAsyncEnumerable<int>` method with `yield return`, consume it via `await foreach`, and build for iOS/Android IL2CPP in Unity 2021.3 LTS. Verify runtime execution. If stripping occurs, add `link.xml` entries and re-test.

## Sub-Phase Index

| Sub-Phase | Name | New Files | Modified Files | Depends On |
|---|---|---|---|---|
| 18a.1 | Extension Framework & `permessage-deflate` | 6 | 4 | Phase 18, Spikes 1 & 2 |
| 18a.2 | `IAsyncEnumerable` Streaming Receive | 1 | 3 | Phase 18, Spike 3 |
| 18a.3 | Connection Metrics & Observability | 2 | 3 | Phase 18 |
| 18a.4 | HTTP Proxy Tunneling | 2 | 2 | Phase 18 |
| 18a.5 | Typed Message Serialization | 3 | 1 | Phase 18 |
| 18a.6 | Connection Health & Diagnostics | 1 | 2 | Phase 18, 18a.3 |
| 18a.7 | Test Suite | 6 | 1 | All above |

## Dependency Graph

```text
Phase 18 (done — core WebSocket)
  ├── 18a.1 Extension Framework & permessage-deflate  [gated by Spikes 1 & 2]
  ├── 18a.2 IAsyncEnumerable Streaming Receive        [gated by Spike 3]
  ├── 18a.3 Connection Metrics & Observability
  │    └── 18a.6 Connection Health & Diagnostics
  ├── 18a.4 HTTP Proxy Tunneling
  ├── 18a.5 Typed Message Serialization
  │
  18a.1-18a.6
      └── 18a.7 Test Suite
```

> [!NOTE]
> Sub-phases 18a.1 through 18a.5 are independent of each other and can be implemented in parallel or in any order. 18a.6 depends on 18a.3 (uses the metrics infrastructure). 18a.7 covers testing for all sub-phases.

---

## 18a.1: Extension Framework & `permessage-deflate`

**Assembly:** `TurboHTTP.WebSocket`
**Files:** 6 new, 4 modified
**Estimated Effort:** 2 weeks
**Gated by:** Spikes 1 & 2

### Motivation

`permessage-deflate` (RFC 7692) is the **only** IANA-registered WebSocket extension and is negotiated by default by all major WebSocket infrastructure (nginx, Cloudflare, AWS ALB, HAProxy, IIS). Phase 18 built the extension hooks (RSV bit validation, `Sec-WebSocket-Extensions` header) but left them unused. This sub-phase fills the gaps with a general-purpose extension framework and the concrete compression implementation.

### Cross-Cutting Design Decisions (18a.1)

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

### Step 1: Define Extension Interface

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

### Step 2: Add Extension Negotiation Pipeline

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

### Step 3: Add RSV Bits to WebSocketFrame and Propagate Through Pipeline

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

### Step 4: Implement `permessage-deflate`

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

### Step 5: Wire Extensions into Connection Pipeline

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

### Step 6: Add Extension Error Types

**File:** `Runtime/WebSocket/WebSocketException.cs` (modify)

Required behavior:

1. Add `WebSocketError` cases:
   - `ExtensionNegotiationFailed` — server offered unsupported extension or parameters.
   - `DecompressionFailed` — deflate decompression error (corrupt data).
   - `CompressionFailed` — deflate compression error.
   - `DecompressedMessageTooLarge` — decompressed output exceeds `MaxMessageSize` (zip bomb protection).
2. All compression/extension errors are NOT retryable (`IsRetryable = false`) — they indicate data corruption or misconfiguration.

---

## 18a.2: `IAsyncEnumerable` Streaming Receive

**Assembly:** `TurboHTTP.WebSocket`
**Files:** 1 new, 3 modified
**Estimated Effort:** 2-3 days
**Gated by:** Spike 3

### Motivation

Phase 18.4 reserved the method name `ReceiveAllAsync` for `IAsyncEnumerable<WebSocketMessage>` but did not implement it. The `await foreach` pattern is the idiomatic C# 8+ way to consume infinite message streams and dramatically reduces consumer boilerplate.

### Step 1: Implement `ReceiveAllAsync`

**File:** `Runtime/WebSocket/WebSocketAsyncEnumerable.cs` (new)

Required behavior:

1. Implement `IAsyncEnumerable<WebSocketMessage>` adapter over the existing `BoundedAsyncQueue`.
2. `GetAsyncEnumerator(CancellationToken)` returns `IAsyncEnumerator<WebSocketMessage>`.
3. `MoveNextAsync()` calls the connection's `ReceiveAsync` and returns `true`/`false`.
4. Enumeration ends when the connection transitions to `Closed` (returns `false`, no exception).
5. `Current` returns the most recent `WebSocketMessage` (caller owns disposal).
6. Cancellation via the `CancellationToken` passed to `GetAsyncEnumerator`.

Implementation constraints:

1. `IAsyncEnumerable<T>` is natively available in .NET Standard 2.1 — **no NuGet dependency** (no `Microsoft.Bcl.AsyncInterfaces` needed). Unity 2021.3 LTS with .NET Standard 2.1 profile includes it. IL2CPP stripping risks are validated by Spike 3; if stripping occurs, `link.xml` entries will be added.
2. Caller must dispose each `WebSocketMessage` yielded by the enumerator.
3. Only one active enumerator per client — concurrent enumeration throws `InvalidOperationException`. Track via `Interlocked.CompareExchange` on an `int` flag. `DisposeAsync()` on the enumerator resets the flag so a new enumerator can be created.

### Step 2: Add to `IWebSocketClient` and Implementations

**Files:** `Runtime/WebSocket/IWebSocketClient.cs` (modify), `Runtime/WebSocket/WebSocketClient.cs` (modify), `Runtime/WebSocket/ResilientWebSocketClient.cs` (modify)

Required behavior:

1. Add `ReceiveAllAsync(CancellationToken ct = default)` returning `IAsyncEnumerable<WebSocketMessage>` to `IWebSocketClient`.
2. Implement in `WebSocketClient` using the adapter from Step 1.
3. Implement in `ResilientWebSocketClient` — during reconnection, the enumerator **blocks** on `MoveNextAsync` until reconnection succeeds (then resumes yielding messages) or reconnection is exhausted (then returns `false`). Messages that were queued in the send buffer during reconnection are replayed on the new connection; the receive enumerator starts fresh (no buffering of inbound messages across reconnection boundaries — this is the safe default).

---

## 18a.3: Connection Metrics & Observability

**Assembly:** `TurboHTTP.WebSocket`
**Files:** 2 new, 3 modified
**Estimated Effort:** 3-4 days

### Motivation

Production WebSocket deployments need visibility into connection health without external monitoring tools. Game developers need frame-level metrics for adaptive quality-of-service. The current implementation tracks `_lastActivityTimestamp` but exposes nothing to consumers.

### Step 1: Define Metrics Snapshot

**File:** `Runtime/WebSocket/WebSocketMetrics.cs` (new)

Required behavior:

1. Define `WebSocketMetrics` as a readonly struct:
   - `BytesSent` (long) — total bytes written to the wire (including frame overhead).
   - `BytesReceived` (long) — total bytes read from the wire.
   - `MessagesSent` (long) — total application messages sent.
   - `MessagesReceived` (long) — total application messages received.
   - `FramesSent` (long) — total frames sent (including control frames, fragments).
   - `FramesReceived` (long) — total frames received.
   - `PingsSent` (long) — keep-alive pings sent.
   - `PongsReceived` (long) — keep-alive pongs received.
   - `UncompressedBytesSent` (long) — pre-compression application payload size (sum of original message lengths before compression). 0 if compression inactive.
   - `CompressedBytesSent` (long) — bytes sent after compression. 0 if compression inactive.
   - `CompressedBytesReceived` (long) — compressed bytes received before decompression. 0 if compression inactive.
   - `CompressionRatio` (double) — computed: `CompressedBytesSent > 0 ? (double)UncompressedBytesSent / CompressedBytesSent : 1.0`.
   - `ConnectionUptime` (TimeSpan) — time since connection opened.
   - `LastActivityAge` (TimeSpan) — time since last frame was sent or received.

Implementation constraints:

1. **32-bit IL2CPP safety.** `Interlocked.Add` on `long` fields is not truly atomic on 32-bit ARM (IL2CPP). Follow the established Phase 6 `HttpMetrics` pattern: use `public long` fields for `Interlocked` operations with the documented caveat that 32-bit reads of these counters may tear. For `CompressionRatio` (double), store as `long` bits via `BitConverter.DoubleToInt64Bits` and reconvert on read. Add 32-bit Android IL2CPP to the validation matrix.
2. `GetSnapshot()` returns a frozen `WebSocketMetrics` value at a consistent point in time (single pass read of all fields).

### Step 2: Implement Metrics Collector

**File:** `Runtime/WebSocket/WebSocketMetricsCollector.cs` (new)

Required behavior:

1. Internal class owned by `WebSocketConnection`.
2. Increment methods called by frame reader/writer: `RecordFrameSent(int byteCount)`, `RecordFrameReceived(int byteCount)`, `RecordMessageSent()`, `RecordMessageReceived()`, `RecordCompression(int originalSize, int compressedSize)`.
3. Thread-safe via `Interlocked.Add` — no locks.
4. Exposes `GetSnapshot()` for external consumption.

### Step 3: Expose Metrics on Client API

**Files:** `Runtime/WebSocket/IWebSocketClient.cs` (modify), `Runtime/WebSocket/WebSocketClient.cs` (modify), `Runtime/WebSocket/WebSocketConnection.cs` (modify)

Required behavior:

1. Add `Metrics` property to `IWebSocketClient` returning `WebSocketMetrics`.
2. Wire counters into frame reader/writer call sites.
3. Optionally expose an `OnMetricsUpdated` event fired at configurable intervals (e.g., every 100 messages or every 5s). **Threading model:** event fires on the network thread (the receive loop thread or the send caller's thread). Unity consumers must marshal to main thread via `MainThreadDispatcher` — document this explicitly.

---

## 18a.4: HTTP Proxy Tunneling

**Assembly:** `TurboHTTP.WebSocket.Transport`
**Files:** 2 new, 2 modified
**Estimated Effort:** 3-4 days

### Motivation

Many enterprise networks, corporate firewalls, and mobile carriers route traffic through HTTP proxies. WebSocket connections fail silently if the client cannot tunnel through the proxy via HTTP CONNECT. This is a common production blocker for Unity apps deployed in corporate environments.

### Step 1: Implement Proxy Configuration

**File:** `Runtime/WebSocket.Transport/WebSocketProxySettings.cs` (new)

Required behavior:

1. Configuration with immutable types:
   - `ProxyUri` (Uri) — proxy endpoint (e.g., `http://proxy.corp:8080`). Validate scheme is `http://` only — HTTPS proxies are deferred.
   - `Credentials` (`ProxyCredentials?`, optional) — for proxy authentication (Basic only for v1).
   - `BypassList` (IReadOnlyList<string>) — hostnames/patterns that bypass the proxy. **Matching semantics:** exact hostname match (case-insensitive) and leading wildcard match (`*.domain` matches `foo.domain` and `bar.baz.domain`). CIDR notation and port-specific matching are deferred.
2. Define `ProxyCredentials` as a custom **readonly struct** (not `System.Net.NetworkCredential` which may be IL2CPP-stripped and is mutable):
   ```csharp
   public readonly struct ProxyCredentials
   {
       public string Username { get; }
       public string Password { get; }
   }
   ```
3. Static `None` property for no proxy.
4. Remove `UseSystemProxy` — system proxy detection is not reliably available across Unity platforms. Defer to a future phase with platform-specific implementations.

> [!WARNING]
> **Security consideration:** Basic proxy authentication sends credentials in Base64 (reversible encoding) over the unencrypted TCP connection to the proxy. Credentials are stored as plaintext `string` in managed memory. Document this limitation explicitly in API documentation and recommend HTTPS-based proxy solutions for sensitive environments.

### Step 2: Implement HTTP CONNECT Tunnel

**File:** `Runtime/WebSocket.Transport/ProxyTunnelConnector.cs` (new)

Required behavior:

1. After TCP connection to proxy, send `CONNECT host:port HTTP/1.1` request with `Host` header.
2. Parse proxy response: `200 Connection Established` → proceed with TLS/WebSocket handshake through the tunnel.
3. Handle proxy authentication:
   - `407 Proxy Authentication Required` → retry with `Proxy-Authorization: Basic <base64>` header.
   - Only **Basic** authentication scheme for v1. Digest auth is deferred (complex nonce handling).
4. Timeout enforcement via `CancellationToken`.
5. Tunnel stream wraps the original TCP stream — transparent to the WebSocket handshake layer.
6. Log a warning when Basic auth credentials are sent over an unencrypted proxy connection.

### Step 3: Integrate into Transport and Add Error Types

**Files:** `Runtime/WebSocket.Transport/RawSocketWebSocketTransport.cs` (modify), `Runtime/WebSocket/WebSocketConnectionOptions.cs` (modify)

Required behavior:

1. Add `ProxySettings` property to `WebSocketConnectionOptions` (default: `WebSocketProxySettings.None`).
2. When proxy is configured, `RawSocketWebSocketTransport.ConnectAsync` connects to proxy first, runs the CONNECT tunnel, then proceeds with optional TLS handshake and WebSocket upgrade.
3. TLS handshake happens **after** tunnel establishment (the proxy sees only encrypted traffic for `wss://`).
4. **Proxy-specific error codes** in `WebSocketError` enum:
   - `ProxyAuthenticationRequired` — 407 received and no credentials configured.
   - `ProxyConnectionFailed` — cannot reach the proxy endpoint.
   - `ProxyTunnelFailed` — CONNECT tunnel rejected (non-200 response from proxy).

---

## 18a.5: Typed Message Serialization

**Assembly:** `TurboHTTP.WebSocket`
**Files:** 3 new, 1 modified
**Estimated Effort:** 3-4 days

### Motivation

Phase 18 overview §19 states "sub-protocol dispatch is out of scope — message handling is the application's responsibility." In practice, every WebSocket consumer needs to serialize/deserialize messages. Providing a lightweight typed layer eliminates boilerplate while remaining unopinionated about serialization format.

### Step 1: Define Typed Message Interface

**File:** `Runtime/WebSocket/IWebSocketMessageSerializer.cs` (new)

Required behavior:

1. Define `IWebSocketMessageSerializer<T>` interface:
   - `Serialize(T message)` → `ReadOnlyMemory<byte>` — always produce UTF-8 bytes directly (for text sub-protocols this avoids the double-copy of string → UTF-8 encode → compress; the serializer writes UTF-8 bytes, the send path uses them directly as a binary-opcode or text-opcode frame payload).
   - `Deserialize(WebSocketMessage raw)` → `T`.
   - `MessageType` — `WebSocketOpcode.Text` or `WebSocketOpcode.Binary` (determines which opcode is used for `SendAsync`).
2. Built-in implementations:
   - `JsonWebSocketSerializer<T>` — uses the existing JSON infrastructure.
   - `RawStringSerializer` — passthrough for plain text messages.

Implementation constraints:

1. `Serialize` returns `ReadOnlyMemory<byte>` (UTF-8 bytes), not `string`. This eliminates the double payload copy (serialize → string → UTF-8 encode) that would occur if the serializer returned `string`. The send path can write these bytes directly as frame payload.

### Step 2: Add Typed Send/Receive Extensions

**File:** `Runtime/WebSocket/WebSocketClientExtensions.cs` (new)

Required behavior:

1. Extension methods on `IWebSocketClient`:
   - `SendAsync<T>(T message, IWebSocketMessageSerializer<T> serializer, CancellationToken ct)`.
   - `ReceiveAsync<T>(IWebSocketMessageSerializer<T> serializer, CancellationToken ct)` → `T`.
   - `ReceiveAllAsync<T>(IWebSocketMessageSerializer<T> serializer, CancellationToken ct)` → `IAsyncEnumerable<T>` (depends on 18a.2).
2. These are convenience extensions — they don't add new protocol capabilities, just reduce boilerplate.

### Step 3: Add JSON Serializer Implementation

**File:** `Runtime/WebSocket/JsonWebSocketSerializer.cs` (new)

Required behavior:

1. Implement `IWebSocketMessageSerializer<T>` using the project's existing JSON infrastructure.
2. Configurable: custom serializer settings, naming policy, etc.
3. Error handling: deserialization failures throw `WebSocketException` with `SerializationFailed` error code (not `ProtocolViolation` — deserialization is an application-layer concern, not a protocol violation). Add `SerializationFailed` to `WebSocketError` enum.
4. **IL2CPP constraint:** generic types parameterized with user types need `link.xml` entries or `[Preserve]` attributes to prevent stripping. Document: constrain to `where T : class` for v1, or add a `[Preserve]` annotation requirement in the usage documentation. Include explicit `link.xml` guidance for common serialization target types.

---

## 18a.6: Connection Health & Diagnostics

**Assembly:** `TurboHTTP.WebSocket`
**Files:** 1 new, 2 modified
**Estimated Effort:** 2-3 days

### Motivation

Game networking and real-time applications need more than binary "connected/disconnected" — they need latency measurement, bandwidth estimation, and connection quality scoring to make adaptive decisions (e.g., reduce update frequency on degraded connections).

### Step 1: Implement Health Monitor

**File:** `Runtime/WebSocket/WebSocketHealthMonitor.cs` (new)

Required behavior:

1. **Latency measurement — event-driven, not polling.**
   - Add internal `OnPongReceived(TimeSpan rtt)` callback on `WebSocketConnection` (fired when a pong is received, with the RTT computed from the matching ping send timestamp). The health monitor **subscribes** to this event — no separate polling timer (avoids the timer churn identified in Phase 18 review R2-W2).
   - Maintain a rolling window of RTT samples (last 10 measurements) in a lock-protected circular buffer.
   - Expose `CurrentRtt` (TimeSpan — latest sample), `AverageRtt` (TimeSpan — mean of window), `RttJitter` (TimeSpan — standard deviation of window).
2. **Bandwidth estimation:** using metrics from 18a.3 (`BytesSent`, `BytesReceived`, `ConnectionUptime`), compute `RecentThroughput` (bytes/second) over a sliding window.
3. **Connection quality scoring:** composite score (0.0-1.0) based on:
   - RTT relative to baseline (mean of first 3 measurements): weight 0.6.
   - Pong loss rate (pings sent vs pongs received from metrics): weight 0.4.
   - ~~Message delivery success rate~~ — **removed** (TCP guarantees delivery; this metric is meaningless for TCP-based WebSocket and would mislead consumers).
4. **Quality change event:** `OnQualityChanged(ConnectionQuality)` fires when quality transitions between bands. Specify explicit thresholds:
   - `Excellent` (score ≥ 0.9)
   - `Good` (score ≥ 0.7)
   - `Fair` (score ≥ 0.5)
   - `Poor` (score ≥ 0.3)
   - `Critical` (score < 0.3)
5. **Baseline establishment:** the first 3 RTT samples establish the baseline. Quality scoring begins after baseline is established. Before baseline, quality is `Unknown`.

Implementation constraints:

1. **Thread safety of rolling window:** the circular buffer (10 `TimeSpan` entries) is accessed by: (a) the pong receipt callback (network thread) writing new samples, and (b) any thread calling `Health` property to read. Use a `lock` on the buffer since it's a small critical section with infrequent access. Do NOT use lock-free structures — the complexity is not warranted for 10 entries.
2. Health monitoring is opt-in via `WebSocketConnectionOptions.EnableHealthMonitoring` (default false) to avoid overhead when not needed.

### Step 2: Expose on Client API

**Files:** `Runtime/WebSocket/IWebSocketClient.cs` (modify), `Runtime/WebSocket/WebSocketClient.cs` (modify)

Required behavior:

1. Add `Health` property returning `WebSocketHealthSnapshot` (RTT, quality, throughput).
2. Add `OnConnectionQualityChanged` event (fires on network thread — Unity consumers must marshal via `MainThreadDispatcher`).

---

## 18a.7: Test Suite

**Assembly:** `TurboHTTP.Tests.Runtime`
**Files:** 6 new, 1 modified
**Estimated Effort:** 1 week

### Step 1: Extension Framework & Compression Tests

**File:** `Tests/Runtime/WebSocket/WebSocketExtensionTests.cs` (new)

- Extension negotiation success/failure scenarios.
- RSV bit management and collision detection.
- **RSV bit propagation:** verify RSV bits stored in `WebSocketFrame`, propagated through reader, preserved by assembler from first fragment.
- **Continuation frame RSV validation:** continuation frames with RSV1 set are rejected as protocol errors (RFC 7692 §6.1).
- `permessage-deflate` compress/decompress round-trip.
- RFC 7692 trailing bytes (`0x00 0x00 0xFF 0xFF`) handling: stripped on outbound, appended on inbound.
- Context takeover behavior (v1: always resets per message).
- Compression threshold bypass for small messages.
- Zip bomb protection: crafted compressed payload that expands beyond `MaxMessageSize` → `DecompressedMessageTooLarge` error with chunk-based detection.
- Control frame passthrough (never compressed, RSV1 unmodified).
- Graceful fallback when server rejects compression.
- `IMemoryOwner<byte>` ownership: verify returned owners have correct `Memory.Length` and are properly disposed.
- **Compression + fragmentation interaction:** large compressed message exceeding `FragmentationThreshold` is correctly fragmented with RSV1 only on first fragment.
- Extension disposal in reverse negotiation order.
- RFC 7230 §3.2.6 quoted-string parameter parsing (backslash escapes, DQUOTE delimiters).

### Step 2: Streaming Receive Tests

**File:** `Tests/Runtime/WebSocket/WebSocketStreamingReceiveTests.cs` (new)

- `await foreach` consumes messages correctly.
- Enumeration ends on connection close (returns `false`, no exception).
- Cancellation of `IAsyncEnumerable` via `CancellationToken`.
- Resilient client: enumeration blocks during reconnection, resumes after reconnect, returns `false` when exhausted.
- Concurrent enumeration rejection (`InvalidOperationException`).
- Enumerator `DisposeAsync` resets the tracking flag, allowing new enumerator creation.
- No `Microsoft.Bcl.AsyncInterfaces` dependency in compilation output.

### Step 3: Metrics Tests

**File:** `Tests/Runtime/WebSocket/WebSocketMetricsTests.cs` (new)

- Counter accuracy after N sends/receives.
- Thread-safety: concurrent counter increments from send + receive threads.
- `UncompressedBytesSent` vs `CompressedBytesSent` ratio computation.
- `CompressionRatio` is `1.0` when compression is inactive.
- `CompressionRatio` division-by-zero guard (`CompressedBytesSent == 0`).
- Snapshot immutability (values don't change after creation).
- `OnMetricsUpdated` event fires at configured interval on network thread.

### Step 4: Proxy Tunneling Tests

**File:** `Tests/Runtime/WebSocket/WebSocketProxyTests.cs` (new)

- CONNECT tunnel establishment with mock proxy.
- Proxy authentication: 407 → retry with Basic auth credentials.
- TLS over proxy tunnel for `wss://`.
- Proxy bypass list: exact hostname match and `*.domain` wildcard match.
- `ProxyCredentials` immutability.
- Proxy-specific `WebSocketError` codes: `ProxyAuthenticationRequired`, `ProxyConnectionFailed`, `ProxyTunnelFailed`.
- Security warning logged when Basic auth used over unencrypted proxy.

### Step 5: Serialization Tests

**File:** `Tests/Runtime/WebSocket/WebSocketSerializationTests.cs` (new)

- JSON round-trip: typed send → typed receive with complex object.
- Serializer always produces `ReadOnlyMemory<byte>` (UTF-8 bytes), not `string`.
- Deserialization error wraps as `WebSocketException` with `SerializationFailed` (not `ProtocolViolation`).
- Raw string serializer passthrough.
- `where T : class` constraint enforced by compiler.

### Step 6: Health Monitor Tests

**File:** `Tests/Runtime/WebSocket/WebSocketHealthMonitorTests.cs` (new)

- RTT measurement from pong receipt event (event-driven, not polling).
- Rolling window statistics (mean, jitter) over 10 samples.
- Quality scoring transitions between bands with explicit threshold values.
- Baseline establishment from first 3 samples; quality is `Unknown` before baseline.
- Quality change event fires on transition only, not on every sample.
- Scoring uses RTT (weight 0.6) and pong loss rate (weight 0.4) only — no "message delivery" factor.

### Step 7: Update Test Echo Server

**File:** `Tests/Runtime/WebSocket/WebSocketTestServer.cs` (modify)

> [!IMPORTANT]
> Adding `permessage-deflate` support to the test server is non-trivial — the server must negotiate, decompress inbound, recompress outbound. This should be estimated as a separate implementation effort within Step 7, not a trivial line item.

- Add `permessage-deflate` negotiation and streaming compression/decompression support (separate implementation task within 18a.7).
- Add configurable latency injection for health monitor testing.
- Add mock HTTP proxy mode for tunnel testing (accept CONNECT, optionally require auth).

---

## Memory Limits (Defaults)

| Setting | Default | Notes |
|---------|---------|-------|
| `CompressionThreshold` | 128 bytes | Messages below this size skip compression |
| `ClientMaxWindowBits` | 15 | LZ77 window (does not matter in v1 `no_context_takeover` mode) |
| `CompressionLevel` | 6 | Balanced speed/ratio (maps to `CompressionLevel.Optimal`) |
| **Per-connection overhead (v1)** | ~0 | `no_context_takeover`: contexts are create-use-dispose per message |
| **Per-connection overhead (future context takeover)** | ~300-600KB | Two zlib contexts (deflate ~256KB, inflate ~44KB). Deferred. |

## All Files Summary

| Sub-Phase | New Files | Modified Files | Total |
|---|---|---|---|
| 18a.1 Extension Framework & Compression | 6 | 4 | 10 |
| 18a.2 IAsyncEnumerable Receive | 1 | 3 | 4 |
| 18a.3 Connection Metrics | 2 | 3 | 5 |
| 18a.4 HTTP Proxy Tunneling | 2 | 2 | 4 |
| 18a.5 Typed Serialization | 3 | 1 | 4 |
| 18a.6 Connection Health | 1 | 2 | 3 |
| 18a.7 Test Suite | 6 | 1 | 7 |
| **Total** | **21** | **16** | **37** |

## Prioritization Matrix

| Sub-Phase | Priority | Effort | Rationale |
|---|---|---|---|
| 18a.1 Compression | **Highest** | 2w | Bandwidth savings, server compatibility. Only standardized WS extension. |
| 18a.2 IAsyncEnumerable | High | 2-3d | Idiomatic C# API, small effort, big developer experience win. |
| 18a.3 Metrics | High | 3-4d | Production requirement for any non-trivial deployment. |
| 18a.4 Proxy | Medium | 3-4d | Enterprise/mobile blocker but not universal. |
| 18a.5 Serialization | Medium | 3-4d | Convenience — apps can do this themselves, but boilerplate reduction is valuable. |
| 18a.6 Health Monitor | Medium-Low | 2-3d | Game-specific feature. Depends on 18a.3. |

## Verification Plan

1. All Phase 18 tests still pass (no regressions).
2. **Pre-implementation spikes pass** (DeflateStream flush, memory, IAsyncEnumerable IL2CPP).
3. Extension negotiation builds correct headers and rejects invalid server responses.
4. `permessage-deflate` compress → decompress round-trip equals original for text and binary.
5. Chunk-based decompression detects zip bombs before full allocation.
6. RSV bits propagate correctly: reader → frame → assembler (first fragment) → extension transform.
7. Continuation frames with RSV1 set are rejected (RFC 7692 §6.1).
8. `await foreach` streaming receive consumes and closes cleanly.
9. Metrics counters are accurate under concurrent send/receive load, including on 32-bit IL2CPP.
10. HTTP CONNECT tunnel works through mock proxy with and without Basic authentication.
11. Typed JSON serialization round-trips complex objects correctly.
12. Health monitor detects quality degradation under injected latency via event-driven RTT.
13. IL2CPP validation: `DeflateStream`, async enumerable, `ProxyCredentials` all work on iOS/Android.
14. Compression + fragmentation: large compressed message fragments correctly with RSV1 on first fragment only.
15. `IMemoryOwner<byte>` ownership semantics verified — no leaks, correct memory lengths.

## Future Considerations (Out of Scope for 18a)

1. **Full context takeover** — pending Spike 1 & 2 results. If `DeflateStream.Flush()` produces correct `Z_SYNC_FLUSH` on Unity Mono, implement in a follow-up. Otherwise, evaluate native zlib P/Invoke.
2. **HTTP/2 WebSocket (RFC 8441)** — CONNECT method with `:protocol` pseudo-header. Requires deep HTTP/2 stream multiplexing changes. Recommend separate Phase 18b.
3. **WebSocket over HTTP/3 (RFC 9220)** — depends on QUIC transport (not yet in TurboHTTP).
4. **Native zlib bindings** — for better compression performance on Mono/Unity. Evaluate after benchmarking.
5. **WebSocket multiplexing** — multiple logical channels over a single WebSocket (draft spec, not standardized).
6. **Custom extension SDK documentation** — Phase 18a delivers the framework; user-facing extension authoring guide is future work.
7. **HTTPS proxy support** — CONNECT through HTTPS proxy endpoint. Deferred from 18a.4.
8. **System proxy detection** — `UseSystemProxy` with platform-specific implementations. Deferred from 18a.4.
9. **Digest proxy authentication** — complex nonce handling. Deferred; Basic auth only for v1.
