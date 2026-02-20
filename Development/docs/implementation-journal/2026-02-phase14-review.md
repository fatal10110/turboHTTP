# Phase 14 Implementation Review — 2026-02-20 (Revision 5, Resolved)

Comprehensive review performed by specialist agents (unity-network-architect and unity-infrastructure-architect) across all Phase 14 roadmap implementation files. **Revision 4** is a full re-review of the entire Phase 14 codebase, identifying new critical findings missed in prior revisions.

## Resolution Update (2026-02-20, Revision 5)

All Revision 4 critical and warning findings are resolved in code.

### Revision 4 Critical/Warning Status Matrix

| ID | Status | Resolution |
|----|--------|------------|
| R4-C1 | Fixed | `NetworkQualityDetector` switched to atomic long-bit double storage |
| R4-C2 | Fixed | OIDC-discovered endpoints now validated through config validation |
| R4-C3 | Fixed | `ProxySettings.Clone()` now deep-clones `NetworkCredential` |
| R4-W1 | Fixed | OAuth refresh guard lifecycle hardened with disposal checks |
| R4-W2 | Fixed | Happy Eyeballs loser-drain/outstanding observations wrapped against cancellation exceptions |
| R4-W3 | Fixed | Adaptive timeout clamping behavior corrected to respect configured bounds |
| R4-W4 | Fixed | CONNECT tunnel HTTP/1.1-only limitation explicitly documented in transport path |
| R4-W5 | Fixed | OAuth token response now validates `token_type` as Bearer-only |
| R4-W6 | Fixed | OIDC discovery issuer validation added per spec |
| R4-W7 | Fixed | iOS background monitor task is awaited/observed during scope disposal |
| R4-W8 | Fixed | Android bridge JNI calls dispatched through `MainThreadDispatcher` |
| R4-W9 | Fixed | Android plugin context initialization now cached and gated |
| R4-W10 | Fixed | Interceptor snapshot reads/writes now use `Volatile.Read/Write` |
| R4-W11 | Fixed | Plugin shutdown wrapped in background task to avoid main-thread deadlock |
| R4-W12 | Fixed | Shared localhost helper used for HTTPS bypass consistency |
| R4-W13 | Fixed | Proxy bypass matcher handles IPv4-mapped IPv6 CIDR comparisons |
| R4-W14 | Fixed | `PluginContext.OptionsSnapshot` now returns cloned snapshot |
| R4-W15 | Fixed | Queued background cancellations now throw `BackgroundRequestQueuedException` |

### Info Status

| ID | Status | Resolution |
|----|--------|------------|
| R4-I1 | Fixed | `OAuthToken` enforces `DateTimeKind.Utc` for `ExpiresAtUtc` |
| R4-I2 | Fixed | `InMemoryTokenStore` now supports `Clear()` and `IDisposable` cleanup |
| R4-I3 | Fixed | Adaptive middleware event path no longer allocates per-request event dictionaries |
| R4-I4 | Fixed | PKCE verifier generation oversamples RNG buffer to reduce refill churn |
| R4-I5 | Fixed | Mock route matching no longer relies on per-match `string[]` splitting |
| R4-I6 | Fixed | `Continue(response)` ambiguity removed; replacement requires explicit `Replace(response)` |
| I-4 | Fixed | Deferred replay cancellations are now distinguishable via typed exception |
| I-10 | Documented | Adaptive middleware currently scopes to timeout adaptation only (roadmap item) |
| W-I1 | Documented | Mutable policy/options setters retained intentionally for configuration ergonomics |

Historical Revision 4 findings remain below for audit trail.

## Review Scope

All Phase 14 files across `Runtime/Core/`, `Runtime/Transport/Connection/`, `Runtime/Auth/`, `Runtime/Testing/`, and `Runtime/Unity/Mobile/`, plus native bridge/plugin surfaces under `Plugins/` where applicable. Integrated surfaces in `UHttpClient.cs` and `UHttpClientOptions.cs`. Sub-phases 14.1 through 14.8.

**Files Reviewed (core + bridge implementation files):**
- `HappyEyeballsConnector.cs`, `HappyEyeballsOptions.cs`
- `ProxySettings.cs` (includes `ProxyBypassMatcher`, `ProxyEnvironmentResolver`)
- `BackgroundExecution.cs`, `BackgroundNetworkingPolicy.cs` (includes `BackgroundNetworkingMiddleware`)
- `NetworkQuality.cs`, `NetworkQualityDetector.cs`, `AdaptiveMiddleware.cs`, `AdaptivePolicy.cs`
- `IHttpInterceptor.cs`, `IHttpPlugin.cs`, `PluginContext.cs`, `ProjectJsonBridge.cs`
- `OAuthClient.cs`, `OAuthConfig.cs`, `OAuthToken.cs`, `PkceUtility.cs`, `ITokenStore.cs`, `OAuthTokenProvider.cs`
- `MockHttpServer.cs`, `MockRoute.cs`, `MockResponseBuilder.cs`, `MockTransport.cs`
- `UHttpClient.cs` (plugin/interceptor integration), `UHttpClientOptions.cs`
- `RawSocketTransport.cs` (proxy forwarding + CONNECT tunnel)
- `TcpConnectionPool.cs` (Happy Eyeballs integration)
- `AndroidBackgroundWorkBridge.cs`, `AndroidBackgroundWorkConfig.cs`
- `IosBackgroundTaskBridge.cs`, `IosBackgroundTaskBindings.cs`, `BackgroundExecutionBridgeFactory.cs`, `BackgroundExecutionScope.cs`

---

## Revision 4 — New Critical Findings (3)

### R4-C1 [Infra] NetworkQualityDetector — Torn Reads on 32-bit IL2CPP

**File:** `Runtime/Core/NetworkQualityDetector.cs`

`GetSnapshot()` uses `Volatile.Read(ref double)` for `_ewmaLatencyMs`, `_timeoutRatio`, and `_successRatio`. On 32-bit IL2CPP (ARMv7 Android), `Volatile.Read` on `double` (64-bit) is **not guaranteed atomic** — a torn read can observe half-old/half-new bits. This is the same issue correctly fixed in `HttpMetrics` (Phase 6) using `BitConverter.DoubleToInt64Bits` + `Volatile.Read(ref long)`.

**Impact:** `AdaptiveMiddleware` can read corrupted latency/ratio values, leading to wildly incorrect timeout multipliers on 32-bit mobile devices.

**Fix:** Use the `HttpMetrics` pattern — store doubles as `long` bits via `BitConverter.DoubleToInt64Bits`, read via `BitConverter.Int64BitsToDouble(Volatile.Read(ref long))`.

---

### R4-C2 [Network] OAuthClient — OIDC-Discovered Endpoints Not Validated for HTTPS

**File:** `Runtime/Auth/OAuthClient.cs`

`ResolveEndpointsAsync` applies OIDC-discovered `authorization_endpoint` and `token_endpoint` URIs without re-validating them for HTTPS. An attacker who compromises the discovery endpoint could return `http://` token endpoints, causing the authorization code + PKCE verifier to be sent in plaintext.

**Fix:** After setting discovered endpoints in `ResolveEndpointsAsync`, call `resolved.Validate()` to enforce HTTPS on the discovered URIs.

---

### R4-C3 [Network] ProxySettings.Clone — NetworkCredential Not Defensively Cloned

**File:** `Runtime/Core/ProxySettings.cs`

`Clone()` copies `Credentials` by reference. `NetworkCredential` is mutable — the caller can change `UserName`/`Password` after cloning, affecting in-flight requests that share the reference via `UHttpClientOptions.Clone()`.

**Fix:**
```csharp
Credentials = Credentials != null
    ? new NetworkCredential(Credentials.UserName, Credentials.Password, Credentials.Domain)
    : null,
```

---

## Revision 4 — New Warning Findings (15)

### R4-W1 [Infra] OAuthClient Semaphore Disposal Race

**File:** `Runtime/Auth/OAuthClient.cs`
`Dispose()` calls `_refreshGuards.Clear()` while concurrent `RefreshTokenAsync` may be holding references. The `finally` block can release a semaphore that was replaced by a new `GetOrAdd` call, causing `SemaphoreFullException` or deadlock.

**Fix:** Add atomic `_disposed` flag via `Interlocked.CompareExchange`, check in `RefreshTokenAsync` before and after semaphore acquisition. Don't clear the dictionary — let semaphores be GC'd naturally.

---

### R4-W2 [Infra] HappyEyeballsConnector — Unobserved Task Exceptions

**File:** `Runtime/Transport/Connection/HappyEyeballsConnector.cs`
After `linkedCts.Cancel()`, `DrainLosersAsync` awaits canceled tasks. Unobserved `OperationCanceledException`s can trigger `TaskScheduler.UnobservedTaskException` on GC.

**Fix:** Wrap all task observations in try/catch for `OperationCanceledException`.

---

### R4-W3 [Infra] AdaptiveMiddleware — Timeout Bypass Can Exceed MaxTimeout

**File:** `Runtime/Core/AdaptiveMiddleware.cs`
The "undo clamping" logic re-applies the multiplier without respecting `MaxTimeout`, potentially producing timeouts above the configured maximum.

**Fix:** Remove the undo logic and respect clamping always, or document the behavior.

---

### R4-W4 [Network] CONNECT Tunnel — HTTP/1.1 Only

**File:** `Runtime/Transport/RawSocketTransport.cs`
CONNECT tunnel forces HTTP/1.1. All proxied HTTPS traffic loses HTTP/2 multiplexing. Should at minimum be documented as a known limitation; ideally offer `"h2", "http/1.1"` in the ALPN list.

---

### R4-W5 [Network] OAuthClient — token_type Not Validated

**File:** `Runtime/Auth/OAuthClient.cs`
Defaults to `"Bearer"` if missing but doesn't validate against `AuthMiddleware`'s expected format. A server returning `mac` token type would produce incorrectly formatted auth headers.

---

### R4-W6 [Network] OAuthClient — OIDC Issuer Not Validated Per Spec

**File:** `Runtime/Auth/OAuthClient.cs`
Per OpenID Connect Discovery 1.0 §4.3, the `issuer` value returned in metadata MUST match the discovery URL issuer. Code extracts `Issuer` but never validates it.

---

### R4-W7 [Infra] IosBackgroundTaskBridge — Fire-and-Forget Task Leak

**File:** `Runtime/Unity/Mobile/iOS/IosBackgroundTaskBridge.cs`
`MonitorExpirationAsync` is fire-and-forget. If it throws (e.g., `ObjectDisposedException` race), the exception is unobserved.

**Fix:** Await the monitor task in the dispose action with a catch block.

---

### R4-W8 [Network] AndroidBackgroundWorkBridge — JNI Calls on Non-Attached Thread

**File:** `Runtime/Unity/Mobile/Android/AndroidBackgroundWorkBridge.cs`
`AndroidJavaClass` and JNI calls must be on a JNI-attached thread. Async continuations from `AcquireAsync` could run on thread pool threads that aren't attached.

**Fix:** Dispatch JNI calls via `MainThreadDispatcher.ExecuteAsync` like the iOS bridge.

---

### R4-W9 [Infra] AndroidBackgroundWorkBridge — TryInitializePluginContext Called Every Acquisition

**File:** `Runtime/Unity/Mobile/Android/AndroidBackgroundWorkBridge.cs`
Creates new `AndroidJavaClass`/`AndroidJavaObject` on every `AcquireAsync` call.

**Fix:** Cache initialization status with a boolean flag.

---

### R4-W10 [Infra] UHttpClient._interceptors — Missing Volatile Semantics

**File:** `Runtime/Core/UHttpClient.cs`
`_interceptors` is read on request threads and written under `_pluginLock`, but without `Volatile.Read`/`Volatile.Write`. On ARM (mobile), reads may see stale references.

**Fix:** Use `Volatile.Read(ref _interceptors)` on the read path and `Volatile.Write` on the write path.

---

### R4-W11 [Infra] UHttpClient.Dispose — Synchronous Task.Wait for Plugin Shutdown

**File:** `Runtime/Core/UHttpClient.cs`
`Task.Wait` blocks the calling thread. If called from Unity main thread and plugin's `ShutdownAsync` posts to the main thread, this deadlocks.

**Fix:** Use `Task.Run(() => plugin.ShutdownAsync(...)).Wait(timeout)` or document threading requirement.

---

### R4-W12 [Network] OAuthConfig Localhost Bypass Inconsistency

**File:** `Runtime/Auth/OAuthClient.cs`
OIDC discovery HTTPS validation allows only `"localhost"`, but `OAuthConfig.ValidateHttpsEndpoint` also allows `127.0.0.1` and `::1`. Inconsistent.

**Fix:** Extract shared `IsLocalhostUri` helper.

---

### R4-W13 [Network] ProxyBypassMatcher — IPv4-Mapped IPv6 Not Handled

**File:** `Runtime/Core/ProxySettings.cs`
If host is IPv4-mapped IPv6 (e.g., `::ffff:192.168.1.1`) and CIDR rule is `192.168.0.0/16`, match fails because `hostBytes.Length != netBytes.Length` (16 vs 4).

---

### R4-W14 [Infra] PluginContext — Mutable OptionsSnapshot Exposed to Plugins

**File:** `Runtime/Core/PluginContext.cs`
`OptionsSnapshot` returns a mutable `UHttpClientOptions`. A buggy plugin could modify it, affecting later plugins.

---

### R4-W15 [Network] BackgroundNetworkingMiddleware — Queued Request Still Throws to Caller

**File:** `Runtime/Core/BackgroundNetworkingPolicy.cs`
After successfully enqueuing a request for deferred replay, the middleware re-throws `OperationCanceledException`. Caller has no way to distinguish "cancelled and lost" from "cancelled but queued."

---

## Previously Resolved Critical Findings (Revisions 1-3) — Still ✅

| ID | Description | Status |
|----|-------------|--------|
| C-1 | HappyEyeballsConnector CTS lifetime + socket.Dispose race | ✅ Fixed (Rev 1) |
| C-2 | PKCE verifier modulo bias → rejection sampling | ✅ Fixed (Rev 1) |
| C-3 | NetworkQualityDetector ring buffer EWMA traversal order | ✅ Fixed (Rev 1) |
| C-4 | OAuthClient.ResolveEndpointsAsync config mutation | ✅ Fixed (Rev 1) |
| C-5 | Zero test files → all 8 test suites created | ✅ Fixed (Rev 1) |
| C-6 | PluginContext.RegisterInterceptor capability gating | ✅ Fixed (Rev 1) |
| C-7 | ProxyEnvironmentResolver HTTPS→HTTP_PROXY silent fallback | ✅ Fixed (Rev 1) |

---

## Previously Resolved Warning/Info Items (Revisions 1-3) — Still ✅

| ID | Description |
|----|-------------|
| W-I2/I3/I4 | Reflection JSON calls consolidated into `ProjectJsonBridge` |
| W-I5 | MockHttpServer history → bounded `Queue.Dequeue()` |
| W-I7 | BackgroundNetworkingMiddleware → bounded internal queue |
| W-N1 | ConnectEndpointAsync → `TaskCompletionSource` + `WhenAny` pattern |
| W-N2 | HappyEyeballsConnector → eliminated per-loop waitList rebuild |
| W-N3 | AdaptiveMiddleware → null-safe body length capture |
| W-N4 | OAuthToken → immutable via constructor |
| W-N5 | MatchesCidr → CIDR prefix length validation |
| W-N6 | MockHttpServer → shared regex cache |
| I-1 | InterceptorResponseAction.Replace added |
| I-2 | MockRoute.RemainingInvocations → internal setter |
| I-3 | CreateAuthorizationRequestAsync → non-async |
| I-5 | ProxySettings.Validate → rejects Credentials when Address is null |
| I-6 | Plugin lifecycle state tracked via PluginDescriptor snapshots |
| I-8 | Deduplicate → StringComparer.Ordinal |
| I-11 | CONNECT tunneling implemented |
| I-12 | Native background bridges for iOS + Android |

---

## Info Items (New + Carried Forward)

| ID | Description |
|----|-------------|
| R4-I1 | OAuthToken.ExpiresAtUtc not validated for `DateTimeKind.Utc` |
| R4-I2 | `InMemoryTokenStore` has no `Clear()`/`IDisposable` for secure token cleanup |
| R4-I3 | `AdaptiveMiddleware` allocates Dictionary on every `RecordEvent` call |
| R4-I4 | PKCE RNG buffer capped at 256 bytes — rejection sampling causes double RNG calls 22.6% of the time |
| R4-I5 | MockHttpServer.SplitPath allocates `string[]` on every route match |
| R4-I6 | `Continue(response)` silently promoting to `Replace` may mask intent bugs |
| I-4 | BackgroundNetworkingMiddleware enqueues then re-throws `OperationCanceledException` |
| I-10 | AdaptiveMiddleware only implements timeout — concurrency/retry not wired |
| W-I1 | Mutable `set` accessors on policy/options classes |

---

## Sub-Phase Implementation Status

| Sub-Phase | Status | Core Logic | Transport/Native Wiring | Tests |
|---|---|---|---|---|
| 14.1 Happy Eyeballs | **Complete** | ✅ | ✅ | ✅ |
| 14.2 Proxy Support | **Complete** | ✅ | ✅ | ✅ |
| 14.3 Background Networking | **Complete** | ✅ | ✅ | ✅ |
| 14.4 Adaptive Network | **Complete** | ✅ | ✅ | ✅ |
| 14.5 OAuth 2.0 / OIDC | **Complete** | ✅ | ✅ | ✅ |
| 14.6 Interceptors | **Complete** | ✅ | ✅ | ✅ |
| 14.7 Mock Server | **Complete** | ✅ | ✅ | ✅ |
| 14.8 Plugin System | **Complete** | ✅ | ✅ | ✅ |

---

## Overall Assessment — Revision 4

**Phase 14 has 3 new CRITICAL findings and 15 new WARNINGs** that were not caught in Revisions 1-3. The most impactful are:

1. **R4-C1 (torn double reads)** — Will produce corrupted timeout values on 32-bit ARM devices. Same class of bug that was already fixed in Phase 6's `HttpMetrics`.
2. **R4-C2 (OIDC HTTPS bypass)** — Security issue allowing plaintext token exchange via compromised discovery endpoint.
3. **R4-C3 (credential mutation)** — Proxy credentials can be changed after client construction.

**Recommendation:** Fix all 3 CRITICAL issues and triage WARNINGs (especially R4-W8 Android JNI threading, R4-W10 volatile interceptors, R4-W11 Dispose deadlock) before marking Phase 14 as release-ready.
