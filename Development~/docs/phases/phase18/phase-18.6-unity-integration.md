# Phase 18.6: Unity Integration

**Depends on:** Phase 18.4 (client API), Phase 18.5 (reconnection events), Phase 15 (MainThreadDispatcher V2)
**Assembly:** `TurboHTTP.Unity.WebSocket` (new assembly — separate from `TurboHTTP.Unity`)
**Files:** 4 new

**Rationale for separate assembly:** `TurboHTTP.Unity` must remain independently includable without requiring `TurboHTTP.WebSocket`. Putting WebSocket Unity code in `TurboHTTP.Unity` would force a hard dependency, violating the module isolation rule. `TurboHTTP.Unity.WebSocket` references both `TurboHTTP.Unity` (for `MainThreadDispatcher`, `LifecycleCancellation`) and `TurboHTTP.WebSocket` (for `IWebSocketClient`).

---

## Step 0: Add Unity WebSocket Assembly Definition

**File:** `Runtime/Unity.WebSocket/TurboHTTP.Unity.WebSocket.asmdef` (new)

Required behavior:

1. Create `TurboHTTP.Unity.WebSocket` assembly definition.
2. References: `TurboHTTP.Core`, `TurboHTTP.WebSocket`, `TurboHTTP.Unity`.
3. Set `autoReferenced` to `false` to keep integration opt-in.
4. Keep `TurboHTTP.Unity` asmdef unchanged (no new dependency from Unity core module to WebSocket).

Implementation constraints:

1. Keep `noEngineReferences` as `false` (assembly contains `MonoBehaviour` components).
2. Do not add direct dependency on `TurboHTTP.WebSocket.Transport` (Unity integration depends on API surface, not socket transport details).

---

## Step 1: Implement Unity WebSocket Event Bridge

**File:** `Runtime/Unity.WebSocket/UnityWebSocketBridge.cs` (new)

Required behavior:

1. Wrap `IWebSocketClient` and marshal all event callbacks to the Unity main thread via `MainThreadDispatcher`.
2. Event bridging:
   - `OnConnected` → dispatched to main thread.
   - `OnMessage` → dispatched to main thread with `WebSocketMessage` payload.
   - `OnReconnecting` → dispatched to main thread with attempt info.
   - `OnReconnected` → dispatched to main thread.
   - `OnError` → dispatched to main thread with exception.
   - `OnClosed` → dispatched to main thread with close code and reason.
3. Expose Unity-friendly event patterns:
   - C# events (`event Action<WebSocketMessage> MessageReceived`) — preferred for high-throughput scenarios.
   - Optional `UnityEvent`-based callbacks for Inspector wiring — document: `UnityEvent<T>` allocates on invoke (boxing for value types). For frequent messages, use C# events instead.
4. Support both `async/await` and callback-based consumption patterns.
5. Provide `ReceiveAsCoroutine()` — wraps the receive loop as a Unity coroutine using `CoroutineWrapper` patterns.
6. Thread-safe subscription/unsubscription for all events using C# `event` keyword. UnityEvent callbacks require locking in the bridge since `UnityEngine.Events` is not thread-safe.

Implementation constraints:

1. Main-thread dispatch must use `MainThreadDispatcher.EnqueueAsync` — never raw `UnitySynchronizationContext.Post`.
2. **Message payload copy strategy:** before dispatching to main thread, copy the message payload into a **regular byte array allocation** (not pooled). Rationale: the main-thread consumer's lifetime is unpredictable — if pooled buffers were used, consumers who fail to dispose would leak pool capacity. Regular `byte[]` is safer for the bridge copy and lets GC handle cleanup. The original pooled buffer from the receive loop is returned after the copy.
3. If dispatcher queue is saturated (backpressure), log dropped messages at `Warning` level and expose `OnMessageDropped` event — do not block the receive loop. Backpressure from WebSocket `Channel<T>` is separate (that blocks the receive loop at the WebSocket layer). The Unity bridge drops are only when the main-thread dispatcher queue itself is full.
4. Bridge must not hold strong references to destroyed `MonoBehaviour` instances.

---

## Step 2: Implement MonoBehaviour Lifecycle Binding

**File:** `Runtime/Unity.WebSocket/UnityWebSocketClient.cs` (new)

Required behavior:

1. `MonoBehaviour`-based WebSocket client component for drag-and-drop Unity usage.
2. Inspector-configurable properties:
   - `Uri` (string, validated on connect).
   - `AutoConnect` (bool, default false) — connect on `Start()`.
   - `AutoReconnect` (bool, default false) — enable default reconnect policy.
   - `SubProtocol` (string, optional).
   - `PingInterval` (float seconds, default 25).
   - `DisconnectOnPause` (bool, default true) — disconnect on `OnApplicationPause(true)`.
3. Lifecycle binding:
   - `OnEnable` → connect (if `AutoConnect` and not already connected).
   - `OnDisable` → disconnect with `GoingAway` close code.
   - `OnDestroy` → abort connection and dispose all resources.
   - `OnApplicationPause(true)` → **default: disconnect** with `GoingAway` close code (configurable via `DisconnectOnPause`). On mobile (iOS/Android), backgrounding suspends the process or throttles networking — a WebSocket left open during pause will likely encounter a broken connection on resume. Disconnect-on-pause avoids wasted battery and broken-pipe errors.
   - `OnApplicationPause(false)` → reconnect if `AutoReconnect` is enabled and was disconnected during pause.
4. **Lifecycle cancellation scope:**
   - Lifecycle token (from `LifecycleCancellation`) cancels all pending async operations (`ConnectAsync`, `SendAsync`, `ReceiveAsync`, `CloseAsync`) on destroy.
   - Connection itself (receive loop) has a separate token that triggers close handshake attempt on destroy (not immediate abort), with a short timeout (1s) before falling back to abort.
5. Public API methods:
   - `Connect()` / `ConnectAsync()` — manual connection trigger.
   - `Disconnect()` / `DisconnectAsync()` — manual clean close.
   - `Send(string message)` / `Send(byte[] data)` — fire-and-forget send (logs errors).
   - `SendAsync(string message)` / `SendAsync(byte[] data)` — async send with error propagation.
6. UnityEvent callbacks (Inspector-assignable):
   - `OnConnectedEvent` (`UnityEvent`)
   - `OnMessageReceivedEvent` (`UnityEvent<string>`) — text messages.
   - `OnBinaryReceivedEvent` (`UnityEvent<byte[]>`) — binary messages.
   - `OnDisconnectedEvent` (`UnityEvent<int>`) — close code.
   - `OnErrorEvent` (`UnityEvent<string>`) — error message.

Implementation constraints:

1. All Unity API access (events, Inspector properties, lifecycle hooks) must be main-thread only.
2. Fire-and-forget `Send()` must not throw — log errors via `Debug.LogError`.
3. Connection state must be queryable from Inspector (expose `IsConnected` in Editor).
4. Component must be safe to add/remove at runtime.
5. **Domain reload cleanup:** register `Application.quitting` handler to abort all connections. Store active `UnityWebSocketClient` instances in a static list, clean up on domain unload. Add Editor test: "Domain reload does not leak background tasks."
6. Multiple `UnityWebSocketClient` components on the same GameObject must operate independently.

---

## Step 3: Add Unity WebSocket Extension Methods

**File:** `Runtime/Unity.WebSocket/UnityWebSocketExtensions.cs` (new)

Required behavior:

1. Add WebSocket convenience extensions in `TurboHTTP.Unity.WebSocket` namespace:
   - `WebSocket(this UHttpClient, Uri uri)` — create a `UnityWebSocketBridge`-wrapped WebSocket client using the HTTP client's configuration context.
   - `WebSocket(this UHttpClient, string url)` — string overload with URI parsing.
2. Extension returns a pre-configured `IWebSocketClient` with main-thread dispatch enabled.
3. WebSocket client inherits relevant settings from the HTTP client (TLS provider, custom headers for auth).

Implementation constraints:

1. Extension methods live in `TurboHTTP.Unity.WebSocket` assembly — users must include this assembly to get the extensions. No modification to `TurboHTTP.Unity` assembly required.
2. If `TurboHTTP.WebSocket` assembly is not present at runtime, the assembly reference will fail at load time — this is acceptable since the user must explicitly include both assemblies.

---

## Verification Criteria

1. WebSocket event callbacks arrive on the Unity main thread (verified via `MainThreadDispatcher.IsMainThread`).
2. `MonoBehaviour` lifecycle binding auto-disconnects on `OnDestroy`.
3. Domain reload does not leak WebSocket connections or background tasks.
4. Inspector-configured `UnityWebSocketClient` can connect, send, receive, and close from Unity UI.
5. Fire-and-forget `Send()` does not throw exceptions — errors are logged.
6. Application pause disconnects by default on mobile platforms.
7. Application resume reconnects when `AutoReconnect` is enabled and was disconnected during pause.
8. Coroutine-based receive loop works with `CoroutineWrapper` patterns.
9. Multiple `UnityWebSocketClient` components operate independently without interference.
10. Message payload copies use regular byte arrays (not pooled) for safe main-thread consumption.
11. Dispatcher saturation logs dropped messages at Warning level.
