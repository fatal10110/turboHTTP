# Phase 19a.4: SAEA Socket I/O Mode (Opt-In)

**Depends on:** Phase 19 Tasks 19.1 + 19.2
**Estimated Effort:** 2 weeks

---

## Step 0: Add Socket I/O Mode Configuration

**File:** `Runtime/Core/ConnectionPoolOptions.cs` (modified)

Required behavior:

1. Add `SocketIoMode` enum:
   - `NetworkStream` (default)
   - `Saea` (opt-in)
2. Add `ConnectionPoolOptions.SocketIoMode` property.
3. Wire property through `Clone()` and `IsDefault()`.

Implementation constraints:

1. Keep default behavior unchanged (`NetworkStream`).
2. Document that SAEA mode is not available on targets where Transport assembly is excluded (WebGL).

---

## Step 1: Implement SAEA Completion Source

**File:** `Runtime/Transport/Tcp/SaeaCompletionSource.cs` (new)

Required behavior:

1. Bridge `SocketAsyncEventArgs.Completed` to `ValueTask<int>` using `IValueTaskSource<int>`.
2. Handle synchronous (`SendAsync/ReceiveAsync` returns `false`) and async completion paths.
3. Support reset/reuse per operation.

Implementation constraints:

1. `Completed` handlers are assigned once per SAEA instance.
2. Socket errors map to exceptions consistently.

---

## Step 2: Implement SAEA Socket Channel

**File:** `Runtime/Transport/Tcp/SaeaSocketChannel.cs` (new)

Required behavior:

1. Add connection-level send/receive primitives based on two SAEA instances (full duplex).
2. Maintain pinned pooled buffers per channel.
3. Support chunked send for payloads larger than pinned buffer.

Implementation constraints:

1. Deterministic disposal returns SAEA resources and pinned buffers.
2. Cancellation and `OperationAborted` are mapped correctly.

---

## Step 3: Integrate With `TcpConnectionPool`

**File:** `Runtime/Transport/Tcp/TcpConnectionPool.cs` (modified)

Required behavior:

1. Select socket I/O implementation from `ConnectionPoolOptions.SocketIoMode`.
2. Keep existing lease semantics (`ConnectionLease`) unchanged.
3. Preserve idle scavenging, health checks, and pooling limits.

Implementation constraints:

1. No public API changes in `RawSocketTransport`.
2. Mixed mode between transport instances is allowed; each instance follows its own options snapshot.

---

## Step 4: TLS Behavior in SAEA Mode

**Files modified:**
- `Runtime/Transport/Tcp/TcpConnectionPool.cs`
- `Runtime/Transport/Tls/*` (as needed)

Required behavior:

1. TLS still uses `ITlsProvider` abstraction.
2. Initial integration may use socket -> stream adapter -> `SslStream` for handshake and encrypted I/O.
3. Benchmark SAEA gains with and without TLS.
4. If needed, implement a custom stream adapter over SAEA to retain gains under TLS.

Implementation constraints:

1. Do not change TLS correctness semantics.
2. Keep ALPN behavior aligned with existing `TlsProviderSelector`.

---

## Step 5: Platform Capability Detection + Logging

**File:** `Runtime/Transport/Tcp/SaeaPlatformDetection.cs` (new)

Required behavior:

1. Detect expected completion model per platform (IOCP/kqueue/epoll/thread-pool fallback).
2. Expose support/benefit metadata for diagnostics.
3. Log selected mode once per transport instance.

Implementation constraints:

1. Capability checks must not rely on fragile runtime probing loops.
2. WebGL fallback logic is not required here because Transport asmdef excludes WebGL builds.

---

## Verification Criteria

1. SAEA mode passes existing transport integration tests on desktop targets.
2. Receive hot path avoids `Task` allocation.
3. Idle pooling, health checks, and disposal remain correct.
4. TLS handshakes and ALPN remain correct in both socket I/O modes.
5. `NetworkStream` mode remains behaviorally unchanged.
