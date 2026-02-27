using System;
using System.Diagnostics;
using System.Text;

namespace TurboHTTP.Transport.Internal
{
    /// <summary>
    /// Shared Latin-1 (ISO-8859-1) encoding for HTTP/1.1 header serialization and parsing.
    /// Both serializer and parser must use the same encoding to ensure round-trip correctness.
    /// </summary>
    internal static class EncodingHelper
    {
        internal static readonly Encoding Latin1 = InitLatin1();

        /// <summary>
        /// Encodes <paramref name="value"/> into Latin-1 bytes written to
        /// <paramref name="destination"/> without allocating a temporary byte array.
        /// Non-Latin-1 characters (code point > 255) are replaced with '?'.
        /// </summary>
        /// <param name="value">String to encode. Null or empty returns 0.</param>
        /// <param name="destination">Output span. Must be at least <paramref name="value"/>.Length bytes.</param>
        /// <returns>Number of bytes written (equals <paramref name="value"/>.Length).</returns>
        internal static int GetLatin1Bytes(string value, Span<byte> destination)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            Debug.Assert(destination.Length >= value.Length,
                $"Destination span is too small: need {value.Length} bytes, got {destination.Length}.");

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                destination[i] = c < 256 ? (byte)c : (byte)'?';
            }
            return value.Length;
        }

        /// <summary>
        /// Returns the number of bytes required to Latin-1-encode <paramref name="value"/>.
        /// Always equals <c>value.Length</c> (Latin-1 is single-byte, one char → one byte).
        /// </summary>
        internal static int GetLatin1ByteCount(string value) =>
            value == null ? 0 : value.Length;

        private static Encoding InitLatin1()
        {
            try
            {
                // Codepage 28591 = ISO-8859-1 (Latin-1).
                // Numeric lookup avoids string-based resolution issues.
                // Encoding.Latin1 static property is .NET 5+ only, NOT .NET Standard 2.1.
                return Encoding.GetEncoding(28591);
            }
            catch
            {
                // IL2CPP may strip codepage data. Fall back to minimal custom implementation.
                return new Latin1Encoding();
            }
        }

        /// <summary>
        /// Minimal Latin-1 (ISO-8859-1) encoder/decoder. Maps bytes 0-255 directly to chars 0-255.
        /// Used as IL2CPP-safe fallback when Encoding.GetEncoding(28591) is stripped.
        /// </summary>
        private sealed class Latin1Encoding : Encoding
        {
            public override int GetByteCount(char[] chars, int index, int count) => count;

            public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
            {
                for (int i = 0; i < charCount; i++)
                {
                    char c = chars[charIndex + i];
                    bytes[byteIndex + i] = c < 256 ? (byte)c : (byte)'?';
                }
                return charCount;
            }

            public override int GetCharCount(byte[] bytes, int index, int count) => count;

            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            {
                for (int i = 0; i < byteCount; i++)
                    chars[charIndex + i] = (char)bytes[byteIndex + i];
                return byteCount;
            }

            public override int GetMaxByteCount(int charCount) => charCount;
            public override int GetMaxCharCount(int byteCount) => byteCount;

            public override string GetString(byte[] bytes, int index, int count)
            {
                var chars = new char[count];
                for (int i = 0; i < count; i++)
                    chars[i] = (char)bytes[index + i];
                return new string(chars);
            }

            public override byte[] GetBytes(string s)
            {
                var bytes = new byte[s.Length];
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    bytes[i] = c < 256 ? (byte)c : (byte)'?';
                }
                return bytes;
            }
        }
    }
}
