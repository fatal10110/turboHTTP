# Phase 19a.4: High-Performance SAEA Socket Wrapper (Opt-In)

**Depends on:** Phase 19 Tasks 19.1 (ValueTask abstraction) + 19.2 (pipeline & transport migration)
**Estimated Effort:** 2 weeks

---

## Step 0: Define `TransportMode` Configuration

**File:** `Runtime/Core/TurboHttpConfig.cs` (modified)

Required behavior:

1. Add a `TransportMode` enum:
    ```csharp
    public enum TransportMode
    {
        NetworkStream, // Default — existing behavior
        Saea,          // Opt-in — SAEA zero-allocation transport
    }
    ```
2. Add a `TransportMode TransportMode` property to `TurboHttpConfig` (default: `TransportMode.NetworkStream`).
3. Document via XML comments that `TransportMode.Saea` is opt-in and provides zero-allocation socket I/O on supported platforms (desktop and server). On unsupported platforms (WebGL), it falls back to `NetworkStream` with a diagnostic warning.

Implementation constraints:

1. The `TransportMode` property must be settable before client construction and immutable thereafter (config freeze pattern).
2. `TransportMode.Saea` must be independent of `UseZeroAllocPipeline` — it can be enabled separately.
3. Add platform validation at construction time: if `Saea` is requested on WebGL, log a warning and fall back to `NetworkStream`.

---

## Step 1: Implement SAEA Completion Callback Infrastructure

**File:** `Runtime/Transport/SaeaCompletionSource.cs` (new)

Required behavior:

1. Create a reusable `SaeaCompletionSource` that bridges `SocketAsyncEventArgs.Completed` event to a `ValueTask<int>` result.
2. On each I/O operation:
   - If the operation completes synchronously (`SendAsync`/`ReceiveAsync` returns `false`), return the result immediately as a completed `ValueTask<int>`.
   - If the operation completes asynchronously (returns `true`), the `Completed` callback signals a `ManualResetValueTaskSourceCore<int>`.
3. Implement `IValueTaskSource<int>` to avoid `Task` allocation on the async completion path.
4. The completion source must be resettable and reusable across multiple I/O operations on the same connection.

Implementation constraints:

1. Use `ManualResetValueTaskSourceCore<int>` (from `System.Threading.Tasks.Extensions` on .NET Standard 2.0) as the backing source.
2. The `Completed` callback must be set ONCE on the `SocketAsyncEventArgs` instance (not per-operation).
3. Handle `SocketError` in the completion callback — set exception on the value task source if the operation failed.
4. The completion source must handle thread-safety for the callback (which fires on an IOCP/epoll thread) vs. the consumer (which awaits on the caller's thread).
5. Increment the `ManualResetValueTaskSourceCore` version on each `Reset()` to prevent stale awaiting.
6. Mark the class as `sealed` for IL2CPP devirtualization.

---

## Step 2: Implement `SaeaSocketTransport`

**File:** `Runtime/Transport/SaeaSocketTransport.cs` (new)

Required behavior:

1. Implement `ITransport` (same interface as the existing `NetworkStream`-based transport).
2. Create and manage a pair of `SocketAsyncEventArgs` instances: one for send, one for receive.
3. Pre-allocate and pin I/O buffers on the SAEA instances to avoid per-operation buffer pinning.
4. **Send path:**
   - `SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)` → copies data to the send SAEA's pinned buffer, calls `Socket.SendAsync(saea)`, returns `ValueTask`.
   - For data larger than the pinned buffer, send in chunks.
5. **Receive path:**
   - `ReceiveAsync(Memory<byte> buffer, CancellationToken ct)` → sets the receive SAEA's buffer, calls `Socket.ReceiveAsync(saea)`, returns `ValueTask<int>`.
6. **Connection lifecycle:**
   - `ConnectAsync(string host, int port, CancellationToken ct)` — connect using SAEA.
   - `Dispose()` — return SAEA instances, close socket.
7. Use `SocketAsyncEventArgs.SetBuffer(byte[], int, int)` overload (NOT the `Memory<byte>` overload) for .NET Standard 2.0 compatibility.

Implementation constraints:

1. Each `SaeaSocketTransport` instance owns exactly 2 SAEA instances — one for send, one for receive. This allows concurrent send and receive (full duplex).
2. Pinned I/O buffer size: 8KB per SAEA (16KB total per connection). This is the optimal size for most network I/O.
3. The transport must handle `SocketError.OperationAborted` (from cancellation) gracefully — translate to `OperationCanceledException`.
4. Do NOT expose SAEA internals to consumers — the `ITransport` interface is the only public surface.
5. Use `Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true)` for connection pooling compatibility.
6. The transport must support both TCP and TLS (by wrapping the SAEA socket in an `SslStream` for TLS connections).

---

## Step 3: Implement Platform-Specific SAEA Completion Models

**File:** `Runtime/Transport/SaeaPlatformDetection.cs` (new)

Required behavior:

1. Detect the platform's SAEA completion model at runtime:
   - **Windows**: IOCP (I/O Completion Ports) — true zero-allocation.
   - **macOS**: kqueue — true zero-allocation.
   - **Linux**: epoll — true zero-allocation.
   - **Android (IL2CPP)**: ThreadPool fallback — reduced benefit but still avoids `NetworkStream` overhead.
   - **iOS (IL2CPP)**: ThreadPool fallback — reduced benefit.
   - **WebGL**: Not supported — fall back to `NetworkStream`.
2. Expose a `SaeaPlatformInfo` struct with:
   - `IsSupported` (bool)
   - `CompletionModel` (enum: IOCP, KQueue, Epoll, ThreadPool, NotSupported)
   - `ExpectedBenefit` (enum: Full, Reduced, None)
3. Log the detected platform and completion model at transport initialization.

Implementation constraints:

1. Detection must use `RuntimeInformation.IsOSPlatform()` and known Mono/IL2CPP behavior — do NOT attempt to probe SAEA support at runtime.
2. The detection must be done once at startup and cached in a static readonly field.
3. On platforms with `ExpectedBenefit = Reduced`, log an informational message explaining that SAEA is functional but uses ThreadPool callbacks instead of kernel-level completion.
4. On WebGL, do not attempt to create SAEA instances at all — fail fast with a descriptive message.

---

## Step 4: Integrate with `TcpConnectionPool`

**File:** `Runtime/Transport/TcpConnectionPool.cs` (modified)

Required behavior:

1. When `TransportMode.Saea` is configured, create `SaeaSocketTransport` instances instead of `NetworkStream`-based transports.
2. `SaeaSocketTransport` must implement `IPooledConnection` — supporting health checks, idle timeouts, and disposal.
3. Health check: use `Socket.Poll(0, SelectMode.SelectRead)` combined with `Socket.Available` to detect dead connections without blocking.
4. Idle timeout: track last I/O timestamp, dispose connections that exceed the idle timeout.
5. Disposal: close socket, return SAEA instances, return pinned buffers.
6. Connection pool must handle mixed transport types during transition — if `TransportMode` changes between requests (unlikely but possible via config reload), existing pooled connections of the old type should drain naturally.

Implementation constraints:

1. Do NOT change the pool's public API — transport type selection is internal to the pool based on `TurboHttpConfig.TransportMode`.
2. The pool must continue to work with TLS — `SaeaSocketTransport` connections that need TLS are wrapped in `SslStream` after the TCP connect, same as `NetworkStream` connections.
3. SAEA pinned buffer reuse: when a connection is returned to the pool, the SAEA instances (and their pinned buffers) stay allocated. They are only freed when the connection is disposed.
4. Maximum SAEA connections should respect the existing pool size limits — no separate limit for SAEA.

---

## Step 5: Implement TLS Integration for SAEA Transport

**File:** `Runtime/Transport/SaeaTlsWrapper.cs` (new)

Required behavior:

1. After SAEA TCP connection is established, wrap the socket in an `SslStream` for TLS connections (same as existing `NetworkStream` path).
2. The `SslStream` wraps a `NetworkStream` created from the connected `Socket` — SAEA is used for the initial connection only; once TLS is negotiated, I/O goes through `SslStream.ReadAsync/WriteAsync`.
3. Alternative approach (for full zero-allocation TLS): implement a custom `SaeaStream : Stream` that uses SAEA for `Read/Write` methods, then wrap THIS stream in `SslStream`. This provides zero-allocation through the TLS layer on platforms where `SslStream` uses the underlying stream's async methods.
4. Certificate validation uses the same `ITlsProvider` callbacks as the existing transport.

Implementation constraints:

1. Start with the simpler approach (SAEA connect → NetworkStream → SslStream) and benchmark. Only implement `SaeaStream` if benchmarks show significant allocation from `NetworkStream` in the TLS path.
2. ALPN protocol negotiation must work the same as the existing transport.
3. TLS handshake timeout must be enforced via `CancellationToken`.
4. Document whether the SAEA benefit is primarily at the TCP level (connect + non-TLS I/O) or extends through TLS.

---

## Verification Criteria

1. `SaeaSocketTransport` connects, sends, and receives data correctly on desktop platforms (Windows, macOS, Linux).
2. `SaeaCompletionSource` correctly bridges synchronous and asynchronous SAEA completions to `ValueTask<int>`.
3. Zero `Task` allocations on the receive hot path (verified via memory profiler).
4. Platform detection correctly identifies IOCP/kqueue/epoll/ThreadPool/NotSupported.
5. `TcpConnectionPool` correctly creates SAEA transports when `TransportMode.Saea` is configured.
6. Connection health checks, idle timeouts, and disposal work with SAEA-backed connections.
7. TLS handshake succeeds over SAEA transport with both `SslStream` and BouncyCastle fallback.
8. All existing integration tests pass with `TransportMode.Saea`.
9. `TransportMode.NetworkStream` (default) is unaffected — zero behavioral change.
10. WebGL gracefully falls back to `NetworkStream` with a diagnostic warning.
11. Android and iOS function correctly with ThreadPool-based SAEA completion.
12. Concurrent send/receive (full duplex) works without data corruption.
