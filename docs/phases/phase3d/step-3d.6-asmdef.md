# Step 3D.6: Assembly Definition

**File:** `Runtime/JSON/TurboHTTP.JSON.asmdef`  
**Depends on:** Nothing  
**Spec:** Unity Assembly Definition

## Purpose

Create a separate assembly for JSON functionality. This allows:
- Optional inclusion (users can exclude if they bring their own JSON)
- Clear dependency boundaries
- Potential separate compilation for tests

## File to Create

### `TurboHTTP.JSON.asmdef`

```json
{
    "name": "TurboHTTP.JSON",
    "rootNamespace": "TurboHTTP.JSON",
    "references": [],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": false,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": true
}
```

## Design Notes

### No Dependencies

This assembly has **no references** to other TurboHTTP assemblies. It's a standalone JSON library that:
- Can be used independently
- Has no Unity engine dependencies (`noEngineReferences: true`)
- Works in any .NET Standard 2.0 environment

### Not Auto-Referenced (Optional)

`autoReferenced: false` means:
- This is a truly optional module
- Users who don't need LiteJson can exclude it entirely
- Assemblies that want JSON must explicitly reference it
- Supports users who bring their own JSON library (Newtonsoft, JsonUtility)

> [!NOTE]
> `TurboHTTP.Core` must add `"TurboHTTP.JSON"` to its references to use the JSON abstraction.

### No Platform Exclusions

JSON works on all platforms including:
- Editor (all OS)
- Standalone (all OS)
- Mobile (iOS, Android)
- WebGL (runs in browser)
- Console platforms

### Namespace Convention

Root namespace is `TurboHTTP.JSON`, matching the folder structure:
- `TurboHTTP.JSON.IJsonSerializer`
- `TurboHTTP.JSON.JsonSerializer`
- `TurboHTTP.JSON.Lite.JsonReader`
- etc.

## Validation Criteria

- [ ] Assembly compiles without errors
- [ ] Assembly has no external dependencies
- [ ] TurboHTTP.Core explicitly references TurboHTTP.JSON
- [ ] No Unity engine references (portable)
- [ ] Works in unit test projects

## Relationship to Other Assemblies

```
TurboHTTP.JSON (this assembly)
    ├── No dependencies
    └── Referenced by:
        ├── TurboHTTP.Core (for UHttpRequestBuilder)
        ├── TurboHTTP.Complete
        └── TurboHTTP.Tests.Runtime
```

After Phase 3D, `TurboHTTP.Core.asmdef` should include:
```json
{
    "references": [
        "TurboHTTP.JSON"  // Add this
    ]
}
```

`TurboHTTP.Complete.asmdef` and `Tests/Runtime/TurboHTTP.Tests.Runtime.asmdef` should also
add `"TurboHTTP.JSON"` to their `references` arrays.
