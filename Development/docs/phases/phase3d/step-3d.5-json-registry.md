# Step 3D.5: JsonSerializer Registry & Facade

**File:** `Runtime/JSON/JsonSerializer.cs`  
**Depends on:** 3D.1 (IJsonSerializer), 3D.4 (LiteJsonSerializer)  
**Spec:** N/A (internal implementation)

## Purpose

Provide a static facade and registry for JSON serialization. This allows users to plug in their preferred JSON library (Newtonsoft, System.Text.Json with source generators, etc.) while defaulting to the built-in LiteJsonSerializer.

## Class to Implement

### `JsonSerializer`

```csharp
using System;
using TurboHTTP.JSON.Lite;

namespace TurboHTTP.JSON
{
    /// <summary>
    /// Static facade and registry for JSON serialization.
    /// Uses LiteJsonSerializer by default, but users can register custom serializers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Thread Safety:</b> Reading the current serializer is lock-free.
    /// Registration uses a lock but is expected to happen once at startup.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Use default LiteJson
    /// var json = JsonSerializer.Serialize(myObject);
    /// 
    /// // Register Newtonsoft at startup
    /// JsonSerializer.SetDefault(new NewtonsoftJsonSerializer());
    /// 
    /// // Now all serialization uses Newtonsoft
    /// var json = JsonSerializer.Serialize(myComplexObject);
    /// </code>
    /// </example>
    public static class JsonSerializer
    {
        private static readonly object _lock = new object();
        private static volatile IJsonSerializer _default = LiteJsonSerializer.Instance;

        /// <summary>
        /// Get or set the default JSON serializer.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="LiteJsonSerializer"/>.
        /// Set this at application startup before any serialization occurs.
        /// </remarks>
        public static IJsonSerializer Default
        {
            get => _default;
            set
            {
                lock (_lock)
                {
                    _default = value ?? LiteJsonSerializer.Instance;
                }
            }
        }

        /// <summary>
        /// Set the default serializer. Convenience method equivalent to setting Default property.
        /// </summary>
        /// <param name="serializer">Serializer implementation to use</param>
        /// <exception cref="ArgumentNullException">Serializer is null</exception>
        public static void SetDefault(IJsonSerializer serializer)
        {
            Default = serializer ?? throw new ArgumentNullException(nameof(serializer));
        }

        /// <summary>
        /// Reset to the built-in LiteJsonSerializer.
        /// </summary>
        public static void ResetToDefault()
        {
            Default = LiteJsonSerializer.Instance;
        }

        /// <summary>
        /// Serialize an object to JSON using the default serializer.
        /// </summary>
        /// <typeparam name="T">Type of object to serialize</typeparam>
        /// <param name="value">Object to serialize</param>
        /// <returns>JSON string</returns>
        /// <exception cref="JsonSerializationException">Serialization failed</exception>
        public static string Serialize<T>(T value)
        {
            return _default.Serialize(value);
        }

        /// <summary>
        /// Serialize an object to JSON using the default serializer (non-generic).
        /// </summary>
        /// <param name="value">Object to serialize</param>
        /// <param name="type">Type of the object</param>
        /// <returns>JSON string</returns>
        /// <exception cref="JsonSerializationException">Serialization failed</exception>
        public static string Serialize(object value, Type type)
        {
            return _default.Serialize(value, type);
        }

        /// <summary>
        /// Deserialize JSON to type T using the default serializer.
        /// </summary>
        /// <typeparam name="T">Target type</typeparam>
        /// <param name="json">JSON string to parse</param>
        /// <returns>Deserialized object</returns>
        /// <exception cref="JsonSerializationException">Deserialization failed</exception>
        public static T Deserialize<T>(string json)
        {
            return _default.Deserialize<T>(json);
        }

        /// <summary>
        /// Deserialize JSON to object using the default serializer (non-generic).
        /// </summary>
        /// <param name="json">JSON string to parse</param>
        /// <param name="type">Target type</param>
        /// <returns>Deserialized object</returns>
        /// <exception cref="JsonSerializationException">Deserialization failed</exception>
        public static object Deserialize(string json, Type type)
        {
            return _default.Deserialize(json, type);
        }

        /// <summary>
        /// Check if a custom serializer is registered.
        /// </summary>
        /// <returns>True if a non-default serializer is registered</returns>
        public static bool HasCustomSerializer()
        {
            return _default != LiteJsonSerializer.Instance;
        }
    }
}
```

## Design Notes

### Thread Safety

- **Reads:** Lock-free access via `volatile` field — safe for concurrent access
- **Writes:** Locked to prevent race conditions during registration
- **Volatile ensures:** All reads see the latest write (memory barrier)
- **Pattern:** "Register once at startup" — no concurrent writes expected

> [!IMPORTANT]
> While changing `Default` after startup is technically safe due to `volatile`, 
> it's not recommended. Register your serializer once at app initialization.

### Singleton Pattern

`LiteJsonSerializer.Instance` is the default. It's a stateless singleton, so sharing is safe.

### User Workflow

**At startup (once):**
```csharp
// Option 1: Use Newtonsoft
JsonSerializer.SetDefault(new NewtonsoftJsonAdapter());

// Option 2: Use System.Text.Json with source generators
JsonSerializer.SetDefault(new SystemTextJsonAdapter(MyJsonContext.Default));

// Option 3: Use built-in (no action needed)
```

**Throughout the app:**
```csharp
var json = JsonSerializer.Serialize(myData);
var data = JsonSerializer.Deserialize<MyType>(json);
```

### Example Newtonsoft Adapter

```csharp
public class NewtonsoftJsonAdapter : IJsonSerializer
{
    private readonly JsonSerializerSettings _settings;

    public NewtonsoftJsonAdapter(JsonSerializerSettings settings = null)
    {
        _settings = settings ?? new JsonSerializerSettings();
    }

    public string Serialize<T>(T value) =>
        JsonConvert.SerializeObject(value, _settings);

    public T Deserialize<T>(string json) =>
        JsonConvert.DeserializeObject<T>(json, _settings);

    public string Serialize(object value, Type type) =>
        JsonConvert.SerializeObject(value, type, _settings);

    public object Deserialize(string json, Type type) =>
        JsonConvert.DeserializeObject(json, type, _settings);
}
```

## Namespace

`TurboHTTP.JSON`

## Validation Criteria

- [ ] Default serializer is LiteJsonSerializer
- [ ] Can register custom serializer
- [ ] Custom serializer is used for all operations
- [ ] ResetToDefault restores LiteJsonSerializer
- [ ] HasCustomSerializer returns correct value
- [ ] Thread-safe registration (concurrent writes don't corrupt state)

## Test Cases

```csharp
// Default behavior
JsonSerializer.Serialize(42) → "42" (via LiteJson)
JsonSerializer.HasCustomSerializer() → false

// Custom registration
var mockSerializer = new MockJsonSerializer();
JsonSerializer.SetDefault(mockSerializer);
JsonSerializer.Serialize(42) → calls mockSerializer.Serialize(42)
JsonSerializer.HasCustomSerializer() → true

// Reset
JsonSerializer.ResetToDefault();
JsonSerializer.HasCustomSerializer() → false

// Null handling
JsonSerializer.SetDefault(null) → throws ArgumentNullException
```
