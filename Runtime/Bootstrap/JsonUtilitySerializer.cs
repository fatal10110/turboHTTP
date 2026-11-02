using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using TurboHTTP.JSON.Lite;
using UnityEngine;
using UnityEngine.Scripting;

namespace TurboHTTP.JSON
{
    /// <summary>
    /// <see cref="IJsonSerializer"/> backed by Unity's <see cref="JsonUtility"/>, so
    /// <c>AsJson&lt;T&gt;()</c>, <c>WithJsonBody(...)</c>, <c>GetJsonAsync&lt;T&gt;</c> and WebSocket
    /// JSON messages work with plain <c>[Serializable]</c> classes. TurboHTTP's startup bootstrap
    /// (TurboHTTP.Complete) installs it as <see cref="JsonSerializer.Default"/> unless a serializer
    /// was registered with <see cref="JsonSerializer.SetDefault"/> before; a later
    /// <see cref="JsonSerializer.SetDefault"/> replaces it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Supported shapes.</b> Custom types follow <see cref="JsonUtility"/> rules: public fields (or
    /// <c>[SerializeField]</c> private fields) of <c>[Serializable]</c> classes and structs, nested
    /// <c>[Serializable]</c> types, arrays and <c>List&lt;T&gt;</c> fields. Properties, dictionary
    /// fields, polymorphic fields and anonymous types are not serialized by <see cref="JsonUtility"/>.
    /// Anonymous types and types without a serializable field (for example DTOs with only
    /// auto-properties) throw <see cref="JsonSerializationException"/> in both directions instead of
    /// producing <c>{}</c> or an all-defaults instance; properties of a type that also has
    /// serializable fields are skipped without an error.
    /// For those, use <c>Dictionary&lt;string, object&gt;</c> or register a full serializer (for
    /// example a Newtonsoft adapter) with <see cref="JsonSerializer.SetDefault"/>.
    /// </para>
    /// <para>
    /// <b>Built-in shapes.</b> <c>object</c>, <c>Dictionary&lt;string, object&gt;</c>,
    /// <c>List&lt;object&gt;</c>, strings, numbers, booleans, enums, <see cref="DateTime"/>,
    /// <see cref="DateTimeOffset"/> and <see cref="Guid"/> go through
    /// <see cref="LiteJsonSerializer"/>, which <see cref="JsonUtility"/> cannot parse at the top
    /// level. A top-level JSON array is read into <c>T[]</c> element by element; a top-level
    /// <c>List&lt;T&gt;</c> or <c>Dictionary&lt;string, T&gt;</c> of a custom <c>T</c> is not
    /// supported (no runtime generic instantiation under IL2CPP): use <c>T[]</c> or a
    /// <c>[Serializable]</c> wrapper class.
    /// </para>
    /// <para>
    /// <b>IL2CPP stripping.</b> <see cref="JsonUtility"/> reads fields through Unity's serializer;
    /// a DTO that the code only creates through JSON keeps its fields as long as the type is
    /// referenced from code (it is, through <c>AsJson&lt;T&gt;</c>). Mark DTOs <c>[Preserve]</c>
    /// when they are reached only by name.
    /// </para>
    /// <para>
    /// <b>Threading.</b> <see cref="JsonUtility.ToJson(object)"/> and
    /// <see cref="JsonUtility.FromJson(string, Type)"/> may be called from background threads (Unity
    /// scripting reference); the instance is stateless, so any thread may use it. Types derived
    /// from <see cref="UnityEngine.Object"/> are not supported by
    /// <see cref="JsonUtility.FromJson(string, Type)"/>.
    /// </para>
    /// </remarks>
    [Preserve]
    public sealed class JsonUtilitySerializer : IJsonSerializer
    {
        private const int MaxDepth = 64;

        /// <summary>
        /// The shared, stateless instance.
        /// </summary>
        public static readonly JsonUtilitySerializer Instance = new JsonUtilitySerializer();

        private JsonUtilitySerializer()
        {
        }

        /// <summary>
        /// Installs <see cref="Instance"/> as <see cref="JsonSerializer.Default"/> when the default is
        /// still the built-in <see cref="LiteJsonSerializer"/>, so a serializer the project
        /// registered earlier is kept. Called by the startup bootstrap on the main thread.
        /// </summary>
        internal static void RegisterAsDefaultIfBuiltIn()
        {
            if (!JsonSerializer.HasCustomSerializer())
                JsonSerializer.SetDefault(Instance);
        }

        /// <inheritdoc />
        public string Serialize<T>(T value)
        {
            return Serialize(value, typeof(T));
        }

        /// <inheritdoc />
        public string Serialize(object value, Type type)
        {
            try
            {
                return SerializeValue(value, 0);
            }
            catch (Exception ex) when (ex is not JsonSerializationException)
            {
                throw new JsonSerializationException(
                    $"Failed to serialize type '{(value?.GetType() ?? type)?.Name ?? "unknown"}'", ex);
            }
        }

        /// <inheritdoc />
        public T Deserialize<T>(string json)
        {
            if (IsBuiltInShape(typeof(T)))
                return LiteJsonSerializer.Instance.Deserialize<T>(json);

            object result = Deserialize(json, typeof(T));
            return result == null ? default : (T)result;
        }

        /// <inheritdoc />
        public object Deserialize(string json, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (string.IsNullOrEmpty(json))
                throw new JsonSerializationException("JSON string is null or empty");

            try
            {
                return DeserializeValue(json, type, 0);
            }
            catch (Exception ex) when (ex is not JsonSerializationException)
            {
                throw new JsonSerializationException(
                    $"Failed to deserialize to type '{type.Name}'", ex);
            }
        }

        /// <summary>
        /// Serializes <paramref name="value"/> as UTF-8 JSON into <paramref name="output"/> (bridges
        /// through the string path, like <see cref="LiteJsonSerializer"/>).
        /// </summary>
        public void Serialize<T>(T value, IBufferWriter<byte> output)
        {
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            string json = Serialize(value);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            bytes.CopyTo(output.GetSpan(bytes.Length));
            output.Advance(bytes.Length);
        }

        /// <summary>
        /// Deserializes <typeparamref name="T"/> from a UTF-8 JSON byte sequence (decoded to a
        /// string first; <see cref="JsonUtility"/> needs contiguous text).
        /// </summary>
        public T Deserialize<T>(ReadOnlySequence<byte> input)
        {
            int length = checked((int)input.Length);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
            string json;
            try
            {
                input.CopyTo(buffer);
                json = Encoding.UTF8.GetString(buffer, 0, length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return Deserialize<T>(json);
        }

        private static string SerializeValue(object value, int depth)
        {
            if (value == null)
                return "null";
            if (depth > MaxDepth)
                throw new JsonSerializationException($"Maximum nesting depth ({MaxDepth}) exceeded");

            Type type = value.GetType();
            if (IsScalar(type))
                return LiteJsonSerializer.Instance.Serialize(value, type);

            if (value is IDictionary dictionary)
                return SerializeDictionary(dictionary, depth);

            if (value is IEnumerable sequence)
                return SerializeSequence(sequence, depth);

            if (IsAnonymousType(type))
            {
                throw new JsonSerializationException(
                    $"Anonymous type '{type.Name}' is not supported by JsonUtility. Use a [Serializable] class "
                    + "with public fields, a Dictionary<string, object>, or register a serializer with "
                    + "JsonSerializer.SetDefault.");
            }

            EnsureHasSerializableFields(type);
            return JsonUtility.ToJson(value);
        }

        private static string SerializeDictionary(IDictionary dictionary, int depth)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!first)
                    sb.Append(',');
                first = false;

                string key = entry.Key as string
                    ?? Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                sb.Append(LiteJsonSerializer.Instance.Serialize(key, typeof(string)));
                sb.Append(':');
                sb.Append(SerializeValue(entry.Value, depth + 1));
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static string SerializeSequence(IEnumerable sequence, int depth)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            bool first = true;
            foreach (object item in sequence)
            {
                if (!first)
                    sb.Append(',');
                first = false;
                sb.Append(SerializeValue(item, depth + 1));
            }

            sb.Append(']');
            return sb.ToString();
        }

        private static object DeserializeValue(string json, Type type, int depth)
        {
            if (depth > MaxDepth)
                throw new JsonSerializationException($"Maximum nesting depth ({MaxDepth}) exceeded");

            if (IsBuiltInShape(type))
                return LiteJsonSerializer.Instance.Deserialize(json, type);

            if (type.IsArray && type.GetArrayRank() == 1)
                return DeserializeArray(json, type.GetElementType(), depth);

            if (typeof(IEnumerable).IsAssignableFrom(type) || type.IsInterface || type.IsAbstract)
            {
                throw new JsonSerializationException(
                    $"Type '{type.Name}' is not supported by JsonUtilitySerializer. Use T[] for a top-level "
                    + "JSON array, a [Serializable] wrapper class, Dictionary<string, object>, or register a "
                    + "serializer with JsonSerializer.SetDefault.");
            }

            Type nullableUnderlying = Nullable.GetUnderlyingType(type);
            if (IsJsonNull(json))
            {
                if (!type.IsValueType || nullableUnderlying != null)
                    return null;
                throw new JsonSerializationException($"Cannot convert null to value type '{type.Name}'");
            }

            Type target = nullableUnderlying ?? type;
            EnsureHasSerializableFields(target);
            return JsonUtility.FromJson(json, target);
        }

        private static Array DeserializeArray(string json, Type elementType, int depth)
        {
            object parsed = LiteJsonSerializer.Instance.Deserialize(json, typeof(object));
            if (parsed == null)
                return null;
            if (!(parsed is List<object> items))
            {
                throw new JsonSerializationException(
                    $"Expected a JSON array for '{elementType.Name}[]' but got a JSON object.");
            }

            Array array = Array.CreateInstance(elementType, items.Count);
            IList target = array;
            for (int i = 0; i < items.Count; i++)
            {
                object item = items[i];
                if (item == null)
                {
                    if (elementType.IsValueType && Nullable.GetUnderlyingType(elementType) == null)
                    {
                        throw new JsonSerializationException(
                            $"Cannot convert null to value type '{elementType.Name}'");
                    }

                    continue;
                }

                string itemJson = LiteJsonSerializer.Instance.Serialize(item, typeof(object));
                target[i] = DeserializeValue(itemJson, elementType, depth + 1);
            }

            return array;
        }

        private static bool IsBuiltInShape(Type type)
        {
            if (type == typeof(object)
                || type == typeof(Dictionary<string, object>)
                || type == typeof(List<object>))
            {
                return true;
            }

            Type underlying = Nullable.GetUnderlyingType(type);
            return IsScalar(underlying ?? type);
        }

        private static bool IsScalar(Type type)
        {
            return type.IsPrimitive
                || type.IsEnum
                || type == typeof(string)
                || type == typeof(decimal)
                || type == typeof(DateTime)
                || type == typeof(DateTimeOffset)
                || type == typeof(Guid);
        }

        private static bool IsAnonymousType(Type type)
        {
            return type.Name.StartsWith("<>", StringComparison.Ordinal)
                && type.Name.Contains("AnonymousType");
        }

        /// <summary>
        /// <see cref="JsonUtility"/> writes a type without serializable fields as <c>{}</c> and reads
        /// it back as defaults, without an error. That is almost always a DTO with auto-properties,
        /// so such a type is rejected instead of losing data silently.
        /// </summary>
        private static void EnsureHasSerializableFields(Type type)
        {
            if (FieldCheckCache.GetOrAdd(type, HasSerializableFieldsFactory))
                return;

            throw new JsonSerializationException(
                $"Type '{type.Name}' has no field JsonUtility can serialize (properties are not serialized). "
                + "Use public fields (or [SerializeField] private fields), a Dictionary<string, object>, or "
                + "register a serializer with JsonSerializer.SetDefault.");
        }

        private static readonly ConcurrentDictionary<Type, bool> FieldCheckCache =
            new ConcurrentDictionary<Type, bool>();

        private static readonly Func<Type, bool> HasSerializableFieldsFactory = HasSerializableFields;

        private static bool HasSerializableFields(Type type)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly;
            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(Flags))
                {
                    if (field.IsInitOnly || field.IsNotSerialized)
                        continue;
                    if (field.IsPublic
                        || field.IsDefined(typeof(SerializeField), false)
                        || field.IsDefined(typeof(SerializeReference), false))
                        return true;
                }
            }

            return false;
        }

        private static bool IsJsonNull(string json)
        {
            int start = 0;
            int end = json.Length - 1;
            while (start <= end && char.IsWhiteSpace(json[start]))
                start++;
            while (end >= start && char.IsWhiteSpace(json[end]))
                end--;
            return end - start == 3 && string.CompareOrdinal(json, start, "null", 0, 4) == 0;
        }
    }
}
