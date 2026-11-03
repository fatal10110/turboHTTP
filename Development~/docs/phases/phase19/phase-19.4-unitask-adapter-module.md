# Phase 19.4: Optional UniTask Adapter Module

**Depends on:** 19.1 (ValueTask Migration), 19.2 (Pipeline & Transport Migration)
**Estimated Effort:** 3-4 days

---

## Step 0: Create TurboHTTP.UniTask Assembly Definition

**File:** `Runtime/UniTask/TurboHTTP.UniTask.asmdef` (new)

Required behavior:

1. Create `TurboHTTP.UniTask` assembly definition.
2. References: `TurboHTTP.Core`, `UniTask` (via `com.cysharp.unitask`).
3. Set `autoReferenced` to `false` to match modular package behavior.
4. Gate the assembly via `versionDefines` — the assembly should only compile when `com.cysharp.unitask` is present in the project.

Implementation constraints:

1. Use `versionDefines` in the `.asmdef` to define a scripting define symbol (e.g., `TURBOHTTP_UNITASK`) when `com.cysharp.unitask >= 2.0.0` is detected.
2. The assembly must **not** be included in builds where UniTask is not installed — `versionDefines` handles this automatically with the `.asmdef` reference resolution.
3. Do not use `#if` preprocessor directives in Core assembly to conditionally reference UniTask — the separation is at the assembly level, not the preprocessor level.
4. Set `noEngineReferences` to `false` — UniTask integration requires `UnityEngine` for `PlayerLoopTiming`.

---

## Step 1: Implement Core Extension Methods

**File:** `Runtime/UniTask/UHttpClientUniTaskExtensions.cs` (new)

Required behavior:

1. Provide `AsUniTask()` extension methods for all public `ValueTask<UHttpResponse>`-returning APIs:
   - `UHttpClient.SendAsync(...).AsUniTask()`
   - `UHttpClient.GetAsync(...).AsUniTask()`
   - `UHttpClient.PostAsync(...).AsUniTask()`
   - `UHttpClient.PutAsync(...).AsUniTask()`
   - `UHttpClient.DeleteAsync(...).AsUniTask()`
   - `UHttpClient.PatchAsync(...).AsUniTask()`
2. Extension methods must pass through the existing `CancellationToken` from the request — do **not** create new `CancellationTokenSource` instances.
3. The adapters must be zero-allocation when the underlying `ValueTask` completes synchronously (UniTask's `ValueTask` → `UniTask` conversion handles this natively).

Implementation constraints:

1. UniTask provides built-in `AsUniTask()` extension on `ValueTask<T>` — the extension methods here are convenience wrappers that chain the conversion, not reimplementations.
2. Use `static` extension method class with `[ExtensionMethod]`-compatible structure.
3. Each extension method should accept the same parameters as the original `UHttpClient` method plus an optional `PlayerLoopTiming` override.
4. XML doc comments must explain that these are zero-overhead wrappers over the Core `ValueTask` API.

---

## Step 2: Implement PlayerLoopTiming Configuration

**File:** `Runtime/UniTask/TurboHttpUniTaskOptions.cs` (new)

Required behavior:

1. Create `TurboHttpUniTaskOptions` static configuration class.
2. Expose `DefaultPlayerLoopTiming` property — default value: `PlayerLoopTiming.Update`.
3. Allow users to configure the default timing globally: `TurboHttpUniTaskOptions.DefaultPlayerLoopTiming = PlayerLoopTiming.FixedUpdate`.
4. Extension methods from Step 1 use `TurboHttpUniTaskOptions.DefaultPlayerLoopTiming` when no explicit timing is passed.

Implementation constraints:

1. `PlayerLoopTiming` determines which Unity player loop phase the UniTask continuation runs on — `Update` is the safest default for UI updates, `FixedUpdate` for physics-related networking.
2. The configuration must be thread-safe — use `volatile` or `Interlocked` for the backing field since it may be read from background threads.
3. Support per-request override: `client.GetAsync(url).AsUniTask(PlayerLoopTiming.LateUpdate)` should override the global default.
4. Do not cache or store `PlayerLoopTiming` values — read the current value at the point of conversion.

---

## Step 3: Implement UniTask-Based Cancellation Helpers

**File:** `Runtime/UniTask/UniTaskCancellationExtensions.cs` (new)

Required behavior:

1. Provide helper methods for common UniTask cancellation patterns with TurboHTTP requests.
2. `WithTimeout(UniTask<UHttpResponse>, TimeSpan)` — wraps UniTask with UniTask-native timeout (avoid creating `CancellationTokenSource` + `Timer`).
3. `AttachToCancellationToken(UniTask<UHttpResponse>, CancellationToken)` — links UniTask cancellation to an external token.
4. Helpers must use UniTask's built-in cancellation mechanisms, not `Task`-based workarounds.

Implementation constraints:

1. UniTask has built-in `Timeout`, `AttachExternalCancellation` — use these rather than reimplementing.
2. These helpers are convenience methods — they should be thin wrappers with no additional logic.
3. Error mapping: UniTask's `OperationCanceledException` should propagate cleanly — do not catch and re-wrap.
4. Do not create `CancellationTokenSource` instances in the helpers — this defeats the zero-allocation purpose.

---

## Step 4: Add WebSocket UniTask Extensions (if Phase 18 complete)

**File:** `Runtime/UniTask/WebSocketUniTaskExtensions.cs` (new, conditional)

Required behavior:

1. If WebSocket APIs from Phase 18 are available, provide UniTask extensions for WebSocket operations:
   - `IWebSocketClient.ConnectAsync(...).AsUniTask()`
   - `IWebSocketClient.SendAsync(...).AsUniTask()`
   - `IWebSocketClient.ReceiveAsync(...).AsUniTask()`
   - `IWebSocketClient.CloseAsync(...).AsUniTask()`
2. WebSocket receive loop integration with UniTask's `IUniTaskAsyncEnumerable<T>` — enable `await foreach` over incoming messages using UniTask's async enumerable.
3. Pass through existing `CancellationToken` — do not create new sources.

Implementation constraints:

1. This step is conditional on Phase 18 WebSocket APIs being merged. If not available, create a stub file with `// TODO: Implement after Phase 18 merge` and skip.
2. The `IUniTaskAsyncEnumerable<WebSocketMessage>` adapter should wrap the `Channel<WebSocketMessage>` reader from Phase 18 — do not create a separate receive mechanism.
3. Gate this file with `#if TURBOHTTP_WEBSOCKET` preprocessor directive (defined by `TurboHTTP.WebSocket` assembly).
4. Reference both `TurboHTTP.WebSocket` and `UniTask` assemblies — ensure the `.asmdef` is updated.

---

## Step 5: Verify Assembly Isolation

Required behavior:

1. Verify that `TurboHTTP.Core` builds successfully **without** `com.cysharp.unitask` installed.
2. Verify that `TurboHTTP.UniTask` assembly is excluded from compilation when UniTask is not present.
3. Verify that installing `com.cysharp.unitask` enables `TurboHTTP.UniTask` automatically via `versionDefines`.
4. Verify that all extension methods work correctly with a real UniTask integration test.
5. Test the full request lifecycle: `UHttpClient.GetAsync(url).AsUniTask()` → await in a `MonoBehaviour` → verify response on main thread.

Implementation constraints:

1. Test in two configurations: (a) Clean project without UniTask installed, (b) Project with UniTask installed.
2. In configuration (a), verify no compilation errors and no references to UniTask types in Core assembly IL.
3. In configuration (b), verify extension methods appear in IntelliSense and function correctly.
4. Verify IL2CPP compatibility of the UniTask adapter on iOS/Android builds.

---

## Verification Criteria

1. `TurboHTTP.Core` compiles and runs correctly without `com.cysharp.unitask` — zero dependency.
2. Installing `com.cysharp.unitask` automatically enables `TurboHTTP.UniTask` assembly.
3. `AsUniTask()` extension methods produce zero-allocation conversions from `ValueTask` (verified by allocation profiling).
4. `PlayerLoopTiming` configuration works — continuations run on the configured player loop phase.
5. `CancellationToken` is passed through correctly — cancelling a request token cancels the UniTask.
6. No `CancellationTokenSource` instances are created by the adapter — truly zero-overhead bridging.
7. Full request lifecycle works end-to-end in a Unity project with UniTask installed.
8. IL2CPP builds succeed with the UniTask adapter module.
