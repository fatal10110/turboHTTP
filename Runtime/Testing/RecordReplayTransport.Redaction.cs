using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

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
                    else if (redact)
                    {
                        // Location, Content-Location, Referer, Link and Refresh carry URLs: user-info and the
                        // sensitive query values are redacted like the match key's query (#1067), so a
                        // replayed redirect still matches the recorded next hop.
                        copiedValues.Add(UrlRedaction.RedactUrlHeaderValue(
                            name,
                            value,
                            _redactionPolicy.ShouldRedactQueryParameter,
                            _redactionPolicy.RedactedValue));
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

        // RFC 9110 section 8.4: Content-Encoding describes the stored representation, and section 8.6:
        // Content-Length is the stored body's length. Redaction parses the body, so a JSON or form body sent
        // with a content coding is decoded first (gzip, x-gzip, deflate; bounded by MaxRecordBytes) and
        // stored as identity, without Content-Encoding. A coding it cannot decode fails the recording rather
        // than storing a placeholder under the old coding (#1078). Content-Length is rewritten whenever the
        // stored body differs from the received one.
        private bool TryPrepareResponseBody(
            ReadOnlySequence<byte> body,
            HttpHeaders headers,
            Dictionary<string, List<string>> storedHeaders,
            out byte[] storedBody,
            out string failure)
        {
            failure = null;
            var received = ToExactByteArray(body);
            var bodyForRedaction = received;

            var codings = GetContentCodings(headers);
            if (codings != null && BodyNeedsRedaction(ClassifyBody(headers.Get("Content-Type"))))
            {
                for (int i = codings.Count - 1; i >= 0; i--)
                {
                    if (!TryDecodeContentCoding(bodyForRedaction, codings[i], _maxRecordBytes, out var decoded, out failure))
                    {
                        storedBody = null;
                        return false;
                    }

                    bodyForRedaction = decoded;
                }

                storedHeaders.Remove("Content-Encoding");
            }

            storedBody = RedactBodyIfNeeded(new ReadOnlyMemory<byte>(bodyForRedaction), headers);
            if (!ReferenceEquals(storedBody, received) && storedHeaders.ContainsKey("Content-Length"))
            {
                storedHeaders["Content-Length"] = new List<string>
                {
                    (storedBody?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
            }

            return true;
        }

        private bool BodyNeedsRedaction(RedactableBodyKind kind)
        {
            switch (kind)
            {
                case RedactableBodyKind.Json:
                    return _redactionPolicy.JsonBodyFieldNames.Count > 0;
                case RedactableBodyKind.FormUrlEncoded:
                    return _redactionPolicy.JsonBodyFieldNames.Count > 0 ||
                           _redactionPolicy.QueryParameterNames.Count > 0;
                default:
                    return false;
            }
        }

        // The codings in the order they were applied, without "identity"; null when there are none.
        private static List<string> GetContentCodings(HttpHeaders headers)
        {
            var values = headers?.GetValues("Content-Encoding");
            if (values == null)
                return null;

            List<string> codings = null;
            for (int i = 0; i < values.Count; i++)
            {
                var parts = (values[i] ?? string.Empty).Split(',');
                for (int j = 0; j < parts.Length; j++)
                {
                    var coding = parts[j].Trim();
                    if (coding.Length == 0 || coding.Equals("identity", StringComparison.OrdinalIgnoreCase))
                        continue;
                    codings ??= new List<string>();
                    codings.Add(coding);
                }
            }

            return codings;
        }

        private static bool TryDecodeContentCoding(
            byte[] input,
            string coding,
            long maxBytes,
            out byte[] decoded,
            out string failure)
        {
            decoded = null;
            failure = null;
            var isGzip = coding.Equals("gzip", StringComparison.OrdinalIgnoreCase) ||
                         coding.Equals("x-gzip", StringComparison.OrdinalIgnoreCase);
            if (!isGzip && !coding.Equals("deflate", StringComparison.OrdinalIgnoreCase))
            {
                failure = "Content-Encoding '" + coding + "' cannot be decoded for redaction (gzip, x-gzip and " +
                          "deflate can). Recordings never store an unredacted body or a placeholder under the " +
                          "original coding: remove the coding from the request's Accept-Encoding while recording.";
                return false;
            }

            try
            {
                using var source = new MemoryStream(input ?? Array.Empty<byte>(), writable: false);
                // RFC 9110 section 8.4.1.2: "deflate" is the zlib format; raw DEFLATE is accepted as well.
                if (!isGzip && HasZlibHeader(input))
                    source.Position = 2;

                using Stream inflater = isGzip
                    ? new GZipStream(source, CompressionMode.Decompress)
                    : new DeflateStream(source, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[DrainChunkBytes];
                int read;
                while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > maxBytes)
                    {
                        failure = "the decoded " + coding + " body exceeds RecordReplayTransportOptions.MaxRecordBytes (" +
                                  maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                  " bytes). Recordings are never truncated: raise MaxRecordBytes.";
                        return false;
                    }

                    output.Write(buffer, 0, read);
                }

                decoded = output.ToArray();
                return true;
            }
            catch (Exception ex)
            {
                failure = "the " + coding + " body could not be decoded for redaction (" + ex.GetType().Name + ").";
                return false;
            }
        }

        // RFC 1950: CM 8 (deflate), CINFO <= 7, FCHECK makes CMF*256+FLG a multiple of 31.
        private static bool HasZlibHeader(byte[] input)
        {
            if (input == null || input.Length < 2)
                return false;
            int cmf = input[0];
            int flg = input[1];
            return (cmf & 0x0F) == 8 && (cmf >> 4) <= 7 && ((cmf << 8) | flg) % 31 == 0;
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
                // The body is already in memory and bounded by MaxRecordBytes, so its size is the cap.
                parsedBody = DeserializeJson(Utf8.GetString(body.Span), body.Length);
            }
            catch (Exception ex)
            {
                return RedactUnparseableBody("JSON", ex.GetType().Name);
            }

            var changed = false;
            if (!TryRedactJsonNode(parsedBody, ref changed))
                return RedactUnparseableBody("JSON", "unsupported JSON node type");

            if (!changed)
                return ToExactByteArray(body);

            try
            {
                return Utf8.GetBytes(SerializeJson(parsedBody));
            }
            catch (Exception ex)
            {
                return RedactUnparseableBody("JSON", ex.GetType().Name);
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
                return RedactUnparseableBody("form", ex.GetType().Name);
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
                    return RedactUnparseableBody("form", ex.GetType().Name);
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

        private byte[] RedactUnparseableBody(string kind, string reason)
        {
            Log($"RecordReplay redaction: {kind} body could not be parsed ({reason}); " +
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

        // Walks the tree the JSON bridge's document model returned (LiteJson whatever JsonSerializer.Default
        // is, #1034). Returns false for any node type it does not know: the caller then records the
        // placeholder instead of the raw body, so redaction fails closed if a bridge ever returns another tree.
        private bool TryRedactJsonNode(object node, ref bool changed)
        {
            switch (node)
            {
                case null:
                case string _:
                case bool _:
                    return true;

                case Dictionary<string, object> objectDict:
                    {
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

                            if (!TryRedactJsonNode(objectDict[key], ref changed))
                                return false;
                        }

                        return true;
                    }

                case List<object> objectList:
                    {
                        for (int i = 0; i < objectList.Count; i++)
                        {
                            if (!TryRedactJsonNode(objectList[i], ref changed))
                                return false;
                        }

                        return true;
                    }

                default:
                    return IsJsonNumber(node);
            }
        }

        private static bool IsJsonNumber(object node)
        {
            return node is double || node is long || node is int || node is float || node is decimal ||
                   node is ulong || node is uint || node is short || node is ushort || node is byte ||
                   node is sbyte;
        }
    }
}
