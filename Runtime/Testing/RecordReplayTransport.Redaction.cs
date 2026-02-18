using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using TurboHTTP.Core;

namespace TurboHTTP.Testing
{
    public sealed partial class RecordReplayTransport
    {
        private Dictionary<string, List<string>> ToDictionary(HttpHeaders headers, bool redact)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (headers == null)
                return result;

            foreach (var name in headers.Names)
            {
                var values = headers.GetValues(name);
                var copiedValues = new List<string>(values.Count);
                for (int i = 0; i < values.Count; i++)
                {
                    var value = values[i] ?? string.Empty;
                    if (redact && _redactionPolicy.ShouldRedactHeader(name))
                    {
                        copiedValues.Add(_redactionPolicy.RedactedValue);
                    }
                    else
                    {
                        copiedValues.Add(value);
                    }
                }
                result[name] = copiedValues;
            }

            return result;
        }

        private static HttpHeaders FromDictionary(Dictionary<string, List<string>> headers)
        {
            var result = new HttpHeaders();
            if (headers == null)
                return result;

            foreach (var pair in headers)
            {
                if (pair.Value == null || pair.Value.Count == 0)
                {
                    result.Set(pair.Key, string.Empty);
                    continue;
                }

                result.Set(pair.Key, pair.Value[0] ?? string.Empty);
                for (int i = 1; i < pair.Value.Count; i++)
                {
                    result.Add(pair.Key, pair.Value[i] ?? string.Empty);
                }
            }

            return result;
        }

        private byte[] RedactJsonBodyIfNeeded(ReadOnlyMemory<byte> body, HttpHeaders headers)
        {
            if (body.IsEmpty)
                return null;
            if (_redactionPolicy.JsonBodyFieldNames.Count == 0)
                return ToExactByteArray(body);

            var contentType = headers?.Get("Content-Type");
            if (string.IsNullOrEmpty(contentType) ||
                contentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return ToExactByteArray(body);
            }

            object parsedBody;
            try
            {
                var json = Utf8.GetString(body.Span);
                parsedBody = DeserializeJson(json, typeof(object));
            }
            catch
            {
                return ToExactByteArray(body);
            }

            if (parsedBody == null)
                return ToExactByteArray(body);

            if (!TryRedactParsedJson(parsedBody))
                return ToExactByteArray(body);

            string redactedJson;
            try
            {
                redactedJson = SerializeJson(parsedBody, typeof(object));
            }
            catch
            {
                return ToExactByteArray(body);
            }

            return Utf8.GetBytes(redactedJson);
        }

        private byte[] RedactJsonBodyIfNeeded(byte[] body, HttpHeaders headers)
        {
            if (body == null || body.Length == 0)
                return body;
            if (_redactionPolicy.JsonBodyFieldNames.Count == 0)
                return body;

            var contentType = headers?.Get("Content-Type");
            if (string.IsNullOrEmpty(contentType) ||
                contentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return body;
            }

            object parsedBody;
            try
            {
                var json = Utf8.GetString(body);
                parsedBody = DeserializeJson(json, typeof(object));
            }
            catch
            {
                return body;
            }

            if (parsedBody == null)
                return body;

            if (!TryRedactParsedJson(parsedBody))
                return body;

            string redactedJson;
            try
            {
                redactedJson = SerializeJson(parsedBody, typeof(object));
            }
            catch
            {
                return body;
            }

            return Utf8.GetBytes(redactedJson);
        }

        private static byte[] ToExactByteArray(ReadOnlyMemory<byte> body)
        {
            if (body.IsEmpty)
                return null;

            if (MemoryMarshal.TryGetArray(body, out var segment))
            {
                if (segment.Offset == 0 && segment.Count == segment.Array.Length)
                    return segment.Array;

                var copy = new byte[segment.Count];
                Buffer.BlockCopy(segment.Array, segment.Offset, copy, 0, segment.Count);
                return copy;
            }

            return body.ToArray();
        }

        private bool TryRedactParsedJson(object node)
        {
            if (node is Dictionary<string, object> objectDict)
            {
                var changed = false;
                var keys = objectDict.Keys.ToArray();
                for (int i = 0; i < keys.Length; i++)
                {
                    var key = keys[i];
                    if (_redactionPolicy.ShouldRedactJsonField(key))
                    {
                        objectDict[key] = _redactionPolicy.RedactedValue;
                        changed = true;
                        continue;
                    }

                    var value = objectDict[key];
                    if (value != null && TryRedactParsedJson(value))
                        changed = true;
                }
                return changed;
            }

            if (node is List<object> objectList)
            {
                var changed = false;
                for (int i = 0; i < objectList.Count; i++)
                {
                    var value = objectList[i];
                    if (value != null && TryRedactParsedJson(value))
                        changed = true;
                }
                return changed;
            }

            return false;
        }
    }
}
