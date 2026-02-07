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
        public static readonly LiteJsonSerializer Instance = new LiteJsonSerializer();

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

                // If target type is object, return directly
                if (type == typeof(object))
                {
                    return result;
                }

                // Validate container type match (P2 fix)
                if (type == typeof(Dictionary<string, object>))
                {
                    if (result is Dictionary<string, object>)
                        return result;
                    throw new JsonSerializationException(
                        $"Expected JSON object but got {(result is List<object> ? "array" : result?.GetType().Name ?? "null")}. " +
                        "Cannot deserialize to Dictionary<string, object>.");
                }
                if (type == typeof(List<object>))
                {
                    if (result is List<object>)
                        return result;
                    throw new JsonSerializationException(
                        $"Expected JSON array but got {(result is Dictionary<string, object> ? "object" : result?.GetType().Name ?? "null")}. " +
                        "Cannot deserialize to List<object>.");
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
            var targetType = typeof(T);
            var underlying = Nullable.GetUnderlyingType(targetType);
            
            // Handle nullable types (P1 fix)
            if (underlying != null)
            {
                if (value == null)
                    return default; // null for Nullable<T>
                
                // Convert to underlying type and box back
                var converted = ConvertPrimitive(value, underlying);
                return (T)converted;
            }
            
            if (value == null)
            {
                if (default(T) == null)
                    return default;
                throw new JsonSerializationException(
                    $"Cannot convert null to value type '{typeof(T).Name}'");
            }

            if (value is T typedValue)
                return typedValue;
            if (value is long l)
            {
                var converted = ConvertInt64(l, targetType);
                if (converted != null) return (T)converted;
            }
            if (value is ulong ul)
            {
                var converted = ConvertUInt64(ul, targetType);
                if (converted != null) return (T)converted;
            }
            if (value is double d)
            {
                var converted = ConvertDouble(d, targetType);
                if (converted != null) return (T)converted;
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

        private object ConvertDouble(double d, Type targetType)
        {
            try
            {
                checked
                {
                    if (targetType == typeof(int)) return (int)d;
                    if (targetType == typeof(long)) return (long)d;
                    if (targetType == typeof(float)) return (float)d;
                    if (targetType == typeof(double)) return d;
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
                var converted = ConvertDouble(d, targetType);
                if (converted != null) return converted;
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
