using System;

namespace TurboHTTP.Transport.Internal
{
    /// <summary>
    /// Field-syntax primitives for HTTP/1.1 fields received from the peer and for request
    /// field-list tokens, shared so every caller trims and validates the same way.
    /// <list type="bullet">
    /// <item>Optional whitespace is SP and HTAB only (RFC 9110 Section 5.6.3, RFC 9112 Section 5).
    /// Never <see cref="char.IsWhiteSpace(char)"/>: on Latin-1 decoded text it also matches
    /// obs-text 0x85/0xA0, which belongs to a field value (RFC 9110 Section 5.5), and VT/FF/CR,
    /// which are not whitespace on the wire.</item>
    /// <item>A field value may hold VCHAR, obs-text, SP and HTAB. NUL, CR, LF, the other C0
    /// controls and DEL are invalid (RFC 9110 Section 5.5, RFC 9112 Section 2.2).</item>
    /// </list>
    /// </summary>
    internal static class HttpFieldSyntax
    {
        /// <summary>True for SP or HTAB, the only optional-whitespace characters.</summary>
        internal static bool IsOws(char c) => c == ' ' || c == '\t';

        /// <summary>Removes leading and trailing SP/HTAB, nothing else.</summary>
        internal static ReadOnlySpan<char> TrimOws(ReadOnlySpan<char> value)
        {
            int start = 0;
            while (start < value.Length && IsOws(value[start]))
                start++;

            int end = value.Length - 1;
            while (end >= start && IsOws(value[end]))
                end--;

            return end >= start
                ? value.Slice(start, end - start + 1)
                : ReadOnlySpan<char>.Empty;
        }

        /// <summary>
        /// Returns the index of the first character a field value may not contain (a control
        /// character other than HTAB, DEL, or anything above U+00FF), or -1 when every character
        /// is allowed.
        /// </summary>
        internal static int IndexOfInvalidFieldValueChar(ReadOnlySpan<char> value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if ((c < 0x20 && c != '\t') || c == 0x7F || c > 0xFF)
                    return i;
            }

            return -1;
        }
    }
}
