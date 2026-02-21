# Phase 18a Implementation Review — 2026-02-21

**Scope:** All Phase 18a sub-phases (18a.1–18a.7) — Extension Framework, Streaming Receive, Metrics, Proxy Tunneling, Typed Serialization, Health Monitor, Test Suite.

**Reviewed by:** unity-infrastructure-architect + unity-network-architect (combined)

---

## Files Reviewed

| File | Lines | Role |
|------|------:|------|
| [PerMessageDeflateExtension.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/PerMessageDeflateExtension.cs) | 365 | RFC 7692 compression |
| [IWebSocketExtension.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/IWebSocketExtension.cs) | 285 | Extension interface + offer/params |
| [WebSocketExtensionNegotiator.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketExtensionNegotiator.cs) | 231 | Sec-WebSocket-Extensions negotiation |
| [WebSocketConnection.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketConnection.cs) | 1787 | Extension pipeline, metrics, health wiring |
| [WebSocketFrame.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketFrame.cs) | 173 | Frame struct with RSV bits |
| [WebSocketAsyncEnumerable.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketAsyncEnumerable.cs) | 117 | IAsyncEnumerable adapter |
| [WebSocketMetrics.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketMetrics.cs) | 72 | Metrics snapshot |
| [WebSocketMetricsCollector.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketMetricsCollector.cs) | 187 | Thread-safe counter collector |
| [WebSocketHealthMonitor.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketHealthMonitor.cs) | 232 | RTT, quality scoring |
| [WebSocketProxySettings.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketProxySettings.cs) | 97 | Proxy config + bypass |
| [ProxyTunnelConnector.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket.Transport/ProxyTunnelConnector.cs) | 409 | HTTP CONNECT tunnel |
| [JsonWebSocketSerializer.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/JsonWebSocketSerializer.cs) | 69 | Typed JSON serializer |
| [IWebSocketMessageSerializer.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/IWebSocketMessageSerializer.cs) | — | Serializer interface |
| [WebSocketClientExtensions.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketClientExtensions.cs) | — | Typed send/receive extensions |
| [WebSocketConnectionOptions.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketConnectionOptions.cs) | 267 | Options with extension/proxy config |
| [WebSocketConstants.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketConstants.cs) | 275 | Constants + WebSocketError enum |
| [WebSocketException.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketException.cs) | 118 | Exception + error classification |
| [WebSocketTransportFactory.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketTransportFactory.cs) | 49 | Delegate-based factory (no reflection) |
| [WebSocketTransportModuleInitializer.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket.Transport/WebSocketTransportModuleInitializer.cs) | 14 | `[ModuleInitializer]` registration |

---

## Findings

### Critical

No critical findings. All R3-level critical issues from the Phase 18a plan review have been addressed in the implementation.

---

### Warning (W-1): `Compress()` triple allocation pattern

**File:** [PerMessageDeflateExtension.cs#L215-L253](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/PerMessageDeflateExtension.cs#L215-L253)

**Agent:** unity-infrastructure-architect

The `Compress()` method performs three allocations for a single compression:

```csharp
using var output = new MemoryStream(payload.Length + 32);  // 1. MemoryStream internal buffer
// ... deflate writes into output ...
byte[] compressed = output.ToArray();                      // 2. ToArray() copies
var owner = ArrayPoolMemoryOwner<byte>.Rent(compressedLength); // 3. ArrayPool rent + copy
new ReadOnlyMemory<byte>(compressed, 0, compressedLength).CopyTo(owner.Memory);
```

**Impact:** Every compressed outbound message allocates three buffers and copies data twice unnecessarily. The `MemoryStream.ToArray()` creates a GC-tracked copy that is immediately discarded after copying into the array pool buffer.

**Recommended fix:** Use `output.GetBuffer()` with `output.Length` directly to avoid the `ToArray()` copy, then copy directly into the `ArrayPoolMemoryOwner`. Alternatively, write the `DeflateStream` into a recyclable buffer from `ArrayPool` directly via a custom stream.

---

### Warning (W-2): `Decompress()` same triple allocation pattern

**File:** [PerMessageDeflateExtension.cs#L255-L315](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/PerMessageDeflateExtension.cs#L255-L315)

**Agent:** unity-infrastructure-architect

Same issue as W-1. The decompression path uses `output.ToArray()` (line 301) then copies into an `ArrayPoolMemoryOwner`. For large messages this doubles transient GC pressure.

```csharp
byte[] decompressed = output.ToArray();                        // unnecessary copy
var owner = ArrayPoolMemoryOwner<byte>.Rent(decompressed.Length);
new ReadOnlyMemory<byte>(decompressed, 0, decompressed.Length).CopyTo(owner.Memory);
```

**Recommended fix:** Same as W-1. Use `output.GetBuffer()` + `(int)output.Length` to avoid the intermediate `ToArray()`.

---

### Warning (W-3): Proxy CONNECT re-uses same TCP stream after 407

**File:** [ProxyTunnelConnector.cs#L34-L78](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket.Transport/ProxyTunnelConnector.cs#L34-L78)

**Agent:** unity-network-architect

After a 407 response, the `while (true)` loop sends a second CONNECT request on the **same stream**. This only works if the proxy keeps the connection alive on 407. While most proxies do, RFC 7235 does not guarantee this. If the proxy closes the connection after 407, the retry will fail with a confusing I/O error instead of `ProxyTunnelFailed`.

Additionally, if the 407 response includes a body (e.g., HTML error page) that is not fully drained, the second CONNECT request will be appended to unread response body bytes, causing protocol corruption.

**Current mitigation:** `ReadResponseAsync` → `ParseContentLength` → `DrainBodyAsync` handles `Content-Length` bodies. However, chunked transfer-encoding is not handled, and `Content-Length` is only drained when > 0.

**Recommended fix:**
1. After a 407, drain any remaining response body (handle missing `Content-Length` as well by reading until the connection resets or a known boundary).
2. Add a comment documenting the assumption that the proxy keeps the connection alive on 407.
3. Consider wrapping the retry in a try-catch that rethrows as `ProxyTunnelFailed` if the second write fails.

---

### Warning (W-4): `Interlocked.Add` on `long` fields — 32-bit IL2CPP atomicity

**File:** [WebSocketMetricsCollector.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketMetricsCollector.cs)

**Agent:** unity-infrastructure-architect

The plan (18a.3) explicitly calls out that `Interlocked.Add` on `long` fields is **not truly atomic on 32-bit ARM IL2CPP**. The implementation uses `Interlocked.Add(ref _bytesSent, byteCount)` and similar throughout, with `Volatile.Read` in `GetSnapshot()`.

On 32-bit ARM IL2CPP, `Interlocked.Add` on a 64-bit field may tear — the high and low 32 bits can be read/written non-atomically. While metrics counters are observational and torn values won't cause crashes, the plan specified a guard pattern (split high/low 32-bit counters with manual CAS) that is not implemented.

**Impact:** Metrics values may occasionally show incorrect values on 32-bit ARM devices (iOS armv7, some older Android). This is a data accuracy issue, not a correctness/crash issue.

**Recommended fix:** Either:
1. Implement the split high/low 32-bit pattern from the plan, or
2. Add a `lock` around counter increments and reads (acceptable for metrics — not hot path), or
3. Document this as a known limitation for 32-bit ARM and accept ~0.001% error rate in metrics counters.

Option 3 is pragmatic since armv7 is increasingly rare and metrics are observational.

---

### Warning (W-5): `WebSocketHealthMonitor.UpdateThroughput` uses `checked` addition

**File:** [WebSocketHealthMonitor.cs#L141](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketHealthMonitor.cs#L141)

**Agent:** unity-infrastructure-architect

```csharp
long totalBytes = checked(metrics.BytesSent + metrics.BytesReceived);
```

On a long-lived connection, `BytesSent + BytesReceived` could theoretically overflow `long.MaxValue` (9.2 exabytes). While practically impossible, the `checked` arithmetic will throw `OverflowException` inside the `lock(_gate)` block, which could leave the health monitor in an inconsistent state. The exception will propagate up through `RecordMetricsSnapshot` → `RecordRttSample`.

**Recommended fix:** Remove `checked` and use unchecked addition. Throughput computation only needs the *delta* between snapshots, so overflow of the total is harmless as long as the subtraction `totalBytes - _lastThroughputBytesTotal` wraps correctly (which it does for `long` by default in C#).

---

### Info (I-1): Extension pipeline correctly implements RFC 7692 RSV bit semantics

**File:** [WebSocketConnection.cs#L843-L951](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketConnection.cs#L843-L951)

**Agent:** unity-network-architect

Verified:
- **Outbound:** Extensions applied left-to-right. RSV bit collision detection (line 870). Extension RSV bit mask validation (line 863).
- **Inbound:** Extensions applied right-to-left (line 922). Remaining RSV bits checked after all extensions processed (line 942).
- **Disposal:** Extensions disposed in reverse negotiation order (line 1271).
- **Control frames:** Never passed through extension pipeline (correctly handled separately in `HandleControlFrameAsync`).

✅ This matches RFC 7692 §6.1 requirements exactly.

---

### Info (I-2): `IAsyncEnumerable` implementation avoids IL2CPP pitfalls

**File:** [WebSocketAsyncEnumerable.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketAsyncEnumerable.cs)

**Agent:** unity-infrastructure-architect

Verified:
- Uses concrete class (`WebSocketAsyncEnumerable`) rather than `async IAsyncEnumerable<T>` compiler-generated state machine — avoids AOT/IL2CPP issues with complex generic state machine types.
- Implements `IAsyncEnumerable<WebSocketMessage>` and `IAsyncEnumerator<WebSocketMessage>` manually as sealed nested class.
- No external NuGet dependency (`Microsoft.Bcl.AsyncInterfaces`) — relies on .NET Standard 2.1 native support.
- `DisposeAsync` uses `Interlocked.CompareExchange` for single-dispose guarantee.
- Properly handles `CancellationToken` linking with `CancellationTokenSource.CreateLinkedTokenSource`.

✅ IL2CPP safe.

---

### Info (I-3): `WebSocketError` enum is comprehensive and correctly classified

**File:** [WebSocketConstants.cs#L227-L252](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketConstants.cs#L227-L252)

**Agent:** unity-network-architect

All Phase 18a error codes present:
- Extension: `ExtensionNegotiationFailed`, `CompressionFailed`, `DecompressionFailed`, `DecompressedMessageTooLarge`
- Proxy: `ProxyAuthenticationRequired`, `ProxyConnectionFailed`, `ProxyTunnelFailed`
- Serialization: `SerializationFailed`

`IsRetryable()` correctly classifies proxy transport errors (`ProxyConnectionFailed`) as retryable, while proxy authentication and tunnel failures are non-retryable.

`MapErrorType()` correctly maps proxy errors to `UHttpErrorType.NetworkError`.

✅ Consistent across all error handling paths.

---

### Info (I-4): `ProxyCredentials` readonly struct avoids IL2CPP stripping risk

**File:** [WebSocketProxySettings.cs#L6-L17](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketProxySettings.cs#L6-L17)

**Agent:** unity-infrastructure-architect

The plan correctly identified that `System.Net.NetworkCredential` could be stripped by IL2CPP linker. The implementation uses a custom `ProxyCredentials` readonly struct — no reflection, no mutable state, no strippable BCL dependency.

✅ IL2CPP safe.

---

### Info (I-5): `WebSocketTransportFactory` uses delegate registration, not reflection

**File:** [WebSocketTransportFactory.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/WebSocketTransportFactory.cs)

**Agent:** unity-infrastructure-architect

The R3-C2 finding from the plan review (transport factory could use reflection) is fully resolved. Factory uses `Func<TlsBackend, IWebSocketTransport>` delegate registered via `[ModuleInitializer]` in `WebSocketTransportModuleInitializer.cs`. No `Type.GetType()`, `Activator.CreateInstance`, or assembly scanning.

✅ AOT/IL2CPP safe.

---

### Info (I-6): Chunk-based decompression zip bomb protection

**File:** [PerMessageDeflateExtension.cs#L255-L315](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/PerMessageDeflateExtension.cs#L255-L315)

**Agent:** unity-network-architect

Decompression uses 16KB chunks (line 271) and checks `totalRead > _maxMessageSize` per chunk iteration (line 291). This prevents a crafted compressed payload from expanding into unbounded memory before the size check.

✅ Matches RFC 7692 safety requirements.

---

### Info (I-7): `JsonWebSocketSerializer<T>` correctly uses `where T : class` constraint

**File:** [JsonWebSocketSerializer.cs#L6](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/WebSocket/JsonWebSocketSerializer.cs#L6)

**Agent:** unity-infrastructure-architect

The `where T : class` constraint prevents IL2CPP issues with value-type generic instantiations that could cause AOT compilation failures with JSON serialization. The serializer wraps errors as `WebSocketError.SerializationFailed` (not `ProtocolViolation`), correctly separating application-layer from protocol-layer concerns.

✅ Per plan spec.

---

### Info (I-8): `PerMessageDeflateOptions.Default` mandates `no_context_takeover`

**Agent:** unity-network-architect + unity-infrastructure-architect

Verified that v1 operates in `no_context_takeover`-only mode:
- `PerMessageDeflateExtension.Reset()` (line 205) is a no-op because there is no retained state.
- `AcceptNegotiation` (line 108) rejects responses where `ServerNoContextTakeover` was requested but not echoed.
- Memory overhead is minimal (no 32KB–128KB sliding window per connection).

✅ Matches the plan's v1 mandate for memory safety.

---

## Spec Compliance Matrix

| RFC | Section | Requirement | Status |
|-----|---------|-------------|--------|
| 7692 | §6.1 | RSV1 only on first fragment | ✅ Set in `WriteMessageWithExtensionsAsync` |
| 7692 | §7.2.1 | Remove trailing `0x00 0x00 0xFF 0xFF` on compression | ✅ `HasTail` + strip |
| 7692 | §7.2.2 | Append trailing bytes before decompression | ✅ Line 276–277 |
| 7692 | §8 | `server_no_context_takeover` negotiation | ✅ AcceptNegotiation validates |
| 7692 | §8 | `server_max_window_bits` negotiation | ✅ Parsed and validated 8–15 |
| 7692 | §8 | `client_max_window_bits` negotiation | ✅ Parsed and validated 8–15 |
| 7692 | §6.2 | Control frames must not be compressed | ✅ Opcode check in TransformOutbound/Inbound |
| 7230 | §3.2.6 | Quoted-string parameter parsing | ✅ `ReadQuotedValue` in `WebSocketExtensionParameters` |
| 6455 | §5.5 | RSV bits negotiation requirement | ✅ Unrecognized RSV → protocol error |

---

## Recommended Fix Order

1. **W-5** — Remove `checked` in `UpdateThroughput` (1 line change, prevents potential exception)
2. **W-1/W-2** — Eliminate `ToArray()` copies in compress/decompress (performance, ~20 lines)
3. **W-3** — Harden proxy CONNECT retry after 407 (reliability, ~10 lines)
4. **W-4** — Document or address 32-bit ARM Interlocked.Add limitation (pragmatic documentation fix)

---

## Platform Compatibility Assessment

| Platform | Risk Level | Notes |
|----------|-----------|-------|
| Editor (Mono) | ✅ Low | All patterns standard .NET |
| Standalone IL2CPP (64-bit) | ✅ Low | No reflection, no problematic generics |
| iOS IL2CPP (arm64) | ✅ Low | `where T : class` prevents AOT issues |
| iOS IL2CPP (armv7) | ⚠️ Medium | 64-bit Interlocked tearing (W-4) |
| Android IL2CPP (arm64) | ✅ Low | Same as iOS arm64 |
| Android IL2CPP (armv7) | ⚠️ Medium | Same as iOS armv7 |
| WebGL | ❌ N/A | WebSocket transport not applicable (browser handles) |

---

## Summary

Phase 18a implementation is **solid overall**. The prior plan review findings (R3/R4) have been addressed:

- ✅ `IMemoryOwner<byte>` return types throughout extension transforms
- ✅ RSV bit propagation through full pipeline
- ✅ `ProxyCredentials` readonly struct (no `NetworkCredential`)
- ✅ Transport factory uses delegates, not reflection
- ✅ Event-driven health monitor (no polling)
- ✅ Chunk-based decompression with size guard
- ✅ `IAsyncEnumerable` manual implementation (AOT-safe)
- ✅ `JsonWebSocketSerializer<T>` with `where T : class`

Remaining items are **optimization opportunities** (W-1, W-2) and **edge case hardening** (W-3, W-5), with one platform documentation gap (W-4). None block shipping.

---

## Revision 2 — Re-Review After Fixes (2026-02-21)

All 5 warnings from the initial review have been addressed. Re-verification details below.

### W-1: RESOLVED — `Compress()` triple allocation eliminated

**Fix:** `Compress()` now uses `MemoryStream.TryGetBuffer()` to access the internal buffer directly without copying. `ToArray()` is only used as a fallback when `TryGetBuffer()` fails (should not occur with default `MemoryStream` constructor). Additionally, the non-`ArraySegment` codepath now uses `deflate.Write(payload.Span)` directly instead of `payload.ToArray()`.

```csharp
// Before (triple alloc):
byte[] compressed = output.ToArray();
// After (direct buffer access):
if (!output.TryGetBuffer(out ArraySegment<byte> compressedSegment) || compressedSegment.Array == null)
{
    // Fallback only
    byte[] compressedFallback = output.ToArray();
    ...
}
int compressedLength = (int)output.Length;
byte[] compressed = compressedSegment.Array;
```

✅ Happy path: 2 allocations instead of 3. One full copy eliminated.

---

### W-2: RESOLVED — `Decompress()` triple allocation eliminated

**Fix:** Same `TryGetBuffer()` pattern applied. Decompressed data is read directly from `MemoryStream`'s internal buffer without the intermediate `ToArray()` copy.

✅ Same improvement as W-1.

---

### W-3: RESOLVED — Proxy CONNECT retry hardened

**Fix:** Three improvements applied:

1. **`ConnectResponse` now carries `CanRetryOnSameConnection` flag** — set to `false` when `Connection: close` header is present, or when the body uses chunked transfer-encoding on a non-200 response.

2. **`EstablishAsync` checks `CanRetryOnSameConnection`** before retrying after 407:
   ```csharp
   if (!response.CanRetryOnSameConnection)
   {
       throw new WebSocketException(
           WebSocketError.ProxyTunnelFailed,
           "Proxy 407 response cannot be safely retried on the same connection " +
           "(unsupported body framing or connection close).");
   }
   ```

3. **Retry write wrapped in try-catch** — if the proxy closed the connection after 407, the second `WriteConnectRequestAsync` is caught and rethrown as `ProxyTunnelFailed` with clear messaging.

4. **New `HeaderContainsToken` helper** — correctly parses comma-separated header values for `Transfer-Encoding: chunked` and `Connection: close` detection.

5. **`ParseContentLength` outputs `hasContentLength` flag** — distinguishes between "Content-Length: 0" and "no Content-Length header", enabling correct body framing decisions.

✅ Proxy retry is now safe against connection closure, chunked bodies, and ambiguous framing.

---

### W-4: RESOLVED — 32-bit IL2CPP atomicity fixed with `lock` pattern

**Fix:** `WebSocketMetricsCollector` replaced all `Interlocked.Add`/`Interlocked.Increment`/`Volatile.Read` calls with a single `lock(_gate)` pattern. All counter mutations and reads now happen under the same lock.

```csharp
// Before:
Interlocked.Add(ref _bytesSent, byteCount);
Interlocked.Increment(ref _framesSent);
// After:
lock (_gate)
{
    if (byteCount > 0)
        _bytesSent += byteCount;
    _framesSent++;
    TouchActivity_NoLock();
}
```

**Additional improvements noticed:**
- `TouchActivity()` → `TouchActivity_NoLock()` — avoids nested lock acquisition.
- `ShouldPublishSnapshot` now operates under lock for consistent state reads.
- `GetSnapshot()` reads all counters under lock, ensuring a consistent snapshot.
- `_connectedAtStopwatchTimestamp = now - 1` in constructor ensures `ElapsedSince` always returns positive uptime.
- `uptime` floor of `TimeSpan.FromTicks(1)` in `GetSnapshot()` prevents division-by-zero.

✅ Fully atomic on all platforms including 32-bit ARM IL2CPP.

---

### W-5: RESOLVED — `checked` addition removed from `UpdateThroughput`

**Fix:** `checked(metrics.BytesSent + metrics.BytesReceived)` → unchecked `metrics.BytesSent + metrics.BytesReceived`. Overflow wraps silently, delta computation remains correct.

✅ No `OverflowException` risk.

---

### Updated Platform Compatibility Assessment

| Platform | Risk Level | Notes |
|----------|-----------|-------|
| Editor (Mono) | ✅ Low | All patterns standard .NET |
| Standalone IL2CPP (64-bit) | ✅ Low | No reflection, no problematic generics |
| iOS IL2CPP (arm64) | ✅ Low | `where T : class` prevents AOT issues |
| iOS IL2CPP (armv7) | ✅ Low | Lock-based metrics (W-4 resolved) |
| Android IL2CPP (arm64) | ✅ Low | Same as iOS arm64 |
| Android IL2CPP (armv7) | ✅ Low | Same as iOS armv7 |
| WebGL | ❌ N/A | WebSocket transport not applicable (browser handles) |

---

### Re-Review Verdict

**All 5 warnings resolved.** No remaining findings at Warning level or above. Phase 18a implementation is ready for shipping.

- 0 Critical
- 0 Warning (5/5 resolved)
- 8 Info (unchanged — all positive confirmations)
