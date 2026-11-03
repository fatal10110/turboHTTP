# Phase 18a.7: Test Suite

**Depends on:** All above (18a.1-18a.6)
**Assembly:** `TurboHTTP.Tests.Runtime`
**Files:** 6 new, 1 modified
**Estimated Effort:** 1 week

---

## Step 1: Extension Framework & Compression Tests

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

---

## Step 2: Streaming Receive Tests

**File:** `Tests/Runtime/WebSocket/WebSocketStreamingReceiveTests.cs` (new)

- `await foreach` consumes messages correctly.
- Enumeration ends on connection close (returns `false`, no exception).
- Cancellation of `IAsyncEnumerable` via `CancellationToken`.
- Resilient client: enumeration blocks during reconnection, resumes after reconnect, returns `false` when exhausted.
- Concurrent enumeration rejection (`InvalidOperationException`).
- Enumerator `DisposeAsync` resets the tracking flag, allowing new enumerator creation.
- No `Microsoft.Bcl.AsyncInterfaces` dependency in compilation output.

---

## Step 3: Metrics Tests

**File:** `Tests/Runtime/WebSocket/WebSocketMetricsTests.cs` (new)

- Counter accuracy after N sends/receives.
- Thread-safety: concurrent counter increments from send + receive threads.
- `UncompressedBytesSent` vs `CompressedBytesSent` ratio computation.
- `CompressionRatio` is `1.0` when compression is inactive.
- `CompressionRatio` division-by-zero guard (`CompressedBytesSent == 0`).
- Snapshot immutability (values don't change after creation).
- `OnMetricsUpdated` event fires at configured interval on network thread.

---

## Step 4: Proxy Tunneling Tests

**File:** `Tests/Runtime/WebSocket/WebSocketProxyTests.cs` (new)

- CONNECT tunnel establishment with mock proxy.
- Proxy authentication: 407 → retry with Basic auth credentials.
- TLS over proxy tunnel for `wss://`.
- Proxy bypass list: exact hostname match and `*.domain` wildcard match.
- `ProxyCredentials` immutability.
- Proxy-specific `WebSocketError` codes: `ProxyAuthenticationRequired`, `ProxyConnectionFailed`, `ProxyTunnelFailed`.
- Security warning logged when Basic auth used over unencrypted proxy.

---

## Step 5: Serialization Tests

**File:** `Tests/Runtime/WebSocket/WebSocketSerializationTests.cs` (new)

- JSON round-trip: typed send → typed receive with complex object.
- Serializer always produces `ReadOnlyMemory<byte>` (UTF-8 bytes), not `string`.
- Deserialization error wraps as `WebSocketException` with `SerializationFailed` (not `ProtocolViolation`).
- Raw string serializer passthrough.
- `where T : class` constraint enforced by compiler.

---

## Step 6: Health Monitor Tests

**File:** `Tests/Runtime/WebSocket/WebSocketHealthMonitorTests.cs` (new)

- RTT measurement from pong receipt event (event-driven, not polling).
- Rolling window statistics (mean, jitter) over 10 samples.
- Quality scoring transitions between bands with explicit threshold values.
- Baseline establishment from first 3 samples; quality is `Unknown` before baseline.
- Quality change event fires on transition only, not on every sample.
- Scoring uses RTT (weight 0.6) and pong loss rate (weight 0.4) only — no "message delivery" factor.

---

## Step 7: Update Test Echo Server

**File:** `Tests/Runtime/WebSocket/WebSocketTestServer.cs` (modify)

> [!IMPORTANT]
> Adding `permessage-deflate` support to the test server is non-trivial — the server must negotiate, decompress inbound, recompress outbound. This should be estimated as a separate implementation effort within Step 7, not a trivial line item.

- Add `permessage-deflate` negotiation and streaming compression/decompression support (separate implementation task within 18a.7).
- Add configurable latency injection for health monitor testing.
- Add mock HTTP proxy mode for tunnel testing (accept CONNECT, optionally require auth).

---

## Verification Criteria

1. All Phase 18 tests still pass (no regressions).
2. Extension negotiation builds correct headers and rejects invalid server responses.
3. `permessage-deflate` compress → decompress round-trip equals original for text and binary.
4. Chunk-based decompression detects zip bombs before full allocation.
5. RSV bits propagate correctly: reader → frame → assembler (first fragment) → extension transform.
6. Continuation frames with RSV1 set are rejected (RFC 7692 §6.1).
7. `await foreach` streaming receive consumes and closes cleanly.
8. Metrics counters are accurate under concurrent send/receive load, including on 32-bit IL2CPP.
9. HTTP CONNECT tunnel works through mock proxy with and without Basic authentication.
10. Typed JSON serialization round-trips complex objects correctly.
11. Health monitor detects quality degradation under injected latency via event-driven RTT.
12. Compression + fragmentation: large compressed message fragments correctly with RSV1 on first fragment only.
13. `IMemoryOwner<byte>` ownership semantics verified — no leaks, correct memory lengths.
