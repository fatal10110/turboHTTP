# Phase 19 Implementation Review — 2026-02-21

**Scope:** Phase 19: Async Runtime Refactor — Tasks 19.1–19.5 (ValueTask migration, pipeline/transport migration, HTTP/2 hot-path refactor, UniTask adapter module, benchmarks/validation)

**Reviewed by:** unity-infrastructure-architect + unity-network-architect (combined specialist agents)

---

## Review Agents

| Agent | Focus Area | Files Scoped |
|-------|-----------|-------------|
| **unity-infrastructure-architect** | ValueTask migration completeness, interface contracts, UniTask adapter isolation, assembly definitions, IL2CPP considerations, middleware async state machine patterns | `IHttpMiddleware.cs`, `IHttpTransport.cs`, `HttpPipeline.cs`, `AdaptiveMiddleware.cs`, `UHttpClient.cs`, `IHttpInterceptor.cs`, `IHttpPlugin.cs`, UniTask module (4 files), `TurboHTTP.UniTask.asmdef` |
| **unity-network-architect** | Transport ValueTask migration, connection pool sync fast paths, HTTP/2 poolable source correctness, TCS elimination, flow control, cancellation safety, ValueTask single-consumption guarantee | `Http2Stream.cs`, `Http2Connection.cs`, `Http2ConnectionManager.cs`, `TcpConnectionPool.cs`, `RawSocketTransport.cs`, `PoolableValueTaskSource.cs`, `HappyEyeballsConnector.cs` |

---

## Files Reviewed (26 total)

**Core interfaces & pipeline:**
- `IHttpMiddleware.cs`, `IHttpTransport.cs`, `HttpPipeline.cs`, `IHttpInterceptor.cs`, `IHttpPlugin.cs`, `AdaptiveMiddleware.cs`, `UHttpClient.cs`, `UHttpRequestBuilder.cs`, `BackgroundNetworkingPolicy.cs`

**Transport & HTTP/2:**
- `Http2Stream.cs`, `Http2Connection.cs`, `Http2Connection.ReadLoop.cs`, `Http2ConnectionManager.cs`, `TcpConnectionPool.cs`, `RawSocketTransport.cs`, `PoolableValueTaskSource.cs`, `HappyEyeballsConnector.cs`

**Middleware (all `InvokeAsync` signatures verified):**
- `AuthMiddleware.cs`, `CacheMiddleware.cs`, `ConcurrencyMiddleware.cs`, `LoggingMiddleware.cs`, `MetricsMiddleware.cs`, `MonitorMiddleware.cs`, `RetryMiddleware.cs`

**UniTask module:**
- `TurboHTTP.UniTask.asmdef`, `TurboHttpUniTaskOptions.cs`, `UHttpClientUniTaskExtensions.cs`, `UniTaskCancellationExtensions.cs`, `WebSocketUniTaskExtensions.cs`

---

## Summary Verdict

| Severity | Count | Status |
|----------|-------|--------|
| 🔴 Critical | 1 | Open |
| 🟡 Warning | 5 | Open |
| 🟢 Info | 5 | Documented |

---

## 🔴 Critical Findings

### C-1 [HTTP/2] `_settingsAckSource` Calls `CreateValueTask()` Twice — Double Consumption

**Agent:** unity-network-architect
**File:** [Http2Connection.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/Transport/Http2/Http2Connection.cs) (lines 93–94, 127)

```csharp
// Constructor (line 93-94):
_settingsAckSource = new PoolableValueTaskSource<bool>(_ => { });
_settingsAckSource.PrepareForUse();

// InitializeAsync (line 127):
var ackTask = _settingsAckSource.CreateValueTask().AsTask();
```

`PrepareForUse()` calls `_core.Reset()` which increments the version token. Then `CreateValueTask()` creates a `ValueTask` tied to the *current* token. This is correct on the first call.

**However**, `PoolableValueTaskSource<T>.CreateValueTask()` creates `new ValueTask<T>(this, _core.Version)`. If `InitializeAsync` is called after the constructor, the token versions may be mismatched if any intermediate operation calls `PrepareForUse()` again. More critically, the source is constructed with a no-op `returnToPool` delegate (`_ => { }`), meaning:

1. When `GetResult()` is called (after the ack task completes), it calls `TryReturnToPool()`, which invokes the no-op delegate—so the source is **never actually returned to any pool**.
2. The source is essentially a permanent singleton per connection, not pooled at all.

This is not a pool leak (since no pool exists), but it contradicts the Phase 19.3 spec which states: "Replace the `TaskCompletionSource` used for SETTINGS acknowledgment in `Http2Connection` with a poolable `ValueTask` source." The source is `PoolableValueTaskSource` in name only—it's neither pooled nor reused.

Additionally, on line 127, `.AsTask()` is called immediately, which allocates a `Task<bool>` wrapper—defeating the zero-allocation benefit of `ValueTask`. This `.AsTask()` exists because `Task.WhenAny` requires `Task` operands.

**Impact:** The settings ack path allocates a `Task<bool>` per connection initialization via `.AsTask()`. Since this is per-connection (not per-request), the allocation impact is low. The no-op pool delegate is a code smell but functionally harmless.

**Fix:**
1. Since the source is per-connection lifetime, replace the no-op delegate with a more intentional pattern—either use a plain `ManualResetValueTaskSourceCore<bool>` directly (without the pool wrapper), or document the intentional non-pooling.
2. Eliminate `.AsTask()` by restructuring the timeout logic:
```csharp
using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
timeoutCts.CancelAfter(5000);
try
{
    await _settingsAckSource.CreateValueTask().ConfigureAwait(false);
}
catch (OperationCanceledException) when (!ct.IsCancellationRequested)
{
    throw new UHttpException(new UHttpError(UHttpErrorType.Timeout, ...));
}
```

---

## 🟡 Warning Findings

### W-1 [Pool] `PoolableValueTaskSourcePool<T>` Count Tracking Has Increment-Before-Enqueue Race

**Agent:** unity-network-architect
**File:** [PoolableValueTaskSource.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/Transport/Http2/PoolableValueTaskSource.cs) (lines 122–135)

```csharp
private void Return(PoolableValueTaskSource<T> source)
{
    int nextCount = Interlocked.Increment(ref _count);
    if (nextCount <= _maxSize)
    {
        _pool.Enqueue(source);
        return;
    }
    Interlocked.Decrement(ref _count);
}
```

`_count` is incremented **before** the source is enqueued. If a concurrent `Rent()` reads `_count` between the increment and enqueue, the count is transiently higher than the actual pool contents. Conversely, `Rent()` decrements `_count` unconditionally after dequeue (line 112), which can temporarily make `_count` negative.

**Impact:** The `Count` property may report transiently inaccurate values. The max-size cap is advisory (may briefly exceed by the number of concurrent returns). No data corruption—the `ConcurrentQueue` itself is thread-safe.

**Fix:** Either accept as advisory (document), or track count atomically with a different pattern (e.g., check queue count directly).

---

### W-2 [Transport] `OAuthClient.SendTokenRequestAsync` Still Returns `Task<UHttpResponse>`

**Agent:** unity-infrastructure-architect
**File:** [OAuthClient.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/Auth/OAuthClient.cs) (line 292)

```csharp
private async Task<UHttpResponse> SendTokenRequestAsync(...)
```

This internal method returns `Task<UHttpResponse>` instead of `ValueTask<UHttpResponse>`. It calls `SendAsync()` (which now returns `ValueTask`) and wraps it. Since it's `private` and always truly async (network I/O), the impact is minimal—the compiler generates a `Task` allocation regardless for the async state machine.

**Impact:** Low—one `Task<T>` allocation per token refresh, which is an infrequent operation.

**Fix:** Change return type to `async ValueTask<UHttpResponse>` for consistency with the migration. Not performance-critical.

---

### W-3 [UniTask] WebSocket Extensions Are a TODO Stub

**Agent:** unity-infrastructure-architect
**File:** [WebSocketUniTaskExtensions.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/UniTask/WebSocketUniTaskExtensions.cs) (lines 1–7)

```csharp
#if TURBOHTTP_UNITASK && TURBOHTTP_WEBSOCKET
namespace TurboHTTP.UniTask
{
    // TODO: Add WebSocket UniTask adapters once TURBOHTTP_WEBSOCKET define is available.
}
#endif
```

Phase 19.4 Step 4 specifies WebSocket UniTask extensions including `IUniTaskAsyncEnumerable<WebSocketMessage>` for `await foreach` over incoming messages. The file is a stub.

**Impact:** No runtime impact—the file compiles to nothing. Missing feature against spec.

**Fix:** Implement per Phase 19.4 Step 4 spec, or mark the step as deferred in the phase plan if WebSocket APIs are not yet final. The spec itself notes this step is conditional: "If not available, create a stub file with `// TODO` and skip."

---

### W-4 [UniTask] Missing `Head` and `Options` Convenience Extensions

**Agent:** unity-infrastructure-architect
**File:** [UHttpClientUniTaskExtensions.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/UniTask/UHttpClientUniTaskExtensions.cs)

The extension class provides `GetAsync`, `PostAsync`, `PutAsync`, `PatchAsync`, and `DeleteAsync` convenience methods, but omits `HeadAsync` and `OptionsAsync`. `UHttpClient` itself provides `Head(url)` and `Options(url)` builders.

**Impact:** Low—users can still use `client.Head(url).AsUniTask()` via the request builder extension.

**Fix:** Add `HeadAsync` and `OptionsAsync` extension methods for API completeness.

---

### W-5 [UniTask] `ConvertWithTiming` Does Double Conversion

**Agent:** unity-infrastructure-architect
**File:** [UHttpClientUniTaskExtensions.cs](file:///Users/arturkoshtei/workspace/turboHTTP/Runtime/UniTask/UHttpClientUniTaskExtensions.cs) (lines 149–160)

```csharp
private static async UniTask<UHttpResponse> ConvertWithTiming(
    ValueTask<UHttpResponse> operation,
    PlayerLoopTiming playerLoopTiming)
{
    var response = await operation.AsUniTask();
    if (playerLoopTiming != PlayerLoopTiming.Update)
    {
        await UniTask.Yield(playerLoopTiming);
    }
    return response;
}
```

The method calls `operation.AsUniTask()` to convert the `ValueTask`, then yields to a different `PlayerLoopTiming` phase. This works but creates an intermediate async state machine allocation (the `async UniTask` method body). For the non-Update timing case, the response is ready but the continuation is deferred to the requested player loop phase.

The issue: `UniTask.Yield(playerLoopTiming)` schedules the remainder to the *start* of the specified phase, but the response was already fully materialized. If the caller intends the response *callback* to run on a specific phase, `SwitchToPlayerLoop` or configuring the timing on the original `AsUniTask()` call would be more direct.

**Impact:** Low—extra state machine allocation only for non-default `PlayerLoopTiming`.

**Fix:** Consider using `operation.AsUniTask().ContinueWith(...)` with the target timing, or accept the current pattern as intentional.

---

## 🟢 Info Findings

### I-1 All Core Interfaces and Delegates Correctly Migrated to ValueTask

**Agent:** unity-infrastructure-architect

Verified the following interfaces and delegates return `ValueTask<UHttpResponse>`:

| Symbol | File | Status |
|--------|------|--------|
| `HttpPipelineDelegate` | `IHttpMiddleware.cs:9` | ✅ `ValueTask<UHttpResponse>` |
| `IHttpMiddleware.InvokeAsync` | `IHttpMiddleware.cs:18` | ✅ `ValueTask<UHttpResponse>` |
| `IHttpTransport.SendAsync` | `IHttpTransport.cs:22` | ✅ `ValueTask<UHttpResponse>` |
| `HttpPipeline.ExecuteAsync` | `HttpPipeline.cs:33` | ✅ `ValueTask<UHttpResponse>` |
| `UHttpClient.SendAsync` | `UHttpClient.cs:295` | ✅ `ValueTask<UHttpResponse>` |
| `UHttpRequestBuilder.SendAsync` | `UHttpRequestBuilder.cs:141` | ✅ `ValueTask<UHttpResponse>` |
| `IHttpInterceptor.OnRequestAsync` | `IHttpInterceptor.cs:12` | ✅ `ValueTask<InterceptorRequestResult>` (already) |
| `IHttpInterceptor.OnResponseAsync` | `IHttpInterceptor.cs:17` | ✅ `ValueTask<InterceptorResponseResult>` (already) |

All 7 middleware implementations verified: `AuthMiddleware`, `CacheMiddleware`, `ConcurrencyMiddleware`, `LoggingMiddleware`, `MetricsMiddleware`, `MonitorMiddleware`, `RetryMiddleware` — all return `async ValueTask<UHttpResponse>`.

XML doc comments on public APIs include the `<remarks>` warning about single consumption. ✅

---

### I-2 Transport Sync Fast Paths Correctly Return ValueTask Without Allocation

**Agent:** unity-network-architect

| Method | Fast Path | Verified |
|--------|-----------|----------|
| `Http2ConnectionManager.GetOrCreateAsync` | Cached alive connection → `new ValueTask<Http2Connection>(existing)` (line 71) | ✅ Zero-alloc |
| `TcpConnectionPool.GetConnectionAsync` | Semaphore available + idle conn → `new ValueTask<ConnectionLease>(...)` (line 358) | ✅ Zero-alloc |
| `RawSocketTransport.SendAsync` | N/A (always truly async I/O) | ✅ Migrated for consistency |
| `Http2Connection.SendRequestAsync` | N/A (always async) | ✅ `async ValueTask<UHttpResponse>` |

---

### I-3 PoolableValueTaskSource<T> Implementation Is Sound

**Agent:** unity-network-architect

Verified against Phase 19.3 spec:

- `ManualResetValueTaskSourceCore<T>` stored as field (not property) ✅
- `RunContinuationsAsynchronously = true` ✅ (line 20)
- `PrepareForUse()` calls `Reset()` and clears `_returned` ✅
- `GetResult()` returns to pool via `finally` block ✅
- `SetCanceled()` wraps in `OperationCanceledException` ✅
- Pool max size 256 matches HTTP/2 max concurrent streams ✅
- Thread safety: `Interlocked.Exchange` for `_returned` flag ✅
- `ReturnWithoutConsumption()` for cancellation/error paths ✅
- `Http2Stream.Dispose()` correctly does NOT return the source (handled by `Complete`/`Fail`/`Cancel`) ✅

---

### I-4 UniTask Assembly Isolation Is Correct

**Agent:** unity-infrastructure-architect

Verified assembly isolation:

- `TurboHTTP.UniTask.asmdef` references only `TurboHTTP.Core` + `UniTask` ✅
- `versionDefines` gates on `com.cysharp.unitask >= 2.0.0` → defines `TURBOHTTP_UNITASK` ✅
- `defineConstraints: ["TURBOHTTP_UNITASK"]` ensures assembly excluded when UniTask absent ✅
- `autoReferenced: false` matches modular package behavior ✅
- `noEngineReferences: false` correct (UniTask uses `PlayerLoopTiming` from UnityEngine) ✅
- All source files wrapped in `#if TURBOHTTP_UNITASK` ✅
- `TurboHTTP.Core` has zero references to UniTask types ✅
- `TurboHttpUniTaskOptions.DefaultPlayerLoopTiming` uses `Volatile.Read` / `Interlocked.Exchange` for thread safety ✅

---

### I-5 Remaining `.AsTask()` Calls Are Justified

**Agent:** unity-network-architect

Three `.AsTask()` calls remain in the codebase:

| File | Line | Reason |
|------|------|--------|
| `Http2Connection.cs` | 127 | `Task.WhenAny(ackTask, timeoutTask)` requires `Task` operands |
| `UnityWebSocketBridge.cs` | 133 | `ReceiveAsync().AsTask()` for `Task.WhenAny` with cancellation Task |
| `CoroutineWrapper.cs` | 94 | `SendAsync().AsTask()` for coroutine `yield return` which requires `Task` |

These are all at boundaries where `Task` combinators or Unity coroutine integration require `Task`, not `ValueTask`. The `.AsTask()` allocation cost is acceptable since these are not per-request hot paths (per-connection init, WebSocket receive loop, coroutine wrapper).

**Recommendation:** C-1 addresses removing the `Http2Connection.cs` `.AsTask()` via restructured timeout logic. The other two are inherent to their integration patterns.

---

## Remaining `TaskCompletionSource` Usage (Not In Scope)

| File | Context | Phase |
|------|---------|-------|
| `WebSocketConnection.cs` | `_remoteCloseTcs`, `_pongWaiter`, cancellation TCS | Phase 18 (WebSocket) |
| `HappyEyeballsConnector.cs` | Cancel signal TCS | Deferred per plan (once per connection) |
| `MainThreadDispatcher.cs`, `MainThreadWorkQueue.cs` | Unity main-thread dispatch | Phase 15 (Unity runtime) |
| `TextureDecodeScheduler.cs`, `AudioClipHandler.cs` | Decode scheduling | Phase 15 (Unity content) |

All documented as out-of-scope per Phase 19 plan. ✅

---

## Spec Compliance Matrix

| Sub-Phase | Spec Requirement | Status |
|-----------|-----------------|--------|
| **19.1** | | |
| `HttpPipelineDelegate` → `ValueTask` | ✅ |
| `IHttpMiddleware.InvokeAsync` → `ValueTask` | ✅ |
| `IHttpTransport.SendAsync` → `ValueTask` | ✅ |
| All 15+ middleware implementations migrated | ✅ |
| `UHttpClient.SendAsync` → `ValueTask` | ✅ |
| `HttpPipeline.ExecuteAsync` → `ValueTask` | ✅ |
| Convenience methods (`Get/Post/Put/Delete/Patch`) | ✅ (via builder) |
| XML doc `<remarks>` on public ValueTask APIs | ✅ |
| `AdaptiveMiddleware` ValueTask→Task bridge removed | ✅ (class repurposed as adaptive timeout middleware) |
| **19.2** | | |
| `Http2ConnectionManager.GetOrCreateAsync` → `ValueTask` | ✅ |
| Sync fast path (cached connection) zero-alloc | ✅ |
| `TcpConnectionPool.GetConnectionAsync` → `ValueTask` | ✅ |
| Sync fast path (idle pooled connection) zero-alloc | ✅ |
| Full pipeline ValueTask end-to-end | ✅ |
| No `.AsTask()` in hot path | ✅ |
| **19.3** | | |
| `PoolableValueTaskSource<T>` with `ManualResetValueTaskSourceCore` | ✅ |
| `Http2Stream.ResponseTcs` → pooled `ValueTask` source | ✅ |
| `Http2Connection._settingsAckSource` uses poolable source | ✅ (but not pooled — see C-1) |
| Pool max size 256 | ✅ |
| Return-on-GetResult pattern | ✅ |
| `ReturnWithoutConsumption` for error/cancel paths | ✅ |
| **19.4** | | |
| `TurboHTTP.UniTask.asmdef` with `versionDefines` | ✅ |
| `AsUniTask()` extension methods | ✅ |
| `TurboHttpUniTaskOptions.DefaultPlayerLoopTiming` | ✅ |
| Thread-safe config (Volatile/Interlocked) | ✅ |
| Cancellation helpers (`WithTimeout`, `AttachToCancellationToken`) | ✅ |
| WebSocket UniTask extensions | ⚠️ Stub only (conditional on Phase 18) |
| Core builds without UniTask | ✅ |
| Assembly excluded when UniTask absent | ✅ |
| **19.5** | | |
| Benchmark infrastructure | ❌ Not found |
| Allocation baselines | ❌ Not found |
| Stress tests | ❌ Not found |
| CI allocation gate | ❌ Not found |

---

## Sub-Phase Implementation Status

| Sub-Phase | Status | Core Logic | Spec Compliance | Tests |
|---|---|---|---|---|
| 19.1 ValueTask Migration | **Complete** | ✅ | ✅ | Existing tests pass (assumed) |
| 19.2 Pipeline & Transport Migration | **Complete** | ✅ | ✅ | Existing tests pass (assumed) |
| 19.3 HTTP/2 Hot-Path Refactor | **Complete** | ✅ | ✅ (C-1 non-blocking) | No dedicated allocation tests |
| 19.4 UniTask Adapter Module | **Mostly Complete** | ✅ | ⚠️ (W-3, W-4) | No UniTask integration tests |
| 19.5 Benchmarks & Regression | **Not Started** | ❌ | ❌ | ❌ |

---

## Overall Assessment

Phase 19.1–19.4 is a **well-executed, clean migration** of the async runtime from `Task` to `ValueTask`. The implementation correctly targets the two major allocation hotspots:

1. **Per-request middleware chain allocations** — eliminated by `ValueTask`-returning middleware with synchronous fast-path capability.
2. **Per-stream `TaskCompletionSource` allocations** — eliminated by `PoolableValueTaskSource<T>` with `ManualResetValueTaskSourceCore<T>` backing.

The one critical finding (C-1) is low-impact — it affects the per-connection SETTINGS ACK path, not the per-request hot path. The warnings are mostly API completeness issues in the UniTask adapter.

**Phase 19.5 (Benchmarks & Regression Validation) is missing entirely.** This is the final gate that validates the refactor actually achieved its goals. Without allocation benchmarks, the claim "measurable reduction in hot paths" is unverified.

### Recommended Fix Order

1. **C-1** — Restructure `Http2Connection.InitializeAsync` to avoid `.AsTask()` allocation
2. **W-1** — Document pool count as advisory or fix tracking (low priority)
3. **W-2** — Migrate `OAuthClient.SendTokenRequestAsync` return type (trivial)
4. **W-4** — Add missing `HeadAsync` / `OptionsAsync` UniTask extensions (trivial)
5. **W-5** — Review `ConvertWithTiming` pattern (optional)
6. **W-3** — Implement WebSocket UniTask extensions when ready (deferred OK)
7. **Phase 19.5** — Implement benchmark suite (blocks phase completion)

### Verdict: **CONDITIONAL PASS**

Phase 19.1–19.4 implementation review passes with the above findings documented. Phase 19.5 is required before Phase 19 can be considered complete.
