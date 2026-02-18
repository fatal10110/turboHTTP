using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace TurboHTTP.Testing
{
    public sealed partial class RecordReplayTransport
    {
        private static Dictionary<string, object> ToSerializableDictionary(RecordingFileDto file)
        {
            var entries = new List<object>(file.Entries.Count);
            for (int i = 0; i < file.Entries.Count; i++)
            {
                entries.Add(ToSerializableDictionary(file.Entries[i]));
            }

            return new Dictionary<string, object>
            {
                ["Version"] = file.Version,
                ["CreatedUtcTicks"] = file.CreatedUtcTicks,
                ["UpdatedUtcTicks"] = file.UpdatedUtcTicks,
                ["Entries"] = entries
            };
        }

        private static Dictionary<string, object> ToSerializableDictionary(RecordingEntryDto entry)
        {
            return new Dictionary<string, object>
            {
                ["Sequence"] = entry.Sequence,
                ["RequestKey"] = entry.RequestKey,
                ["Method"] = entry.Method,
                ["Url"] = entry.Url,
                ["RequestHeaders"] = ToSerializableDictionary(entry.RequestHeaders),
                ["RequestBodyHash"] = entry.RequestBodyHash,
                ["RequestBodyBase64"] = entry.RequestBodyBase64,
                ["StatusCode"] = entry.StatusCode,
                ["ResponseHeaders"] = ToSerializableDictionary(entry.ResponseHeaders),
                ["ResponseBodyBase64"] = entry.ResponseBodyBase64,
                ["Error"] = ToSerializableDictionary(entry.Error),
                ["ThrowsException"] = entry.ThrowsException,
                ["TimestampUtcTicks"] = entry.TimestampUtcTicks
            };
        }

        private static Dictionary<string, object> ToSerializableDictionary(Dictionary<string, List<string>> headers)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (headers == null)
                return result;

            foreach (var pair in headers)
            {
                var values = new List<object>();
                if (pair.Value != null)
                {
                    for (int i = 0; i < pair.Value.Count; i++)
                    {
                        values.Add(pair.Value[i] ?? string.Empty);
                    }
                }
                result[pair.Key] = values;
            }

            return result;
        }

        private static Dictionary<string, object> ToSerializableDictionary(RecordingErrorDto error)
        {
            if (error == null)
                return null;

            return new Dictionary<string, object>
            {
                ["Type"] = error.Type,
                ["Message"] = error.Message,
                ["StatusCode"] = error.StatusCode.HasValue ? (object)error.StatusCode.Value : null
            };
        }

        private static RecordingFileDto FromSerializableObject(object value)
        {
            if (value is not Dictionary<string, object> dict)
            {
                throw new InvalidOperationException(
                    "Recording payload must deserialize into Dictionary<string, object>.");
            }

            var file = new RecordingFileDto
            {
                Version = ReadInt(dict, "Version"),
                CreatedUtcTicks = ReadLong(dict, "CreatedUtcTicks"),
                UpdatedUtcTicks = ReadLong(dict, "UpdatedUtcTicks"),
                Entries = new List<RecordingEntryDto>()
            };

            var entriesObject = ReadValue(dict, "Entries");
            if (entriesObject is List<object> entries)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    file.Entries.Add(FromSerializableEntry(entries[i]));
                }
            }

            return file;
        }

        private static RecordingEntryDto FromSerializableEntry(object value)
        {
            if (value is not Dictionary<string, object> dict)
            {
                throw new InvalidOperationException(
                    "Recording entry payload must be Dictionary<string, object>.");
            }

            return new RecordingEntryDto
            {
                Sequence = ReadLong(dict, "Sequence"),
                RequestKey = ReadString(dict, "RequestKey"),
                Method = ReadString(dict, "Method"),
                Url = ReadString(dict, "Url"),
                RequestHeaders = ParseHeaders(ReadValue(dict, "RequestHeaders")),
                RequestBodyHash = ReadString(dict, "RequestBodyHash"),
                RequestBodyBase64 = ReadString(dict, "RequestBodyBase64"),
                StatusCode = ReadInt(dict, "StatusCode"),
                ResponseHeaders = ParseHeaders(ReadValue(dict, "ResponseHeaders")),
                ResponseBodyBase64 = ReadString(dict, "ResponseBodyBase64"),
                Error = FromSerializableError(ReadValue(dict, "Error")),
                ThrowsException = ReadBool(dict, "ThrowsException"),
                TimestampUtcTicks = ReadLong(dict, "TimestampUtcTicks")
            };
        }

        private static RecordingErrorDto FromSerializableError(object value)
        {
            if (value is not Dictionary<string, object> dict)
                return null;

            return new RecordingErrorDto
            {
                Type = ReadString(dict, "Type"),
                Message = ReadString(dict, "Message"),
                StatusCode = TryReadNullableInt(dict, "StatusCode")
            };
        }

        private static Dictionary<string, List<string>> ParseHeaders(object value)
        {
            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (value is not Dictionary<string, object> dict)
                return headers;

            foreach (var pair in dict)
            {
                if (pair.Value is List<object> objectList)
                {
                    var values = new List<string>(objectList.Count);
                    for (int i = 0; i < objectList.Count; i++)
                    {
                        values.Add(objectList[i]?.ToString() ?? string.Empty);
                    }
                    headers[pair.Key] = values;
                    continue;
                }

                if (pair.Value is IEnumerable enumerable && pair.Value is not string)
                {
                    var values = new List<string>();
                    foreach (var item in enumerable)
                    {
                        values.Add(item?.ToString() ?? string.Empty);
                    }
                    headers[pair.Key] = values;
                    continue;
                }

                headers[pair.Key] = new List<string> { pair.Value?.ToString() ?? string.Empty };
            }

            return headers;
        }

        private static object ReadValue(Dictionary<string, object> dict, string key)
        {
            return dict.TryGetValue(key, out var value) ? value : null;
        }

        private static string ReadString(Dictionary<string, object> dict, string key)
        {
            return ReadValue(dict, key)?.ToString();
        }

        private static bool ReadBool(Dictionary<string, object> dict, string key)
        {
            var value = ReadValue(dict, key);
            if (value == null)
                return false;
            if (value is bool b)
                return b;
            if (bool.TryParse(value.ToString(), out var parsed))
                return parsed;
            return false;
        }

        private static int ReadInt(Dictionary<string, object> dict, string key)
        {
            var value = ReadValue(dict, key);
            if (value == null)
                return 0;
            if (value is int i)
                return i;
            if (value is long l)
                return (int)l;
            if (value is ulong ul)
                return (int)ul;
            if (value is double d)
                return (int)d;
            if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return 0;
        }

        private static int? TryReadNullableInt(Dictionary<string, object> dict, string key)
        {
            var value = ReadValue(dict, key);
            if (value == null)
                return null;
            return ReadInt(dict, key);
        }

        private static long ReadLong(Dictionary<string, object> dict, string key)
        {
            var value = ReadValue(dict, key);
            if (value == null)
                return 0L;
            if (value is long l)
                return l;
            if (value is int i)
                return i;
            if (value is ulong ul)
                return (long)ul;
            if (value is double d)
                return (long)d;
            if (long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return 0L;
        }

        private static string SerializeJson(object value, Type type)
        {
            var serializerType = Type.GetType("TurboHTTP.JSON.JsonSerializer, TurboHTTP.JSON", throwOnError: false);
            if (serializerType == null)
            {
                throw new InvalidOperationException(
                    "TurboHTTP.JSON assembly is required for RecordReplayTransport serialization.");
            }

            var serializeMethod = serializerType.GetMethod(
                "Serialize",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(object), typeof(Type) },
                modifiers: null);
            if (serializeMethod == null)
            {
                throw new InvalidOperationException(
                    "TurboHTTP.JSON.JsonSerializer.Serialize(object, Type) was not found.");
            }

            try
            {
                return (string)serializeMethod.Invoke(null, new[] { value, type });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw new InvalidOperationException("RecordReplay serialization failed.", tie.InnerException);
            }
        }

        private static object DeserializeJson(string json, Type type)
        {
            var serializerType = Type.GetType("TurboHTTP.JSON.JsonSerializer, TurboHTTP.JSON", throwOnError: false);
            if (serializerType == null)
            {
                throw new InvalidOperationException(
                    "TurboHTTP.JSON assembly is required for RecordReplayTransport deserialization.");
            }

            var deserializeMethod = serializerType.GetMethod(
                "Deserialize",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string), typeof(Type) },
                modifiers: null);
            if (deserializeMethod == null)
            {
                throw new InvalidOperationException(
                    "TurboHTTP.JSON.JsonSerializer.Deserialize(string, Type) was not found.");
            }

            try
            {
                return deserializeMethod.Invoke(null, new object[] { json, type });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw new InvalidOperationException("RecordReplay deserialization failed.", tie.InnerException);
            }
        }
    }
}
