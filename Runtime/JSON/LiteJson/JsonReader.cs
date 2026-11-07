using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TurboHTTP.JSON.Lite
{
    /// <summary>
    /// Minimal JSON reader. Parses JSON strings into Dictionary/List/primitive types.
    /// AOT-safe: no reflection, no Activator.CreateInstance.
    /// </summary>
    /// <remarks>
    /// The grammar is strict RFC 8259: whitespace is only space, tab, LF and CR (section 2); numbers
    /// follow <c>[-] int [frac] [exp]</c> with ASCII digits, no leading zeros and at least one digit
    /// in each part (section 6); string contents below U+0020 must be escaped and <c>\uXXXX</c>
    /// takes exactly four hex digits (section 7). One leading byte-order mark (U+FEFF) is ignored
    /// (section 8.1). Integers are returned as <see cref="long"/>, or <see cref="ulong"/> above
    /// <see cref="long.MaxValue"/>, so 64-bit values are exact; fractions and exponents become
    /// <see cref="double"/>.
    /// </remarks>
    public sealed class JsonReader
    {
        private const int MaxDepth = 64;

        private readonly string _json;
        private readonly StringBuilder _stringBuffer;
        private int _position;
        private int _depth;

        public JsonReader(string json)
        {
            _json = json ?? throw new ArgumentNullException(nameof(json));
            _stringBuffer = new StringBuilder(256);
            _position = 0;
            _depth = 0;
        }

        /// <summary>
        /// Parse the JSON string and return the root value.
        /// </summary>
        /// <returns>
        /// Parsed value: null, bool, long/ulong/double, string,
        /// List&lt;object&gt;, or Dictionary&lt;string, object&gt;
        /// </returns>
        /// <exception cref="JsonParseException">Invalid JSON syntax or nesting too deep</exception>
        public object Parse()
        {
            // RFC 8259 section 8.1: a parser MAY ignore a leading byte-order mark.
            if (_position == 0 && _json.Length > 0 && _json[0] == '\uFEFF')
                _position = 1;

            SkipWhitespace();
            var result = ReadValue();
            SkipWhitespace();

            if (_position < _json.Length)
                throw new JsonParseException($"Unexpected character at position {_position}");

            return result;
        }

        private object ReadValue()
        {
            SkipWhitespace();

            if (_position >= _json.Length)
                throw new JsonParseException("Unexpected end of JSON");

            char c = _json[_position];

            if (c == '{')
                return ReadObjectWithDepthCheck();
            if (c == '[')
                return ReadArrayWithDepthCheck();
            if (c == '"')
                return ReadString();
            if (c == 't' || c == 'f')
                return ReadBoolean();
            if (c == 'n')
                return ReadNull();
            if (c == '-' || IsAsciiDigit(c))
                return ReadNumber();

            throw new JsonParseException($"Unexpected character '{c}' at position {_position}");
        }

        private void EnterNesting()
        {
            if (++_depth > MaxDepth)
                throw new JsonParseException($"Maximum nesting depth ({MaxDepth}) exceeded");
        }

        private void ExitNesting() => _depth--;

        private Dictionary<string, object> ReadObjectWithDepthCheck()
        {
            EnterNesting();
            try
            {
                return ReadObject();
            }
            finally
            {
                ExitNesting();
            }
        }

        private List<object> ReadArrayWithDepthCheck()
        {
            EnterNesting();
            try
            {
                return ReadArray();
            }
            finally
            {
                ExitNesting();
            }
        }

        private Dictionary<string, object> ReadObject()
        {
            Expect('{');
            var dict = new Dictionary<string, object>();
            SkipWhitespace();

            if (Peek() == '}')
            {
                Advance();
                return dict;
            }

            while (true)
            {
                SkipWhitespace();
                string key = ReadString();
                SkipWhitespace();
                Expect(':');
                object value = ReadValue();
                dict[key] = value;
                SkipWhitespace();

                char c = Peek();
                if (c == '}')
                {
                    Advance();
                    return dict;
                }
                if (c == ',')
                {
                    Advance();
                    continue;
                }
                throw new JsonParseException($"Expected ',' or '}}' at position {_position}");
            }
        }

        private List<object> ReadArray()
        {
            Expect('[');
            var list = new List<object>();
            SkipWhitespace();

            if (Peek() == ']')
            {
                Advance();
                return list;
            }

            while (true)
            {
                object value = ReadValue();
                list.Add(value);
                SkipWhitespace();

                char c = Peek();
                if (c == ']')
                {
                    Advance();
                    return list;
                }
                if (c == ',')
                {
                    Advance();
                    continue;
                }
                throw new JsonParseException($"Expected ',' or ']' at position {_position}");
            }
        }

        private string ReadString()
        {
            Expect('"');
            _stringBuffer.Clear();

            while (_position < _json.Length)
            {
                char c = _json[_position++];

                if (c == '"')
                    return _stringBuffer.ToString();

                if (c == '\\')
                {
                    if (_position >= _json.Length)
                        throw new JsonParseException("Unterminated string escape");

                    char escaped = _json[_position++];
                    switch (escaped)
                    {
                        case '"': _stringBuffer.Append('"'); break;
                        case '\\': _stringBuffer.Append('\\'); break;
                        case '/': _stringBuffer.Append('/'); break;
                        case 'b': _stringBuffer.Append('\b'); break;
                        case 'f': _stringBuffer.Append('\f'); break;
                        case 'n': _stringBuffer.Append('\n'); break;
                        case 'r': _stringBuffer.Append('\r'); break;
                        case 't': _stringBuffer.Append('\t'); break;
                        case 'u': AppendUnicodeEscape(); break;
                        default:
                            throw new JsonParseException($"Invalid escape sequence '\\{escaped}'");
                    }
                }
                else if (c < '\u0020')
                {
                    // RFC 8259 section 7: control characters must be escaped.
                    throw new JsonParseException(
                        $"Unescaped control character U+{(int)c:X4} in string at position {_position - 1}");
                }
                else
                {
                    _stringBuffer.Append(c);
                }
            }

            throw new JsonParseException("Unterminated string");
        }

        /// <summary>
        /// Parses \uXXXX escape and appends to buffer.
        /// Handles surrogate pairs for non-BMP characters (e.g., emoji).
        /// </summary>
        private void AppendUnicodeEscape()
        {
            int codePoint = ReadHexCodeUnit();

            // Check for high surrogate (U+D800 to U+DBFF)
            if (codePoint >= 0xD800 && codePoint <= 0xDBFF)
            {
                // Expect low surrogate: \uXXXX
                if (_position + 6 <= _json.Length &&
                    _json[_position] == '\\' &&
                    _json[_position + 1] == 'u')
                {
                    _position += 2; // Skip \u
                    int lowSurrogate = ReadHexCodeUnit();

                    // Check for low surrogate (U+DC00 to U+DFFF)
                    if (lowSurrogate >= 0xDC00 && lowSurrogate <= 0xDFFF)
                    {
                        // Combine surrogates into code point
                        _stringBuffer.Append((char)codePoint);
                        _stringBuffer.Append((char)lowSurrogate);
                        return;
                    }
                }
                throw new JsonParseException("Invalid surrogate pair: missing low surrogate");
            }

            // Check for lone low surrogate (invalid)
            if (codePoint >= 0xDC00 && codePoint <= 0xDFFF)
            {
                throw new JsonParseException("Invalid surrogate pair: unexpected low surrogate");
            }

            _stringBuffer.Append((char)codePoint);
        }

        private int ReadHexCodeUnit()
        {
            if (_position + 4 > _json.Length)
                throw new JsonParseException("Invalid unicode escape sequence");

            // Exactly four hex digits (int.TryParse(HexNumber) would also accept surrounding white space).
            int codePoint = 0;
            for (int i = 0; i < 4; i++)
            {
                int digit = HexValue(_json[_position + i]);
                if (digit < 0)
                {
                    throw new JsonParseException(
                        $"Invalid unicode escape sequence '\\u{_json.Substring(_position, 4)}'");
                }

                codePoint = (codePoint << 4) | digit;
            }

            _position += 4;
            return codePoint;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        private void ReadDigits(string part)
        {
            int start = _position;
            while (_position < _json.Length && IsAsciiDigit(_json[_position]))
                _position++;

            if (_position == start)
                throw new JsonParseException($"Expected a digit in the {part} of a number at position {_position}");
        }

        private object ReadNumber()
        {
            int start = _position;
            bool hasFraction = false;
            bool hasExponent = false;

            // RFC 8259 section 6: number = [ minus ] int [ frac ] [ exp ]; int = zero / ( digit1-9 *DIGIT )
            if (Peek() == '-')
                Advance();

            if (_position < _json.Length && _json[_position] == '0')
            {
                Advance();
                if (_position < _json.Length && IsAsciiDigit(_json[_position]))
                    throw new JsonParseException($"Leading zero in number at position {start}");
            }
            else
            {
                ReadDigits("integer part");
            }

            if (_position < _json.Length && _json[_position] == '.')
            {
                hasFraction = true;
                Advance();
                ReadDigits("fraction");
            }

            if (_position < _json.Length && (_json[_position] == 'e' || _json[_position] == 'E'))
            {
                hasExponent = true;
                Advance();
                if (_position < _json.Length && (_json[_position] == '+' || _json[_position] == '-'))
                    Advance();
                ReadDigits("exponent");
            }

            string numStr = _json.Substring(start, _position - start);

            if (!hasFraction && !hasExponent)
            {
                if (long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                    return l;

                if (numStr[0] != '-' &&
                    ulong.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong ul))
                    return ul;

                throw new JsonParseException($"Integer out of range '{numStr}'");
            }

            if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
                return result;

            throw new JsonParseException($"Invalid number '{numStr}'");
        }

        private bool ReadBoolean()
        {
            if (Match("true")) return true;
            if (Match("false")) return false;
            throw new JsonParseException($"Expected 'true' or 'false' at position {_position}");
        }

        private object ReadNull()
        {
            if (Match("null")) return null;
            throw new JsonParseException($"Expected 'null' at position {_position}");
        }

        private bool Match(string expected)
        {
            if (_position + expected.Length > _json.Length)
                return false;

            for (int i = 0; i < expected.Length; i++)
            {
                if (_json[_position + i] != expected[i])
                    return false;
            }

            _position += expected.Length;
            return true;
        }

        private void SkipWhitespace()
        {
            // RFC 8259 section 2: ws = space / horizontal tab / line feed / carriage return.
            while (_position < _json.Length)
            {
                char c = _json[_position];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                    return;
                _position++;
            }
        }

        private char Peek()
        {
            if (_position >= _json.Length)
                throw new JsonParseException("Unexpected end of JSON");
            return _json[_position];
        }

        private void Advance() => _position++;

        private void Expect(char expected)
        {
            if (_position >= _json.Length || _json[_position] != expected)
                throw new JsonParseException($"Expected '{expected}' at position {_position}");
            _position++;
        }
    }

    /// <summary>
    /// Exception thrown when JSON parsing fails.
    /// </summary>
    public class JsonParseException : Exception
    {
        public JsonParseException(string message) : base(message) { }
    }
}
