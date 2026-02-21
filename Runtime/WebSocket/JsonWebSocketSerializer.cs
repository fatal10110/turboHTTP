using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using TurboHTTP.Core;

namespace TurboHTTP.WebSocket
{
    public sealed class JsonWebSocketSerializer<T> : IWebSocketMessageSerializer<T>
        where T : class
    {
        private const int MaxConversionDepth = 64;

        public WebSocketOpcode MessageType => WebSocketOpcode.Text;

        public ReadOnlyMemory<byte> Serialize(T message)
        {
            try
            {
                string json;
                try
                {
                    json = ProjectJsonBridge.Serialize(
                        message,
                        typeof(T),
                        requiredBy: "WebSocket JSON serialization");
                }
                catch
                {
                    object normalized = NormalizeForJson(message, depth: 0);
                    json = ProjectJsonBridge.Serialize(
                        normalized,
                        normalized?.GetType() ?? typeof(object),
                        requiredBy: "WebSocket JSON serialization");
                }

                return WebSocketConstants.StrictUtf8.GetBytes(json ?? string.Empty);
            }
            catch (Exception ex)
            {
                throw new WebSocketException(
                    WebSocketError.SerializationFailed,
                    "Failed to serialize WebSocket payload to JSON.",
                    ex);
            }
        }

        public T Deserialize(WebSocketMessage raw)
        {
            if (raw == null)
                throw new ArgumentNullException(nameof(raw));

            try
            {
                string json = ExtractJson(raw);
                return DeserializeCore(json);
            }
            catch (Exception ex)
            {
                throw new WebSocketException(
                    WebSocketError.SerializationFailed,
                    "Failed to deserialize WebSocket JSON payload.",
                    ex);
            }
        }

        private static string ExtractJson(WebSocketMessage raw)
        {
            if (raw.IsText && raw.Text != null)
                return raw.Text;

            if (raw.Length == 0)
                return string.Empty;

            return WebSocketConstants.StrictUtf8.GetString(raw.Data.Span);
        }

        private static T DeserializeCore(string json)
        {
            try
            {
                object direct = ProjectJsonBridge.Deserialize(
                    json,
                    typeof(T),
                    requiredBy: "WebSocket JSON deserialization");

                if (direct is T typed)
                    return typed;

                if (direct == null)
                    return null;

                return (T)ConvertToTargetType(direct, typeof(T), depth: 0);
            }
            catch
            {
                object parsed = ProjectJsonBridge.Deserialize(
                    json,
                    typeof(object),
                    requiredBy: "WebSocket JSON deserialization");

                return (T)ConvertToTargetType(parsed, typeof(T), depth: 0);
            }
        }

        private static object NormalizeForJson(object value, int depth)
        {
            if (depth > MaxConversionDepth)
                throw new InvalidOperationException("Maximum JSON conversion depth exceeded.");

            if (value == null)
                return null;

            Type valueType = value.GetType();
            if (IsDirectlyJsonSerializable(valueType))
                return value;

            if (value is IDictionary dictionary)
            {
                var mapped = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    string key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                    mapped[key] = NormalizeForJson(entry.Value, depth + 1);
                }

                return mapped;
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                var list = new List<object>();
                foreach (object item in enumerable)
                {
                    list.Add(NormalizeForJson(item, depth + 1));
                }

                return list;
            }

            var objectMap = new Dictionary<string, object>(StringComparer.Ordinal);

            PropertyInfo[] properties = valueType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                    continue;

                objectMap[property.Name] = NormalizeForJson(property.GetValue(value, null), depth + 1);
            }

            FieldInfo[] fields = valueType.GetFields(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < fields.Length; i++)
            {
                objectMap[fields[i].Name] = NormalizeForJson(fields[i].GetValue(value), depth + 1);
            }

            return objectMap;
        }

        private static object ConvertToTargetType(object value, Type targetType, int depth)
        {
            if (depth > MaxConversionDepth)
                throw new InvalidOperationException("Maximum JSON conversion depth exceeded.");

            if (targetType == null)
                throw new ArgumentNullException(nameof(targetType));

            Type nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nonNullableType == typeof(object))
                return value;

            if (value == null)
            {
                if (!nonNullableType.IsValueType || Nullable.GetUnderlyingType(targetType) != null)
                    return null;

                return Activator.CreateInstance(nonNullableType);
            }

            if (nonNullableType.IsInstanceOfType(value))
                return value;

            if (nonNullableType.IsEnum)
            {
                if (value is string enumString)
                    return Enum.Parse(nonNullableType, enumString, ignoreCase: true);

                object enumNumeric = Convert.ChangeType(
                    value,
                    Enum.GetUnderlyingType(nonNullableType),
                    CultureInfo.InvariantCulture);
                return Enum.ToObject(nonNullableType, enumNumeric);
            }

            if (nonNullableType == typeof(Guid))
                return value is Guid guid ? guid : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture));

            if (nonNullableType == typeof(Uri))
                return value is Uri uri ? uri : new Uri(Convert.ToString(value, CultureInfo.InvariantCulture));

            if (nonNullableType == typeof(DateTime))
            {
                if (value is DateTime dateTime)
                    return dateTime;

                return DateTime.Parse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
            }

            if (nonNullableType == typeof(DateTimeOffset))
            {
                if (value is DateTimeOffset dateTimeOffset)
                    return dateTimeOffset;

                return DateTimeOffset.Parse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
            }

            if (nonNullableType == typeof(string))
                return Convert.ToString(value, CultureInfo.InvariantCulture);

            if (nonNullableType.IsPrimitive || nonNullableType == typeof(decimal))
            {
                return Convert.ChangeType(value, nonNullableType, CultureInfo.InvariantCulture);
            }

            if (nonNullableType.IsArray && value is List<object> arrayItems)
            {
                Type elementType = nonNullableType.GetElementType();
                var array = Array.CreateInstance(elementType, arrayItems.Count);
                for (int i = 0; i < arrayItems.Count; i++)
                {
                    object converted = ConvertToTargetType(arrayItems[i], elementType, depth + 1);
                    array.SetValue(converted, i);
                }

                return array;
            }

            if (TryGetListElementType(nonNullableType, out Type listElementType) &&
                value is List<object> listItems)
            {
                IList list = CreateList(nonNullableType, listElementType);
                for (int i = 0; i < listItems.Count; i++)
                {
                    list.Add(ConvertToTargetType(listItems[i], listElementType, depth + 1));
                }

                return list;
            }

            if (TryGetDictionaryValueType(nonNullableType, out Type dictionaryValueType) &&
                value is Dictionary<string, object> dictionaryItems)
            {
                IDictionary dictionary = CreateDictionary(nonNullableType, dictionaryValueType);
                foreach (KeyValuePair<string, object> pair in dictionaryItems)
                {
                    dictionary[pair.Key] = ConvertToTargetType(pair.Value, dictionaryValueType, depth + 1);
                }

                return dictionary;
            }

            if (value is Dictionary<string, object> objectMap)
                return MapToObject(objectMap, nonNullableType, depth + 1);

            throw new InvalidOperationException(
                $"Cannot map JSON value of type '{value.GetType().Name}' to '{nonNullableType.Name}'.");
        }

        private static object MapToObject(Dictionary<string, object> source, Type targetType, int depth)
        {
            object instance = Activator.CreateInstance(targetType, nonPublic: true);
            if (instance == null)
                throw new InvalidOperationException("Could not create instance of type " + targetType.FullName + ".");

            var lookup = new Dictionary<string, object>(source, StringComparer.OrdinalIgnoreCase);

            PropertyInfo[] properties = targetType.GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (!property.CanWrite || property.GetIndexParameters().Length != 0)
                    continue;

                if (!lookup.TryGetValue(property.Name, out object rawValue))
                    continue;

                object converted = ConvertToTargetType(rawValue, property.PropertyType, depth + 1);
                property.SetValue(instance, converted, null);
            }

            FieldInfo[] fields = targetType.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.IsInitOnly || field.IsStatic)
                    continue;

                if (!lookup.TryGetValue(field.Name, out object rawValue))
                    continue;

                object converted = ConvertToTargetType(rawValue, field.FieldType, depth + 1);
                field.SetValue(instance, converted);
            }

            return instance;
        }

        private static bool IsDirectlyJsonSerializable(Type type)
        {
            if (type == typeof(string))
                return true;
            if (type.IsPrimitive || type.IsEnum)
                return true;

            return type == typeof(decimal) ||
                   type == typeof(Guid) ||
                   type == typeof(DateTime) ||
                   type == typeof(DateTimeOffset);
        }

        private static bool TryGetListElementType(Type type, out Type elementType)
        {
            if (type.IsGenericType)
            {
                Type genericDefinition = type.GetGenericTypeDefinition();
                if (genericDefinition == typeof(List<>) ||
                    genericDefinition == typeof(IList<>) ||
                    genericDefinition == typeof(ICollection<>) ||
                    genericDefinition == typeof(IEnumerable<>) ||
                    genericDefinition == typeof(IReadOnlyList<>))
                {
                    elementType = type.GetGenericArguments()[0];
                    return true;
                }
            }

            elementType = null;
            return false;
        }

        private static bool TryGetDictionaryValueType(Type type, out Type valueType)
        {
            if (type.IsGenericType)
            {
                Type genericDefinition = type.GetGenericTypeDefinition();
                if ((genericDefinition == typeof(Dictionary<,>) ||
                     genericDefinition == typeof(IDictionary<,>) ||
                     genericDefinition == typeof(IReadOnlyDictionary<,>)) &&
                    type.GetGenericArguments()[0] == typeof(string))
                {
                    valueType = type.GetGenericArguments()[1];
                    return true;
                }
            }

            valueType = null;
            return false;
        }

        private static IList CreateList(Type targetType, Type elementType)
        {
            if (targetType.IsInterface || targetType.IsAbstract)
            {
                Type listType = typeof(List<>).MakeGenericType(elementType);
                return (IList)Activator.CreateInstance(listType);
            }

            if (Activator.CreateInstance(targetType, nonPublic: true) is IList existingList)
                return existingList;

            Type fallbackType = typeof(List<>).MakeGenericType(elementType);
            return (IList)Activator.CreateInstance(fallbackType);
        }

        private static IDictionary CreateDictionary(Type targetType, Type valueType)
        {
            if (targetType.IsInterface || targetType.IsAbstract)
            {
                Type dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);
                return (IDictionary)Activator.CreateInstance(dictionaryType);
            }

            if (Activator.CreateInstance(targetType, nonPublic: true) is IDictionary existingDictionary)
                return existingDictionary;

            Type fallbackType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);
            return (IDictionary)Activator.CreateInstance(fallbackType);
        }
    }
}
