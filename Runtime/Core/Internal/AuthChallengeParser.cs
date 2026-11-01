using System;
using System.Collections.Generic;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// Reads the auth-schemes offered by <c>WWW-Authenticate</c> / <c>Proxy-Authenticate</c>
    /// field values (RFC 9110 section 11.6.1 / 11.7.1).
    /// </summary>
    /// <remarks>
    /// A field value is a comma-separated list of challenges, and each challenge's auth-params are
    /// comma-separated too: <c>Digest realm="a", qop="auth", Basic realm="b"</c>. A list element
    /// starts a new challenge when its leading token is not followed by <c>=</c> (an auth-param
    /// name is). Quoted strings, which may contain commas, are skipped. Scheme names compare
    /// case-insensitively (RFC 9110 section 11.1). The parse is a single linear pass over values
    /// already bounded by the header reader.
    /// </remarks>
    internal static class AuthChallengeParser
    {
        /// <summary>
        /// Returns whether any of <paramref name="fieldValues"/> offers a challenge for
        /// <paramref name="scheme"/>.
        /// </summary>
        public static bool OffersScheme(IReadOnlyList<string> fieldValues, string scheme)
        {
            if (fieldValues == null || string.IsNullOrEmpty(scheme))
                return false;

            for (int i = 0; i < fieldValues.Count; i++)
            {
                if (FieldValueOffersScheme(fieldValues[i], scheme))
                    return true;
            }

            return false;
        }

        private static bool FieldValueOffersScheme(string fieldValue, string scheme)
        {
            if (string.IsNullOrEmpty(fieldValue) || string.IsNullOrEmpty(scheme))
                return false;

            int length = fieldValue.Length;
            int i = 0;
            while (i < length)
            {
                while (i < length && (fieldValue[i] == ',' || IsWhitespace(fieldValue[i])))
                    i++;

                int tokenStart = i;
                while (i < length && IsTokenChar(fieldValue[i]))
                    i++;
                int tokenLength = i - tokenStart;

                int next = i;
                while (next < length && IsWhitespace(fieldValue[next]))
                    next++;
                bool isAuthParam = next < length && fieldValue[next] == '=';

                if (tokenLength == scheme.Length &&
                    !isAuthParam &&
                    string.Compare(fieldValue, tokenStart, scheme, 0, tokenLength, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return true;
                }

                i = SkipToNextElement(fieldValue, i);
            }

            return false;
        }

        private static int SkipToNextElement(string value, int i)
        {
            int length = value.Length;
            while (i < length && value[i] != ',')
            {
                if (value[i] == '"')
                {
                    i++;
                    while (i < length && value[i] != '"')
                    {
                        if (value[i] == '\\')
                            i++;
                        i++;
                    }
                }

                i++;
            }

            return i;
        }

        private static bool IsWhitespace(char c) => c == ' ' || c == '\t';

        // RFC 9110 section 5.6.2 tchar.
        private static bool IsTokenChar(char c)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                return true;

            switch (c)
            {
                case '!':
                case '#':
                case '$':
                case '%':
                case '&':
                case '\'':
                case '*':
                case '+':
                case '-':
                case '.':
                case '^':
                case '_':
                case '`':
                case '|':
                case '~':
                    return true;
                default:
                    return false;
            }
        }
    }
}
