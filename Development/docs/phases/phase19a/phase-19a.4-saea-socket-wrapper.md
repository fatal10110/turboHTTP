# Phase 19a.4: SAEA & Poll/Select Socket I/O Modes (Opt-In)

**Depends on:** Phase 19 Tasks 19.1 + 19.2
**Estimated Effort:** 2 weeks

---

## Step 0: Add Socket I/O Mode Configuration

**File:** `Runtime/Core/ConnectionPoolOptions.cs` (modified)

Required behavior:

1. Add `SocketIoMode` enum:
   - `NetworkStream` (default)
   - `Saea` (opt-in, high-performance, uses OS async I/O completion ports)
   - `PollSelect` (opt-in, stable on IL2CPP, uses synchronous non-blocking `Socket.Poll`/`Select`)
2. Add `ConnectionPoolOptions.SocketIoMode` property.
3. Wire property through `Clone()` and `IsDefault()`.
4. Document that `Saea` relies on runtime async callbacks which may be unstable under IL2CPP on some platforms, and that `PollSelect` is the recommended alternative for consoles and mobile.

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

## Step 3b: Implement Poll/Select Socket Channel

**File:** `Runtime/Transport/Tcp/PollSelectSocketChannel.cs` (new)

Required behavior:

1. Add connection-level send/receive primitives using `Socket.Poll` + synchronous `Socket.Send`/`Socket.Receive`.
2. Run a tight poll loop on a dedicated thread (or multiplexed `Socket.Select` loop) — read/write only when the socket signals readiness.
3. Bridge to `ValueTask<int>` via `TaskCompletionSource` or `ManualResetValueTaskSourceCore` so the rest of the pipeline stays async.
4. Maintain pinned pooled buffers per channel (same pattern as `SaeaSocketChannel`).
5. Support chunked send for payloads larger than pinned buffer.

Implementation constraints:

1. No reliance on `SocketAsyncEventArgs` or runtime async callbacks.
2. Deterministic disposal stops the poll thread and returns pinned buffers.
3. Cancellation closes the socket, causing `Poll` to return immediately.
4. Poll timeout should be configurable (default ~1ms) to balance latency vs CPU.

---

## Step 3c: Add `PollSelectStream` Adapter

**File:** `Runtime/Transport/Tcp/PollSelectStream.cs` (new)

Required behavior:

1. Thin `Stream` adapter over `PollSelectSocketChannel` (same pattern as `SaeaStream`).
2. Owns the channel and disposes it.
3. TLS works via `SslStream` wrapping this stream.

---

## Step 4: TLS Behavior in SAEA and PollSelect Modes

**Files modified:**
- `Runtime/Transport/Tcp/TcpConnectionPool.cs`
- `Runtime/Transport/Tls/*` (as needed)

Required behavior:

1. TLS still uses `ITlsProvider` abstraction.
2. Both SAEA and PollSelect modes use socket → stream adapter → `SslStream` for handshake and encrypted I/O.
3. Benchmark gains with and without TLS in each mode.
4. If needed, implement custom stream adapters to retain gains under TLS.

Implementation constraints:

1. Do not change TLS correctness semantics.
2. Keep ALPN behavior aligned with existing `TlsProviderSelector`.

---

## Step 5: Platform Capability Detection + Logging

**File:** `Runtime/Transport/Tcp/SaeaPlatformDetection.cs` (new)

Required behavior:

1. Detect expected completion model per platform (IOCP/kqueue/epoll/thread-pool fallback).
2. Expose support/benefit metadata for diagnostics.
3. Log selected mode once per transport instance (including `PollSelect`).
4. For `PollSelect`, log that synchronous non-blocking mode is active.

Implementation constraints:

1. Capability checks must not rely on fragile runtime probing loops.
2. WebGL fallback logic is not required here because Transport asmdef excludes WebGL builds.

---

## Verification Criteria

1. SAEA mode passes existing transport integration tests on desktop targets.
2. PollSelect mode passes existing transport integration tests on all targets.
3. Receive hot path avoids `Task` allocation in both SAEA and PollSelect modes.
4. Idle pooling, health checks, and disposal remain correct in all three modes.
5. TLS handshakes and ALPN remain correct in all socket I/O modes.
6. `NetworkStream` mode remains behaviorally unchanged.
7. PollSelect mode is stable under IL2CPP on mobile (iOS/Android) builds.
