# Step 3D.7: Update UHttpRequestBuilder

**File:** `Runtime/Core/UHttpRequestBuilder.cs` (modified)  
**Depends on:** 3D.5 (JsonSerializer)  
**Spec:** N/A (API update)

## Purpose

Update `UHttpRequestBuilder` to use the new JSON abstraction layer instead of direct `System.Text.Json` dependency. This enables JSON serialization to work on platforms where `System.Text.Json` is unavailable.

## Changes Required

### 1. Update Using Statements

**Before:**
```csharp
using System.Text.Json;
```

**After:**
```csharp
// Optional S.T.J support via define symbol
#if TURBOHTTP_USE_SYSTEM_TEXT_JSON
using System.Text.Json;
#endif
using TurboHTTP.JSON;
```

### 2. Update `WithJsonBody<T>(T value)`

**Before:**
```csharp
public UHttpRequestBuilder WithJsonBody<T>(T value)
{
    var json = JsonSerializer.Serialize(value);
    return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
}
```

**After:**
```csharp
/// <summary>
/// Serialize the object to JSON and set as body.
/// Uses the registered JSON serializer (LiteJson by default, or Newtonsoft/S.T.J if registered).
/// For IL2CPP safety, use <see cref="WithJsonBody(string)"/> with pre-serialized JSON.
/// </summary>
public UHttpRequestBuilder WithJsonBody<T>(T value)
{
    var json = TurboHTTP.JSON.JsonSerializer.Serialize(value);
    return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
}
```

### 3. Update `WithJsonBody<T>(T value, JsonSerializerOptions options)`

**Before:**
```csharp
public UHttpRequestBuilder WithJsonBody<T>(T value, JsonSerializerOptions options)
{
    var json = JsonSerializer.Serialize(value, options);
    return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
}
```

**After:**
```csharp
#if TURBOHTTP_USE_SYSTEM_TEXT_JSON
/// <summary>
/// Serialize the object to JSON with System.Text.Json options and set as body.
/// Only available when TURBOHTTP_USE_SYSTEM_TEXT_JSON is defined.
/// For IL2CPP safety, use source-generated <see cref="System.Text.Json.JsonSerializerOptions"/>.
/// </summary>
/// <remarks>
/// This overload requires System.Text.Json to be available.
/// Define TURBOHTTP_USE_SYSTEM_TEXT_JSON in your project to enable.
/// </remarks>
public UHttpRequestBuilder WithJsonBody<T>(T value, System.Text.Json.JsonSerializerOptions options)
{
    var json = System.Text.Json.JsonSerializer.Serialize(value, options);
    return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
}
#endif
```

### 4. Add New Overload with IJsonSerializer

```csharp
/// <summary>
/// Serialize the object to JSON using a specific serializer and set as body.
/// Useful for one-off serialization with different settings.
/// </summary>
/// <param name="value">Object to serialize</param>
/// <param name="serializer">Specific serializer to use</param>
public UHttpRequestBuilder WithJsonBody<T>(T value, IJsonSerializer serializer)
{
    if (serializer == null) throw new ArgumentNullException(nameof(serializer));
    var json = serializer.Serialize(value);
    return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
}
```

## Complete Modified File Section

```csharp
// At top of file
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if TURBOHTTP_USE_SYSTEM_TEXT_JSON
using System.Text.Json;
#endif
using TurboHTTP.JSON;

// ... class definition ...

/// <summary>
/// Set body to pre-serialized JSON string. This is the recommended approach
/// for IL2CPP builds — users can serialize with their own IL2CPP-safe
/// serializer (Unity's JsonUtility, Newtonsoft with AOT, or source-generated
/// System.Text.Json). Sets Content-Type to application/json.
/// </summary>
/// <exception cref="ArgumentNullException">Thrown when <paramref name="json"/> is null.</exception>
public UHttpRequestBuilder WithJsonBody(string json)
{
    if (json == null) throw new ArgumentNullException(nameof(json));
    return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
}

/// <summary>
/// Serialize the object to JSON and set as body.
/// Uses the registered JSON serializer (LiteJson by default).
/// </summary>
/// <remarks>
/// <para><b>IL2CPP Note:</b> LiteJson is AOT-safe but only supports primitives,
/// dictionaries, and lists. For complex types, either:</para>
/// <list type="bullet">
/// <item>Register a Newtonsoft serializer at startup</item>
/// <item>Use <see cref="WithJsonBody(string)"/> with pre-serialized JSON</item>
/// </list>
/// </remarks>
public UHttpRequestBuilder WithJsonBody<T>(T value)
{
    var json = TurboHTTP.JSON.JsonSerializer.Serialize(value);
    return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
}

/// <summary>
/// Serialize the object to JSON using a specific serializer.
/// </summary>
/// <param name="value">Object to serialize</param>
/// <param name="serializer">Serializer to use (overrides default)</param>
public UHttpRequestBuilder WithJsonBody<T>(T value, IJsonSerializer serializer)
{
    if (serializer == null) throw new ArgumentNullException(nameof(serializer));
    var json = serializer.Serialize(value);
    return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
}

#if TURBOHTTP_USE_SYSTEM_TEXT_JSON
/// <summary>
/// Serialize using System.Text.Json with custom options.
/// Only available when TURBOHTTP_USE_SYSTEM_TEXT_JSON is defined.
/// </summary>
public UHttpRequestBuilder WithJsonBody<T>(T value, System.Text.Json.JsonSerializerOptions options)
{
    var json = System.Text.Json.JsonSerializer.Serialize(value, options);
    return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
}
#endif
```

## API Changes Summary

| Method | Change |
|--------|--------|
| `WithJsonBody(string)` | Unchanged (still accepts pre-serialized JSON) |
| `WithJsonBody<T>(T)` | Now uses TurboHTTP.JSON abstraction |
| `WithJsonBody<T>(T, JsonSerializerOptions)` | Conditional compilation (`#if`) |
| `WithJsonBody<T>(T, IJsonSerializer)` | **NEW** — use specific serializer |

## Usage Notes

### Default Serializer
LiteJson is used by default. No configuration needed.

### Using System.Text.Json
Add `TURBOHTTP_USE_SYSTEM_TEXT_JSON` to Scripting Define Symbols:
1. Edit → Project Settings → Player
2. Scripting Define Symbols: `TURBOHTTP_USE_SYSTEM_TEXT_JSON`

### Using Newtonsoft
Register at startup:
```csharp
TurboHTTP.JSON.JsonSerializer.SetDefault(new NewtonsoftAdapter());
```

## Validation Criteria

- [ ] Existing tests pass without modification
- [ ] `WithJsonBody<T>(T)` uses LiteJson by default
- [ ] `WithJsonBody(string)` still works unchanged
- [ ] `WithJsonBody<T>(T, IJsonSerializer)` uses specified serializer
- [ ] S.T.J overload compiles when define is set
- [ ] No compilation errors when S.T.J is unavailable

## Test Cases

```csharp
// Basic usage
builder.WithJsonBody(new { name = "test" }) 
// → calls TurboHTTP.JSON.JsonSerializer.Serialize

// Pre-serialized (unchanged)
builder.WithJsonBody("{\"name\":\"test\"}")
// → direct byte conversion

// Custom serializer
builder.WithJsonBody(myObject, new NewtonsoftAdapter())
// → calls NewtonsoftAdapter.Serialize

// System.Text.Json (conditional)
#if TURBOHTTP_USE_SYSTEM_TEXT_JSON
builder.WithJsonBody(myObject, MyJsonContext.Default.Options)
// → calls System.Text.Json.JsonSerializer.Serialize
#endif
```
