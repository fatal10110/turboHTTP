# Phase 18.1–18.3 Implementation Review — 2026-02-21

Comprehensive review performed by specialist agents (**websocket-protocol-architect** and **network-transport-architect**) across all Phase 18.1, 18.2, and 18.3 implementation files.

## Review Agents

| Agent | Focus Area | Files Scoped |
|-------|-----------|-------------|
| **websocket-protocol-architect** | RFC 6455 framing compliance, wire format, masking, UTF-8 validation, fragmentation rules, handshake request/response, close handshake semantics, close code validation, payload length rules | `WebSocketFrame.cs`, `WebSocketConstants.cs`, `WebSocketFrameReader.cs`, `WebSocketFrameWriter.cs`, `MessageAssembler.cs`, `WebSocketHandshake.cs`, `WebSocketHandshakeValidator.cs`, `link.xml` |
| **network-transport-architect** | Connection lifecycle, state machine correctness, threading & atomicity, disposal patterns, resource leaks, transport lifecycle, send/receive loops, backpressure, keep-alive, CTS management | `WebSocketConnection.cs`, `WebSocketConnectionOptions.cs`, `WebSocketException.cs`, `WebSocketMessage.cs`, `WebSocketState.cs`, `IWebSocketTransport.cs`, `RawSocketWebSocketTransport.cs`, assembly definitions |

## Review Scope

All Phase 18.1–18.3 files across `Runtime/WebSocket/` and `Runtime/WebSocket.Transport/`.

**Files Reviewed (17 total):**
- `WebSocketFrame.cs`, `WebSocketConstants.cs`, `WebSocketFrameReader.cs`, `WebSocketFrameWriter.cs`
- `MessageAssembler.cs`, `WebSocketMessage.cs`, `WebSocketAssembledMessage` (within `MessageAssembler.cs`)
- `WebSocketHandshake.cs`, `WebSocketHandshakeValidator.cs`
- `WebSocketConnection.cs`, `WebSocketConnectionOptions.cs`, `WebSocketState.cs`
- `WebSocketException.cs`, `WebSocketReconnectPolicy.cs`
- `IWebSocketTransport.cs`, `RawSocketWebSocketTransport.cs`
- `link.xml`, `TurboHTTP.WebSocket.asmdef`, `TurboHTTP.WebSocket.Transport.asmdef`

---

## Summary Verdict

| Severity | Count | Status |
|----------|-------|--------|
| 🔴 Critical | 4 | ✅ Resolved |
| 🟡 Warning | 8 | ✅ Resolved |
| 🟢 Info | 6 | Documented |

---

## 🔴 Critical Findings

### C-1 [Transport] TryTransitionState Is Public and Throws on Invalid Transitions

**Agent:** network-transport-architect
**File:** `Runtime/WebSocket/WebSocketConnection.cs` (lines 361–372)

```csharp
public bool TryTransitionState(WebSocketState expected, WebSocketState next)
{
    ValidateTransition(expected, next);  // throws InvalidOperationException
    ...
}
```

`TryTransitionState` is `public` and calls `ValidateTransition` which throws `InvalidOperationException` for disallowed state transitions. This violates two principles:

1. **Encapsulation:** External callers can transition the state machine, corrupting connection lifecycle.
2. **Try pattern:** "Try" methods should return `false` on failure, not throw. The CAS-based return-false pattern is spec-mandated. `FinalizeClose` (line 819–841) uses `TryTransitionState` in a polling loop under the assumption it never throws for "wrong current state" — but `ValidateTransition` will throw for transitions not in `AllowedTransitions` like `None→Closed`.

**Impact:** `FinalizeClose` can throw `InvalidOperationException` if called while state is `None` (e.g., transport.ConnectAsync fails), leaving the connection in a partially torn-down state.

**Fix:**
1. Make `TryTransitionState` `private` (or `internal` for testing)
2. Replace `ValidateTransition` call with: return `false` if transition is not in `AllowedTransitions`
3. Callers that need exception semantics should wrap: `if (!TryTransitionState(...)) throw ...`

---

### C-2 [Transport] ReceiveLoopAsync Double-Disposes Control Frame Payloads

**Agent:** network-transport-architect
**File:** `Runtime/WebSocket/WebSocketConnection.cs` (lines 419–427)

```csharp
if (frameLease.Frame.IsControlFrame)
{
    bool shouldClose = await HandleControlFrameAsync(frameLease, ct).ConfigureAwait(false);
    frameLease.Dispose();  // ← second dispose
    ...
}
```

`HandleControlFrameAsync` passes `frameLease` (including `frame.Payload`) to methods like `WritePongAsync` and `ParseCloseStatus`. It uses `frame.Payload` which reads from the lease's buffer. However, `HandleControlFrameAsync` does **not** dispose the lease — the caller does on line 421. 

The problem: `WritePongAsync` is an `async` operation that captures `frame.Payload` (a `ReadOnlyMemory<byte>` into the pooled buffer). If the pong write is slow, the `frameLease.Dispose()` on line 421 returns the buffer while `WritePongAsync` may still be referencing it.

**Wait — re-checking:** `HandleControlFrameAsync` awaits `SendLockedAsync(...WritePongAsync...)` before returning. So the pong write completes before `Dispose`. The dispose is safe. 

**Revised analysis:** This is actually fine. The `await` ensures the async pong write completes first. **Downgrading to Info.** However, `frame.Payload` is a `ReadOnlyMemory<byte>` pointing into the pooled buffer. After `HandleControlFrameAsync` returns, `frameLease.Dispose()` returns the buffer. This is safe **only because** `HandleControlFrameAsync` awaits all operations. But the pattern is fragile — any future change that doesn't `await` before return would create a use-after-free.

**Recommendation:** Move `frameLease.Dispose()` into `HandleControlFrameAsync` at its end, or document the ownership contract clearly.

---

### C-3 [Protocol] HandleControlFrameAsync — Close Frame Uses frame.Payload After Potential Buffer Mutation

**Agent:** websocket-protocol-architect
**File:** `Runtime/WebSocket/WebSocketConnection.cs` (lines 530–549)

```csharp
if (frame.Opcode == WebSocketOpcode.Close)
{
    var remoteStatus = ParseCloseStatus(frame.Payload);  // reads from pooled buffer
    _remoteCloseTcs.TrySetResult(remoteStatus);
    ...
    await SendCloseFrameIfNeededAsync(..., ct).ConfigureAwait(false);
    return true;
}
```

`ParseCloseStatus` reads from `frame.Payload`, which is a `ReadOnlyMemory<byte>` backed by the pooled buffer in `frameLease`. This happens before `frameLease.Dispose()`. The code is safe because `ParseCloseStatus` extracts the close code and reason as value types/strings, and does not retain a reference to the buffer. ✅

However, if `ParseCloseStatus` fails (e.g., invalid UTF-8 in close reason), the exception propagates up through `HandleControlFrameAsync` → `ReceiveLoopAsync`. The `frameLease.Dispose()` on line 421 would still execute. But if `ParseCloseStatus` throws, `HandleControlFrameAsync` throws, and line 421 (`frameLease.Dispose()`) executes unconditionally. **This is correct.**

**Revised verdict:** Downgrading from Critical — the flow is safe. But documenting for completeness.

---

### C-3 (Revised) [Transport] FinalizeClose Does Not Handle None→Closed Transition

**Agent:** network-transport-architect
**File:** `Runtime/WebSocket/WebSocketConnection.cs` (lines 818–842)

```csharp
var state = State;
while (state != WebSocketState.Closed)
{
    if (state == WebSocketState.Open)
        { if (TryTransitionState(Open, Closed)) break; }
    else if (state == WebSocketState.Closing)
        { if (TryTransitionState(Closing, Closed)) break; }
    else if (state == WebSocketState.Connecting)
        { if (TryTransitionState(Connecting, Closed)) break; }
    else
        { break; }  // ← None falls through here
    state = State;
}
```

When state is `None`, the loop hits `break` without ever transitioning to `Closed`. This means `FinalizeClose` exits with state still `None`, leaving a zombie connection. The `StateChanged` event is never fired for the `None→Closed` transition.

This occurs when `ConnectAsync` fails before transitioning from `None→Connecting` (which requires `TryTransitionState` to succeed first — so actually `None` is entered only if the CAS from `None→Connecting` hasn't happened yet, but `ConnectAsync` does that first). 

**Revised check:** `ConnectAsync` transitions `None→Connecting` at line 112. If that fails, it throws before anything else. If transport.ConnectAsync throws, state is `Connecting`, and `FinalizeClose` handles `Connecting→Closed` correctly. So `state == None` in `FinalizeClose` would only happen if someone calls `Abort()` before `ConnectAsync`. In that case, `FinalizeClose` would leave state as `None`.

**Impact:** Calling `Abort()` on a never-connected `WebSocketConnection` leaves state as `None` instead of `Closed`. Subsequent state checks may behave unexpectedly.

**Fix:** Add `None→Closed` to `AllowedTransitions`:
```csharp
[WebSocketState.None] = new[] { WebSocketState.Connecting, WebSocketState.Closed },
```

---

### C-4 [Transport] BoundedAsyncQueue — Completed Queue Still Raises Semaphore for Waiting Readers

**Agent:** network-transport-architect
**File:** `Runtime/WebSocket/WebSocketConnection.cs` (lines 1164–1179)

```csharp
public void Complete(Exception error)
{
    int readersToWake;
    lock (_gate)
    {
        if (_completed) return;
        _completed = true;
        _completionError = error;
        readersToWake = _waitingReaders;
    }
    if (readersToWake > 0)
        _items.Release(readersToWake);
}
```

After `Complete` is called, `readersToWake` captures `_waitingReaders` under lock. But between the lock release and `_items.Release(readersToWake)`, a new reader could call `DequeueAsync`, see `_queue.Count == 0`, increment `_waitingReaders`, and then call `_items.WaitAsync`. This new reader gets the semaphore signal that was meant for a previously-counted reader, and the original reader gets stuck waiting indefinitely.

**Impact:** Under concurrent `Complete` + `DequeueAsync`, a reader can hang forever. In practice, `ReceiveLoopAsync` is the sole consumer, so concurrency is low — but the API is public on the nested class.

**Fix:** Release inside the lock, or use a sentinel pattern. Alternatively, cancel the semaphore:
```csharp
lock (_gate)
{
    _completed = true;
    _completionError = error;
    readersToWake = _waitingReaders;
    _items.Release(readersToWake > 0 ? readersToWake : 1); // inside lock
}
```

---

## 🟡 Warning Findings

### W-1 [Protocol] MessageAssembler Uses FrameTooLarge Error for Message-Level Violations

**Agent:** websocket-protocol-architect
**File:** `Runtime/WebSocket/MessageAssembler.cs` (lines 160–168)

```csharp
throw new WebSocketProtocolException(
    WebSocketError.FrameTooLarge,     // should be MessageTooLarge
    "Message size exceeds configured limit.",
    WebSocketCloseCode.MessageTooBig);
```

The error code `FrameTooLarge` describes single-frame violations (`WebSocketFrameReader` correctly uses it). But here the violation is a multi-fragment **message** exceeding the configured `_maxMessageSize`. Should use `WebSocketError.MessageTooLarge`.

---

### W-2 [Protocol] WebSocketCloseStatus vs Writer — Inconsistent Close Reason Validation

**Agent:** websocket-protocol-architect
**Files:** `WebSocketFrame.cs` (lines 131–145), `WebSocketFrameWriter.cs` (lines 148–157)

`WebSocketCloseStatus` constructor throws `ArgumentException` if the close reason exceeds 123 UTF-8 bytes. `WebSocketFrameWriter.WriteCloseAsync` silently truncates the reason at character/codepoint boundaries to fit within 123 bytes. This means:

- `new WebSocketCloseStatus(code, longReason)` → throws
- `WriteCloseAsync(stream, code, longReason, ct)` → silently truncates

**Impact:** Callers using `CloseAsync` pass through `WebSocketConstants.ValidateCloseCode` but the reason goes directly to the writer, bypassing the `WebSocketCloseStatus` constructor. Silent truncation may surprise callers.

**Fix:** Either remove the validation from `WebSocketCloseStatus` (allow truncation everywhere) or validate in `WriteCloseAsync` and throw. Pick one path.

---

### W-3 [Transport] SendLockedAsync Always Marks Activity as Non-Application

**Agent:** network-transport-architect
**File:** `Runtime/WebSocket/WebSocketConnection.cs` (line 673)

```csharp
await send(stream, ct).ConfigureAwait(false);
TouchActivity(applicationMessage: false);  // always false
```

All sends (text, binary, ping, pong, close) are marked as non-application activity. The receive loop correctly marks data messages as `applicationMessage: true` (line 497). But outbound text/binary messages should also reset the application-message timestamp.

**Impact:** `IdleTimeout` fires based only on *received* application messages, not sent ones. If a client sends messages but receives no responses, the idle timeout fires prematurely.

**Fix:** Pass `applicationMessage` context through `SendLockedAsync`, or add a flag to distinguish data vs control sends.

---

### W-4 [Transport] Keep-Alive Ping Send Has TOCTOU Race with Closing State

**Agent:** network-transport-architect
**File:** `Runtime/WebSocket/WebSocketConnection.cs` (lines 704–738)

```csharp
if (State != WebSocketState.Open)
    break;                                    // check on line 704

// ... compute ping payload ...

await SendLockedAsync(
    allowClosingState: false,                 // rejects Closing state
    (stream, token) => _frameWriter.WritePingAsync(...),
    ct).ConfigureAwait(false);                // send on line 732
```

Between the state check on line 704 and `SendLockedAsync` on line 732, the state could transition to `Closing` (e.g., remote close frame received by receive loop). `SendLockedAsync` with `allowClosingState: false` throws `InvalidOperationException`.

**Impact:** Unhandled exception in keep-alive loop → caught by generic catch on line 766 → `FinalizeClose` with `PongTimeout` error code — masking the actual `Closing` transition.

**Fix:** Either use `allowClosingState: true` for pings (they're control frames), or catch `InvalidOperationException` in the ping send logic and break gracefully.

---

### W-5 [Protocol] WebSocketFrameReader CancellationToken Uses Stream Disposal for Cancellation

**Agent:** websocket-protocol-architect
**File:** `Runtime/WebSocket/WebSocketFrameReader.cs` (lines 69–74)

```csharp
cancellationRegistration = ct.Register(static state =>
{
    try { ((Stream)state).Dispose(); }
    catch { }
}, stream);
```

Cancellation disposes the stream, causing in-progress `ReadAsync` to throw `ObjectDisposedException`. This works but has a side effect: after cancellation, the stream is permanently destroyed. The connection cannot gracefully recover or send a close frame.

**Impact:** If a transient timeout triggers cancellation (e.g., handshake timeout), the stream is destroyed even though a graceful close might have been possible.

**Consideration:** This is a common pattern in .NET for cancelling stream reads, but worth documenting. Modern .NET code prefers `CancellationToken` overloads on `ReadAsync` directly.

---

### W-6 [Transport] WebSocketFrameWriter Dispose Does Not Release _maskingChunkBuffer or _headerBuffer

**Agent:** network-transport-architect
**File:** `Runtime/WebSocket/WebSocketFrameWriter.cs` (lines 283–289)

```csharp
public void Dispose()
{
    if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        return;

    _rng.Dispose();
}
```

The writer allocates `_maskingChunkBuffer` (8KB working buffer for chunked masking) and `_headerBuffer` (14 bytes) as `new byte[]` allocations, and `_maskKeyBatchBuffer` (256 bytes). These are not pooled from `ArrayPool`, so there's nothing to return. ✅ The `_rng` is the only disposable resource, which is correctly disposed.

**Revised:** Upon rechecking, the writer constructor uses `new byte[]` for these buffers, not `ArrayPool.Rent`. The dispose is correct. **Downgrading to Info.**

---

### W-6 (Revised) [Transport] RawSocketWebSocketTransport.Dispose Does Not Close Active Connections

**Agent:** network-transport-architect
**File:** `Runtime/WebSocket.Transport/RawSocketWebSocketTransport.cs` (lines 81–84)

```csharp
public void Dispose()
{
    Interlocked.Exchange(ref _disposed, 1);
}
```

`Dispose()` only sets a flag. It does not close/dispose any sockets or streams that may have been created by `ConnectAsync`. The returned `Stream` (and its underlying `Socket`) is owned by the caller (`WebSocketConnection`), so this is intentional — the transport is a factory, not an owner.

**However:** If `ConnectAsync` is called concurrently from two callers, and one disposes the transport mid-connection for the other, the disposed flag is checked first but there's a TOCTOU window: the second caller passes the check, then the first disposes, and `ConnectAsync` continues allocating a socket that will never be closed by the transport.

**Impact:** Low — `WebSocketConnection` is 1:1 with transport usage in practice.

---

### W-7 [Protocol] Handshake Validator Returns Error Body as byte[] Without Size Limit Check on Prefetched Data

**Agent:** websocket-protocol-architect
**File:** `Runtime/WebSocket/WebSocketHandshakeValidator.cs` (lines 530–571)

```csharp
int copy = Math.Min(prefetched.Length, maxBytes);
Buffer.BlockCopy(prefetched, 0, output, 0, copy);
written += copy;
```

`ReadErrorBodyAsync` correctly caps at `maxBytes` when copying from prefetched data. ✅ However, the `output` buffer is rented with `ArrayPool.Rent(maxBytes)` — if `maxBytes` is very large (caller overrides the default 4096), this could rent a huge buffer for an error body that may not exist.

**Impact:** Low — default is 4KB. Only matters if a caller passes a very large `maxErrorBodyBytes` value.

---

### W-8 [Transport] ConnectAsync Does Not Await ReceiveLoopTask on Failure After Loop Start

**Agent:** network-transport-architect
**File:** `Runtime/WebSocket/WebSocketConnection.cs` (lines 182–204)

If `RunKeepAliveLoopAsync` throws synchronously (before its first `await`), the exception would propagate to `ConnectAsync`'s catch block. But `_receiveLoopTask` is already running. `FinalizeClose` cancels `_lifecycleCts` which should stop the receive loop, but `_receiveLoopTask` is never awaited in the failure path.

**Impact:** Unobserved task exception if the receive loop also fails. The generic `FinalizeClose` in the catch block cancels the CTS, which should cause the receive loop to exit cleanly via `OperationCanceledException`.

**Revised:** The catch block calls `FinalizeClose` which cancels `_lifecycleCts`. The receive loop catches `OperationCanceledException`. The task may become unobserved but won't crash. **Acceptable but fragile.**

---

## 🟢 Info Findings

### I-1 No WebSocket-Specific Tests Exist

**Agents:** websocket-protocol-architect, network-transport-architect

No test files matching `*WebSocket*Test*` were found. The specs list 28+ test scenarios across phases 18.1–18.3. Tests are expected in Phase 18.7.

---

### I-2 [Protocol] Duplicate Fragmentation Validation in Reader + Assembler

**Agent:** websocket-protocol-architect

Both `WebSocketFrameReader.ReadAsync` (lines 170–180) and `MessageAssembler.TryAssemble` (lines 67–75, 92–99) validate fragmentation rules (continuation without active fragment, new data frame during fragment). The reader validates at parse time, the assembler re-validates at assembly time. Redundant but not harmful — defense in depth. The reader uses the `fragmentedMessageInProgress` parameter passed from the connection.

---

### I-3 [Transport] PrefetchedStream Does Not Override Dispose(bool) for Async Disposal

**Agent:** network-transport-architect
**File:** `Runtime/WebSocket/WebSocketConnection.cs` (lines 942–1074)

`PrefetchedStream` overrides `Dispose(bool)` but not `DisposeAsync`. When the connection calls `SafeDispose(_stream)` during `FinalizeClose`, it calls the synchronous `Dispose`. If the inner stream is a `SslStream`, the synchronous dispose is functional but may block briefly on shutdown.

---

### I-4 [Protocol] WebSocketHandshake BuildRequest Encodes to ASCII

**Agent:** websocket-protocol-architect
**File:** `Runtime/WebSocket/WebSocketHandshake.cs` (line 135)

```csharp
var requestBytes = Encoding.ASCII.GetBytes(builder.ToString());
```

HTTP/1.1 headers are ASCII by spec (RFC 7230 §3.2.6). Custom header values with non-ASCII characters would be silently corrupted. The `ValidateCustomHeader` method only checks for CRLF injection, not non-ASCII bytes.

**Recommendation:** Add a check in `ValidateCustomHeader` for non-ASCII characters, or document the limitation.

---

### I-5 [Transport] WebSocketReconnectPolicy Is a Placeholder

**Agent:** network-transport-architect
**File:** `Runtime/WebSocket/WebSocketReconnectPolicy.cs`

Explicitly marked as "Placeholder reconnect policy for phase 18.5 wiring." Correct — Phase 18.5 (Reconnection & Resilience) is a separate phase.

---

### I-6 [Protocol] WaitForPongAfterAsync Polls at Fixed 50ms Interval

**Agent:** websocket-protocol-architect
**File:** `Runtime/WebSocket/WebSocketConnection.cs` (line 789)

```csharp
await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
```

50ms polling interval is reasonable for pong detection but creates unnecessary timer churn under low-traffic conditions. Consider using a `TaskCompletionSource` triggered by pong receipt instead of polling.

---

## Spec Compliance Matrix

| Spec Requirement | Status | Notes |
|---|:---:|---|
| **Phase 18.1** | | |
| WebSocketOpcode enum with explicit hex values | ✅ | |
| WebSocketFrame readonly struct | ✅ | |
| Control frame 125-byte / FIN validation | ✅ | Reader + Writer |
| WebSocketCloseCode enum all RFC codes | ✅ | |
| Close code 1005/1006 wire-tx rejection | ✅ | `ValidateCloseCode(allowReservedLocal: false)` |
| Close reason 123-byte UTF-8 codepoint truncation | ✅ | Writer path |
| Reserved opcode rejection | ✅ | Reader + Constants |
| ReadExactAsync partial-read helper | ✅ | Clean EOF vs mid-read EOF |
| 64-bit MSB validation | ✅ | `0x8000000000000000UL` check |
| Masked server frame rejection | ✅ | Configurable flag, default true |
| RSV bit validation | ✅ | Configurable `allowedRsvMask` |
| Fragmentation rule validation | ✅ | Reader + Assembler (redundant) |
| Batch mask key generation | ✅ | 256-byte buffer, lock-protected |
| Chunked masking | ✅ | 8KB working buffer |
| Message fragmentation | ✅ | Configurable threshold |
| ComputeAcceptKey / GenerateClientKey | ✅ | SHA-1 + Base64 |
| EnsureSha1Available platform check | ✅ | |
| WebSocketError enum complete | ✅ | |
| MessageAssembler max size/count enforcement | ✅ | Pre-allocation check |
| MessageAssembler buffer return on error | ✅ | Fragment list cleared |
| ArrayPool usage for all buffers | ✅ | Reader, Writer, Assembler, Validator |
| **Phase 18.2** | | |
| RFC 6455 §4.1 wire format | ✅ | |
| Host header default-port omission | ✅ | |
| URI fragment stripping | ✅ | `UriComponents.PathAndQuery` |
| Sub-protocol negotiation | ✅ | |
| Extension header support | ✅ | |
| Custom header CRLF injection check | ✅ | |
| Reserved header protection | ✅ | HashSet |
| Key generation via WebSocketConstants | ✅ | |
| 101 status validation | ✅ | |
| Token-based Upgrade/Connection parsing | ✅ | `ContainsToken` splits + trims |
| Sec-WebSocket-Accept validation | ✅ | `FixedTimeEquals` |
| Non-101 error body bounded read | ✅ | 4KB default |
| Max header size limit | ✅ | 8KB default |
| **Phase 18.3** | | |
| State machine with atomic CAS transitions | ✅ | `Interlocked.CompareExchange` |
| AllowedTransitions map | ✅ | `None→Closed` added (C-3 fixed) |
| StateChanged event (snapshot pattern) | ✅ | |
| IDisposable + IAsyncDisposable | ✅ | Idempotent |
| Dispose calls Abort | ✅ | |
| DisposeAsync with 1s CloseAsync timeout | ✅ | |
| Close frame sent exactly once | ✅ | `_closeFrameSent` atomic flag |
| Handshake timeout via linked CTS | ✅ | |
| Receive loop as long-running async Task | ✅ | Not `Task.Run`, not `async void` |
| UTF-8 validation for text frames | ✅ | `StrictUtf8` after reassembly |
| Bounded receive queue with backpressure | ✅ | Custom `BoundedAsyncQueue` |
| Control frames bypass data queue | ✅ | Handled inline in receive loop |
| Send serialization via SemaphoreSlim(1,1) | ✅ | try/finally release |
| IOException + SocketException handling | ✅ | Both caught, mapped to WebSocketException |
| Clean close handshake (client + server init) | ✅ | |
| Close handshake timeout → Abort | ✅ | |
| 1005/1006 rejection in CloseAsync | ✅ | |
| Ping/pong keep-alive | ✅ | `Task.Delay`, monotonic time |
| Pong timeout → AbnormalClosure | ✅ | |
| IdleTimeout orthogonal to PongTimeout | ✅ | |
| IWebSocketTransport interface | ✅ | WebSocket assembly, no Transport dep |
| Transport ALPN empty for WSS | ✅ | `Array.Empty<string>()` |
| Transport asmdef excludes WebGL | ✅ | |
| `link.xml` preserves SHA1/SHA1Managed/RNG | ✅ | Both mscorlib + Algorithms |

---

## File-Level Coverage Matrix

| Sub-Phase | Spec'd File | Found | Status |
|-----------|-------------|-------|--------|
| 18.1 | `WebSocketFrame.cs` | ✅ | Complete |
| 18.1 | `WebSocketConstants.cs` | ✅ | Complete |
| 18.1 | `WebSocketFrameReader.cs` | ✅ | Complete |
| 18.1 | `WebSocketFrameWriter.cs` | ✅ | Complete |
| 18.1 | `MessageAssembler.cs` | ✅ | Complete |
| 18.1 | `TurboHTTP.WebSocket.asmdef` | ✅ | Complete |
| 18.1 | `link.xml` | ✅ | Complete |
| 18.2 | `WebSocketHandshake.cs` | ✅ | Complete |
| 18.2 | `WebSocketHandshakeValidator.cs` | ✅ | Complete |
| 18.3 | `WebSocketConnection.cs` | ✅ | Complete |
| 18.3 | `WebSocketConnectionOptions.cs` | ✅ | Complete |
| 18.3 | `WebSocketState.cs` | ✅ | Complete |
| 18.3 | `IWebSocketTransport.cs` | ✅ | Complete |
| 18.3 | `RawSocketWebSocketTransport.cs` | ✅ | Complete |
| 18.3 | `TurboHTTP.WebSocket.Transport.asmdef` | ✅ | Complete |

---

## Sub-Phase Implementation Status

| Sub-Phase | Status | Core Logic | Protocol Compliance | Tests |
|---|---|---|---|---|
| 18.1 WebSocket Framing | **Complete** | ✅ | ✅ | ❌ (Phase 18.7) |
| 18.2 HTTP Upgrade Handshake | **Complete** | ✅ | ✅ | ❌ (Phase 18.7) |
| 18.3 Connection & Lifecycle | **Complete** | ✅ | ✅ | ❌ (Phase 18.7) |

---

## Overall Assessment

Phase 18.1–18.3 is **architecturally well-implemented** with strong RFC 6455 compliance across the framing, handshake, and connection lifecycle layers. The code uses correct primitives throughout: `ArrayPool` for buffer management, `Interlocked.CompareExchange` for state transitions, `SemaphoreSlim` for send serialization, proper `CancellationToken` propagation, and defense-in-depth validation at both the reader and assembler layers.

The four critical findings are:
1. **C-1 (TryTransitionState public + throws)** — Most impactful. Breaks encapsulation and the Try pattern, and can crash `FinalizeClose`.
2. **C-3 (None→Closed missing)** — `Abort()` on a never-connected instance leaves state as `None` instead of `Closed`.
3. **C-4 (BoundedAsyncQueue Complete race)** — Edge-case reader hang under concurrent `Complete + Dequeue`.
4. **C-2 was downgraded** — Control frame lifecycle is correct after full analysis.

**Recommendation:** Fix C-1, C-3, and C-4 before proceeding to Phase 18.4. Address warnings W-1 through W-4 as part of the fix pass. No tests exist yet (expected in Phase 18.7), but the critical findings should be fixed before adding tests to avoid encoding bugs into test expectations.

---

## Revision 2 — Verification Re-Review (2026-02-21)

Full re-review of all remediated files by both specialist agents. All original critical and warning findings are **confirmed resolved**.

### Verification Matrix — Revision 1 Findings

| ID | Verified | Evidence |
|---|---|---|
| C-1 | ✅ Fixed | `TryTransitionState` is now `private`. `ValidateTransition` removed entirely. New `IsTransitionAllowed` method returns `false` instead of throwing. State machine is fully encapsulated. |
| C-2 | ✅ Fixed | `HandleControlFrameAsync` now disposes `frameLease` in a `finally` block (line 563), ensuring disposal even on exception. Receive loop no longer disposes the lease separately. |
| C-3 | ✅ Fixed | `AllowedTransitions[None]` now includes `WebSocketState.Closed` (line 19). `FinalizeClose` handles `None→Closed` transition (lines 858–862). `Abort()` on never-connected instance correctly transitions to Closed. |
| C-4 | ✅ Fixed | `BoundedAsyncQueue.Complete` now calls `_items.Release(_waitingReaders)` **inside the lock** (lines 1222–1223), eliminating the race between `Complete` and concurrent `DequeueAsync`. |
| W-1 | ✅ Fixed | `MessageAssembler.TryStageFragment` now uses `WebSocketError.MessageTooLarge` (line 165) instead of `FrameTooLarge`. |
| W-2 | ✅ Fixed | `WebSocketCloseStatus` constructor now uses `GetTruncatedCloseReasonByteCount` and silently truncates (lines 129–131) — consistent with the writer's behavior. Both paths now truncate at codepoint boundaries. |
| W-3 | ✅ Fixed | `SendLockedAsync` now accepts a `bool applicationMessage` parameter (line 649). `SendTextAsync` and `SendBinaryAsync` pass `applicationMessage: true` (lines 215, 227). Control frame sends pass `false`. `TouchActivity` correctly distinguishes data vs control. |
| W-4 | ✅ Fixed | Keep-alive ping send wrapped in `try/catch (InvalidOperationException) when (State != Open) { break; }` (lines 756–760). State race causes clean loop exit instead of masking as PongTimeout. |
| W-5 | ✅ Fixed | `CancellationTokenRegistration` stream-disposal pattern removed from `WebSocketFrameReader`. Cancellation relies on `CancellationToken` propagation through `ReadAsync` natively. |
| W-6 | N/A | Downgraded to Info in Revision 1 — writer buffers are `new byte[]`, not pooled. Dispose is correct. |
| W-7 | N/A | Low impact, documented. No change needed. |
| W-8 | ✅ Fixed | `ConnectAsync` failure path now calls `ObserveReceiveLoopTerminationAsync()` (line 203) to observe the receive loop task after `FinalizeClose`, preventing unobserved task exceptions. |

### Revision 2 — New Warning Findings (3)

#### R2-W1 [Protocol] ParseCloseStatus Allocates from Span via ToArray

**Agent:** websocket-protocol-architect
**File:** `Runtime/WebSocket/WebSocketConnection.cs` (line 595)

```csharp
reason = WebSocketConstants.StrictUtf8.GetString(
    payload.Slice(2).Span.ToArray(),   // ← allocates byte[]
    0,
    payload.Length - 2);
```

`Span.ToArray()` allocates a new `byte[]` copy of the close reason payload. Close frames are infrequent (once per connection lifetime).

**Impact:** Low — one allocation per connection close. Not a hot path.

---

#### R2-W2 [Transport] WaitForPongAfterAsync Still Uses 50ms Polling

**Agent:** network-transport-architect
**File:** `Runtime/WebSocket/WebSocketConnection.cs` (line 811)

50ms polling creates timer churn (20 timers/second per connection). Could be replaced with a `TaskCompletionSource` set by pong receipt. Acceptable for typical connection counts.

**Impact:** Low — optimization opportunity for high-density scenarios.

---

#### R2-W3 [Transport] Abort() Does Not Check Disposed State

**Agent:** network-transport-architect
**File:** `Runtime/WebSocket/WebSocketConnection.cs` (lines 317–325)

`Abort()` checks `State == Closed` but not `_disposed`. Calling after dispose doesn't throw `ObjectDisposedException`. Protected by `FinalizeClose`'s atomic `_finalized` guard — stylistic inconsistency, not a bug.

**Impact:** Low — no data corruption or undefined behavior.

---

### Revision 2 Overall Assessment

All Revision 1 critical and warning findings are confirmed fixed. Three new **low-severity warnings** found:

1. **R2-W1** — `ParseCloseStatus` allocates via `Span.ToArray()` (once per close)
2. **R2-W2** — Pong detection uses 50ms polling (acceptable, optimization opportunity)
3. **R2-W3** — `Abort()` doesn't throw after dispose (stylistic, protected by `_finalized`)

### Verdict: **PASS**

Phase 18.1–18.3 implementation review is complete. All blocking findings resolved. R2 warnings are non-blocking. Ready to proceed to Phase 18.4.

---

## Revision 3 — Full Phase 18 Review (Phases 18.4–18.6) — 2026-02-21

Comprehensive review of all remaining Phase 18 implementation files (Phases 18.4 Send/Receive API, 18.5 Reconnection & Resilience, 18.6 Unity Integration) by both specialist agents (**unity-infrastructure-architect** and **unity-network-architect**). Includes cross-verification of agent findings against actual codebase.

### Review Scope — Revision 3

**New files reviewed (8):**
- `Runtime/WebSocket/IWebSocketClient.cs` — Public API surface with events
- `Runtime/WebSocket/WebSocketClient.cs` — Default client implementation with reflection-based transport factory
- `Runtime/WebSocket/WebSocketMessage.cs` — Message type with eager text decode, detached copy pattern
- `Runtime/WebSocket/WebSocketException.cs` — Exception type mapping to UHttpErrorType
- `Runtime/WebSocket/AsyncBoundedQueue.cs` — Top-level bounded queue implementation
- `Runtime/WebSocket/WebSocketReconnectPolicy.cs` — Exponential backoff with jitter
- `Runtime/WebSocket/ResilientWebSocketClient.cs` — IWebSocketClient wrapper with reconnection loop
- `Runtime/WebSocket.Transport/RawSocketWebSocketTransport.cs` — DNS + Happy Eyeballs + TLS transport
- `Runtime/Unity.WebSocket/UnityWebSocketBridge.cs` — Main-thread dispatch bridge
- `Runtime/Unity.WebSocket/UnityWebSocketClient.cs` — MonoBehaviour component
- `Runtime/Unity.WebSocket/UnityWebSocketExtensions.cs` — Extension methods on UHttpClient

**Assembly definitions verified (3 new + 2 modified):**
- `Runtime/WebSocket/TurboHTTP.WebSocket.asmdef` — refs Core only, `noEngineReferences: true`
- `Runtime/WebSocket.Transport/TurboHTTP.WebSocket.Transport.asmdef` — refs Core+WebSocket+Transport, excludes WebGL
- `Runtime/Unity.WebSocket/TurboHTTP.Unity.WebSocket.asmdef` — refs Core+WebSocket+Unity
- `Runtime/TurboHTTP.Complete.asmdef` — Already includes all 3 WebSocket assembly refs
- `Tests/Runtime/TurboHTTP.Tests.Runtime.asmdef` — Already includes all 3 WebSocket assembly refs

### Agent Finding Cross-Verification

Several agent findings were invalidated upon direct codebase verification:

| Agent Finding | Verdict | Evidence |
|---|---|---|
| `TlsBackend` type in Transport assembly (Infra C1) | **False alarm** | `TlsBackend` enum is in `Runtime/Core/TlsBackend.cs` (TurboHTTP.Core), accessible to all assemblies |
| Missing `link.xml` (both agents) | **False alarm** | `Runtime/WebSocket/link.xml` exists, preserves SHA1, SHA1Managed, RandomNumberGenerator in both mscorlib and System.Security.Cryptography.Algorithms |
| Missing `PrefetchedStream` (Infra I3) | **False alarm** | Defined as nested class in `WebSocketConnection.cs` lines 1021–1168 |
| `BoundedAsyncQueue` naming (Infra I2) | **Partially valid** | The *nested* class in WebSocketConnection is `BoundedAsyncQueue<T>` and the *top-level* class is `AsyncBoundedQueue<T>` — these are separate types, confirming C-4 below |

---

### R3 Critical Issues

#### R3-C1: Duplicate `AsyncQueueCompletedException` and Queue Types

**Agents:** Both
**Files:** `WebSocketConnection.cs` (nested private classes) + `AsyncBoundedQueue.cs` (top-level internal classes)

`WebSocketConnection` defines a private nested `BoundedAsyncQueue<T>` and `AsyncQueueCompletedException`. Separately, `AsyncBoundedQueue.cs` defines a top-level internal `AsyncBoundedQueue<T>` and `AsyncQueueCompletedException`. These are distinct C# types despite similar names.

**Impact:** `catch (AsyncQueueCompletedException)` in `WebSocketClient.ReceiveAsync` catches the top-level type, while `WebSocketConnection` throws its nested type. If the wrong exception type propagates across the boundary, it will not be caught, surfacing as an unhandled exception.

**Fix:** Remove the nested duplicates from `WebSocketConnection`. Use the top-level `AsyncBoundedQueue<T>` and `AsyncQueueCompletedException` throughout. Rename `AsyncBoundedQueue` to `BoundedAsyncQueue` for spec consistency.

---

#### R3-C2: Reflection-Based Transport Creation Not IL2CPP-Safe

**Agent:** Both
**File:** `WebSocketClient.cs` lines 521–563

`Type.GetType("TurboHTTP.WebSocket.Transport.RawSocketWebSocketTransport, TurboHTTP.WebSocket.Transport")` followed by `Activator.CreateInstance` is reflection-heavy and will return null under IL2CPP stripping. The existing HTTP transport uses `[ModuleInitializer]` with `Register(Func<IHttpTransport>)` precisely to avoid this pattern.

**Impact:** WebSocket will fail to connect on IL2CPP builds (iOS/Android) when no explicit transport is provided.

**Fix:** Add a `[ModuleInitializer]` registration pattern in `TurboHTTP.WebSocket.Transport` (matching `TransportModuleInitializer` from Phase 3), or add `link.xml` entries preserving `RawSocketWebSocketTransport` and its constructors. Module initializer approach is strongly preferred for consistency.

---

#### R3-C3: Private Property Reflection in UnityWebSocketExtensions Not IL2CPP-Safe

**Agent:** Both
**File:** `UnityWebSocketExtensions.cs` lines 15–17

```csharp
private static readonly PropertyInfo ClientOptionsProperty = typeof(UHttpClient).GetProperty(
    "ClientOptions",
    BindingFlags.Instance | BindingFlags.NonPublic);
```

Accessing private members via reflection will fail under IL2CPP code stripping. If the property is stripped or renamed, the extension silently returns default options (no TLS settings, no default headers).

**Impact:** WebSocket connections created via `client.WebSocket(uri)` on IL2CPP builds will not inherit TLS/header settings from the HTTP client.

**Fix:** Either expose a public method/property on `UHttpClient` for WebSocket option extraction, or use `InternalsVisibleTo` from Core to Unity.WebSocket, or accept `WebSocketConnectionOptions` parameter directly.

---

### R3 Important Issues

#### R3-I1: `_lifecycleCts` Disposed While Receive Loop May Still Reference Token

**Agent:** Network
**File:** `WebSocketConnection.cs` lines 847–912

`FinalizeClose` cancels `_lifecycleCts` then disposes it. If the receive loop checks `ct.IsCancellationRequested` after disposal, this may throw `ObjectDisposedException` on some .NET implementations.

**Fix:** Defer `_lifecycleCts` disposal to `Dispose()`/`DisposeAsync()` after the receive loop task has been observed.

---

#### R3-I2: Separate Stream Writes for Header and Payload (TLS Record Overhead)

**Agent:** Network
**File:** `WebSocketFrameWriter.cs` lines 278–280

Header and payload are written as separate `WriteAsync` calls. For SslStream, each call results in a separate TLS record. The spec says "frame header + mask key should be written in a single buffer to minimize stream write calls."

**Fix:** For small payloads (control frames especially), combine header + masked payload into a single write.

---

#### R3-I3: `Span.ToArray()` Allocation in `ParseCloseStatus`

**Agent:** Network
**File:** `WebSocketConnection.cs` lines 610–614

`Span.ToArray()` creates an unnecessary heap allocation. .NET Standard 2.1 provides `Encoding.GetString(ReadOnlySpan<byte>)`.

**Fix:** Use `WebSocketConstants.StrictUtf8.GetString(payload.Slice(2).Span)`.

---

#### R3-I4: Duplicate UTF-8 Encode in `WebSocketClient.SendAsync(string)`

**Agent:** Network
**File:** `WebSocketClient.cs` lines 162–191

`WebSocketClient.SendAsync(string)` manually encodes to UTF-8 then calls the `ReadOnlyMemory<byte>` overload. `WebSocketConnection.SendTextAsync(string)` already exists and does the same encoding. The client bypasses the connection's string overload.

**Fix:** Delegate to `connection.SendTextAsync(string)` directly, eliminating the duplicate encoding path.

---

#### R3-I5: Resilient Client Doesn't Await Previous Receive Pump

**Agent:** Network
**File:** `ResilientWebSocketClient.cs` lines 231–268

`ConnectReplacementClientAsync` assigns `_receivePumpTask` to a new task without awaiting the previous one. The spec explicitly states "Prevent two receive loops running simultaneously during the transition."

**Fix:** Await `StopReceivePumpAsync()` in `ConnectReplacementClientAsync` before creating the new pump.

---

#### R3-I6: `WebSocketReconnectPolicy.None` Allocates RNG Unnecessarily

**Agent:** Network
**File:** `WebSocketReconnectPolicy.cs`

The static `None` instance (MaxRetries = 0) still allocates `RandomNumberGenerator`, `Random`, and a lock object that will never be used.

**Fix:** Add fast path in constructor: if `maxRetries == 0`, skip RNG initialization.

---

#### R3-I7: `OnDestroy` Blocks Main Thread for Up to 1 Second

**Agent:** Network
**File:** `UnityWebSocketClient.cs` lines 389–418

```csharp
client.CloseAsync(...).GetAwaiter().GetResult();
```

Synchronously blocks the Unity main thread during `OnDestroy`. Problematic during scene transitions or application shutdown.

**Fix:** Use `Abort()` directly in `OnDestroy()` instead of attempting synchronous close handshake. The `DisconnectAsync()` path handles clean close properly for intentional disconnects.

---

#### R3-I8: Double Message Copy in Unity Bridge Pipeline

**Agent:** Network
**File:** `UnityWebSocketBridge.cs` lines 192–221

`WebSocketClient.RunReceiveListenerAsync` creates a detached copy for the event, then the bridge creates another detached copy for main-thread dispatch. Two copies of every message payload.

**Fix:** Consider having the bridge subscribe to the connection's events directly, or have the client pass the original pooled message and let the bridge manage the single copy.

---

### R3 Minor Issues

| ID | File | Issue |
|---|---|---|
| R3-M1 | `WebSocketConstants.cs:59–71` | `GenerateClientKey` creates/disposes RNG per call. Acceptable (once per connection). |
| R3-M2 | `WebSocketHandshakeValidator.cs:516–528` | `FixedTimeEqualsAscii` allocates two byte arrays per handshake. Could use stackalloc. |
| R3-M3 | `WebSocketConnectionOptions.cs:94` | `Validate()` does not check sub-protocol token validity. Caught at connect time instead. |
| R3-M4 | `WebSocketConnection.cs:342–349` | `Dispose()` does not dispose `_frameWriter` (owns RNG). Minor resource leak. |
| R3-M5 | `WebSocketReconnectPolicy.cs:105–108` | Lock contention on shared `Default` instance under concurrent reconnects. Acceptable for infrequent reconnections. |

---

### R3 Correct Implementation Highlights

1. **IWebSocketClient interface** — Clean public API with events (`OnConnected`, `OnMessage`, `OnError`, `OnClosed`), `ConnectAsync` overloads, `SendAsync` overloads (string, `ReadOnlyMemory<byte>`, `byte[]`), `ReceiveAsync` (ValueTask), `CloseAsync`, `Abort`. Extends both `IDisposable` and `IAsyncDisposable`.
2. **WebSocketMessage** — Eager text decode, `ReadOnlyMemory<byte> Data`, `IDisposable` for pooled buffer return, `_returnToPool` flag for detached copies.
3. **WebSocketException** — Correctly extends `UHttpException`, maps `WebSocketError` to `UHttpErrorType`, `IsRetryable()` returns true for ConnectionClosed/PongTimeout/SendFailed/ReceiveFailed.
4. **ResilientWebSocketClient** — Events: `OnReconnecting(attempt, delay)`, `OnReconnected`. Policy-based retry loop with full client disposal before replacement.
5. **WebSocketReconnectPolicy** — Exponential backoff with crypto-seeded jitter, lock-protected RNG, `MaxDelay` cap, close code predicate filtering.
6. **RawSocketWebSocketTransport** — DNS with timeout, `HappyEyeballsConnector` reuse, empty ALPN (`Array.Empty<string>()`) for WSS, `TlsProviderSelector.GetProvider` for backend selection.
7. **UnityWebSocketBridge** — All events dispatched via `MainThreadDispatcher.ExecuteAsync`, detached message copies with regular `byte[]`, dispatcher saturation logging + `OnMessageDropped` event, `ReceiveAsCoroutine` with `LifecycleCancellation.Bind`.
8. **UnityWebSocketClient** — Full Inspector surface (URI, AutoConnect, AutoReconnect, SubProtocol, PingInterval, DisconnectOnPause). Lifecycle binding (OnEnable→connect, OnDisable→disconnect, OnDestroy→abort). `ResetStatics` for domain reload. `Application.quitting` handler aborts all active connections. UnityEvent callbacks for Inspector wiring.
9. **Assembly dependency graph** — All three assemblies correctly implement module isolation rules. No circular dependencies. `autoReferenced: false` on all. `noEngineReferences: true` on WebSocket and WebSocket.Transport.
10. **Complete.asmdef and Tests.asmdef** — Both already include references to all 3 WebSocket assemblies.

---

### R3 Platform Compatibility Assessment

| Platform | Status | Notes |
|----------|--------|-------|
| Editor (Mono) | **Works** | Full functionality expected |
| Standalone (Win/Mac/Linux) | **Works** | Full functionality expected |
| iOS (IL2CPP) | **At Risk** | R3-C2 (reflection transport), R3-C3 (private property reflection) |
| Android (IL2CPP) | **At Risk** | Same as iOS |
| WebGL | N/A | WebSocket.Transport excluded; core WebSocket assembly is WebGL-compatible |

---

### R3 Recommended Fix Order

1. **R3-C1** (duplicate types) — Build correctness / catch handler mismatch
2. **R3-C2** (reflection transport) — IL2CPP platform support
3. **R3-C3** (private property reflection) — IL2CPP platform support
4. **R3-I7** (OnDestroy blocking) — Main thread safety
5. **R3-I5** (dual receive pumps) — Spec compliance
6. **R3-I1** (CTS disposal) — Runtime stability
7. **R3-I4** (duplicate encoding) — Maintainability
8. **R3-I2** (TLS record overhead) — Performance
9. Remaining I and M issues — Non-blocking

---

### R3 Overall Assessment

Phase 18.4–18.6 implementation is **architecturally sound** with proper layering, clean module isolation, and correct protocol semantics. The three critical issues are all related to IL2CPP compatibility (R3-C1 is a type mismatch, R3-C2 and R3-C3 are reflection patterns). These must be resolved before any iOS/Android testing.

The important issues are primarily performance/correctness refinements (double copies, blocking main thread, duplicate encoding). None are blocking for Editor/Standalone use.

### Verdict: **CONDITIONAL PASS**

Phase 18 full implementation review is complete. Three critical IL2CPP issues must be resolved before platform validation. All R1/R2 findings remain resolved. Implementation is ready for Editor/Standalone use and Phase 18.7 test suite development.

---

## Revision 4 — Phase 18a Plan Review (2026-02-21)

Full plan review of `Development/docs/phases/phase-18a-websocket-advanced.md` by both specialist agents (**unity-infrastructure-architect** and **unity-network-architect**). This is a **plan review**, not a code review — evaluating the design document before implementation begins.

### Review Scope

All 7 sub-phases of Phase 18a:
- 18a.1: Extension Framework & `permessage-deflate`
- 18a.2: `IAsyncEnumerable` Streaming Receive
- 18a.3: Connection Metrics & Observability
- 18a.4: HTTP Proxy Tunneling
- 18a.5: Typed Message Serialization
- 18a.6: Connection Health & Diagnostics
- 18a.7: Test Suite

### Summary

| Severity | Count |
|----------|-------|
| Critical | 5 |
| High | 8 |
| Medium | 8 |
| Low | 7 |

**Verdict: CONDITIONAL APPROVAL — Revisions Required**

The plan is architecturally sound with proper module isolation and correct sub-phase decomposition. However, several critical issues around IL2CPP compatibility, memory management, RFC compliance gaps, and API design must be addressed before implementation.

---

### Critical Issues

#### PC-1: `TransformOutbound`/`TransformInbound` Return Type Creates Ownership Ambiguity (18a.1)

**Agents:** Both

The plan specifies `byte[]` return from `ArrayPool` with "Caller returns buffer after use." Problems:
1. **No valid data length** — `ArrayPool.Rent` returns buffers larger than requested. Caller cannot know valid range.
2. **Ownership ambiguity** — Caller can't distinguish pooled buffer from regular allocation.
3. **Inconsistent with codebase** — Existing code uses `ReadOnlyMemory<byte>` and the `WebSocketFrameReadLease` pattern.

**Fix:** Use `IMemoryOwner<byte>` (from `System.Buffers`) so ownership and valid length are explicit:
```csharp
IMemoryOwner<byte> TransformOutbound(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, out byte rsvBits);
IMemoryOwner<byte> TransformInbound(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, byte rsvBits);
```
Return `null` for passthrough. Caller disposes via `using`.

---

#### PC-2: RSV Bit Propagation Through Frame Reader and Message Assembler Missing (18a.1)

**Agents:** Both (Network primary)

The plan adds RSV bits to `WebSocketFrame` but does not address:
1. **Reader propagation** — `WebSocketFrameReader` reads RSV bits from wire (line 74) but **discards them**. Must propagate into `WebSocketFrame` constructor.
2. **Assembler propagation** — Per RFC 7692 Section 6, RSV1 is set only on the **first fragment** of a compressed message. The `MessageAssembler` must preserve RSV1 from the first fragment and pass it to the transform layer alongside the assembled payload.
3. **Continuation frame validation** — Continuation frames must NOT have RSV1 set (RFC 7692 Section 6.1).

**Fix:** Add explicit steps for: (a) reader propagating RSV bits into frame, (b) assembler preserving first-fragment RSV bits, (c) continuation frame RSV validation.

---

#### PC-3: `IAsyncEnumerable<T>` Compatibility Strategy Unclear (18a.2)

**Agents:** Both

The plan says "Requires `Microsoft.Bcl.AsyncInterfaces` NuGet package or `IAsyncEnumerable` polyfill." This is partially incorrect:
- .NET Standard 2.1 **does** include `IAsyncEnumerable<T>` natively
- Unity 2021.3 LTS with .NET Standard 2.1 profile includes it
- The actual risk is **IL2CPP code stripping** of async enumerable state machine infrastructure
- Adding NuGet dependencies to a UPM/Asset Store package has distribution friction

**Fix:** (a) Remove `Microsoft.Bcl.AsyncInterfaces` references — not needed, (b) add a concrete validation spike: test `await foreach` compilation and runtime in Unity 2021.3 LTS with IL2CPP on iOS/Android, (c) add `link.xml` entries if stripping occurs, (d) this should be a pre-implementation validation, not a post-implementation "verify."

---

#### PC-4: `permessage-deflate` Context Takeover Memory Model Underspecified (18a.1)

**Agents:** Both

Plan states "Context takeover costs ~64KB per connection (two 32KB sliding windows)." This is significantly underestimated:
1. **`DeflateStream` internal state** — zlib deflate state is ~256KB for compression at default level, ~44KB for decompression. Total with context takeover: **300–600KB per connection**, not 64KB.
2. **`DeflateStream` does not expose sliding window management** — Context takeover requires keeping `DeflateStream` alive across messages (never disposing it). The plan's `Reset()` method semantics are unclear.
3. **`DeflateStream.Flush()` behavior varies across runtimes** — Unity Mono may produce `Z_FINISH` instead of `Z_SYNC_FLUSH`, breaking RFC 7692 §7.2.1 trailing bytes handling.

**Fix:** (a) Revise memory estimate to 300–600KB, (b) make `no_context_takeover` the hard requirement for v1 (plan already suggests this but should mandate it), (c) gate full context takeover behind a separate implementation phase with benchmarking, (d) **add a pre-implementation spike** to verify `DeflateStream.Flush()` produces `Z_SYNC_FLUSH` output on Unity Mono.

---

#### PC-5: Proxy `NetworkCredential` Not IL2CPP-Safe (18a.4)

**Agent:** Infrastructure

`System.Net.NetworkCredential` may be stripped by IL2CPP linker if not used elsewhere. It's also a mutable reference type with lifecycle management complexity.

**Fix:** Replace with a custom immutable struct:
```csharp
public readonly struct ProxyCredentials
{
    public string Username { get; }
    public string Password { get; }
}
```
Also remove Digest auth claim — start with Basic only.

---

### High Issues

#### PH-1: Decompression Buffer Growth Strategy Unspecified (18a.1)

**Agent:** Infrastructure

Plan says "Decompression buffer starts at 4× compressed size, caps at `MaxMessageSize`." But:
- DEFLATE ratios can exceed 10:1 for compressible data
- No reallocation strategy specified when 4× is insufficient

**Fix:** Use chunk-based streaming decompression: read from `DeflateStream` in 16KB chunks, append to `ArrayBufferWriter<byte>`, check `MaxMessageSize` after each chunk (zip bomb protection).

---

#### PH-2: Metrics `CompressionRatio` Division-by-Zero and Missing Field (18a.3)

**Agent:** Network

`CompressionRatio` defined as "original / compressed, 1.0 if no compression" but the plan has no `UncompressedBytesSent` field. `BytesSent` includes frame overhead, making ratio computation incorrect.

**Fix:** Add `UncompressedBytesSent` (pre-compression application payload size). Define ratio as: `CompressedBytesSent > 0 ? (double)UncompressedBytesSent / CompressedBytesSent : 1.0`.

---

#### PH-3: Metrics Counters Need 32-bit IL2CPP-Safe Pattern (18a.3)

**Agent:** Infrastructure

`Interlocked.Add` on `long` fields is not truly atomic on 32-bit IL2CPP. Phase 6 `HttpMetrics` already solved this: public fields for `Interlocked` compatibility, `double` stored as `long` bits via `BitConverter.DoubleToInt64Bits`.

**Fix:** Document that 18a.3 must follow the Phase 6 `HttpMetrics` pattern exactly. Add 32-bit Android IL2CPP to validation plan.

---

#### PH-4: HTTP CONNECT Proxy Credentials Sent in Cleartext (18a.4)

**Agent:** Network

Basic auth over `http://` proxy sends credentials unencrypted. Plan excludes HTTPS proxies as "rare and complex."

**Fix:** (a) Document the security implication explicitly, (b) log a warning when Basic auth used over unencrypted proxy, (c) consider `SecureString` or at minimum document that `ProxyCredentials` stores passwords in plaintext in managed memory.

---

#### PH-5: Proxy Bypass List Pattern Matching Unspecified (18a.4)

**Agents:** Both

`BypassList` as `IReadOnlyList<string>` with "hostnames/patterns" — no matching semantics specified. Wildcard? CIDR? Regex?

**Fix:** Specify: exact hostname match + leading wildcard (`*.domain`) for v1. CIDR and port-specific matching deferred.

---

#### PH-6: `DeflateStream.Flush()` Behavior Differs Across Runtimes (18a.1)

**Agent:** Network

RFC 7692 §7.2.1 requires stripping trailing `0x00 0x00 0xFF 0xFF` (from `Z_SYNC_FLUSH`). On Unity Mono, `DeflateStream.Flush()` may produce `Z_FINISH` instead. `FlushMode` is not exposed in .NET Standard 2.1.

**Fix:** Add pre-implementation spike to verify flush behavior on Unity Mono. If `Z_SYNC_FLUSH` not produced, evaluate native zlib P/Invoke as fallback.

---

#### PH-7: Health Monitor RTT Should Be Event-Driven, Not Polling (18a.6)

**Agent:** Infrastructure

Phase 18 review (R2-W2) already identified 50ms pong polling as timer churn. Health monitor should subscribe to pong receipt events rather than polling.

**Fix:** Add internal `OnPongReceived(TimeSpan rtt)` event/callback on `WebSocketConnection`. Health monitor subscribes to it instead of polling.

---

#### PH-8: WebSocketFrame Constructor Change Is Breaking (18a.1)

**Agent:** Infrastructure

`WebSocketFrame` is a `readonly struct` with a public constructor. Adding `rsvBits` parameter is a breaking change.

**Fix:** Add overloaded constructor with `byte rsvBits = 0` default parameter, maintaining backward compatibility.

---

### Medium Issues

| ID | Sub-Phase | Issue | Fix |
|---|---|---|---|
| PM-1 | 18a.1 | Extension framework operates at message level, not frame level. Not stated. | Document explicitly: transforms operate post-assembly, per-frame extensions would need different hook point. |
| PM-2 | 18a.4 | `UseSystemProxy` platform availability unspecified. | Remove from 18a.4 (defer to future phase) or specify Standalone-only for v1. |
| PM-3 | 18a.5 | `JsonWebSocketSerializer<T>` generic type needs IL2CPP `link.xml` / `[Preserve]`. | Constrain to `where T : class` or document `link.xml` requirements. |
| PM-4 | 18a.6 | Health monitor rolling window thread safety unspecified. | Specify: lock-protected circular buffer, or single-threaded (keep-alive loop only). |
| PM-5 | 18a.4 | Missing proxy-specific `WebSocketError` codes. | Add `ProxyAuthenticationRequired`, `ProxyConnectionFailed`, `ProxyTunnelFailed`. |
| PM-6 | 18a.3 | `OnMetricsUpdated` event threading model unspecified. | Specify: fires on network thread. Unity consumers must marshal via `MainThreadDispatcher`. |
| PM-7 | 18a.5 | Typed serialization creates double payload copy (serialize → encode → compress). | Serializer should always produce `ReadOnlyMemory<byte>` (UTF-8 bytes directly), not `string`. |
| PM-8 | 18a.6 | Quality scoring algorithm undefined (factor weights, baseline method, "message delivery success rate" inapplicable to TCP). | Simplify: RTT + pong loss rate only. Remove "message delivery success rate." Specify weights and baseline sample count. |

---

### Low Issues

| ID | Sub-Phase | Issue |
|---|---|---|
| PL-1 | 18a.1 | `WebSocketExtensionParameters.Parse` complexity understated — RFC 7230 §3.2.6 quoted-string grammar needed. |
| PL-2 | 18a.7 | Test echo server `permessage-deflate` support is non-trivial — should be called out as separate effort. |
| PL-3 | 18a.5 | `ProtocolViolation` wrong error type for JSON deserialization failures — add `SerializationFailed`. |
| PL-4 | 18a.2 | `ReceiveAllAsync` reconnection semantics unclear — does enumerator block during reconnect? What about queued messages? |
| PL-5 | 18a.1 | Extension disposal order not specified — should be reverse of negotiation order. |
| PL-6 | 18a.2 | Concurrent enumeration rejection needs `Interlocked.CompareExchange` tracking + `DisposeAsync` cleanup. |
| PL-7 | 18a.7 | Missing test for compression + fragmentation interaction (large compressed message fragments). |

---

### Pre-Implementation Spikes Required

Before 18a.1 implementation begins:
1. **DeflateStream Flush Spike** — Verify `DeflateStream.Flush()` on Unity Mono produces `Z_SYNC_FLUSH` (not `Z_FINISH`). Test trailing `0x00 0x00 0xFF 0xFF` presence. If fails, evaluate native zlib P/Invoke.
2. **DeflateStream Memory Spike** — Benchmark actual memory usage with/without context takeover on Unity Mono and IL2CPP.

Before 18a.2 implementation begins:
3. **IAsyncEnumerable IL2CPP Spike** — Test `await foreach` with `async IAsyncEnumerable` method bodies in Unity 2021.3 LTS IL2CPP builds (iOS/Android). Verify no stripping.

---

### RFC Compliance Gaps

| RFC | Section | Plan Reference | Status |
|-----|---------|---------------|--------|
| RFC 7692 | Section 6 (RSV1 first-fragment-only) | 18a.1 Step 3/5 | **MISSING** — see PC-2 |
| RFC 7692 | Section 7.2.1 (Z_SYNC_FLUSH) | 18a.1 Step 4 | **AT RISK** — see PH-6 |
| RFC 7230 | Section 3.2.6 (parameter grammar) | 18a.1 Step 1 | **UNDERSTATED** — see PL-1 |

All other RFC references (6455 §9.1, 7692 §7.1/7.2/8, 1951) are correctly applied.

---

### Plan Revision Checklist

Before proceeding to implementation, revise the plan document to address:

- [ ] PC-1: Change transform return type to `IMemoryOwner<byte>`
- [ ] PC-2: Add RSV bit propagation through reader/assembler with RFC 7692 §6 semantics
- [ ] PC-3: Remove `Microsoft.Bcl.AsyncInterfaces` reference, add IL2CPP validation spike
- [ ] PC-4: Revise context takeover memory estimate (300–600KB), mandate `no_context_takeover` for v1
- [ ] PC-5: Replace `NetworkCredential` with custom immutable struct
- [ ] PH-1: Specify chunk-based streaming decompression
- [ ] PH-2: Add `UncompressedBytesSent` metric field
- [ ] PH-3: Document 32-bit IL2CPP metrics pattern requirement
- [ ] PH-4: Add cleartext credential security documentation
- [ ] PH-5: Specify proxy bypass matching semantics
- [ ] PH-6: Add `DeflateStream.Flush()` pre-implementation spike
- [ ] PH-7: Specify event-driven RTT measurement
- [ ] PH-8: Use overloaded constructor for backward compat
- [ ] PM-1 through PM-8: Address during plan revision or document for implementation phase
- [ ] Add pre-implementation spikes section to plan
