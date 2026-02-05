# Phase 3B: HTTP/2 Protocol Implementation

**Date:** 2026-02-03
**Phase:** 3B (HTTP/2)
**Status:** Complete (pending review results)

## What Was Implemented

Full HTTP/2 protocol support: binary framing, HPACK header compression, stream multiplexing, flow control, and integration with the existing transport layer. 12 new files, 2 modified files, 8 test files + 1 test helper.

## Files Created

### Runtime Implementation (12 new files)

| File | Description |
|------|-------------|
| `Runtime/Transport/Http2/Http2Frame.cs` | Frame types enum, flags, error codes, settings IDs, Http2Frame class, Http2Constants, Http2ProtocolException, HpackDecodingException |
| `Runtime/Transport/Http2/Http2FrameCodec.cs` | Frame reader/writer over Stream. ReadFrameAsync validates frame size, WriteFrameAsync masks stream ID high bit, WritePrefaceAsync writes 24-byte connection preface |
| `Runtime/Transport/Http2/HpackStaticTable.cs` | 61-entry static HPACK table (RFC 7541 Appendix A). 1-indexed. FindMatch returns FullMatch/NameMatch/None |
| `Runtime/Transport/Http2/HpackHuffman.cs` | Full 256-entry Huffman encoding table from RFC 7541 Appendix B. Binary tree decoder built at static init. Encode/Decode/GetEncodedLength |
| `Runtime/Transport/Http2/HpackIntegerCodec.cs` | HPACK prefix-coded integer encoding/decoding (RFC 7541 Section 5.1). Overflow protection at m > 28 |
| `Runtime/Transport/Http2/HpackDynamicTable.cs` | FIFO cache with eviction. Entry size = name.Length + value.Length + 32. `_entries[0]` = newest = HPACK index 62 |
| `Runtime/Transport/Http2/HpackEncoder.cs` | Header encoder with static/dynamic table lookup, Huffman when shorter. Sensitive headers use never-indexed representation. Latin-1 encoding |
| `Runtime/Transport/Http2/HpackDecoder.cs` | Header decoder. Bounds checks use headerBlockEnd not data.Length. Dynamic table size update validated against settings limit. Latin-1 decoding |
| `Runtime/Transport/Http2/Http2Settings.cs` | Connection settings management. SerializeClientSettings sends ENABLE_PUSH=0, MAX_CONCURRENT_STREAMS=100. ParsePayload validates payload length % 6 |
| `Runtime/Transport/Http2/Http2Stream.cs` | Stream state machine (Idle→Open→HalfClosedLocal→Closed). WindowSize uses Interlocked. PendingEndStream for deferred CONTINUATION completion. TaskCompletionSource with RunContinuationsAsynchronously |
| `Runtime/Transport/Http2/Http2Connection.cs` | ~580 lines. Full connection lifecycle: InitializeAsync (preface + SETTINGS + ACK), SendRequestAsync (stream creation, HPACK encode, HEADERS/DATA), ReadLoopAsync (background frame dispatch). Handles all frame types including GOAWAY, RST_STREAM, PING, PUSH_PROMISE rejection |
| `Runtime/Transport/Http2/Http2ConnectionManager.cs` | Per-host connection cache with ConcurrentDictionary. GetIfExists fast path, GetOrCreateAsync with per-key SemaphoreSlim for thundering herd prevention |

### Modified Files (2)

| File | Changes |
|------|---------|
| `Runtime/Transport/Tcp/TcpConnectionPool.cs` | Added NegotiatedAlpnProtocol property, TransferOwnership() method on ConnectionLease, ALPN protocol list passed to TLS handshake |
| `Runtime/Transport/RawSocketTransport.cs` | Added Http2ConnectionManager, HTTP/2 fast path before pool, ALPN-based protocol routing, HTTP/2 handling in stale-retry path |

### New Supporting File (1)

| File | Description |
|------|-------------|
| `Runtime/Transport/AssemblyInfo.cs` | `[assembly: InternalsVisibleTo("TurboHTTP.Tests.Runtime")]` |

### Test Files (8 + 1 helper)

| File | Coverage |
|------|----------|
| `Tests/Runtime/Transport/Http2/HpackIntegerCodecTests.cs` | RFC 7541 C.1 vectors, round-trip, overflow, offset advancement |
| `Tests/Runtime/Transport/Http2/HpackStaticTableTests.cs` | Get, FindMatch, boundary checks, entry count |
| `Tests/Runtime/Transport/Http2/HpackHuffmanTests.cs` | RFC vectors (www.example.com, no-cache, custom-key, custom-value), round-trip, padding |
| `Tests/Runtime/Transport/Http2/HpackDynamicTableTests.cs` | Add, eviction, size tracking, FindMatch, SetMaxSize |
| `Tests/Runtime/Transport/Http2/Http2FrameCodecTests.cs` | Round-trip all frame types, stream ID masking, zero payload, size validation, preface |
| `Tests/Runtime/Transport/Http2/HpackEncoderDecoderTests.cs` | Round-trip, dynamic table reuse, sensitive headers, Latin-1 preservation, size update validation |
| `Tests/Runtime/Transport/Http2/Http2ConnectionTests.cs` | Connection init (preface, settings, ACK), GET/POST lifecycle, GOAWAY, RST_STREAM, PUSH_PROMISE rejection, CONTINUATION ordering, padding validation, stream ID progression |
| `Tests/Runtime/Transport/Http2/Http2FlowControlTests.cs` | WINDOW_UPDATE (connection/stream), zero increment, overflow, data sending respects windows, blocking/unblocking, window update auto-send |
| `Tests/Runtime/Transport/Http2/Helpers/TestDuplexStream.cs` | Bidirectional in-memory stream for Http2Connection tests. Uses BlockingCollection-backed byte queues |

## Decisions Made

1. **Latin-1 encoding throughout HPACK** — Both encoder and decoder use `EncodingHelper.Latin1` (not ASCII) to preserve obs-text bytes per RFC 7230/7541. This ensures byte-level fidelity for non-ASCII header values.

2. **Write lock per DATA frame, not per body** — SendDataAsync acquires/releases the write lock for each DATA frame rather than holding it for the entire body. This prevents deadlock when the read loop needs to send control frames (PING ACK, SETTINGS ACK) while a large body is being transmitted.

3. **PendingEndStream flag on Http2Stream** — When HEADERS has END_STREAM but not END_HEADERS, completion is deferred until the final CONTINUATION frame arrives. The PendingEndStream flag tracks this.

4. **Stream ID overflow guard** — Uses `Interlocked.Add` with post-check. If the stream ID wraps negative, throws UHttpException asking the caller to close and reopen the connection.

5. **Thundering herd prevention in Http2ConnectionManager** — Per-key SemaphoreSlim ensures only one caller creates a new HTTP/2 connection per host. Losers of the race dispose their orphaned TLS stream.

6. **TestDuplexStream design** — Uses BlockingCollection<byte[]> internally rather than System.IO.Pipelines (not available in Unity 2021.3). Each endpoint writes chunks to the other's read queue.

7. **Best-effort GOAWAY on Dispose** — Http2Connection.Dispose sends a GOAWAY with NO_ERROR and waits up to 1 second. Failure is silently ignored. The read loop is also given 2 seconds to complete.

## Review Fixes Incorporated During Implementation

All issues from prior review passes were addressed:

- **[GPT-1]** Control frame writes acquire write lock
- **[GPT-2]** String bounds check uses headerBlockEnd, not data.Length
- **[GPT-3]** Dynamic table size update validated against SETTINGS limit
- **[GPT-4]** DATA exceeding recv window → FLOW_CONTROL_ERROR
- **[GPT-5]** Padding bounds validation on DATA and HEADERS frames
- **[GPT-7]** SETTINGS ACK with non-zero payload → FRAME_SIZE_ERROR
- **[GPT-8]** Stream state transitions tested for POST requests
- **[A3]** CONTINUATION ordering enforcement (wrong stream, unexpected, non-CONTINUATION while expecting)
- **[A4]** Dispose disposes underlying stream
- **[A6]** Early cancellation check prevents stream leak
- **[Q1]** Latin-1 decoding in HpackDecoder
- **[R2-1]** Write lock released between DATA frames (deadlock prevention)
- **[R2-3]** Window overflow checked with long arithmetic before Interlocked.Add
- **[R2-4]** FailAllStreams sets _goawayReceived + cancels CTS; SendRequestAsync re-checks after adding to _activeStreams
- **[R2-5]** SendRstStreamAsync catches ObjectDisposedException
- **[R2-6]** Stream ID overflow guard
- **[R2-7]** PendingEndStream for CONTINUATION completion
- **[R2-9]** Orphaned TLS stream disposed in thundering herd scenario
- **[R2-10]** Encoder uses Latin-1 (matching decoder)

## Post-Implementation Review Fixes (R3)

Issues identified by external reviewer and specialist agent post-implementation reviews:

- **[R3-1]** Missing stream-level receive window tracking and WINDOW_UPDATE — added `RecvWindowSize` to `Http2Stream`, stream-level window accounting and WINDOW_UPDATE emission in `HandleDataFrameAsync`
- **[R3-2]** Header table size update compared against constant not current state — added `_lastHeaderTableSize` tracking field, update encoder on any change including back-to-default
- **[R3-3]** ReadLoop used `_remoteSettings.MaxFrameSize` for inbound validation (peer's receive limit) — changed to `_localSettings.MaxFrameSize` (what we're willing to receive)
- **[R3-4]** `HpackIntegerCodec.Decode` checked `data.Length` not header block end — added `end` parameter, all decoder callsites pass `headerBlockEnd`
- **[R3-5]** `InitializeAsync` failure leaked connection/stream — added try/catch in `Http2ConnectionManager.GetOrCreateAsync` to dispose on failure
- **[R3-6]** HPACK decoder tracked `seenHeaderField` but never enforced size-update ordering — added throw when dynamic table size update appears after header field
- **[R3-7]** SETTINGS ACK accepted without checking stream ID — moved `frame.StreamId != 0` check before ACK handling
- **[R3-8]** DATA flow control used `dataPayload.Length` (stripped) instead of `frame.Length` (includes padding per RFC 7540 Section 6.1)
- **[R3-rename]** `Http2Stream.WindowSize` → `SendWindowSize`, `AdjustWindowSize` → `AdjustSendWindowSize` for clarity alongside new `RecvWindowSize`

## External Review Fixes (R4)

Issues identified by external reviewer and specialist agent reviews post-R3:

- **[R4-1]** HTTP/2 stale connection retry could duplicate non-idempotent requests — added `request.Method.IsIdempotent()` guard to H2 retry `when` clause in `RawSocketTransport`
- **[R4-2]** HPACK dynamic table size used `string.Length` (char count) instead of octet length — replaced with `EncodingHelper.Latin1.GetByteCount()` via `EntrySize()` helper in `HpackDynamicTable`
- **[R4-3]** HPACK decoder `_expectingSizeUpdate` flag was set but never enforced — `Decode()` now checks at end of header block and throws `COMPRESSION_ERROR` if expected size update was missing
- **[R4-4]** `te` header not filtered for HTTP/2 — `SendRequestAsync` now filters `te` header, only allowing `te: trailers` per RFC 7540 Section 8.1.2.2
- **[R4-5]** Missing `:status` validation — `DecodeAndSetHeaders` now validates `:status` is present and numeric (100-999); missing/invalid `:status` fails the stream
- **[R4-6]** `SETTINGS_MAX_CONCURRENT_STREAMS` parsed but not enforced — `SendRequestAsync` now checks `_activeStreams.Count` against limit before allocating a stream
- **[R4-7]** `SETTINGS_MAX_HEADER_LIST_SIZE` not enforced on inbound headers — `DecodeAndSetHeaders` computes header list size and rejects responses exceeding the limit
- **[R4-8]** Protocol errors in read loop did not send GOAWAY — `ReadLoopAsync` now catches `Http2ProtocolException` and sends GOAWAY with appropriate error code before failing streams
- **[R4-compile-1]** `Http11ResponseParser.ReadBodyByContentLengthOrEnd` used `out` parameter with `async` — refactored to return `(byte[] Body, bool UsedReadToEnd)` tuple
- **[R4-compile-2]** `UHttpClient.Options` property conflicted with `Options()` method — renamed property to `ClientOptions`, updated `UHttpRequestBuilder` references

## Post-R4 Review Fixes (R5)

Issues identified by R4 verification review agents and compilation fixes:

- **[R5-M3]** `HpackDecodingException` not mapped to `Http2ProtocolException(CompressionError)` — HPACK decoding errors in `ReadLoopAsync` fell through to the generic `catch (Exception)` which called `FailAllStreams` without sending GOAWAY. Per RFC 7540 Section 4.3, HPACK errors must send GOAWAY with COMPRESSION_ERROR. Added dedicated `catch (HpackDecodingException)` handler before `catch (Http2ProtocolException)` that wraps into `Http2ProtocolException(CompressionError)` and sends GOAWAY.
- **[R5-M6]** `SendDataAsync` continued sending DATA frames after server sent RST_STREAM — added `stream.ResponseTcs.Task.IsCompleted` check at top of send loop to break early when the stream has been reset or cancelled by the read loop.
- **[R5-M1/M2]** `MaxConcurrentStreams` check is not atomic with stream creation — documented known race condition in code comment, deferred to Phase 10 (SemaphoreSlim-based gating). Servers handle this gracefully with REFUSED_STREAM.
- **[R5-compile-1]** `EncodingHelper.Latin1Encoding.GetBytes()` had CS0029 error — ternary `(byte)(c < 256 ? c : (byte)'?')` resolved to `char`. Fixed to `c < 256 ? (byte)c : (byte)'?'` in both `GetBytes` overloads.
- **[R5-compile-2]** Test files used `HttpMethod.Get`/`HttpMethod.Post` instead of `HttpMethod.GET`/`HttpMethod.POST` — fixed in `Http2FlowControlTests.cs` and `Http2ConnectionTests.cs`.
- **[R5-compile-3]** Test files used `new RequestContext()` but constructor requires `UHttpRequest` — fixed all test callsites to pass the request object.
- **[R5-test-1]** Added `HpackDecodingError_SendsGoAwayCompressionError` test — verifies HPACK index-0 error triggers GOAWAY with COMPRESSION_ERROR (0x9).

## Test Fixes and Refinements (R6)

Issues identified by test execution and external review:

- **[R6-P2]** Stale H2 connection only removed from manager on idempotent retry — restructured `RawSocketTransport.cs` stale connection handling: now removes from manager on ANY failure (not just idempotent), then only retries if `request.Method.IsIdempotent()`. Non-idempotent requests re-throw after removing dead connection.
- **[R6-link.xml]** Removed `System.Text.Encoding.CodePages` preserve from `link.xml` — unnecessary bundle size overhead since `EncodingHelper` already has custom `Latin1Encoding` fallback for IL2CPP code stripping.
- **[R6-huffman]** `HpackHuffman.Decode` threw "Invalid Huffman code sequence" on RoundTrip_AllByteValues — decoder hit null node during final byte's padding bits (EOS prefix). Fixed by detecting when we're in the final byte's padding region: if `node == null && isLastByte && bitVal == 1`, return early after verifying remaining bits are all 1s.
- **[R6-test-exceptions]** Tests using `Assert.ThrowsAsync<Exception>` failed in Unity NUnit (exact type matching) — replaced with try-catch patterns allowing specific exception types (`Http2ProtocolException`, `ObjectDisposedException`, `OperationCanceledException`) in:
  - `ContinuationFrame_WrongStream_ConnectionDies`
  - `NonContinuation_WhileExpectingContinuation_ConnectionDies`
  - `DataFrame_PaddingLengthExceedsPayload_ConnectionDies`
  - `HpackDecodingError_SendsGoAwayCompressionError`
  - `Dispose_FailsAllActiveStreams`
- **[R6-flowcontrol]** `DataReceiving_SendsWindowUpdate` and `StreamLevelRecvWindow_SendsStreamWindowUpdate` timed out — WINDOW_UPDATE threshold condition is `< 65535/2 = 32767` (strict less-than), so 32768 bytes was not enough. Updated tests to send 49152 bytes (3 × 16384) to ensure window drops below threshold.

## External Review Fixes (R7)

Issues identified by external reviewer:

- **[R7-data-before-headers]** `HandleDataFrameAsync` accepted DATA frames even if HEADERS hadn't been received. Per RFC 7540 Section 8.1, a response MUST start with HEADERS. If DATA arrives before HEADERS, this is a protocol error. Added validation in `HandleDataFrameAsync` to check `stream.HeadersReceived` — if false, fails the stream with `Http2ProtocolException(ProtocolError)`, sends RST_STREAM, removes from `_activeStreams`, and disposes the stream. Connection recv window is still decremented to maintain flow control accounting.
- **[R7-test]** Added `DataFrame_BeforeHeaders_SendsRstStream` test to verify DATA before HEADERS results in RST_STREAM with PROTOCOL_ERROR (0x1).
- **[R7-huffman-overflow]** `HpackHuffman.Encode` had `bitBuffer` overflow on long inputs. After emitting each byte, the high bits remained in `bitBuffer`. For 256+ byte inputs, `bitBuffer << bitLength` would overflow `long`, corrupting the encoded stream. Fixed by masking `bitBuffer &= (1L << bitCount) - 1` after each byte emit to keep only the remaining bits.
- **[R7-race-fix]** Infrastructure agent identified race condition in DATA-before-HEADERS fix: `Fail()` triggers user continuations which could call `Dispose()` concurrently, leading to double-dispose. Fixed by reordering to `TryRemove()` before `Fail()` with `if` guard, matching the safe pattern used elsewhere in the codebase.

## Post-R7 Review Fixes (R8)

- **[R8-settings-overflow]** Guarded HTTP/2 SETTINGS values that are defined as `uint` to prevent `int` overflow. `HeaderTableSize`, `MaxConcurrentStreams`, and `MaxHeaderListSize` now clamp to `int.MaxValue` when peers send values above `int.MaxValue`.

## Memory and Performance Fixes (R9)

Issues identified from codebase review for memory efficiency and performance:

### R9-1: Frame Codec Memory Allocations

**Problem:** `Http2FrameCodec` allocated new 9-byte arrays for every frame read/write operation, causing high GC pressure in hot paths.

**Fix:** Added reusable pre-allocated buffers:
- `_readHeaderBuffer` (9 bytes) - reused by single-threaded read loop
- `_writeHeaderBuffer` (9 bytes) - reused by write operations under lock

For payload buffers > 256 bytes, now uses `ArrayPool<byte>.Shared` to rent buffers, reducing allocations for large DATA/HEADERS frames.

**Files modified:** `Runtime/Transport/Http2/Http2FrameCodec.cs`

### R9-2: HeaderBlockBuffer Byte-by-Byte Copying

**Problem:** `Http2Stream.AppendHeaderBlock()` used `List<byte>.Add()` in a loop, causing O(n) individual Add operations with List growth overhead.

**Fix:** Changed `HeaderBlockBuffer` from `List<byte>` to `MemoryStream`:
- `AppendHeaderBlock()` now uses `MemoryStream.Write()` for bulk copy
- Added `ClearHeaderBlock()` helper method that uses `SetLength(0)`
- Added disposal in `Http2Stream.Dispose()`

**Files modified:** `Runtime/Transport/Http2/Http2Stream.cs`, `Runtime/Transport/Http2/Http2Connection.cs`

### R9-3: Unbounded Response Body Growth

**Problem:** `Http2Stream.ResponseBody` (MemoryStream) had no size limit, allowing malicious or misconfigured servers to cause unbounded memory growth.

**Fix:** Added `MaxResponseBodySize` setting to `Http2Settings`:
- Default: 100 MB (configurable)
- Set to 0 for unlimited
- Enforced in `HandleDataFrameAsync()` - exceeding limit fails stream with RST_STREAM(CANCEL)

**Files modified:** `Runtime/Transport/Http2/Http2Settings.cs`, `Runtime/Transport/Http2/Http2Connection.cs`

### R9-4: MAX_CONCURRENT_STREAMS Race Documentation

**Issue:** Non-atomic check between `_activeStreams.Count` check and stream creation allows brief limit exceedance under high concurrency.

**Status:** Already documented in code comments (lines 131-135 in Http2Connection.cs). Deferred to Phase 10 for proper SemaphoreSlim-based gating. Server handles gracefully with REFUSED_STREAM.

### R9-5: Phase 3C Plan (BouncyCastle TLS Fallback)

**Issue:** SslStream ALPN negotiation via reflection may fail on IL2CPP/AOT platforms (iOS, Android) due to code stripping or platform TLS differences.

**Resolution:** Created `docs/phases/phase-03c-bouncy-castle-tls.md` documenting:
- Optional BouncyCastle TLS fallback module (pure C#, no reflection)
- `ITlsProvider` abstraction for pluggable TLS backends
- `TlsProviderSelector` with Auto/SslStream/BouncyCastle modes
- Platform validation matrix
- Integration approach following BestHTTP pattern

## Summary of R9 Changes

| Category | Issue | Severity | Resolution |
|----------|-------|----------|------------|
| Memory | Frame header allocations | Medium | Reusable buffers |
| Memory | Payload allocations | Medium | ArrayPool for large payloads |
| Memory | Byte-by-byte header copy | High | MemoryStream bulk write |
| Security | Unbounded response body | Medium | MaxResponseBodySize limit |
| Docs | MAX_CONCURRENT_STREAMS race | High | Documented, deferred to Phase 10 |
| Platform | ALPN on IL2CPP | High | Phase 3C plan with BouncyCastle fallback |
