# Phase 14.3: Background Networking on Mobile

**Depends on:** Phase 11
**Assembly:** `TurboHTTP.Mobile`, `TurboHTTP.Unity`, `TurboHTTP.Tests.Runtime`
**Files:** 9 new, 3 modified

---

## Step 1: iOS Background Execution Bridge

**Files:**
- `Runtime/Mobile/iOS/IosBackgroundTaskBridge.cs` (new)
- `Runtime/Mobile/iOS/IosBackgroundTaskBindings.cs` (new)
- `Runtime/Unity/UnityExtensions.cs` (modify)

### Technical Spec

Required contract:

```csharp
public interface IBackgroundExecutionScope : IAsyncDisposable
{
    string ScopeId { get; }
    DateTime StartedAtUtc { get; }
    TimeSpan RemainingBudget { get; }
    CancellationToken ExpirationToken { get; }
}
```

iOS flow:

1. On request start, call `BeginBackgroundTask` and capture native task id.
2. Register expiration callback that triggers `ExpirationToken`.
3. Wrap request transport execution in linked token (`requestToken + expirationToken`).
4. Always call `EndBackgroundTask` in `finally`, exactly once per native id.
5. Emit timeline events:
   - `mobile.bg.acquire.start`
   - `mobile.bg.acquire.success`
   - `mobile.bg.expired` (if fired)
   - `mobile.bg.release`

Lifecycle invariants:

1. Multiple concurrent requests each receive distinct background task ids.
2. Retry/redirect hops share same background scope when within same logical request chain.
3. Background scope is no-op for Editor and non-iOS platforms.

### Implementation Constraints

1. Native bridge failure must not crash request pipeline; fall back to foreground semantics.
2. Task-id registry must be thread-safe and leak-free under cancellation storms.
3. Avoid `async void` callbacks from native expiration event; use safe dispatch queue.
4. No Unity API calls from non-main thread in bridge internals.

---

## Step 2: Android Background Execution Bridge

**Files:**
- `Runtime/Mobile/Android/AndroidBackgroundWorkBridge.cs` (new)
- `Runtime/Mobile/Android/AndroidBackgroundWorkConfig.cs` (new)
- `Plugins/Android/src/main/java/com/turbohttp/background/TurboHttpBackgroundWorker.kt` (new)
- `Plugins/Android/src/main/java/com/turbohttp/background/TurboHttpBackgroundPlugin.kt` (new)
- `Plugins/Android/src/main/AndroidManifest.xml` (new)
- `Plugins/Android/mainTemplate.gradle` (modify)

### Technical Spec

Execution modes:

1. `InProcessGuard`: keep in-flight request alive while app is backgrounded (short windows).
2. `DeferredWork`: enqueue resumable request payload to `WorkManager`.
3. `ForegroundTransfer`: optional hook for long uploads/downloads requiring foreground service.

Native bridge packaging:

1. Unity Android plugin must include `androidx.work:work-runtime-ktx` dependency pin via Gradle template.
2. Manifest merge must declare required `WorkManager` provider/services only once (avoid duplicate-authority collisions).
3. C# bridge communicates through a minimal JNI surface (enqueue, cancel, query status) exposed by `TurboHttpBackgroundPlugin`.
4. `TurboHttpBackgroundWorker` restores serialized request envelopes and delegates execution back to managed runtime bridge when process is active.
5. If managed runtime is unavailable (cold start/headless execution), worker executes a constrained native fallback path or reschedules deterministically per policy.

Queue model:

1. Persist resumable envelope: method, URL, headers policy, body pointer/reference, retry metadata.
2. Assign deterministic `WorkId` from request id hash + dedupe token.
3. Use unique-work replace/keep policy configurable per request class.
4. On resume, reconcile queued tasks with in-memory request registry.

Android constraints:

1. Do not serialize non-replayable bodies.
2. Only queue methods/payloads explicitly marked replay-safe.
3. Keep max queued jobs bounded; reject with deterministic error when full.
4. Ensure manifest and dependency setup is validated in CI Android export checks (template merge + Gradle sync).

---

## Step 3: Add `BackgroundNetworkingMiddleware`

**Files:**
- `Runtime/Mobile/BackgroundNetworkingMiddleware.cs` (new)
- `Runtime/Core/UHttpClientOptions.cs` (modify)

### Technical Spec

Policy surface:

```csharp
public sealed class BackgroundNetworkingPolicy
{
    public bool Enable { get; init; }
    public int MaxQueuedRequests { get; init; } = 256;
    public TimeSpan GracePeriodBeforeQueue { get; init; } = TimeSpan.FromSeconds(2);
    public bool QueueOnAppPause { get; init; } = true;
    public bool RequireReplayableBodyForQueue { get; init; } = true;
}
```

Middleware behavior:

1. Enter platform scope before `next(...)`.
2. On background-expiration cancellation:
   - if policy allows and request replayable, enqueue for resume;
   - otherwise return deterministic cancellation error.
3. On success, clear any provisional queue marker.
4. Expose counters via diagnostics (`queued`, `replayed`, `expired`, `dropped`).

### Implementation Constraints

1. Middleware must remain transparent when policy disabled.
2. Replay queue processing must be idempotent across app restarts.
3. Request replay must preserve idempotency policy (`GET/HEAD` safe by default; unsafe methods require explicit opt-in).

---

## Step 4: Add Mobile Lifecycle and Reliability Tests

**File:** `Tests/Runtime/Mobile/BackgroundNetworkingTests.cs` (new)

### Required Test Matrix

| Case | Setup | Expected Result |
|---|---|---|
| `IosScope_AcquireReleaseAlways` | success, failure, cancellation | `EndBackgroundTask` always called once |
| `IosExpiration_TriggersQueue` | forced expiration callback | replayable request queued |
| `AndroidQueue_DeduplicatesWorkId` | repeated enqueue same dedupe key | single queued item |
| `AndroidManifestMerge_NoProviderConflict` | Unity export + manifest merge | single valid WorkManager provider authority |
| `AndroidNativeBridge_JniContractStable` | enqueue/cancel/status across JNI | deterministic result codes and no JNI leaks |
| `ReplayUnsafeBody_Rejected` | non-replayable stream body | deterministic policy error |
| `PolicyDisabled_NoBehaviorChange` | middleware disabled | baseline pipeline behavior |
| `PauseResume_ReplayCompletes` | app pause then resume | queued request re-executed once |

---

## Verification Criteria

1. In-flight requests survive short background windows where platform permits.
2. Expired background windows degrade gracefully with deterministic fallback.
3. Queue/replay behavior is bounded, idempotent, and policy-controlled.
4. Foreground and editor behavior remain unchanged when feature is disabled.
5. Android native plugin integration (Gradle dependency + manifest + JNI bridge) is explicitly covered and validated.
