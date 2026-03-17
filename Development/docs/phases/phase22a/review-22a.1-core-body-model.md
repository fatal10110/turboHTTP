# Phase 22a.1 Review: Core Body Model and Public API Split

## Review Round 2 (Verification Pass)

**Review date:** 2026-03-18
**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Verdict:** PASSED — all 6 critical issues resolved, no new blocking issues

---

## Implementation Completeness

All 15 spec steps are implemented and match the specification:

| Step | Component | Status |
|------|-----------|--------|
| 1 | `RequestBodyReplayability` enum | Complete |
| 2 | `UHttpRequestBody` abstract + 5 concrete implementations | Complete |
| 3 | `RequestBodyReadSession` | Complete |
| 4 | `IResponseBodySource` interface | Complete |
| 5 | `ResponseBodyStream` adapter | Complete |
| 6 | `UHttpStreamingResponse` | Complete |
| 7 | Updated `IHttpHandler` contract | Complete |
| 8 | `BufferedResponseCollectorHandler` rewrite | Complete |
| 9 | `BufferedDispatchBridge` + `StreamingDispatchBridge` | Complete |
| 10 | `UHttpClient` API update (`SendBufferedAsync`/`SendStreamingAsync`) | Complete |
| 11 | `UHttpRequest` body update (`Body` → `Content`) | Complete |
| 12 | `SingleReaderChannel<T>` (SPSC async channel in Transport) | Complete |
| 13 | `MockResponseBodySource` | Complete |
| 14 | `FileRequestBody` + `FileRequestBuilderExtensions` | Complete |
| 15 | `link.xml` updates | Complete |

Supporting items verified:
- `InternalsVisibleTo("TurboHTTP.Files")` on Core's `AssemblyInfo.cs`
- `CapabilityEnforcedInterceptor` and `ObservedHandler` stub migration in `PluginContext.cs`
- Compile-surface migration sweep (UniTask, JSON, OAuthClient, FileDownloader, UnityExtensions, AudioClipHandler, Texture2DHandler, CoroutineWrapper)
- `LegacyHttpHandlerCompatExtensions` test-only compat layer
- All `IHttpHandler` implementations updated to new contract
- Test files for all new types

---

## Round 1 Issue Resolution Status

### CRITICAL Issues — All Resolved

| # | Issue | Status | How Fixed |
|---|-------|--------|-----------|
| C-1 | CancellationToken.None in body drain | **FIXED** | `_cancellationToken` stored in `BufferedResponseCollectorHandler` constructor, passed to `body.ReadAsync` and `body.GetTrailersAsync` |
| C-2 | AttachRequestRelease TOCTOU race | **FIXED** | Disposed check moved inside the `do/while` CAS loop. If disposal races, CAS fails, loop retries, disposed check fires, `releaseAction()` invoked immediately |
| C-3 | StreamingDispatchBridge lease transfer gap | **FIXED** | `RetainForResponse` + `AttachRequestRelease` + `TrySetResult` now all happen inside `OnResponseStartAsync` with a `try/finally` that disposes the response and releases the retain on failure. `SendStreamingAsync` no longer does `AttachRequestRelease` |
| C-4 | RequestBodyReadSession dispose ordering | **FIXED** | New `_onDisposeFailure` callback. When `Stream.Dispose()` throws, `_onDisposeFailure` fires (calls `FailSessionAndRelease` which sets `_sessionFaulted`), and `_onDispose` is NOT called. `ThrowIfSessionFaulted()` prevents reopen after disposal failure |
| C-5 | ResponseBodyStream.Dispose disposes response | **FIXED** | Decoupled ownership. `DisposeStreamCore()` only calls `_owner.AbortBody()` (not `_owner.Dispose()`). Consumer must dispose `UHttpStreamingResponse` separately |
| C-6 | HandlerCallbackSafetyWrapper contract | **FIXED** | `IHttpHandler.cs` now has explicit XML doc contract: "Once `OnResponseStartAsync` returns successfully, the handler owns the body source. Subsequent failures surface from `IResponseBodySource` operations, not `OnResponseError`." And `OnResponseError` doc: "Called only if dispatch fails before `OnResponseStartAsync` completes successfully." |

### WARNING Issues — All Resolved or Mitigated

| # | Issue | Status | How Fixed |
|---|-------|--------|-----------|
| W-1 | FileRequestBody.Length allocates per access | **FIXED** | `_length` cached as `readonly long` in constructor via `new FileInfo(path).Length`. Property returns field directly |
| W-2 | SingleReaderChannel cancellation token lost | **FIXED** | `_pendingCancellationToken` field stored in `ReadAsync`, read in `CancelPendingRead`, passed to `new OperationCanceledException(cancellationToken)` |
| W-3 | UHttpRequestBody no finalizer safety | **FIXED** | `OwnedMemoryRequestBody` and `StreamRequestBody` have finalizers calling `DisposeCoreFromFinalizer()`. `DisposeCoreOnce` guards with `_resourcesDisposed` to prevent double-dispose |
| W-4 | NullHandler missing DisposeAsync | **FIXED** | `DisposeBodyAsync` now calls both `body.Abort()` and `await body.DisposeAsync()` |
| W-5 | MockTransport/RecordReplay SendAsync | **MITIGATED** | Both marked `[Obsolete]` and delegate to `TransportDispatchHelper.CollectResponseAsync`. Both implement `DispatchAsync` as the primary path |
| W-6 | IL2CPP Spike (Step 0) | **OPEN** | Process item — no code fix possible. Must be confirmed on physical devices before milestone closes |
| W-7 | Http2Stream SegmentedBuffer copy | **DEFERRED** | Known, eliminated in 22a.3+ when `Http2ResponseBodySource` is implemented |

### NOTE Issues — All Resolved or Acknowledged

| # | Issue | Status |
|---|-------|--------|
| N-1 | link.xml incomplete | **FIXED** — Core: `RequestBodyReadSession`, `BufferedResponseBodySource`, `IResponseBodySource`, `UHttpStreamingResponse`, `IAsyncDisposable`, `ValueTask<RequestBodyReadSession>`. Transport: open generic + concrete `SingleReaderChannel<ReadOnlyMemory<byte>>` |
| N-2 | EmptyRequestBody per-call allocation | **NOT ADDRESSED** — acceptable deferral, low-priority empty path |
| N-3 | FactoryRequestBody dead catch block | **FIXED** — removed |
| N-4 | ResponseBodyStream.DisposeAsync SuppressFinalize | **FIXED** — `GC.SuppressFinalize(this)` added |
| N-5 | Duplicate completion methods | **FIXED** — only `EnsureCompleted` remains |
| N-6 | SingleReaderChannel double-fault guard | **FIXED** — `if (_completed) return` prevents double-completion |
| N-7 | LegacyHttpHandlerCompatExtensions sync block | **DOCUMENTED** — comment explains test-only sync shim constraint |
| N-8 | MockResponseBodySource thread-safety | **FIXED** — comment: "Test-only single-reader source: sequential chunk cursors are intentionally not synchronized" |

---

## New Findings (Round 2)

### From unity-network-architect

**NEW-W-1 (WARNING): RetryDetectorHandler disposes body on 5xx concurrently with transport writes**

`RetryDetectorHandler.OnResponseStartAsync` calls `body.DisposeAsync()` immediately on 5xx. This relies on body source being safe for concurrent dispose. Validated safe for `BufferedResponseBodySource`; should be validated for streaming body sources when introduced in 22a.2/22a.3.

**NEW-W-2 (WARNING): RecordingHandler records only buffered body data**

`RecordingHandler.OnResponseStartAsync` captures body data only via `body.TryGetBufferedData()`. Streaming responses will replay with empty bodies. Should be documented as a known limitation of record/replay with streaming responses.

**NEW-W-3 (WARNING): Theoretical race in HandlerCallbackSafetyWrapper between inner callback return and `_terminated` set**

Between `_inner.OnResponseStartAsync` returning and `Interlocked.Exchange(ref _terminated, 1)`, a concurrent `OnResponseError` could theoretically slip through. Not exploitable with current single-path dispatch architecture — `OnResponseError` is delivered from `ContinueWith` which only fires after the dispatch task completes. Safe for current architecture.

### From unity-infrastructure-architect

**NEW-N-1 (NOTE): `ResponseBodyStream.Dispose(bool)` relies on BCL `Stream.Dispose(true)` for `GC.SuppressFinalize`**

Correct via inheritance but fragile — depends on BCL contract rather than explicit call. No action required.

**NEW-N-2 (NOTE): `BufferedDispatchBridge.AttachCompletion` lacks early `IsCompleted` check**

Unlike `StreamingDispatchBridge`, the buffered bridge does not check `ResponseTask.IsCompleted` before fault/cancel paths. Not a correctness issue — `TrySet*` methods are idempotent. Slightly redundant delegate allocation in error cases.

---

## Remaining Open Items

| # | Severity | Item | Target |
|---|----------|------|--------|
| W-6 | WARNING | IL2CPP device validation of `IAsyncDisposable` + `ValueTask<T>` | Before 22a.1 milestone close |
| W-7 | WARNING | Http2Stream SegmentedBuffer → byte[] copy for large responses | 22a.3 (`Http2ResponseBodySource`) |
| N-2 | NOTE | EmptyRequestBody per-call stream allocation | Future optimization pass |
| NEW-W-1 | WARNING | RetryDetectorHandler concurrent dispose safety for streaming sources | Validate in 22a.2/22a.3 |
| NEW-W-2 | WARNING | RecordingHandler records empty body for streaming responses | Document as known limitation |
| NEW-W-3 | WARNING | HandlerCallbackSafetyWrapper theoretical race window | Safe for current architecture; revisit if multi-threaded transports added |

---

## Verdict

**PASSED** — Phase 22a.1 may proceed to 22a.2.

All 6 critical issues from Round 1 are resolved. All warning and note items are either fixed, mitigated, or documented with clear deferral targets. The 3 new warnings from Round 2 are non-blocking (2 are validation items for future phases, 1 is architecture-safe).

The only actionable pre-milestone item is **W-6** (IL2CPP device validation), which is a process gate, not a code issue.

---

## Review Round 1 (Initial Review)

**Review date:** 2026-03-17
**Verdict:** BLOCKED — 6 critical issues identified

<details>
<summary>Round 1 details (click to expand)</summary>

### CRITICAL Issues (Round 1)

#### C-1: CancellationToken.None in body drain

**File:** `Runtime/Core/Pipeline/ResponseCollectorHandler.cs`, lines 137, 145
**Found by:** Both agents

`BufferedResponseCollectorHandler.CollectAsync` passes `CancellationToken.None` to `ReadAsync` and `GetTrailersAsync`. This means a request timeout (enforced by `RawSocketTransport` via `CancellationTokenSource.CancelAfter`) cannot abort a slow body drain. If the server trickles bytes slowly, the buffered send hangs indefinitely after headers arrive.

**Fix:** Store the caller's `CancellationToken` in `BufferedResponseCollectorHandler` and forward it to both `body.ReadAsync` and `body.GetTrailersAsync`. Both `BufferedDispatchBridge` and the collector must propagate the token from the dispatch call site.

---

#### C-2: AttachRequestRelease TOCTOU race leaks connection lease

**File:** `Runtime/Core/UHttpStreamingResponse.cs`, lines 86–103
**Found by:** Both agents

If `Dispose()` runs between the disposed check (line 90) and the CAS loop (line 102):
1. `InvokeDisposeCallbacks()` executes `Interlocked.Exchange(ref _onDispose, null)?.Invoke()`, setting `_onDispose = null`
2. The CAS sees `prior == null` and `_onDispose == null`, so it succeeds — registering `combined` onto a field that `Dispose` will never read again
3. The `releaseAction` is never invoked. Connection lease leaked.

**Fix:** Move the disposed check inside the CAS loop.

---

#### C-3: StreamingDispatchBridge lease transfer gap

**File:** `Runtime/Core/Pipeline/StreamingDispatchBridge.cs`, `Runtime/Core/UHttpClient.cs` line 433
**Found by:** Both agents

`AttachRequestRelease` is called in `SendStreamingAsync` *after* `CollectResponseAsync` returns. A window exists: if `TrySetResult` succeeds inside `OnResponseStartAsync` but `SendStreamingAsync` throws before executing `response.AttachRequestRelease(...)`, the response sits in the TCS task with no connection release registered.

**Fix:** Attach the connection release action to `UHttpStreamingResponse` inside `OnResponseStartAsync`, before `TrySetResult` is called.

---

#### C-4: RequestBodyReadSession dispose ordering breaks retry safety

**File:** `Runtime/Core/Internal/RequestBodyReadSession.cs`, lines 50–66
**Found by:** Both agents

When `Stream.Dispose()` throws, `_onDispose` fires releasing the session, but the stream is in unknown state. A retry on a `Replayable` stream will seek a corrupted stream.

**Fix:** Do not invoke `_onDispose` when stream disposal throws; use a separate `_onDisposeFailure` callback that marks the body as faulted.

---

#### C-5: ResponseBodyStream.Dispose disposes the owning response

**File:** `Runtime/Core/ResponseBodyStream.cs`, line 121
**Found by:** Network architect

Stream disposal kills the entire `UHttpStreamingResponse` — inverted ownership from standard .NET `Stream` semantics.

**Fix:** Decouple: `ResponseBodyStream.Dispose` should only abort the body source, not dispose the entire response.

---

#### C-6: HandlerCallbackSafetyWrapper terminates before body ownership transfer

**File:** `Runtime/Core/Pipeline/HandlerCallbackSafetyWrapper.cs`, lines 56–62
**Found by:** Infrastructure architect

`_terminated = 1` set on sync success path causes subsequent `OnResponseError` calls to be silently dropped.

**Fix:** Document the transport contract: errors after response start go through `IResponseBodySource.Fault`, not `OnResponseError`.

---

### WARNING Issues (Round 1)

| # | Issue |
|---|-------|
| W-1 | FileRequestBody.Length allocates FileInfo on every access |
| W-2 | SingleReaderChannel.CancelPendingRead loses the cancellation token |
| W-3 | UHttpRequestBody has no finalizer safety net for abandoned sessions |
| W-4 | NullHandler.OnResponseStartAsync calls Abort but not DisposeAsync |
| W-5 | MockTransport.SendAsync / RecordReplayTransport.SendAsync still exist |
| W-6 | IL2CPP Spike (Step 0) — no evidence of completion |
| W-7 | Http2Stream.CompleteAsync copies SegmentedBuffer to byte[] |

### NOTE Issues (Round 1)

| # | Issue |
|---|-------|
| N-1 | link.xml incomplete for new generic instantiations |
| N-2 | EmptyRequestBody.OpenReadSessionCoreAsync allocates per call |
| N-3 | FactoryRequestBody dead catch block |
| N-4 | ResponseBodyStream.DisposeAsync missing GC.SuppressFinalize |
| N-5 | Duplicate completion methods in BufferedResponseCollectorHandler |
| N-6 | SingleReaderChannel.Complete silently overwrites error on double fault |
| N-7 | LegacyHttpHandlerCompatExtensions blocks with GetAwaiter().GetResult() |
| N-8 | MockResponseBodySource inconsistent thread-safety annotations |

</details>
