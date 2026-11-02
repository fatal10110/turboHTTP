# 2026-11 Sync-over-async sites and the ban regression test (#181)

## What

Finishes #181. The `ValueTaskAwaiter(`1).GetResult` ban (PR #233) and the Android bridge (PR #265)
were already on main; this change decides every remaining site, removes the blocking one, and adds
an automated test for the ban itself.

| Site | Decision |
| --- | --- |
| `UHttpStreamingResponse.Dispose` | **Fixed.** The fully-read path no longer blocks on a pending `_bodySource.DisposeAsync()`. `StartBodyDispose` consumes the ValueTask with `AsTask()` (no allocation when already completed); a completed one aborts on failure and releases inline as before; a pending one is handed to `CompleteDisposeWhenBodyDisposed`, a `ContinueWith` continuation (static lambda, `ExecuteSynchronously`) that aborts the source if the dispose failed and then runs the dispose callbacks (request release), the same order `DisposeAsync` uses after its await. Both baseline lines removed. |
| `HandlerCallbackSafetyWrapper.OnResponseStartAsync` | **Fixed.** The `IsCompletedSuccessfully` + `GetResult` fast path is gone; every callback goes through `AwaitResponseStartAsync`, which runs to the end synchronously for a completed callback (still a completed, non-allocating ValueTask). Baseline line removed. |
| `Http2Connection.FinalizeResourceDispose` | **Fixed.** Both callers pass a completed task (`Dispose` checks `IsCompleted`; `DisposeResourcesAfterShutdownAsync` awaited it), so the `GetResult` only observed a fault. Replaced by reading `Exception` when `IsFaulted`; it can never wait. Baseline line removed. |
| `BufferedDispatchBridge` / `StreamingDispatchBridge` | **Kept baselined.** `GetResult` runs only inside `if (task.IsCanceled)` on an already completed task, to rethrow its `OperationCanceledException` and tell a plain cancellation from a typed one; a canceled `Task` exposes no exception any other way. Rationale comment made precise. |
| `IL2CPPCompatibility.CheckAsyncAwait` | **Kept baselined, rationale corrected.** The old comment said it blocks "a worker"; it blocks the caller. The class is internal and only the self-tests call `ValidateAll`; the `Task.Run` body has no `SynchronizationContext`, so the wait cannot deadlock. |
| `SaeaStream` / `PollSelectStream` sync `Read`/`Write` | **Kept baselined, rationale corrected.** The old comment said the transport only calls the async overloads; `BouncyCastleTlsStream`'s synchronous `Read`/`Write` and its best-effort close_notify in `Dispose` (`DrainOutputHoldingGate`, `FlushPendingOutput`) do reach them. Its handshake does not (#169; `BouncyCastleHandshakeThreadPoolTests` asserts zero sync I/O). Replacing them with `NotSupportedException` would break those sync paths. |

Out of scope and untouched: `DecompressionHandler`, `ResponseBodyStream.Read`, `TcpConnectionPool`
`_scavengeTask?.Wait`, `UnityWebSocketBridge`, `CoroutineWrapper` baselines.

## Files

- `Runtime/Core/UHttpStreamingResponse.cs`, `Runtime/Core/Pipeline/HandlerCallbackSafetyWrapper.cs`,
  `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`, `Runtime/Core/IL2CPPCompatibility.cs` (comment)
- `Tools~/ci/analyzer-baseline.txt` (4 RS0030 entries removed, 3 rationales rewritten)
- `Tools~/ci/test_banned_symbols.py` (new), `Tools~/ci/check.sh` (runs it under `--full`, so PR CI's lint job runs it)
- `Tests/Runtime/Core/UHttpStreamingResponseSyncDisposeTests.cs`, `Tests/Runtime/Pipeline/HandlerCallbackSafetyWrapperTests.cs` (new)

## Decisions

- Sync `Dispose` with a pending body dispose: continue on an owned continuation instead of blocking,
  aborting, or dropping the task. Abort would kill a connection the pending dispose is returning to the
  pool; blocking is the defect; the continuation is the owner CLAUDE.md section 4 asks for. Phase 22a
  ("`IDisposable.Dispose()` is a synchronous fallback ... does not attempt drain") is kept: no drain
  is started. Observable change: when the body dispose is still running, `Dispose` returns before the
  request lease is released; it is released when the body dispose finishes. Exceptions in the
  continuation (abort, callbacks) go to `TurboDiagnostics.Log` with the exception type only.
- Criterion 2 ("a deliberately-added violation in a scratch file fails the shadow build") is an
  automated Python test, not a manual procedure: `test_banned_symbols.py` builds a scratch project
  with `Shadow.props` (same analyzer package and `BannedSymbols.txt`) through `shadow_build.build`
  (same command and diagnostic parser) and asserts RS0030 on each of `ValueTaskAwaiter.GetResult`,
  `ValueTaskAwaiter<T>.GetResult`, `TaskAwaiter(.<T>).GetResult`, `Task<T>.Result`, `Task.Wait`, none
  on `await`, and that `shadow_build`'s baseline comparison treats every hit as new. It needs only the
  .NET SDK (about 2 s), no Unity DLLs.

## Verification

- `Tools~/ci/test_banned_symbols.py`: 4 tests pass. With the two `ValueTaskAwaiter` lines deleted from
  `BannedSymbols.txt` it fails (`no RS0030 on scratch lines [7, 8]`, and the new-warning count is 4 of 6),
  so it catches the regression #181 describes.
- `Tools~/ci/check.sh`: OK (no new analyzer warning after removing the four baseline lines).
- `UHttpStreamingResponseSyncDisposeTests.Dispose_WhileBodyDisposeIsPending_ReturnsAtOnce_AndReleasesWhenItCompletes`
  fails on the old code: the old `Dispose` blocks until a 10 s watchdog fails the pending dispose, then
  the "Dispose waited" and "released only after the body dispose" assertions fail. On the new code the
  watchdog is removed before it can fire, so the test does not depend on timing.
- Unity scoped runs: see the PR "Local gates" section.
- Not run: the full Unity EditMode/PlayMode suites (host-editor periodic run), IL2CPP player builds, devices.
