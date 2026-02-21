# Phase 19a: Extreme Performance & Zero-Allocation Networking

**Milestone:** M4 (v1.2)
**Dependencies:** Phase 19 (Async Runtime Refactor)
**Estimated Complexity:** Very High
**Estimated Effort:** 5-7 weeks
**Critical:** No – Extreme Performance Optimization for High-Concurrency & High-Tick-Rate applications.

## Overview

While Phase 19 addresses the profound CPU scheduling overhead by integrating `ValueTask` into internal hot paths, it leaves existing memory management and buffer strategies primarily untouched. Phase 19a resolves the **Garbage Collector (GC) freeze overhead**—the single biggest network performance killer in Unity—by implementing a completely zero-allocation messaging pipeline.

This phase transforms TurboHTTP from a capable baseline library into an enterprise-grade, high-frequency networking stack comparable to best-in-class assets like BestHTTP, by adopting strategies like `ArrayPool<byte>`, segmented sequence framing, and zero-allocation socket wrappers.

## Why These Enhancements?

| Feature | The Problem | The Zero-Allocation Solution |
|---|---|---|
| **Buffer Pooling** | Remaining `new byte[]` allocations in HTTP/2 framing, HPACK encoding, and handshake validation cause Managed Heap pressure. | Complete remaining migration to `ArrayPool<byte>.Shared` via `IMemoryOwner<byte>` for all internal buffers, guaranteeing deterministic disposal. |
| **Direct Serialization** | Serializers write to intermediate arrays which are then copied into the socket's outbound buffer. | Expose `IBufferWriter<byte>` via new additive interfaces, allowing opt-in direct writes into pooled network buffers without breaking existing APIs. |
| **Segmented Decompression** | Growing contiguous buffers (e.g. `16KB -> 32KB -> 64KB`) causes Large Object Heap (LOH) exhaustion and GC spikes. | Decompress/read into a linked list of buffers (resembling `ReadOnlySequence<byte>`) bypassing chunk copying entirely. |
| **Object Pooling** | HTTP context and header dictionary allocations per request bloat memory under heavy connection load. | Aggressively pool `UHttpResponse`, `Http2Stream`, and header collections via the existing `ObjectPool<T>`. |
| **System TLS Priority** | BouncyCastle's internal buffer management creates massive GC pressure during symmetric encryption. | Prioritize `SslStream` (system TLS) on platforms where hardware-accelerated TLS 1.2/1.3 is available, falling back to BouncyCastle only where system TLS is unavailable. |
| **Non-Blocking Sockets** | Unity's `NetworkStream.ReadAsync` exhibits legacy boxing/APM overhead allocating a `Task` per packet payload on mono targets. | Implement opt-in zero-allocation Socket wrappers utilizing `SocketAsyncEventArgs` (SAEA) on supported platforms. |

## Sub-Phase Index

| Sub-Phase | Name | Depends On | Estimated Effort |
|---|---|---|---|
| 19a.0 | Safety Infrastructure & Debug Guards | None | 2-3 days |
| 19a.1 | `ArrayPool<byte>` Completion Sweep | Phase 19 | 1-2 days |
| 19a.2 | `IBufferWriter<byte>` Serialization Paths (Additive) | 19a.1 | 1 week |
| 19a.3 | Segmented Sequences (`ReadOnlySequence`) | 19a.1 | 1 week |
| 19a.4 | High-Performance SAEA Socket Wrapper (Opt-In) | Phase 19 Tasks 19.1 + 19.2 | 2 weeks |
| 19a.5 | HTTP Object Pooling | Phase 19 | 2-3 days |
| 19a.6 | System TLS Priority with BouncyCastle Fallback | 19a.1 | 1 week |

---

## 19a.0: Safety Infrastructure & Debug Guards

**Goal:** Establish the debug-mode validation and feature-flag infrastructure required to safely ship zero-allocation changes.

**Required Behavior:**
1.  Introduce a `TurboHttpConfig.UseZeroAllocPipeline` configuration flag that gates new zero-allocation codepaths, allowing opt-in during alpha and fallback to proven paths.
2.  Create a `PooledBuffer<T>` debug wrapper (enabled via `#if DEBUG` or `TURBOHTTP_POOL_DIAGNOSTICS`) that detects:
    -   Use-after-return (accessing a buffer after it was returned to the pool).
    -   Double-return (returning the same buffer twice).
    -   Buffer overrun from pooled-array-larger-than-requested (slicing validation).
3.  Add diagnostic counters (rent count, return count, miss count) to `ByteArrayPool` and `ObjectPool<T>` for profiling.

---

## 19a.1: `ArrayPool<byte>` Completion Sweep

**Goal:** Eliminate **all remaining** unprotected `new byte[]` or unbounded `MemoryStream` allocations in the hot paths.

**Current State (already implemented):**
-   `ArrayPool<byte>.Shared` is already used at **50+ call sites** across `RawSocketTransport`, `Http2FrameCodec`, `Http2Connection.Send`, `Http2Connection.Lifecycle`, `Http2Stream`, `HpackEncoder`, `HpackHuffman`, and `Http11RequestSerializer`.
-   `ByteArrayPool` static facade already exists in `TurboHTTP.Performance`.
-   `ArrayPoolMemoryOwner<T>` implementing `IMemoryOwner<T>` already exists in the WebSocket subsystem.

**Remaining Work:**
1.  **Promote `ArrayPoolMemoryOwner<T>`** from `TurboHTTP.WebSocket` namespace to `TurboHTTP.Performance` namespace for project-wide reuse.
2.  Convert the ~20 remaining `new byte[]` sites outside BouncyCastle to use `ArrayPool`:
    -   `Http2Connection.Lifecycle` (GOAWAY payload, frame headers at lines 258, 274).
    -   `Http2FrameCodec` (line 67: `new byte[frame.Length]`).
    -   `Http2Settings` (line 113: settings payload).
    -   `HpackEncoder` (line 211: result array) and `HpackHuffman` (lines 335, 466).
    -   `WebSocketConstants` (line 63: key bytes).
    -   `WebSocketHandshakeValidator` (lines 283, 290, 554: header/trailing/prefetch).
    -   `WebSocketMessage` (line 91: copy array).
    -   `EncodingHelper` (line 69: encoding buffer).
3.  Convert `MultipartFormDataBuilder.Build()` from `MemoryStream` to a pooled buffer strategy.
4.  All data **must** be passed using `Memory<byte>`, `ReadOnlyMemory<byte>`, or `Span<byte>` and wrapped in `IMemoryOwner<byte>` for deterministic disposal.

> **Note:** Allocations inside `Auth/` (`PkceUtility`, `OAuthClient`), `Observability/` (`MonitorMiddleware`), and `Testing/` (`RecordReplayTransport`) are cold-path and excluded from this sweep.

---

## 19a.2: `IBufferWriter<byte>` Serialization Paths

**Goal:** Eliminate intermediate serialization buffers by upgrading serialization interfaces to support direct buffer writes.

**Current State:**
-   `IJsonSerializer` has `string Serialize<T>(T value)` signature — string-based, forces intermediate allocation.
-   The built-in `LiteJson` serializer operates on strings.
-   `MultipartFormDataBuilder.Build()` returns `byte[]` via internal `MemoryStream`.

**Required Behavior:**
1.  Extend `IJsonSerializer` with buffer-based methods:
    ```csharp
    public interface IJsonSerializer
    {
        // Existing methods
        string Serialize<T>(T value);
        T Deserialize<T>(string json);
        string Serialize(object value, Type type);
        object Deserialize(string json, Type type);

        // New buffer-based methods
        void Serialize<T>(T value, IBufferWriter<byte> output);
        T Deserialize<T>(ReadOnlySequence<byte> input);
    }
    ```
    Update all existing implementations (`LiteJson`, etc.) to implement the new methods. The `LiteJson` default implementation can bridge via `Serialize<T>() → Encoding.GetBytes()` into the buffer writer; purpose-built implementations (e.g., `System.Text.Json`) write directly.
2.  Create a `PooledArrayBufferWriter<byte>` that backs `ArrayBufferWriter<byte>` with pooled arrays from `ArrayPool<byte>.Shared`.
3.  Replace `MultipartFormDataBuilder.Build()` with buffer-based output:
    ```csharp
    public void WriteTo(IBufferWriter<byte> output); // Primary API
    ```

---

## 19a.3: Segmented Sequences (`ReadOnlySequence<byte>`)

**Goal:** Prevent Large Object Heap (LOH) allocations caused by large fragmented messages or chunked HTTP bodies.

**Required Behavior:**
1.  Instead of reading 16KB and copying it into a newly allocated 32KB array when full, the reader should rent a second 16KB segment and link them.
2.  Construct a `ReadOnlySequence<byte>` to represent the logical message payload to consumers.
3.  For decompression (`permessage-deflate` and HTTP chunked transfer), implement a `SegmentedStream` adapter that allows `DeflateStream` to read from `ReadOnlySequence<byte>` input without forcing contiguous array flattening. This is the primary engineering challenge of this sub-phase.

**Decompression Strategy:**
-   The existing `PerMessageDeflateExtension` uses `IMemoryOwner<byte>` and `ReadOnlyMemory<byte>`. Converting it to accept segmented input requires a `SegmentedReadStream : Stream` wrapper that reads across segment boundaries.
-   Output decompression buffers will be linked as segments (rent additional segments on demand) rather than growing a single contiguous buffer.
-   If `SegmentedStream` performance proves insufficient for a specific subsystem, that subsystem may fall back to flattening with a pooled buffer — but this must be documented and measured.

---

## 19a.4: High-Performance SAEA Socket Wrapper (Opt-In)

**Goal:** Bypass `NetworkStream` internal boxing and async state-machine allocations on supported platforms.

**Dependencies:** Requires Phase 19 **Task 19.1** (ValueTask abstraction layer) and **Task 19.2** (pipeline & transport migration) to be complete, as the SAEA completion callbacks must plumb into the `ValueTask` system.

**Platform Support Matrix:**

| Platform | SAEA Support | Completion Model | Status |
|---|---|---|---|
| Windows (Mono/IL2CPP) | ✅ | IOCP | Full zero-alloc benefit |
| macOS (Mono) | ✅ | kqueue | Full zero-alloc benefit |
| Linux (Mono/IL2CPP) | ✅ | epoll | Full zero-alloc benefit |
| Android (IL2CPP) | ⚠️ | ThreadPool fallback | Reduced benefit; opt-in |
| iOS (IL2CPP) | ⚠️ | ThreadPool fallback | Reduced benefit; opt-in |
| WebGL | ❌ | N/A | Not supported (no raw sockets) |

**Required Behavior:**
1.  Implement a `SaeaSocketTransport` as an **opt-in alternative** to the existing `NetworkStream`-based transport. `NetworkStream` remains the default.
2.  SAEA inherently supports zero-allocation by pinning the IO buffers and letting the developer recycle the event context.
3.  Plumb the `ValueTask` system from Phase 19 into the SAEA completion callbacks, avoiding the legacy APM `IAsyncResult` overhead.
4.  Use the `SocketAsyncEventArgs.SetBuffer(byte[], int, int)` overload (not `Memory<byte>`) for .NET Standard 2.0 compatibility with all Unity IL2CPP targets.
5.  Integrate with the existing `TcpConnectionPool` lifecycle:
    -   `SaeaSocketTransport` must implement the same `ITransport` / `IPooledConnection` interfaces.
    -   Connection health checks, idle timeouts, and disposal must continue to work.

**Configuration:**
```csharp
var config = new TurboHttpConfig
{
    TransportMode = TransportMode.NetworkStream, // Default
    // TransportMode = TransportMode.Saea,       // Opt-in for supported platforms
};
```

---

## 19a.5: HTTP Object Pooling

**Goal:** Recycle state-tracking objects of HTTP connections to support extremely high requests-per-second (RPS) without GC freeze.

**Current State:**
-   `ObjectPool<T>` already exists in `TurboHTTP.Performance` with lock-based synchronization, bounded capacity, and configurable reset callbacks. This is consistent with project guidelines.

**Required Behavior:**
1.  Pool the following **actual TurboHTTP types**:
    -   `UHttpResponse` — reset body, headers, status; return body buffer to `ArrayPool` on reset.
    -   `Http2Stream` — recycle per-stream state objects during HTTP/2 multiplexing.
    -   Header dictionary collections — reuse `Dictionary<string, string>` instances via `.Clear()` on reset.
    -   `HpackEncoder.BufferWriter` internal state — avoid per-encode allocation.
2.  Reset state completely (`Clear()`) on objects before returning them to the pool. The existing `ObjectPool<T>` reset callback mechanism handles this.
3.  Ensure thread safety for connection multiplexing (especially under HTTP/2) — the existing lock-based `ObjectPool<T>` already satisfies this requirement.

---

## 19a.6: System TLS Priority with BouncyCastle Fallback

**Goal:** Eliminate the massive GC pressure caused by BouncyCastle's internal buffer allocations by prioritizing `SslStream` (system TLS) on platforms that support it, falling back to BouncyCastle only where system TLS is unavailable or insufficient.

**Rationale:** BouncyCastle's vendored library contains **1000+ `new byte[]` allocations** across `ByteQueue`, `TlsProtocol`, `TlsUtilities`, `BigInteger`, and crypto internals. These are deeply embedded in BC's TLS state machines and cannot be efficiently shimmed without creating an unmaintainable fork. Instead, we bypass BouncyCastle entirely on platforms where system TLS is hardware-accelerated and fully functional.

**Platform TLS Strategy:**

| Platform | TLS Provider | Rationale |
|---|---|---|
| Windows (Mono/IL2CPP) | `SslStream` (SChannel) | Hardware-accelerated, zero-copy kernel TLS |
| macOS (Mono) | `SslStream` (SecureTransport) | Hardware-accelerated, native TLS stack |
| Linux (Mono/IL2CPP) | `SslStream` (OpenSSL) | Hardware-accelerated, well-maintained |
| Android (IL2CPP) | `SslStream` (platform) | Uses Android's native TLS; avoids BC overhead |
| iOS (IL2CPP) | `SslStream` (SecureTransport) | Hardware-accelerated; Apple requires ATS compliance anyway |
| WebGL | Browser TLS | Handled by browser; neither BC nor SslStream applies |
| Platforms without system TLS | BouncyCastle (existing) | Fallback for edge cases (e.g., custom embedded targets) |

**Required Behavior:**
1.  Introduce a `TlsProviderMode` configuration enum:
    ```csharp
    public enum TlsProviderMode
    {
        SystemPreferred, // Default: use SslStream, fall back to BC if unavailable
        SystemOnly,      // Require SslStream; throw if unavailable
        BouncyCastleOnly // Force BC (existing behavior, for backward compat)
    }
    ```
2.  Implement a `SystemTlsStreamProvider` that wraps `SslStream` with the same interface as the existing BC TLS integration in `TcpConnectionPool`.
3.  Add runtime detection: attempt `SslStream` negotiation first; if it throws `PlatformNotSupportedException` or fails TLS version requirements, fall back to BouncyCastle with a diagnostic log.
4.  **Do NOT modify any BouncyCastle source code.** BC remains as-is for the fallback path.
5.  Expose a certificate validation callback for `SslStream` that matches the existing BC certificate validation behavior (custom root CAs, pinning).

**Performance Impact:** On platforms using `SslStream`, all 1000+ BouncyCastle allocation sites are completely bypassed. The system TLS stack handles encryption/decryption in kernel space with hardware acceleration (AES-NI, ARM Crypto Extensions), achieving both zero managed allocation and higher throughput.

---

## Reference against the Industry (e.g., BestHTTP)

A major differentiator for libraries like BestHTTP is exactly the features proposed here. BestHTTP employs its own heavily tuned `BufferPool`, manages contiguous data via sliced array lists (avoiding LOH), and implements custom socket polling and platform-native TLS selection. Phase 19a essentially enforces these enterprise architecture decisions into TurboHTTP, while avoiding the maintenance burden of forking TLS internals.

## Verification

### Benchmarking (Critical)
-   Run a memory profiler (Unity Profiler / dotMemory) tracking a **10,000 requests-per-second** pipeline.
-   **Steady-state metric** (after 1,000-request warmup): Managed heap expansion must be **< 1 KB/sec** of new allocations.
-   Use `BenchmarkDotNet` with `MemoryDiagnoser` for micro-benchmarks of individual sub-phases.

### Integration
-   Sequence readers correctly process fragments spanning buffer boundaries without data corruption.
-   TLS provider selection correctly falls back to BouncyCastle on unsupported platforms.
-   SAEA transport (opt-in) passes the full existing test suite on desktop platforms.

### Safety Validation
-   `PooledBuffer<T>` debug mode detects all use-after-return and double-return bugs during test suite execution.
-   All existing tests pass with `UseZeroAllocPipeline = true` and `UseZeroAllocPipeline = false`.
-   No regression in API behavior, error mapping, or middleware ordering.

### Platform Matrix
-   IL2CPP AOT validation on iOS and Android.
-   `SslStream` TLS negotiation verified on Windows, macOS, Linux, iOS, Android.
-   BouncyCastle fallback verified on at least one platform by forcing `TlsProviderMode.BouncyCastleOnly`.
