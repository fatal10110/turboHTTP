# Step 3D.1: IJsonSerializer Interface

**File:** `Runtime/JSON/IJsonSerializer.cs`  
**Depends on:** Nothing  
**Spec:** Custom abstraction

## Purpose

Define the abstraction layer for JSON serializers, allowing TurboHTTP to work with any JSON library (LiteJson, Newtonsoft, System.Text.Json, Unity JsonUtility). This interface decouples the HTTP client from specific JSON implementations.

## Interface to Implement

### `IJsonSerializer`

```csharp
using System;

namespace TurboHTTP.JSON
{
    /// <summary>
    /// Abstraction over JSON serialization implementation.
    /// Allows switching between LiteJson, Newtonsoft, System.Text.Json,
    /// or any custom JSON library without changing calling code.
    /// </summary>
    public interface IJsonSerializer
    {
        /// <summary>
        /// Serialize an object to a JSON string.
        /// </summary>
        /// <typeparam name="T">Type of object to serialize</typeparam>
        /// <param name="value">Object to serialize</param>
        /// <returns>JSON string representation</returns>
        /// <exception cref="JsonSerializationException">
        /// Serialization failed due to unsupported type or circular reference
        /// </exception>
        string Serialize<T>(T value);

        /// <summary>
        /// Deserialize a JSON string to an object.
        /// </summary>
        /// <typeparam name="T">Target type to deserialize to</typeparam>
        /// <param name="json">JSON string to parse</param>
        /// <returns>Deserialized object instance</returns>
        /// <exception cref="JsonSerializationException">
        /// Deserialization failed due to invalid JSON or type mismatch
        /// </exception>
        T Deserialize<T>(string json);

        /// <summary>
        /// Serialize an object to a JSON string (non-generic).
        /// </summary>
        /// <param name="value">Object to serialize</param>
        /// <param name="type">Type of the object</param>
        /// <returns>JSON string representation</returns>
        string Serialize(object value, Type type);

        /// <summary>
        /// Deserialize a JSON string to an object (non-generic).
        /// </summary>
        /// <param name="json">JSON string to parse</param>
        /// <param name="type">Target type to deserialize to</param>
        /// <returns>Deserialized object instance</returns>
        object Deserialize(string json, Type type);
    }

    /// <summary>
    /// Exception thrown when JSON serialization or deserialization fails.
    /// </summary>
    public class JsonSerializationException : Exception
    {
        public JsonSerializationException(string message) 
            : base(message) { }
        
        public JsonSerializationException(string message, Exception innerException) 
            : base(message, innerException) { }
    }
}
```

## Design Notes

### Generic vs Non-Generic Methods

Both are provided for flexibility:
- **Generic:** Preferred for type-safe code, AOT-friendly when T is known at compile time
- **Non-Generic:** Required for dynamic scenarios (middleware, plugins)

### Exception Handling

- **`JsonSerializationException`:** Custom exception for consistent error handling
- Wraps underlying library exceptions (Newtonsoft's `JsonException`, S.T.J's `JsonException`, etc.)
- Includes inner exception for debugging

### No Async Methods

JSON serialization is typically memory-bound, not I/O-bound. For large payloads:
- Use streaming APIs (out of scope for Phase 3D)
- Or run serialization on thread pool: `await Task.Run(() => serializer.Serialize(obj))`

## Namespace

`TurboHTTP.JSON`

## Validation Criteria

- [ ] Interface compiles without errors
- [ ] No Unity engine references
- [ ] No external dependencies (pure .NET Standard 2.0)
- [ ] XML documentation comments are complete
- [ ] Exception type is portable (derived from System.Exception)

## Implementation Notes

Implementations of this interface:
- **Step 3D.4:** `LiteJsonSerializer` (built-in, AOT-safe)
- **User-provided:** Newtonsoft adapter, System.Text.Json adapter, etc.

Users can create adapters like:

```csharp
public class NewtonsoftJsonSerializer : IJsonSerializer
{
    public string Serialize<T>(T value) => 
        Newtonsoft.Json.JsonConvert.SerializeObject(value);
    
    public T Deserialize<T>(string json) => 
        Newtonsoft.Json.JsonConvert.DeserializeObject<T>(json);
    
    // ... non-generic implementations
}
```
