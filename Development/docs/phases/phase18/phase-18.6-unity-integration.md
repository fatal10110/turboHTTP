# Phase 18.6: Unity Integration

**Depends on:** Phase 18.4 (client API), Phase 15 (MainThreadDispatcher V2)
**Assembly:** `TurboHTTP.Unity`
**Files:** 2 new, 1 modified

---

## Step 1: Implement Unity WebSocket Event Bridge

**File:** `Runtime/Unity/UnityWebSocketBridge.cs` (new)

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
   - C# events (`event Action<WebSocketMessage> MessageReceived`).
   - Optional `UnityEvent`-based callbacks for Inspector wiring.
4. Support both `async/await` and callback-based consumption patterns.
5. Provide `ReceiveAsCoroutine()` — wraps the receive loop as a Unity coroutine using `CoroutineWrapper` patterns.
6. Thread-safe subscription/unsubscription for all events.

Implementation constraints:

1. Main-thread dispatch must use `MainThreadDispatcher.EnqueueAsync` — never raw `UnitySynchronizationContext.Post`.
2. Message payload must be copied or ownership-transferred before dispatch (pooled buffers may be reused by receive loop).
3. If dispatcher queue is saturated (backpressure), messages must be dropped with diagnostic logging — not block the receive loop.
4. Bridge must not hold strong references to destroyed `MonoBehaviour` instances.

---

## Step 2: Implement MonoBehaviour Lifecycle Binding

**File:** `Runtime/Unity/UnityWebSocketClient.cs` (new)

Required behavior:

1. `MonoBehaviour`-based WebSocket client component for drag-and-drop Unity usage.
2. Inspector-configurable properties:
   - `Uri` (string, validated on connect).
   - `AutoConnect` (bool, default false) — connect on `Start()`.
   - `AutoReconnect` (bool, default false) — enable default reconnect policy.
   - `SubProtocol` (string, optional).
   - `PingInterval` (float seconds, default 30).
3. Lifecycle binding:
   - `OnEnable` → connect (if `AutoConnect` and not already connected).
   - `OnDisable` → disconnect with `GoingAway` close code.
   - `OnDestroy` → abort connection and dispose all resources.
   - `OnApplicationPause(true)` → optionally send ping or disconnect (configurable).
   - `OnApplicationPause(false)` → optionally reconnect if disconnected during pause.
4. Public API methods:
   - `Connect()` / `ConnectAsync()` — manual connection trigger.
   - `Disconnect()` / `DisconnectAsync()` — manual clean close.
   - `Send(string message)` / `Send(byte[] data)` — fire-and-forget send (logs errors).
   - `SendAsync(string message)` / `SendAsync(byte[] data)` — async send with error propagation.
5. UnityEvent callbacks (Inspector-assignable):
   - `OnConnectedEvent` (`UnityEvent`)
   - `OnMessageReceivedEvent` (`UnityEvent<string>`) — text messages.
   - `OnBinaryReceivedEvent` (`UnityEvent<byte[]>`) — binary messages.
   - `OnDisconnectedEvent` (`UnityEvent<int>`) — close code.
   - `OnErrorEvent` (`UnityEvent<string>`) — error message.
6. Integration with Phase 15 `LifecycleCancellation`:
   - Bind WebSocket operations to the `MonoBehaviour` lifecycle token.
   - Auto-cancel pending operations on destroy.

Implementation constraints:

1. All Unity API access (events, Inspector properties, lifecycle hooks) must be main-thread only.
2. Fire-and-forget `Send()` must not throw — log errors via `Debug.LogError`.
3. Connection state must be queryable from Inspector (expose `IsConnected` in Editor).
4. Component must be safe to add/remove at runtime.
5. Domain reload must clean up connections deterministically.
6. Multiple `UnityWebSocketClient` components on the same GameObject must operate independently.

---

## Step 3: Add Unity WebSocket Extensions

**Files:**
- `Runtime/Unity/UnityExtensions.cs` (modify)

Required behavior:

1. Add WebSocket convenience extensions to `UnityExtensions`:
   - `WebSocket(this UHttpClient, Uri uri)` — create a `UnityWebSocketBridge`-wrapped WebSocket client using the HTTP client's configuration context.
   - `WebSocket(this UHttpClient, string url)` — string overload with URI parsing.
2. Extension returns a pre-configured `IWebSocketClient` with main-thread dispatch enabled.
3. WebSocket client inherits relevant settings from the HTTP client (TLS provider, custom headers for auth).

Implementation constraints:

1. Extension methods must not introduce a hard dependency from `TurboHTTP.Unity` on `TurboHTTP.WebSocket` — use conditional compilation or runtime type checking if assemblies are optional.
2. If `TurboHTTP.WebSocket` assembly is not present, extension methods must throw a clear `NotSupportedException`.

---

## Verification Criteria

1. WebSocket event callbacks arrive on the Unity main thread (verified via `MainThreadDispatcher.IsMainThread`).
2. `MonoBehaviour` lifecycle binding auto-disconnects on `OnDestroy`.
3. Domain reload does not leak WebSocket connections.
4. Inspector-configured `UnityWebSocketClient` can connect, send, receive, and close from Unity UI.
5. Fire-and-forget `Send()` does not throw exceptions — errors are logged.
6. Application pause/resume handling reconnects when configured.
7. Coroutine-based receive loop works with `CoroutineWrapper` patterns.
8. Multiple `UnityWebSocketClient` components operate independently without interference.
