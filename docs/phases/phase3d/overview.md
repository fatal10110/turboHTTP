# Phase 3D Implementation Plan — Overview

Phase 3D implements a fallback JSON parser for internal use, ensuring TurboHTTP works reliably on all Unity versions including those where `System.Text.Json` is unavailable or unreliable (IL2CPP/AOT builds). Following the proven BestHTTP pattern, we bundle a lightweight internal JSON parser while allowing users to plug in their preferred serializer.

## Step Index

| Step | Name | Files | Depends On |
|---|---|---|---|
| [3D.1](step-3d.1-ijson-serializer.md) | IJsonSerializer Interface | 1 new | — |
| [3D.2](step-3d.2-lite-json-reader.md) | LiteJson Reader | 1 new | — |
| [3D.3](step-3d.3-lite-json-writer.md) | LiteJson Writer | 1 new | — |
| [3D.4](step-3d.4-lite-json-serializer.md) | LiteJsonSerializer Implementation | 1 new | 3D.1, 3D.2, 3D.3 |
| [3D.5](step-3d.5-json-registry.md) | JsonSerializer Registry & Facade | 1 new | 3D.1, 3D.4 |
| [3D.6](step-3d.6-asmdef.md) | Assembly Definition | 1 new | — |
| [3D.7](step-3d.7-update-request-builder.md) | Update UHttpRequestBuilder | 1 modified | 3D.5 |
| [3D.8](step-3d.8-tests.md) | Unit & Integration Tests | 3 new | 3D.1–3D.7 |

## Dependency Graph

```
No dependencies (parallel):
    ├── 3D.1 IJsonSerializer Interface
    ├── 3D.2 LiteJson Reader
    ├── 3D.3 LiteJson Writer
    └── 3D.6 Assembly Definition

Layer 2 (Core Implementation):
    └── 3D.4 LiteJsonSerializer  ← 3D.1, 3D.2, 3D.3

Layer 3 (Registry):
    └── 3D.5 JsonSerializer Facade ← 3D.1, 3D.4

Layer 4 (Integration):
    └── 3D.7 UHttpRequestBuilder ← 3D.5

Layer 5 (Validation):
    └── 3D.8 Tests ← ALL above
```

## New Directory Structure

```
Runtime/JSON/
    TurboHTTP.JSON.asmdef           — Step 3D.6
    IJsonSerializer.cs              — Step 3D.1
    JsonSerializer.cs               — Step 3D.5
    LiteJson/
        JsonReader.cs               — Step 3D.2
        JsonWriter.cs               — Step 3D.3
        LiteJsonSerializer.cs       — Step 3D.4
```

## Modified Files

| File | Step | Changes |
|------|------|---------|
| `Runtime/Core/UHttpRequestBuilder.cs` | 3D.7 | Add conditional compilation, use abstraction layer |

## Key Design Decisions

### 1. Namespace Isolation

All internal JSON code uses `TurboHTTP.JSON.*` namespace to prevent conflicts:
- `TurboHTTP.JSON.IJsonSerializer`
- `TurboHTTP.JSON.JsonSerializer`
- `TurboHTTP.JSON.Lite.JsonReader`
- `TurboHTTP.JSON.Lite.JsonWriter`

### 2. Two-Layer Architecture

**Internal Layer (LiteJson):**
- Minimal JSON parser for TurboHTTP's internal needs
- AOT-safe, no reflection
- Always available, zero dependencies

**User Layer (Registry):**
- `JsonSerializer.Register<T>()` for custom serializers
- Falls back to LiteJson if nothing registered
- Allows Newtonsoft, JsonUtility, or source-generated S.T.J

## Exclusions (NOT Implemented in Phase 3D)

- **JSON-RPC:** Protocol-specific features deferred
- **JSON Patch (RFC 6902):** Too specialized for core library
- **JSON Schema validation:** Out of scope
- **Streaming large JSON:** Basic implementation only
- **Custom converters:** Deferred to user implementations

## Implementation Notes

- **LiteJson is minimal:** ~500 lines of code, handles 95% of use cases
- **Users can upgrade:** Register Newtonsoft for advanced features
- **Thread-safe registry:** Volatile field for reads, locked writes
- **IL2CPP tested:** Manual verification required on physical device
- **C# 7/8 compatible:** No C# 9+ features used

> [!IMPORTANT]  
> **LiteJson does NOT use reflection.** All serialization/deserialization uses explicit property mapping. For complex types, users should register a full-featured serializer like Newtonsoft.

> [!WARNING]
> **Double Precision Limitation:** All JSON numbers are parsed as `double`. This loses precision for:
> - Integers > 2^53 (9,007,199,254,740,992) 
> - Large financial values requiring exact decimal precision
> 
> **Workaround:** For large IDs or financial values, use string representation in JSON and parse manually, or register Newtonsoft with appropriate converters.

## Validation Matrix

| Scenario | LiteJson | System.Text.Json | Newtonsoft |
|----------|----------|------------------|------------|
| Primitives | ✅ | ✅ | ✅ |
| Arrays | ✅ | ✅ | ✅ |
| Nested Objects | ✅ | ✅ | ✅ |
| Custom Types | ⚠️ Limited | ✅ | ✅ |
| Polymorphism | ❌ | ✅ | ✅ |
| IL2CPP Safe | ✅ | ⚠️ Source-gen only | ⚠️ AOT mode |
