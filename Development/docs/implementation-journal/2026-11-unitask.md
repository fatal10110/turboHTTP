# 2026-11 UniTask adapters: cancel through the request, split out WebSocket (#97, #116)

## What
- **#97.** `UniTaskCancellationExtensions.WithTimeout`/`AttachToCancellationToken` take an already-started
  `UniTask<UHttpResponse>`, so they can never reach the request's token. They are now `[Obsolete]` (they only
  stop waiting) and leak-safe: when the timeout or token wins, a continuation on the abandoned source becomes
  the single owner of its result and disposes a late response (a late fault is observed). They no longer
  wrap UniTask's `Timeout()` (player-loop `DeltaTime`, time-scale dependent) but a wall-clock
  `CancellationTokenSource.CancelAfter`, and keep the exception types (`TimeoutException`,
  `OperationCanceledException`). The supported paths cancel the request itself: the token passed to
  `GetAsync`/`AsUniTask` reaches the transport, and `UHttpRequest.WithTimeout` is enforced by the transport
  (`UHttpErrorType.Timeout`). `Documentation~/Modules/UniTask.md` documents both and warns that UniTask's own
  `Timeout()`/`AttachExternalCancellation()` leave the request running (pass `taskCancellationTokenSource:`).
- `AsUniTask` (client and request) now await the operation in one private `AwaitResponse`; a response is
  disposed if the non-`Update` `UniTask.Yield` throws after it arrived.
- **#116.** `WebSocketUniTaskExtensions.cs` moved to `Runtime/UniTask.WebSocket/` (assembly
  `TurboHTTP.UniTask.WebSocket`: Core, WebSocket, UniTask; same `versionDefines`/`defineConstraints`, not
  auto-referenced, namespace still `TurboHTTP.UniTask`). `TurboHTTP.UniTask` references only Core and UniTask.
- **Compile fixes found by reading UniTask 2.5.11 source** (the module had never been compiled: no host
  project installs UniTask and the shadow build drops it): UniTask's `ValueTask.AsUniTask()` exists only for
  `UNITASK_NETCORE || UNITY_2022_3_OR_NEWER` (`UniTask.AsValueTask.cs`), so on 2021.3 the request path and the
  WebSocket `ReceiveAsync`/`MoveNextAsync`/`DisposeAsync` adapters did not compile; they now await through
  private helpers. Inside `namespace TurboHTTP.UniTask` the bare simple name `UniTask` binds to the namespace
  (C# name lookup checks namespace members before compilation-unit usings), so `UniTask.Yield` and the
  non-generic `UniTask` return type are written `Cysharp.Threading.Tasks.UniTask`. `UniTask<T>` is fine (a
  namespace never matches a name with type arguments). The client path also stops using `Task.AsUniTask()`,
  which calls `TaskScheduler.FromCurrentSynchronizationContext()` and throws off the main thread.

## Files
- `Runtime/UniTask/{TurboHTTP.UniTask.asmdef, UniTaskCancellationExtensions.cs, UHttpClientUniTaskExtensions.cs}`
- `Runtime/UniTask.WebSocket/` (new folder: asmdef, moved `WebSocketUniTaskExtensions.cs`, metas)
- `Tests/Runtime/UniTask/` (new `TurboHTTP.Tests.UniTask` asmdef, gated on `UNITY_INCLUDE_TESTS` + `TURBOHTTP_UNITASK`;
  `UniTaskCancellationTests.cs`)
- `Tests/Editor/ModuleAutoReferenceTests.cs` (new asmdef stays explicit; references and define gates of both halves)
- `Tools~/ci/check_repo.py` (`ALLOWED_REFS`: split entries, test assembly)
- Docs: `Documentation~/Modules/UniTask.md`, `README.md`, `Third-Party Notices.md`, `Development/docs/00-overview.md`

## Decisions
- No new public timeout overloads: Core already has the transport-enforced `UHttpRequest.WithTimeout`, and the
  token parameter already reaches the transport. Deprecating the race wrappers is honest about what they can do.
- CHANGELOG not edited: `check_repo` requires the top entry to match `package.json`'s version, so the release
  owner adds the user-visible note (WebSocket adapters need `TurboHTTP.UniTask.WebSocket`) when cutting the next version.
- `Development/docs/agents/architecture.md` is not edited here (agent-docs lanes only): #312.

## Deferred
- #311: no gate compiles the UniTask assemblies or runs `UniTaskCancellationTests`.
- #312: architecture.md asmdef table.

## Verification
- `'./Tools~/ci/check.sh'`: OK (the shadow build skips both UniTask assemblies, as before).
- Unity EditMode `--filter 'ModuleAutoReferenceTests'`: 22/22 passed (Unity 2021.3.45f2, project without UniTask).
- **Not run:** `UniTaskCancellationTests` and any compile of the UniTask assemblies (UniTask is not installed in
  any project we have; installing it in the lane project was not permitted from the agent environment). The
  UniTask API use was checked by hand against UniTask 2.5.11 source. No PlayMode, IL2CPP or device run.
