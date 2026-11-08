using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.JSON.Lite
{
    /// <summary>
    /// Built-in JSON serializer using LiteJson reader/writer.
    /// AOT-safe, zero dependencies, works on all platforms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Limitations:</b> This serializer works with primitives, strings, <see cref="DateTime"/>,
    /// <see cref="DateTimeOffset"/>, <see cref="Guid"/>, <see cref="Uri"/>, dictionaries and lists.
    /// It does not map custom types (POCOs, typed collections such as <c>List&lt;int&gt;</c>): asking
    /// for one throws <see cref="NotSupportedException"/> in both directions. Convert to/from
    /// Dictionary&lt;string, object&gt; manually, or use another serializer: TurboHTTP.Complete
    /// installs the <c>JsonUtility</c>-backed <c>JsonUtilitySerializer</c> at startup (which
    /// delegates these built-in shapes to this class), or register a System.Text.Json/Newtonsoft
    /// adapter with <see cref="JsonSerializer.SetDefault"/>.
    /// </para>
    /// <para>
    /// <b>Input:</b> strict RFC 8259 (see <see cref="JsonReader"/>); one leading UTF-8 byte-order mark
    /// is ignored; input larger than <see cref="MaxInputBytes"/> (default
    /// <see cref="DefaultMaxInputBytes"/>, 16 MiB) is rejected before it is decoded or parsed.
    /// Timestamps keep their kind: <c>"...Z"</c> deserializes to a <see cref="DateTime"/> with
    /// <see cref="DateTimeKind.Utc"/>. Integers are exact across the whole
    /// <see cref="long"/>/<see cref="ulong"/> range.
    /// </para>
    /// </remarks>
    public sealed class LiteJsonSerializer : IJsonSerializer
    {
        /// <summary>
        /// Default for <see cref="MaxInputBytes"/>: 16 MiB (16,777,216 bytes) of UTF-8 input.
        /// </summary>
        public const int DefaultMaxInputBytes = 16 * 1024 * 1024;

        private const string UnsupportedTypeAdvice =
            "LiteJsonSerializer maps JSON only to object, Dictionary<string, object>, List<object>, " +
            "strings, numbers, bool, DateTime, DateTimeOffset, Guid and Uri (no reflection, IL2CPP-safe). " +
            "For custom types use TurboHTTP.Complete (it installs the JsonUtility-backed JsonUtilitySerializer " +
            "for [Serializable] types), register a System.Text.Json or Newtonsoft adapter with " +
            "JsonSerializer.SetDefault, or convert to/from Dictionary<string, object>.";

        /// <summary>
        /// Singleton instance for convenience, with <see cref="DefaultMaxInputBytes"/>.
        /// </summary>
        public static readonly LiteJsonSerializer Instance = new LiteJsonSerializer();

        /// <summary>
        /// Creates a serializer that accepts at most <see cref="DefaultMaxInputBytes"/> of input.
        /// </summary>
        public LiteJsonSerializer()
            : this(DefaultMaxInputBytes)
        {
        }

        /// <summary>
        /// Creates a serializer with an input-size cap. Register it with
        /// <see cref="JsonSerializer.SetDefault"/> to change the limit used by <c>AsJson</c>.
        /// </summary>
        /// <param name="maxInputBytes">Largest accepted JSON input in UTF-8 bytes (BOM included).</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxInputBytes"/> is not positive.</exception>
        public LiteJsonSerializer(int maxInputBytes)
        {
            if (maxInputBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxInputBytes), maxInputBytes, "Must be positive.");
            MaxInputBytes = maxInputBytes;
        }

        /// <summary>
        /// Largest JSON input this instance deserializes, in UTF-8 bytes. Larger input throws
        /// <see cref="JsonSerializationException"/> before it is decoded or parsed. Default
        /// <see cref="DefaultMaxInputBytes"/> (16 MiB). Immutable after construction.
        /// </summary>
        public int MaxInputBytes { get; }

        /// <summary>
        /// Serialize an object to JSON.
        /// </summary>
        /// <remarks>
        /// Supported types: primitives, strings, DateTime, Guid, enums,
        /// arrays, lists, and dictionaries. Custom types (also nested in a collection) throw
        /// <see cref="NotSupportedException"/> naming the alternatives (JsonUtility, System.Text.Json).
        /// </remarks>
        /// <exception cref="NotSupportedException">The value contains a custom (POCO) type.</exception>
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
            catch (Exception ex) when (ex is not JsonSerializationException && ex is not NotSupportedException)
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
        /// <item>Use another serializer (<c>JsonUtilitySerializer</c>, or your own via <see cref="JsonSerializer.SetDefault"/>)</item>
        /// </list>
        /// </para>
        /// </remarks>
        /// <exception cref="NotSupportedException"><typeparamref name="T"/> is a custom (POCO) or typed collection type.</exception>
        /// <exception cref="JsonSerializationException">Invalid JSON, input over <see cref="MaxInputBytes"/>, or a type mismatch.</exception>
        public T Deserialize<T>(string json)
        {
            var result = Deserialize(json, typeof(T));
            return ConvertTo<T>(result);
        }

        /// <summary>
        /// Deserialize JSON to object (non-generic).
        /// </summary>
        /// <exception cref="NotSupportedException"><paramref name="type"/> is a custom (POCO) or typed collection type.</exception>
        /// <exception cref="JsonSerializationException">Invalid JSON, input over <see cref="MaxInputBytes"/>, or a type mismatch.</exception>
        public object Deserialize(string json, Type type)
        {
            EnsureSupportedTarget(type);
            if (string.IsNullOrEmpty(json))
            {
                throw new JsonSerializationException("JSON string is null or empty");
            }

            // UTF-8 needs at most 3 bytes per UTF-16 char, so only count when that bound exceeds the cap.
            if ((long)json.Length * 3 > MaxInputBytes)
            {
                int byteCount = json.Length > MaxInputBytes ? json.Length : Encoding.UTF8.GetByteCount(json);
                if (byteCount > MaxInputBytes)
                    throw InputTooLarge(byteCount);
            }

            return ParseAndConvert(json, type);
        }

        private object ParseAndConvert(string json, Type type)
        {
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
            catch (Exception ex) when (ex is not JsonSerializationException)
            {
                // One clause: under IL2CPP an exception thrown by an earlier clause of a filtered try is
                // caught by a later one (#320).
                if (ex is JsonParseException parseError)
                    throw new JsonSerializationException($"JSON parse error: {parseError.Message}", parseError);

                throw new JsonSerializationException(
                    $"Failed to deserialize to type '{type?.Name ?? "unknown"}'", ex);
            }
        }

        // ── IJsonSerializer buffer-writer bridge ─────────────────────────────────

        /// <summary>
        /// Serializes <paramref name="value"/> as UTF-8 JSON directly into
        /// <paramref name="output"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// LiteJson does not have a native <see cref="IBufferWriter{T}"/> writer, so this
        /// implementation bridges via the string path and encodes the resulting JSON string
        /// into the writer. There are two temporary allocations:
        /// <list type="number">
        /// <item>The intermediate JSON string (unavoidable until LiteJson gains a streaming writer).</item>
        /// <item>A temporary <c>byte[]</c> from <c>Encoding.UTF8.GetBytes(string)</c> — required
        ///   because <c>Encoding.UTF8.GetBytes(ReadOnlySpan&lt;char&gt;, Span&lt;byte&gt;)</c>
        ///   is .NET Core 2.1+ only and not available on .NET Standard 2.1 / Unity IL2CPP.</item>
        /// </list>
        /// Both allocations are known limitations of this bridge. The allocation reduction from
        /// the buffer-writer path is realized in the caller (no intermediate <c>byte[]</c> copy
        /// needed between the serializer and the request builder). The serializer's internal
        /// allocations can be eliminated in a future version by adding a span-based writer to
        /// LiteJson.
        /// </para>
        /// </remarks>
        public void Serialize<T>(T value, IBufferWriter<byte> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));

            var json = Serialize(value);

            // GetByteCount for exact sizing, then GetBytes into a temporary array, copy to writer.
            // Array-based path required for .NET Standard 2.1 / Unity IL2CPP compatibility —
            // Encoding.UTF8.GetBytes(ReadOnlySpan<char>, Span<byte>) is .NET Core 2.1+ only.
            int byteCount = Encoding.UTF8.GetByteCount(json);
            var span = output.GetSpan(byteCount);
            var bytes = Encoding.UTF8.GetBytes(json);
            bytes.CopyTo(span);
            output.Advance(byteCount);
        }

        /// <summary>
        /// Deserializes <typeparamref name="T"/> from a UTF-8 JSON byte sequence.
        /// Single-segment sequences are decoded directly. Multi-segment sequences are
        /// flattened into a temporary string before parsing (LiteJson requires contiguous input).
        /// </summary>
        /// <exception cref="NotSupportedException"><typeparamref name="T"/> is a custom (POCO) or typed collection type.</exception>
        /// <exception cref="JsonSerializationException">Invalid JSON, input over <see cref="MaxInputBytes"/>, or a type mismatch.</exception>
        public T Deserialize<T>(ReadOnlySequence<byte> input)
        {
            EnsureSupportedTarget(typeof(T));

            // Checked on the raw byte count, before anything is copied, decoded or parsed.
            if (input.Length > MaxInputBytes)
                throw InputTooLarge(input.Length);

            string json;

            if (input.IsSingleSegment)
            {
                json = Encoding.UTF8.GetString(input.First.Span.ToArray(), 0, (int)input.Length);
            }
            else
            {
                // Multi-segment: copy to a pooled array, then decode.
                var length = (int)input.Length;
                var buffer = ByteArrayPool.Rent(length);
                try
                {
                    input.CopyTo(buffer);
                    json = Encoding.UTF8.GetString(buffer, 0, length);
                }
                finally
                {
                    ByteArrayPool.Return(buffer);
                }
            }

            if (json.Length == 0)
                throw new JsonSerializationException("JSON string is null or empty");

            return ConvertTo<T>(ParseAndConvert(json, typeof(T)));
        }

        private JsonSerializationException InputTooLarge(long byteCount)
        {
            return new JsonSerializationException(
                $"JSON input of {byteCount} bytes exceeds MaxInputBytes ({MaxInputBytes}).");
        }

        /// <summary>
        /// Throws <see cref="NotSupportedException"/> for targets this serializer can never produce
        /// (custom classes/structs, typed collections). Type metadata checks only: no reflection over
        /// members, nothing IL2CPP strips.
        /// </summary>
        private static void EnsureSupportedTarget(Type type)
        {
            if (type == null || IsSupportedTarget(Nullable.GetUnderlyingType(type) ?? type))
                return;

            throw new NotSupportedException($"Cannot deserialize JSON to '{type.Name}'. {UnsupportedTypeAdvice}");
        }

        private static bool IsSupportedTarget(Type type)
        {
            return type.IsPrimitive
                || type.IsEnum
                || type == typeof(string)
                || type == typeof(decimal)
                || type == typeof(DateTime)
                || type == typeof(DateTimeOffset)
                || type == typeof(Guid)
                || type == typeof(Uri)
                || type.IsAssignableFrom(typeof(Dictionary<string, object>))
                || type.IsAssignableFrom(typeof(List<object>));
        }

        internal static string UnsupportedSerializeMessage(Type type)
        {
            return $"Cannot serialize type '{type.Name}' to JSON. {UnsupportedTypeAdvice}";
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
                // RoundtripKind: "Z" stays DateTimeKind.Utc (and no zone stays Unspecified) instead of
                // being shifted to local time.
                if (targetType == typeof(DateTime))
                    return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (targetType == typeof(DateTimeOffset))
                    return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
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
