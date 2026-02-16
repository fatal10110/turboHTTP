# Phase 14.8: Plugin System

**Depends on:** Phase 14.6
**Assembly:** `TurboHTTP.Extensibility`, `TurboHTTP.Tests.Runtime`
**Files:** 3 new, 1 modified

---

## Step 1: Define Plugin Lifecycle Contracts

**Files:**
- `Runtime/Extensibility/IHttpPlugin.cs` (new)
- `Runtime/Extensibility/PluginContext.cs` (new)

### Technical Spec

Plugin contract:

```csharp
public interface IHttpPlugin
{
    string Name { get; }
    string Version { get; }
    PluginCapabilities Capabilities { get; }
    ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken);
    ValueTask ShutdownAsync(CancellationToken cancellationToken);
}
```

`PluginContext` surface:

1. Read-only client options snapshot.
2. Registration APIs for interceptors/middleware/event listeners.
3. Logger abstraction with redaction rules.
4. Capability-gated extension points.

Capability model:

| Capability | Allows |
|---|---|
| `ObserveRequests` | subscribe to request/response timeline events |
| `MutateRequests` | register request interceptors/middleware |
| `MutateResponses` | register response interceptors |
| `HandleErrors` | subscribe to failure hooks |
| `Diagnostics` | emit plugin-scoped diagnostics |

Lifecycle state machine:

1. `Created` -> `Initializing` -> `Initialized` -> `ShuttingDown` -> `Disposed`.
2. Failed initialization transitions to `Faulted` and blocks request pipeline registration from that plugin.
3. Duplicate registration by plugin name is rejected unless explicit replacement policy enabled.

### Implementation Constraints

1. Initialization and shutdown must be idempotent.
2. Plugin cannot access forbidden extension points without capability grant.
3. Plugin exceptions include plugin metadata and lifecycle phase.
4. Context must not expose mutable global client internals directly.

---

## Step 2: Implement Client Plugin Registry

**File:** `Runtime/Core/UHttpClient.cs` (modify)

### Technical Spec

Registry APIs:

```csharp
Task RegisterPluginAsync(IHttpPlugin plugin, CancellationToken ct = default);
Task UnregisterPluginAsync(string pluginName, CancellationToken ct = default);
IReadOnlyList<PluginDescriptor> GetRegisteredPlugins();
```

Registry behavior:

1. Register plugins in deterministic order.
2. Serialize lifecycle transitions with async lock to avoid concurrent initialization races.
3. On registration failure:
   - rollback partial interceptor/middleware subscriptions;
   - keep other plugins active.
4. On unregister:
   - remove plugin contributions from pipeline/event buses;
   - invoke plugin shutdown with timeout policy.

Performance rules:

1. If no plugins are registered, pipeline fast path must remain unchanged.
2. Plugin hooks execute only when capability requires path interception.
3. Registry read APIs must not allocate per call.

### Implementation Constraints

1. Avoid deadlocks when plugin init registers interceptors that reference client services.
2. Plugin initialization timeout must be configurable and deterministic.
3. Unregister failures should surface diagnostics but must not leave pipeline in inconsistent state.

---

## Step 3: Add Deterministic Plugin Tests

**File:** `Tests/Runtime/Extensibility/PluginRegistryTests.cs` (new)

### Required Test Matrix

| Case | Setup | Expected Result |
|---|---|---|
| `Register_InitializeOnce` | register same instance twice | second attempt rejected or ignored by policy |
| `InitFailure_RollsBackContributions` | plugin throws during init | no dangling interceptors/listeners |
| `CapabilityGating_BlocksForbiddenAccess` | plugin requests unauthorized capability | deterministic failure |
| `Unregister_RemovesHooks` | plugin registered then removed | no plugin callbacks on new requests |
| `Ordering_Deterministic` | 3 plugins with hooks | execution order stable |
| `NoPlugins_FastPath` | empty registry | baseline request performance preserved |
| `ShutdownTimeout_Handled` | hanging plugin shutdown | timeout diagnostics emitted, registry remains consistent |

---

## Verification Criteria

1. Plugin lifecycle is deterministic, capability-scoped, and failure-isolated.
2. Registry operations are race-safe and leave no partial pipeline state.
3. Plugin opt-out path maintains baseline performance and behavior.
4. Diagnostics provide actionable metadata without exposing sensitive request data.
