using System;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Internal
{
    /// <summary>
    /// The one field-name/field-value check for outgoing request header and trailer fields, shared by
    /// the HTTP/1.1 serializer and the HTTP/2 HPACK path so both protocols accept exactly the same
    /// fields.
    /// <list type="bullet">
    /// <item>Name: a non-empty RFC 9110 Section 5.1 token. This rejects CR, LF, NUL, SP, ':' and
    /// therefore every caller-supplied pseudo-header name (RFC 9113 Section 8.3: the endpoint generates
    /// those). Uppercase letters are tokens: HTTP/2 lowercases the validated name before encoding.</item>
    /// <item>Value: RFC 9110 Section 5.5 <c>field-value</c>, i.e. VCHAR / obs-text / SP / HTAB only,
    /// with no leading or trailing SP/HTAB. NUL, CR, LF, the other C0 controls and DEL are rejected
    /// (RFC 9110 Section 5.5, RFC 9113 Section 8.2.1). Characters above U+00FF are rejected because
    /// field octets are Latin-1 on both wires.</item>
    /// </list>
    /// Exception messages carry the (validated) field name and a character code, never the value.
    /// </summary>
    internal static class HttpFieldValidator
    {
        /// <summary>
        /// Throws <see cref="ArgumentException"/> when <paramref name="name"/> or
        /// <paramref name="value"/> may not be sent as a request header or trailer field.
        /// A null value is treated as empty (an empty field value is valid).
        /// </summary>
        internal static void ValidateField(string name, string value)
        {
            ValidateName(name);
            ValidateValue(name, value);
        }

        /// <summary>
        /// Validates every field of <paramref name="headers"/>. HTTP/2 calls this before a stream slot
        /// or stream id is taken, so an invalid request consumes nothing and writes no frame.
        /// </summary>
        internal static void ValidateFields(HttpHeaders headers)
        {
            if (headers == null)
                return;

            foreach (var name in headers.Names)
            {
                ValidateName(name);
                var values = headers.GetValues(name);
                for (int i = 0; i < values.Count; i++)
                    ValidateValue(name, values[i]);
            }
        }

        private static void ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Header name cannot be null or empty");

            if (name[0] == ':')
            {
                throw new ArgumentException(
                    "Header names starting with ':' are reserved for pseudo-header fields, " +
                    "which the transport generates (RFC 9113 Section 8.3)");
            }

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!EncodingHelper.IsRfc9110TChar(c))
                {
                    throw new ArgumentException(
                        $"Header name contains invalid characters (U+{(int)c:X4} at index {i}); " +
                        "field names must be RFC 9110 tokens");
                }
            }
        }

        private static void ValidateValue(string name, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\r' || c == '\n')
                    throw new ArgumentException($"Header value for '{name}' contains CRLF characters");

                if ((c < 0x20 && c != '\t') || c == 0x7F)
                {
                    throw new ArgumentException(
                        $"Header value for '{name}' contains control character 0x{(int)c:X2} at index {i}");
                }

                if (c > 0xFF)
                {
                    throw new ArgumentException(
                        $"Header value for '{name}' contains non-Latin-1 character U+{(int)c:X4} at index {i}. " +
                        "Encode the value (e.g., percent-encode) before setting the header.");
                }
            }

            if (IsWhitespace(value[0]) || IsWhitespace(value[value.Length - 1]))
            {
                throw new ArgumentException(
                    $"Header value for '{name}' has leading or trailing whitespace, " +
                    "which is not part of a field value (RFC 9110 Section 5.5)");
            }
        }

        private static bool IsWhitespace(char c) => c == ' ' || c == '\t';
    }
}
