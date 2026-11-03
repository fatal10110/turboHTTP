# Step 3D.3: LiteJson Writer

**File:** `Runtime/JSON/LiteJson/JsonWriter.cs`  
**Depends on:** Nothing  
**Spec:** RFC 8259 (JSON)

## Purpose

Implement a minimal, AOT-safe JSON writer that converts .NET objects to JSON strings. This writer complements the JsonReader and together they form the foundation of the built-in LiteJson serializer.

## Class to Implement

### `JsonWriter`

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TurboHTTP.JSON.Lite
{
    /// <summary>
    /// Minimal JSON writer. Converts primitives, collections, and dictionaries to JSON.
    /// AOT-safe: no reflection, explicit type handling only.
    /// </summary>
    public sealed class JsonWriter
    {
        private const int MaxDepth = 64;
        
        private readonly StringBuilder _sb;
        private readonly bool _prettyPrint;
        private int _indent;
        private int _depth;

        public JsonWriter(bool prettyPrint = false)
        {
            _sb = new StringBuilder(256);
            _prettyPrint = prettyPrint;
            _indent = 0;
            _depth = 0;
        }

        /// <summary>
        /// Write an object to JSON and return the string.
        /// </summary>
        /// <param name="value">Value to serialize</param>
        /// <returns>JSON string</returns>
        /// <exception cref="JsonSerializationException">
        /// Type is not supported for serialization
        /// </exception>
        public string Write(object value)
        {
            _sb.Clear();
            WriteValue(value);
            return _sb.ToString();
        }

        private void WriteValue(object value)
        {
            switch (value)
            {
                case null:
                    _sb.Append("null");
                    break;

                case bool b:
                    _sb.Append(b ? "true" : "false");
                    break;

                case string s:
                    WriteString(s);
                    break;

                case char c:
                    WriteString(c.ToString());
                    break;

                case int i:
                    _sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;

                case long l:
                    _sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;

                case float f:
                    WriteFloat(f);
                    break;

                case double d:
                    WriteDouble(d);
                    break;

                case decimal m:
                    _sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    break;

                case byte b:
                    _sb.Append(b.ToString(CultureInfo.InvariantCulture));
                    break;

                case sbyte sb:
                    _sb.Append(sb.ToString(CultureInfo.InvariantCulture));
                    break;

                case short s:
                    _sb.Append(s.ToString(CultureInfo.InvariantCulture));
                    break;

                case ushort us:
                    _sb.Append(us.ToString(CultureInfo.InvariantCulture));
                    break;

                case uint ui:
                    _sb.Append(ui.ToString(CultureInfo.InvariantCulture));
                    break;

                case ulong ul:
                    _sb.Append(ul.ToString(CultureInfo.InvariantCulture));
                    break;

                case DateTime dt:
                    WriteString(dt.ToString("O", CultureInfo.InvariantCulture));
                    break;

                case DateTimeOffset dto:
                    WriteString(dto.ToString("O", CultureInfo.InvariantCulture));
                    break;

                case Guid g:
                    WriteString(g.ToString());
                    break;

                case Enum e:
                    _sb.Append(Convert.ToInt64(e).ToString(CultureInfo.InvariantCulture));
                    break;

                case IDictionary<string, object> dict:
                    WriteObjectWithDepthCheck(dict);
                    break;

                case IDictionary dict:
                    WriteGenericDictionaryWithDepthCheck(dict);
                    break;

                case IEnumerable enumerable:
                    WriteArrayWithDepthCheck(enumerable);
                    break;

                default:
                    throw new JsonSerializationException(
                        $"Type '{value.GetType().Name}' is not supported. " +
                        "Use a dictionary or register a custom serializer.");
            }
        }

        private void WriteString(string s)
        {
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':
                        _sb.Append("\\\"");
                        break;
                    case '\\':
                        _sb.Append("\\\\");
                        break;
                    case '\b':
                        _sb.Append("\\b");
                        break;
                    case '\f':
                        _sb.Append("\\f");
                        break;
                    case '\n':
                        _sb.Append("\\n");
                        break;
                    case '\r':
                        _sb.Append("\\r");
                        break;
                    case '\t':
                        _sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            _sb.Append("\\u");
                            _sb.Append(((int)c).ToString("X4"));
                        }
                        else
                        {
                            _sb.Append(c);
                        }
                        break;
                }
            }
            _sb.Append('"');
        }

        private void WriteFloat(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f))
            {
                _sb.Append("null"); // JSON doesn't support NaN/Infinity
            }
            else
            {
                _sb.Append(f.ToString("G9", CultureInfo.InvariantCulture));
            }
        }

        private void WriteDouble(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                _sb.Append("null"); // JSON doesn't support NaN/Infinity
            }
            else
            {
                _sb.Append(d.ToString("G17", CultureInfo.InvariantCulture));
            }
        }

        private void EnterNesting()
        {
            if (++_depth > MaxDepth)
                throw new JsonSerializationException($"Maximum nesting depth ({MaxDepth}) exceeded");
        }

        private void ExitNesting() => _depth--;

        private void WriteObjectWithDepthCheck(IDictionary<string, object> dict)
        {
            EnterNesting();
            try { WriteObject(dict); }
            finally { ExitNesting(); }
        }

        private void WriteGenericDictionaryWithDepthCheck(IDictionary dict)
        {
            EnterNesting();
            try { WriteGenericDictionary(dict); }
            finally { ExitNesting(); }
        }

        private void WriteArrayWithDepthCheck(IEnumerable enumerable)
        {
            EnterNesting();
            try { WriteArray(enumerable); }
            finally { ExitNesting(); }
        }

        private void WriteObject(IDictionary<string, object> dict)
        {
            _sb.Append('{');
            _indent++;

            bool first = true;
            foreach (var kvp in dict)
            {
                if (!first)
                    _sb.Append(',');
                first = false;

                WriteNewLine();
                WriteIndent();
                WriteString(kvp.Key);
                _sb.Append(_prettyPrint ? ": " : ":");
                WriteValue(kvp.Value);
            }

            _indent--;
            if (!first)
            {
                WriteNewLine();
                WriteIndent();
            }
            _sb.Append('}');
        }

        private void WriteGenericDictionary(IDictionary dict)
        {
            _sb.Append('{');
            _indent++;

            bool first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first)
                    _sb.Append(',');
                first = false;

                WriteNewLine();
                WriteIndent();
                WriteString(entry.Key?.ToString() ?? "null");
                _sb.Append(_prettyPrint ? ": " : ":");
                WriteValue(entry.Value);
            }

            _indent--;
            if (!first)
            {
                WriteNewLine();
                WriteIndent();
            }
            _sb.Append('}');
        }

        private void WriteArray(IEnumerable enumerable)
        {
            _sb.Append('[');
            _indent++;

            bool first = true;
            foreach (var item in enumerable)
            {
                if (!first)
                    _sb.Append(',');
                first = false;

                WriteNewLine();
                WriteIndent();
                WriteValue(item);
            }

            _indent--;
            if (!first)
            {
                WriteNewLine();
                WriteIndent();
            }
            _sb.Append(']');
        }

        private void WriteNewLine()
        {
            if (_prettyPrint)
                _sb.Append('\n');
        }

        private void WriteIndent()
        {
            if (_prettyPrint)
                _sb.Append(' ', _indent * 2);
        }
    }
}
```

## Design Notes

### Supported Types

The writer explicitly handles these types (no reflection):

| Category | Types |
|----------|-------|
| Null | `null` |
| Boolean | `bool` |
| String | `string`, `char`, `Guid` |
| Integer | `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` |
| Floating | `float`, `double`, `decimal` |
| Date | `DateTime`, `DateTimeOffset` (ISO 8601) |
| Enum | Any enum (serialized as integer) |
| Object | `IDictionary<string, object>`, `IDictionary` |
| Array | `IEnumerable` (arrays, lists, etc.) |

### Unsupported Types

Custom classes/structs are NOT supported directly. Users must either:
1. Convert to `Dictionary<string, object>` first
2. Register a custom `IJsonSerializer` (Newtonsoft, etc.)

### Special Value Handling

- **NaN/Infinity:** Written as `null` (JSON doesn't support these)
- **Control characters:** Escaped as `\uXXXX`
- **Unicode:** Written as-is (UTF-8 safe)

### Pretty Print

Optional indentation for debugging:
```csharp
var writer = new JsonWriter(prettyPrint: true);
var json = writer.Write(myDict);
```

## Namespace

`TurboHTTP.JSON.Lite`

## Validation Criteria

- [ ] Writes all primitive types correctly
- [ ] Writes nested dictionaries and arrays
- [ ] Handles all escape sequences properly
- [ ] Throws clear error for unsupported types
- [ ] Pretty print mode produces valid, indented JSON
- [ ] Round-trip with JsonReader produces identical data
- [ ] Throws on nesting depth > 64
- [ ] Reuses StringBuilder (no per-Write allocations)

## Test Cases

```csharp
// Primitives
Write(null) → "null"
Write(true) → "true"
Write(false) → "false"
Write(42) → "42"
Write(3.14) → "3.14"
Write("hello") → "\"hello\""
Write("line\nbreak") → "\"line\\nbreak\""
Write("tab\there") → "\"tab\\there\""

// Collections
Write(new List<int>{1,2,3}) → "[1,2,3]"
Write(new Dictionary<string,object>{{"a",1}}) → "{\"a\":1}"

// Special
Write(float.NaN) → "null"
Write(DateTime.Parse("2024-01-15T10:30:00Z")) → "\"2024-01-15T10:30:00.0000000Z\""
Write(Guid.Empty) → "\"00000000-0000-0000-0000-000000000000\""

// Nested
Write(new Dictionary<string, object>{
    {"arr", new List<object>{1, 2, 3}},
    {"nested", new Dictionary<string, object>{{"b", 2}}}
}) → "{\"arr\":[1,2,3],\"nested\":{\"b\":2}}"

// Error
Write(new CustomClass()) → throws JsonSerializationException
```
