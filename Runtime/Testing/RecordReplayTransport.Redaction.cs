using System;
using System.Buffers;
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

        private enum RedactableBodyKind
        {
            None,
            Json,
            FormUrlEncoded
        }

        private byte[] RedactBodyIfNeeded(ReadOnlySequence<byte> body, HttpHeaders headers)
        {
            if (body.IsEmpty)
                return null;

            return RedactBodyIfNeeded(new ReadOnlyMemory<byte>(ToExactByteArray(body)), headers);
        }

        // Fails closed: a JSON or form body that cannot be parsed, or whose redacted form cannot be
        // serialized, is replaced by the placeholder instead of being stored raw (#93).
        private byte[] RedactBodyIfNeeded(ReadOnlyMemory<byte> body, HttpHeaders headers)
        {
            if (body.IsEmpty)
                return null;

            switch (ClassifyBody(headers?.Get("Content-Type")))
            {
                case RedactableBodyKind.Json:
                    return RedactJsonBody(body);
                case RedactableBodyKind.FormUrlEncoded:
                    return RedactFormBody(body);
                default:
                    return ToExactByteArray(body);
            }
        }

        // RFC 6839 section 3.1: any media type with the "+json" structured syntax suffix is JSON.
        private static RedactableBodyKind ClassifyBody(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
                return RedactableBodyKind.None;

            var separator = contentType.IndexOf(';');
            var mediaType = (separator >= 0 ? contentType.Substring(0, separator) : contentType).Trim();

            if (mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                return RedactableBodyKind.FormUrlEncoded;

            if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase) ||
                mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            {
                return RedactableBodyKind.Json;
            }

            return RedactableBodyKind.None;
        }

        private byte[] RedactJsonBody(ReadOnlyMemory<byte> body)
        {
            if (_redactionPolicy.JsonBodyFieldNames.Count == 0)
                return ToExactByteArray(body);

            object parsedBody;
            try
            {
                parsedBody = DeserializeJson(Utf8.GetString(body.Span), typeof(object));
            }
            catch (Exception ex)
            {
                return RedactUnparseableBody("JSON", ex);
            }

            if (parsedBody == null || !TryRedactParsedJson(parsedBody))
                return ToExactByteArray(body);

            try
            {
                return Utf8.GetBytes(SerializeJson(parsedBody, typeof(object)));
            }
            catch (Exception ex)
            {
                return RedactUnparseableBody("JSON", ex);
            }
        }

        // application/x-www-form-urlencoded: names are percent- and '+'-decoded before matching; the
        // value of a sensitive field is replaced and every other byte is kept as recorded.
        private byte[] RedactFormBody(ReadOnlyMemory<byte> body)
        {
            string text;
            try
            {
                text = StrictUtf8.GetString(body.Span);
            }
            catch (Exception ex)
            {
                return RedactUnparseableBody("form", ex);
            }

            var pairs = text.Split('&');
            var changed = false;
            for (int i = 0; i < pairs.Length; i++)
            {
                var pair = pairs[i];
                var equals = pair.IndexOf('=');
                var rawName = equals >= 0 ? pair.Substring(0, equals) : pair;
                string name;
                try
                {
                    name = Uri.UnescapeDataString(rawName.Replace('+', ' '));
                }
                catch (Exception ex)
                {
                    return RedactUnparseableBody("form", ex);
                }

                if (equals < 0 || !_redactionPolicy.ShouldRedactFormField(name))
                    continue;

                pairs[i] = rawName + "=" + Uri.EscapeDataString(_redactionPolicy.RedactedValue);
                changed = true;
            }

            return changed
                ? Utf8.GetBytes(string.Join("&", pairs))
                : ToExactByteArray(body);
        }

        private byte[] RedactUnparseableBody(string kind, Exception ex)
        {
            Log($"RecordReplay redaction: {kind} body could not be parsed ({ex.GetType().Name}); " +
                "it is recorded as the redaction placeholder.");
            return Utf8.GetBytes(_redactionPolicy.RedactedValue);
        }

        private static byte[] ToExactByteArray(ReadOnlyMemory<byte> body)
        {
            if (body.IsEmpty)
                return null;

            if (MemoryMarshal.TryGetArray(body, out var segment) && segment.Array != null)
            {
                if (segment.Offset == 0 && segment.Count == segment.Array.Length)
                    return segment.Array;

                var copy = new byte[segment.Count];
                Buffer.BlockCopy(segment.Array, segment.Offset, copy, 0, segment.Count);
                return copy;
            }

            return body.ToArray();
        }

        private static byte[] ToExactByteArray(ReadOnlySequence<byte> body)
        {
            if (body.IsEmpty)
                return null;

            if (body.IsSingleSegment &&
                MemoryMarshal.TryGetArray(body.First, out var segment) &&
                segment.Array != null)
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
