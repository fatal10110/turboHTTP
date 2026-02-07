# Step 3D.4: LiteJsonSerializer Implementation

**File:** `Runtime/JSON/LiteJson/LiteJsonSerializer.cs`  
**Depends on:** 3D.1 (IJsonSerializer), 3D.2 (JsonReader), 3D.3 (JsonWriter)  
**Spec:** N/A (internal implementation)

## Purpose

Implement the `IJsonSerializer` interface using the LiteJson reader and writer. This provides a built-in, AOT-safe JSON serializer that works on all platforms without external dependencies.

## Class to Implement

### `LiteJsonSerializer`

```csharp
using System;
using System.Collections.Generic;

namespace TurboHTTP.JSON.Lite
{
    /// <summary>
    /// Built-in JSON serializer using LiteJson reader/writer.
    /// AOT-safe, zero dependencies, works on all platforms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Limitations:</b> This serializer works with primitives, dictionaries, 
    /// and lists. Custom types must be converted to/from Dictionary&lt;string, object&gt;
    /// manually, or use a full-featured serializer like Newtonsoft.
    /// </para>
    /// </remarks>
    public sealed class LiteJsonSerializer : IJsonSerializer
    {
        /// <summary>
        /// Singleton instance for convenience.
        /// </summary>
        public static readonly LiteJsonSerializer Instance = new();

        /// <summary>
        /// Serialize an object to JSON.
        /// </summary>
        /// <remarks>
        /// Supported types: primitives, strings, DateTime, Guid, enums,
        /// arrays, lists, and dictionaries. Custom types throw an exception.
        /// </remarks>
        public string Serialize<T>(T value)
        {
            return Serialize((object)value, typeof(T));
        }

        /// <summary>
        /// Serialize an object to JSON (non-generic).
        /// </summary>
        public string Serialize(object value, Type type)
        {
            try
            {
                var writer = new JsonWriter();
                return writer.Write(value);
            }
            catch (Exception ex) when (ex is not JsonSerializationException)
            {
                throw new JsonSerializationException(
                    $"Failed to serialize type '{type?.Name ?? "unknown"}'", ex);
            }
        }

        /// <summary>
        /// Deserialize JSON to type T.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For primitive types (int, string, bool, etc.), returns the parsed value.
        /// For complex types, returns Dictionary&lt;string, object&gt; or List&lt;object&gt;.
        /// </para>
        /// <para>
        /// To deserialize to custom types, either:
        /// <list type="bullet">
        /// <item>Manually convert the returned dictionary</item>
        /// <item>Use a full-featured serializer like Newtonsoft</item>
        /// </list>
        /// </para>
        /// </remarks>
        public T Deserialize<T>(string json)
        {
            var result = Deserialize(json, typeof(T));
            return ConvertTo<T>(result);
        }

        /// <summary>
        /// Deserialize JSON to object (non-generic).
        /// </summary>
        public object Deserialize(string json, Type type)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new JsonSerializationException("JSON string is null or empty");
            }

            try
            {
                var reader = new JsonReader(json);
                var result = reader.Parse();

                // If target type is object or matches parsed type, return directly
                if (type == typeof(object) || 
                    type == typeof(Dictionary<string, object>) ||
                    type == typeof(List<object>))
                {
                    return result;
                }

                // Try to convert primitive types
                return ConvertPrimitive(result, type);
            }
            catch (JsonParseException ex)
            {
                throw new JsonSerializationException($"JSON parse error: {ex.Message}", ex);
            }
            catch (Exception ex) when (ex is not JsonSerializationException)
            {
                throw new JsonSerializationException(
                    $"Failed to deserialize to type '{type?.Name ?? "unknown"}'", ex);
            }
        }

        private T ConvertTo<T>(object value)
        {
            if (value == null)
            {
                if (default(T) == null)
                    return default;
                throw new JsonSerializationException(
                    $"Cannot convert null to value type '{typeof(T).Name}'");
            }

            if (value is T typedValue)
                return typedValue;

            // Handle numeric conversions
            if (value is long l)
            {
                return (T)ConvertInt64<T>(l);
            }
            if (value is ulong ul)
            {
                return (T)ConvertUInt64<T>(ul);
            }
            if (value is double d)
            {
                return (T)ConvertDouble<T>(d);
            }

            // Handle dictionary to Dictionary<string, object>
            if (value is Dictionary<string, object> dict && typeof(T) == typeof(Dictionary<string, object>))
            {
                return (T)(object)dict;
            }

            // Handle list to List<object>
            if (value is List<object> list && typeof(T) == typeof(List<object>))
            {
                return (T)(object)list;
            }

            throw new JsonSerializationException(
                $"Cannot convert '{value.GetType().Name}' to '{typeof(T).Name}'. " +
                "Use Dictionary<string, object> or register a custom serializer.");
        }

        private object ConvertInt64<T>(long value)
        {
            var type = typeof(T);

            try
            {
                checked
                {
                    if (type == typeof(int)) return (int)value;
                    if (type == typeof(long)) return value;
                    if (type == typeof(float)) return (float)value;
                    if (type == typeof(double)) return (double)value;
                    if (type == typeof(decimal)) return (decimal)value;
                    if (type == typeof(byte)) return (byte)value;
                    if (type == typeof(sbyte)) return (sbyte)value;
                    if (type == typeof(short)) return (short)value;
                    if (type == typeof(ushort)) return (ushort)value;
                    if (type == typeof(uint)) return (uint)value;
                    if (type == typeof(ulong)) return (ulong)value;

                    if (type == typeof(int?)) return (int?)value;
                    if (type == typeof(long?)) return (long?)value;
                    if (type == typeof(float?)) return (float?)value;
                    if (type == typeof(double?)) return (double?)value;
                    if (type == typeof(decimal?)) return (decimal?)value;
                }
            }
            catch (OverflowException)
            {
                throw new JsonSerializationException(
                    $"Value {value} overflows target type '{type.Name}'");
            }

            throw new JsonSerializationException(
                $"Cannot convert number to '{type.Name}'");
        }

        private object ConvertUInt64<T>(ulong value)
        {
            var type = typeof(T);

            try
            {
                checked
                {
                    if (type == typeof(uint)) return (uint)value;
                    if (type == typeof(ulong)) return value;
                    if (type == typeof(int)) return (int)value;
                    if (type == typeof(long)) return (long)value;
                    if (type == typeof(float)) return (float)value;
                    if (type == typeof(double)) return (double)value;
                    if (type == typeof(decimal)) return (decimal)value;
                    if (type == typeof(byte)) return (byte)value;
                    if (type == typeof(sbyte)) return (sbyte)value;
                    if (type == typeof(short)) return (short)value;
                    if (type == typeof(ushort)) return (ushort)value;

                    if (type == typeof(uint?)) return (uint?)value;
                    if (type == typeof(ulong?)) return (ulong?)value;
                    if (type == typeof(int?)) return (int?)value;
                    if (type == typeof(long?)) return (long?)value;
                    if (type == typeof(float?)) return (float?)value;
                    if (type == typeof(double?)) return (double?)value;
                    if (type == typeof(decimal?)) return (decimal?)value;
                }
            }
            catch (OverflowException)
            {
                throw new JsonSerializationException(
                    $"Value {value} overflows target type '{type.Name}'");
            }

            throw new JsonSerializationException(
                $"Cannot convert number to '{type.Name}'");
        }

        private object ConvertDouble<T>(double d)
        {
            var type = typeof(T);
            
            // Use checked context to catch overflow
            try
            {
                checked
                {
                    if (type == typeof(int)) return (int)d;
                    if (type == typeof(long)) return (long)d;
                    if (type == typeof(float)) return (float)d;
                    if (type == typeof(double)) return d;
                    if (type == typeof(decimal)) return (decimal)d;
                    if (type == typeof(byte)) return (byte)d;
                    if (type == typeof(sbyte)) return (sbyte)d;
                    if (type == typeof(short)) return (short)d;
                    if (type == typeof(ushort)) return (ushort)d;
                    if (type == typeof(uint)) return (uint)d;
                    if (type == typeof(ulong)) return (ulong)d;

                    // Nullable numerics
                    if (type == typeof(int?)) return (int?)d;
                    if (type == typeof(long?)) return (long?)d;
                    if (type == typeof(float?)) return (float?)d;
                    if (type == typeof(double?)) return (double?)d;
                }
            }
            catch (OverflowException)
            {
                throw new JsonSerializationException(
                    $"Value {d} overflows target type '{type.Name}'");
            }

            throw new JsonSerializationException(
                $"Cannot convert number to '{type.Name}'");
        }

        private object ConvertInt64(long value, Type targetType)
        {
            try
            {
                checked
                {
                    if (targetType == typeof(int)) return (int)value;
                    if (targetType == typeof(long)) return value;
                    if (targetType == typeof(float)) return (float)value;
                    if (targetType == typeof(double)) return (double)value;
                    if (targetType == typeof(decimal)) return (decimal)value;
                    if (targetType == typeof(byte)) return (byte)value;
                    if (targetType == typeof(sbyte)) return (sbyte)value;
                    if (targetType == typeof(short)) return (short)value;
                    if (targetType == typeof(ushort)) return (ushort)value;
                    if (targetType == typeof(uint)) return (uint)value;
                    if (targetType == typeof(ulong)) return (ulong)value;
                }
            }
            catch (OverflowException)
            {
                throw new JsonSerializationException(
                    $"Value {value} overflows target type '{targetType.Name}'");
            }

            return null;
        }

        private object ConvertUInt64(ulong value, Type targetType)
        {
            try
            {
                checked
                {
                    if (targetType == typeof(uint)) return (uint)value;
                    if (targetType == typeof(ulong)) return value;
                    if (targetType == typeof(int)) return (int)value;
                    if (targetType == typeof(long)) return (long)value;
                    if (targetType == typeof(float)) return (float)value;
                    if (targetType == typeof(double)) return (double)value;
                    if (targetType == typeof(decimal)) return (decimal)value;
                    if (targetType == typeof(byte)) return (byte)value;
                    if (targetType == typeof(sbyte)) return (sbyte)value;
                    if (targetType == typeof(short)) return (short)value;
                    if (targetType == typeof(ushort)) return (ushort)value;
                }
            }
            catch (OverflowException)
            {
                throw new JsonSerializationException(
                    $"Value {value} overflows target type '{targetType.Name}'");
            }

            return null;
        }

        private object ConvertPrimitive(object value, Type targetType)
        {
            // Null handling
            if (value == null)
            {
                if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null)
                    return null;
                throw new JsonSerializationException(
                    $"Cannot convert null to value type '{targetType.Name}'");
            }

            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                return ConvertPrimitive(value, underlying);

            // Direct type match
            if (targetType.IsAssignableFrom(value.GetType()))
                return value;

            if (value is long l)
            {
                var converted = ConvertInt64(l, targetType);
                if (converted != null) return converted;
            }

            if (value is ulong ul)
            {
                var converted = ConvertUInt64(ul, targetType);
                if (converted != null) return converted;
            }

            // Numeric conversion from double (with overflow checking)
            if (value is double d)
            {
                try
                {
                    checked
                    {
                        if (targetType == typeof(int)) return (int)d;
                        if (targetType == typeof(long)) return (long)d;
                        if (targetType == typeof(float)) return (float)d;
                        if (targetType == typeof(decimal)) return (decimal)d;
                        if (targetType == typeof(byte)) return (byte)d;
                        if (targetType == typeof(sbyte)) return (sbyte)d;
                        if (targetType == typeof(short)) return (short)d;
                        if (targetType == typeof(ushort)) return (ushort)d;
                        if (targetType == typeof(uint)) return (uint)d;
                        if (targetType == typeof(ulong)) return (ulong)d;
                    }
                }
                catch (OverflowException)
                {
                    throw new JsonSerializationException(
                        $"Value {d} overflows target type '{targetType.Name}'");
                }
            }

            // String to special types
            if (value is string s)
            {
                if (targetType == typeof(DateTime))
                    return DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
                if (targetType == typeof(DateTimeOffset))
                    return DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
                if (targetType == typeof(Guid))
                    return Guid.Parse(s);
                if (targetType == typeof(Uri))
                    return new Uri(s);
            }

            throw new JsonSerializationException(
                $"Cannot convert '{value.GetType().Name}' to '{targetType.Name}'. " +
                "Use Dictionary<string, object> or register a custom serializer.");
        }
    }
}
```

## Design Notes

### Type Conversion Strategy

1. **Primitives:** Direct conversion with range checking
2. **Strings:** Support for DateTime, Guid, Uri parsing
3. **Collections:** Return as `Dictionary<string, object>` or `List<object>`
4. **Custom types:** NOT supported — users must convert manually or use Newtonsoft

Note: LiteJsonReader returns `long`/`ulong` for integer tokens and `double` for fractional/exponent tokens.

### Why No Reflection

Reflection-based object mapping is:
- Slow
- Breaks under IL2CPP AOT
- Requires complex attribute handling

Instead, we keep it simple:
- Deserialize to dictionary
- Let callers convert to their types (they know the structure)

### Usage Pattern for Custom Types

```csharp
// Serialize: convert to dictionary first
var dict = new Dictionary<string, object>
{
    ["name"] = user.Name,
    ["age"] = user.Age
};
var json = LiteJsonSerializer.Instance.Serialize(dict);

// Deserialize: convert from dictionary
var dict = LiteJsonSerializer.Instance.Deserialize<Dictionary<string, object>>(json);
var user = new User
{
    Name = (string)dict["name"],
    Age = dict["age"] is long l ? (int)l : (int)(double)dict["age"]
        // Integer tokens are long/ulong, fractional tokens are double
};
```

### Error Handling

All exceptions are wrapped in `JsonSerializationException`:
- Parse errors from JsonReader
- Type conversion errors
- Unsupported type errors

## Namespace

`TurboHTTP.JSON.Lite`

## Validation Criteria

- [ ] Serializes primitives correctly
- [ ] Serializes dictionaries and lists
- [ ] Deserializes to primitive types with conversion
- [ ] Deserializes to Dictionary<string, object>
- [ ] Throws clear errors for unsupported conversions
- [ ] Round-trip: Serialize → Deserialize produces equivalent data

## Test Cases

```csharp
// Round-trip primitives
Serialize(42) → Deserialize<int>() → 42
Serialize("hello") → Deserialize<string>() → "hello"
Serialize(true) → Deserialize<bool>() → true

// Round-trip collections
var dict = new Dictionary<string, object>{{"a", 1}, {"b", "two"}};
Serialize(dict) → Deserialize<Dictionary<string, object>>() → equivalent dict

var list = new List<object>{1, "two", 3};
Serialize(list) → Deserialize<List<object>>() → equivalent list

// Type conversion
Deserialize<int>("42") → 42
Deserialize<ulong>("42") → 42UL
Deserialize<long>("9223372036854775807") → 9223372036854775807
Deserialize<DateTime>("\"2024-01-15T10:30:00Z\"") → DateTime(...)
Deserialize<Guid>("\"550e8400-e29b-41d4-a716-446655440000\"") → Guid(...)

// Error cases
Deserialize<int>("\"not a number\"") → throws JsonSerializationException
Deserialize<long>("9223372036854775808") → throws JsonSerializationException
Serialize(new CustomClass()) → throws JsonSerializationException
```
