# Step 3D.2: LiteJson Reader

**File:** `Runtime/JSON/LiteJson/JsonReader.cs`  
**Depends on:** Nothing  
**Spec:** RFC 8259 (JSON)

## Purpose

Implement a minimal, AOT-safe JSON parser that reads JSON strings and converts them to .NET objects. This reader is the foundation of the built-in LiteJson serializer.

## Class to Implement

### `JsonReader`

```csharp
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

            // C# 7 compatible - no 'or' patterns
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
            if (c == '-' || char.IsDigit(c))
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
                    // C# 7 compatible - use switch statement instead of expression
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
            
            string hex = _json.Substring(_position, 4);
            _position += 4;
            
            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint))
                return codePoint;
            
            throw new JsonParseException($"Invalid unicode escape sequence '\\u{hex}'");
        }

        private object ReadNumber()
        {
            int start = _position;
            bool hasFraction = false;
            bool hasExponent = false;

            if (Peek() == '-')
                Advance();

            while (_position < _json.Length && char.IsDigit(_json[_position]))
                Advance();

            if (_position < _json.Length && _json[_position] == '.')
            {
                hasFraction = true;
                Advance();
                while (_position < _json.Length && char.IsDigit(_json[_position]))
                    Advance();
            }

            if (_position < _json.Length && (_json[_position] == 'e' || _json[_position] == 'E'))
            {
                hasExponent = true;
                Advance();
                if (_position < _json.Length && (_json[_position] == '+' || _json[_position] == '-'))
                    Advance();
                while (_position < _json.Length && char.IsDigit(_json[_position]))
                    Advance();
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
            while (_position < _json.Length && char.IsWhiteSpace(_json[_position]))
                _position++;
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
```

## Design Notes

### Return Types

The parser returns a limited set of types:
- **Null:** `null`
- **Boolean:** `bool`
- **Number:** `long`/`ulong` for integer tokens, `double` for fractional/exponent tokens
- **String:** `string`
- **Array:** `List<object>`
- **Object:** `Dictionary<string, object>`

### Memory Efficiency

**StringBuilder Pooling:** The reader reuses a single `StringBuilder` instance for all string parsing, clearing it between uses. This avoids per-string allocations during parsing.

### Recursion Depth Limit

**MaxDepth = 64:** Nested objects/arrays are limited to 64 levels deep. This prevents:
- Stack overflow from deeply nested JSON
- DoS attacks via malicious payloads
- Matches common limits (e.g., System.Text.Json defaults to 64)

### No Reflection

The reader does NOT attempt to map JSON to custom CLR types. That's the responsibility of:
- `LiteJsonSerializer` (basic type mapping)
- User-provided serializers (full object mapping)

### Error Messages

Error messages include position for debugging:
- `"Unexpected character 'x' at position 42"`
- `"Expected ':' at position 15"`

### Unicode Support

Full Unicode escape support (`\uXXXX`) per RFC 8259, including surrogate pairs.

## Namespace

`TurboHTTP.JSON.Lite`

## Validation Criteria

- [ ] Parses all JSON primitive types (null, bool, number, string)
- [ ] Parses empty objects `{}` and arrays `[]`
- [ ] Parses nested objects and arrays
- [ ] Handles all escape sequences: `\" \\ \/ \b \f \n \r \t \uXXXX`
- [ ] Handles negative numbers and scientific notation
- [ ] Integer tokens return `long`/`ulong` or throw if out of range
- [ ] Handles surrogate pairs in `\uXXXX` escapes
- [ ] Throws clear errors for invalid JSON
- [ ] No reflection or Activator usage (AOT-safe)
- [ ] Throws on nesting depth > 64
- [ ] No per-string StringBuilder allocations (uses pooled instance)

## Test Cases

```csharp
// Primitives
Parse("null") → null
Parse("true") → true
Parse("false") → false
Parse("42") → 42L
Parse("-3.14") → -3.14
Parse("1e10") → 1e10
Parse("18446744073709551615") → 18446744073709551615UL
Parse("\"hello\"") → "hello"
Parse("\"line\\nbreak\"") → "line\nbreak"
Parse("\"unicode: \\u0041\"") → "unicode: A"

// Collections
Parse("[]") → List<object>(empty)
Parse("[1, 2, 3]") → List<object>{1L, 2L, 3L}
Parse("{}") → Dictionary<string, object>(empty)
Parse("{\"a\": 1}") → Dictionary<string, object>{{"a", 1L}}

// Nested
Parse("{\"arr\": [1, {\"b\": 2}]}") → nested structure

// Unicode surrogate pair
Parse("\"\\uD83D\\uDE00\"") → char.ConvertFromUtf32(0x1F600)

// Errors
Parse("") → throws JsonParseException
Parse("{invalid}") → throws JsonParseException
Parse("[1,]") → throws JsonParseException
Parse("9223372036854775808") → throws JsonParseException (integer out of range)
```
