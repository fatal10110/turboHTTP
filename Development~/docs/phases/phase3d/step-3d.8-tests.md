# Step 3D.8: Unit & Integration Tests

**Files:** Multiple new test files  
**Depends on:** 3D.1–3D.7 (all previous steps)  
**Spec:** N/A (testing)

## Purpose

Validate the JSON implementation with comprehensive tests covering:
- LiteJson reader/writer correctness
- Serializer abstraction
- Registry functionality
- Integration with UHttpRequestBuilder

## Test Files to Create

```
Tests/Runtime/JSON/
    JsonReaderTests.cs           — LiteJson reader
    JsonWriterTests.cs           — LiteJson writer  
    LiteJsonSerializerTests.cs   — Full serializer
    JsonSerializerRegistryTests.cs — Registry/facade
Tests/Runtime/Core/
    UHttpRequestBuilderJsonTests.cs — Integration
```

> [!IMPORTANT]
> Since `TurboHTTP.JSON` has `autoReferenced: false`, you must add it to the test assembly's references:
> ```json
> // In Tests/Runtime/TurboHTTP.Tests.Runtime.asmdef
> {
>     "references": [
>         "TurboHTTP.Core",
>         "TurboHTTP.JSON"  // Add this line
>     ]
> }
> ```

## Test Implementations

### 1. `JsonReaderTests.cs`

```csharp
using NUnit.Framework;
using TurboHTTP.JSON.Lite;
using System.Collections.Generic;

namespace TurboHTTP.Tests.JSON
{
    [TestFixture]
    public class JsonReaderTests
    {
        // === Primitives ===

        [Test]
        public void Parse_Null_ReturnsNull()
        {
            var reader = new JsonReader("null");
            Assert.That(reader.Parse(), Is.Null);
        }

        [Test]
        public void Parse_True_ReturnsTrue()
        {
            var reader = new JsonReader("true");
            Assert.That(reader.Parse(), Is.True);
        }

        [Test]
        public void Parse_False_ReturnsFalse()
        {
            var reader = new JsonReader("false");
            Assert.That(reader.Parse(), Is.False);
        }

        [TestCase("0", 0L)]
        [TestCase("42", 42L)]
        [TestCase("-17", -17L)]
        public void Parse_Integers_ReturnsLong(string json, long expected)
        {
            var reader = new JsonReader(json);
            Assert.That(reader.Parse(), Is.EqualTo(expected));
        }

        [Test]
        public void Parse_UlongMax_ReturnsUlong()
        {
            var reader = new JsonReader("18446744073709551615");
            Assert.That(reader.Parse(), Is.EqualTo(18446744073709551615UL));
        }

        [TestCase("3.14", 3.14)]
        [TestCase("-0.5", -0.5)]
        [TestCase("1e10", 1e10)]
        [TestCase("2.5e-3", 2.5e-3)]
        [TestCase("1E+5", 1E+5)]
        public void Parse_Floats_ReturnsDouble(string json, double expected)
        {
            var reader = new JsonReader(json);
            Assert.That(reader.Parse(), Is.EqualTo(expected).Within(0.0001));
        }

        [Test]
        public void Parse_EmptyString_ReturnsEmptyString()
        {
            var reader = new JsonReader("\"\"");
            Assert.That(reader.Parse(), Is.EqualTo(""));
        }

        [Test]
        public void Parse_SimpleString_ReturnsString()
        {
            var reader = new JsonReader("\"hello world\"");
            Assert.That(reader.Parse(), Is.EqualTo("hello world"));
        }

        [TestCase("\"line\\nbreak\"", "line\nbreak")]
        [TestCase("\"tab\\there\"", "tab\there")]
        [TestCase("\"quote\\\"here\"", "quote\"here")]
        [TestCase("\"back\\\\slash\"", "back\\slash")]
        [TestCase("\"slash\\/here\"", "slash/here")]
        [TestCase("\"carriage\\rreturn\"", "carriage\rreturn")]
        public void Parse_EscapedStrings_ReturnsUnescaped(string json, string expected)
        {
            var reader = new JsonReader(json);
            Assert.That(reader.Parse(), Is.EqualTo(expected));
        }

        [Test]
        public void Parse_UnicodeEscape_ReturnsUnicode()
        {
            var reader = new JsonReader("\"\\u0041\\u0042\\u0043\"");
            Assert.That(reader.Parse(), Is.EqualTo("ABC"));
        }

        [Test]
        public void Parse_UnicodeSurrogatePair_ReturnsUnicode()
        {
            var reader = new JsonReader("\"\\uD83D\\uDE00\"");
            var expected = char.ConvertFromUtf32(0x1F600);
            Assert.That(reader.Parse(), Is.EqualTo(expected));
        }

        // === Arrays ===

        [Test]
        public void Parse_EmptyArray_ReturnsEmptyList()
        {
            var reader = new JsonReader("[]");
            var result = reader.Parse() as List<object>;
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Count, Is.EqualTo(0));
        }

        [Test]
        public void Parse_NumberArray_ReturnsList()
        {
            var reader = new JsonReader("[1, 2, 3]");
            var result = reader.Parse() as List<object>;
            Assert.That(result, Is.EqualTo(new List<object> { 1L, 2L, 3L }));
        }

        [Test]
        public void Parse_MixedArray_ReturnsList()
        {
            var reader = new JsonReader("[1, \"two\", true, null]");
            var result = reader.Parse() as List<object>;
            Assert.That(result, Is.EqualTo(new List<object> { 1L, "two", true, null }));
        }

        // === Objects ===

        [Test]
        public void Parse_EmptyObject_ReturnsEmptyDict()
        {
            var reader = new JsonReader("{}");
            var result = reader.Parse() as Dictionary<string, object>;
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Count, Is.EqualTo(0));
        }

        [Test]
        public void Parse_SimpleObject_ReturnsDict()
        {
            var reader = new JsonReader("{\"name\": \"test\", \"value\": 42}");
            var result = reader.Parse() as Dictionary<string, object>;
            Assert.That(result["name"], Is.EqualTo("test"));
            Assert.That(result["value"], Is.EqualTo(42L));
        }

        [Test]
        public void Parse_NestedObject_ReturnsNestedDict()
        {
            var reader = new JsonReader("{\"outer\": {\"inner\": 1}}");
            var result = reader.Parse() as Dictionary<string, object>;
            var outer = result["outer"] as Dictionary<string, object>;
            Assert.That(outer["inner"], Is.EqualTo(1L));
        }

        // === Error Cases ===

        [Test]
        public void Parse_InvalidJson_ThrowsException()
        {
            var reader = new JsonReader("{invalid}");
            Assert.That(() => reader.Parse(), Throws.TypeOf<JsonParseException>());
        }

        [Test]
        public void Parse_UnterminatedString_ThrowsException()
        {
            var reader = new JsonReader("\"unterminated");
            Assert.That(() => reader.Parse(), Throws.TypeOf<JsonParseException>());
        }

        [Test]
        public void Parse_TrailingComma_ThrowsException()
        {
            var reader = new JsonReader("[1, 2,]");
            Assert.That(() => reader.Parse(), Throws.TypeOf<JsonParseException>());
        }

        [Test]
        public void Parse_IntegerOutOfRange_ThrowsException()
        {
            var reader = new JsonReader("9223372036854775808");
            Assert.That(() => reader.Parse(), Throws.TypeOf<JsonParseException>());
        }

        // === Depth Limit Tests ===

        [Test]
        public void Parse_DeeplyNested_ThrowsAtMaxDepth()
        {
            // Create JSON with 65 levels of nesting (exceeds MaxDepth of 64)
            var json = new string('[', 65) + new string(']', 65);
            var reader = new JsonReader(json);
            Assert.That(() => reader.Parse(), Throws.TypeOf<JsonParseException>()
                .With.Message.Contains("Maximum nesting depth"));
        }

        [Test]
        public void Parse_ExactlyMaxDepth_Succeeds()
        {
            // Create JSON with exactly 64 levels of nesting
            var json = new string('[', 64) + new string(']', 64);
            var reader = new JsonReader(json);
            Assert.That(() => reader.Parse(), Throws.Nothing);
        }

        [Test]
        public void Parse_DeeplyNestedObjects_ThrowsAtMaxDepth()
        {
            // Create nested objects exceeding max depth
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 65; i++)
                sb.Append("{\"a\":");
            sb.Append("1");
            for (int i = 0; i < 65; i++)
                sb.Append("}");
            
            var reader = new JsonReader(sb.ToString());
            Assert.That(() => reader.Parse(), Throws.TypeOf<JsonParseException>()
                .With.Message.Contains("Maximum nesting depth"));
        }

        // === Surrogate Pair Tests (Non-BMP Unicode) ===

        [Test]
        public void Parse_SurrogatePair_DecodesEmoji()
        {
            // 😀 is U+1F600, encoded as \uD83D\uDE00
            var reader = new JsonReader("\"\\uD83D\\uDE00\"");
            var result = reader.Parse() as string;
            Assert.That(result, Is.EqualTo("😀"));
        }

        [Test]
        public void Parse_MultipleSurrogatePairs_DecodesCorrectly()
        {
            // 👋🏽 = U+1F44B (wave) + U+1F3FD (medium skin tone)
            var reader = new JsonReader("\"\\uD83D\\uDC4B\\uD83C\\uDFFD\"");
            var result = reader.Parse() as string;
            Assert.That(result, Is.EqualTo("👋🏽"));
        }

        [Test]
        public void Parse_LoneHighSurrogate_ThrowsException()
        {
            // High surrogate without low surrogate
            var reader = new JsonReader("\"\\uD83D\"");
            Assert.That(() => reader.Parse(), Throws.TypeOf<JsonParseException>()
                .With.Message.Contains("surrogate"));
        }

        [Test]
        public void Parse_LoneLowSurrogate_ThrowsException()
        {
            // Low surrogate without high surrogate
            var reader = new JsonReader("\"\\uDE00\"");
            Assert.That(() => reader.Parse(), Throws.TypeOf<JsonParseException>()
                .With.Message.Contains("surrogate"));
        }
    }
}
```

### 2. `JsonWriterTests.cs`

```csharp
using NUnit.Framework;
using TurboHTTP.JSON.Lite;
using System.Collections.Generic;
using System;

namespace TurboHTTP.Tests.JSON
{
    [TestFixture]
    public class JsonWriterTests
    {
        // === Primitives ===

        [Test]
        public void Write_Null_ReturnsNull()
        {
            var writer = new JsonWriter();
            Assert.That(writer.Write(null), Is.EqualTo("null"));
        }

        [Test]
        public void Write_True_ReturnsTrue()
        {
            var writer = new JsonWriter();
            Assert.That(writer.Write(true), Is.EqualTo("true"));
        }

        [Test]
        public void Write_False_ReturnsFalse()
        {
            var writer = new JsonWriter();
            Assert.That(writer.Write(false), Is.EqualTo("false"));
        }

        [TestCase(0, "0")]
        [TestCase(42, "42")]
        [TestCase(-17, "-17")]
        public void Write_Integers_ReturnsNumberString(int value, string expected)
        {
            var writer = new JsonWriter();
            Assert.That(writer.Write(value), Is.EqualTo(expected));
        }

        [Test]
        public void Write_String_ReturnsQuotedString()
        {
            var writer = new JsonWriter();
            Assert.That(writer.Write("hello"), Is.EqualTo("\"hello\""));
        }

        [Test]
        public void Write_StringWithEscapes_ReturnsEscaped()
        {
            var writer = new JsonWriter();
            Assert.That(writer.Write("line\nbreak"), Is.EqualTo("\"line\\nbreak\""));
            Assert.That(writer.Write("tab\there"), Is.EqualTo("\"tab\\there\""));
            Assert.That(writer.Write("quote\"here"), Is.EqualTo("\"quote\\\"here\""));
        }

        [Test]
        public void Write_NaN_ReturnsNull()
        {
            var writer = new JsonWriter();
            Assert.That(writer.Write(double.NaN), Is.EqualTo("null"));
        }

        [Test]
        public void Write_Infinity_ReturnsNull()
        {
            var writer = new JsonWriter();
            Assert.That(writer.Write(double.PositiveInfinity), Is.EqualTo("null"));
            Assert.That(writer.Write(double.NegativeInfinity), Is.EqualTo("null"));
        }

        // === Collections ===

        [Test]
        public void Write_EmptyList_ReturnsEmptyArray()
        {
            var writer = new JsonWriter();
            Assert.That(writer.Write(new List<object>()), Is.EqualTo("[]"));
        }

        [Test]
        public void Write_NumberList_ReturnsArray()
        {
            var writer = new JsonWriter();
            var json = writer.Write(new List<int> { 1, 2, 3 });
            Assert.That(json, Does.Contain("1"));
            Assert.That(json, Does.Contain("2"));
            Assert.That(json, Does.Contain("3"));
        }

        [Test]
        public void Write_EmptyDict_ReturnsEmptyObject()
        {
            var writer = new JsonWriter();
            Assert.That(writer.Write(new Dictionary<string, object>()), Is.EqualTo("{}"));
        }

        [Test]
        public void Write_Dict_ReturnsObject()
        {
            var writer = new JsonWriter();
            var dict = new Dictionary<string, object> { { "a", 1 }, { "b", "two" } };
            var json = writer.Write(dict);
            Assert.That(json, Does.Contain("\"a\""));
            Assert.That(json, Does.Contain("\"b\""));
        }

        // === Special Types ===

        [Test]
        public void Write_DateTime_ReturnsIsoString()
        {
            var writer = new JsonWriter();
            var dt = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
            var json = writer.Write(dt);
            Assert.That(json, Does.Contain("2024-01-15"));
        }

        [Test]
        public void Write_Guid_ReturnsQuotedString()
        {
            var writer = new JsonWriter();
            var guid = Guid.Empty;
            var json = writer.Write(guid);
            Assert.That(json, Is.EqualTo("\"00000000-0000-0000-0000-000000000000\""));
        }

        // === Error Cases ===

        [Test]
        public void Write_UnsupportedType_ThrowsException()
        {
            var writer = new JsonWriter();
            Assert.That(() => writer.Write(new CustomClass()), 
                Throws.TypeOf<JsonSerializationException>());
        }

        // === Depth Limit Tests ===

        [Test]
        public void Write_DeeplyNested_ThrowsAtMaxDepth()
        {
            // Create 65-level nested structure
            object nested = new List<object>();
            for (int i = 0; i < 64; i++)
                nested = new List<object> { nested };
            
            var writer = new JsonWriter();
            Assert.That(() => writer.Write(nested), Throws.TypeOf<JsonSerializationException>()
                .With.Message.Contains("Maximum nesting depth"));
        }

        [Test]
        public void Write_ExactlyMaxDepth_Succeeds()
        {
            // Create exactly 64-level nested structure
            object nested = new List<object>();
            for (int i = 0; i < 63; i++)
                nested = new List<object> { nested };
            
            var writer = new JsonWriter();
            Assert.That(() => writer.Write(nested), Throws.Nothing);
        }

        private class CustomClass { }
    }
}
```

### 3. `LiteJsonSerializerTests.cs`

```csharp
using NUnit.Framework;
using TurboHTTP.JSON;
using TurboHTTP.JSON.Lite;
using System.Collections.Generic;

namespace TurboHTTP.Tests.JSON
{
    [TestFixture]
    public class LiteJsonSerializerTests
    {
        private LiteJsonSerializer _serializer;

        [SetUp]
        public void SetUp()
        {
            _serializer = LiteJsonSerializer.Instance;
        }

        [Test]
        public void Instance_IsSingleton()
        {
            Assert.That(LiteJsonSerializer.Instance, Is.SameAs(_serializer));
        }

        [Test]
        public void RoundTrip_Integer()
        {
            var json = _serializer.Serialize(42);
            var result = _serializer.Deserialize<int>(json);
            Assert.That(result, Is.EqualTo(42));
        }

        [Test]
        public void RoundTrip_String()
        {
            var json = _serializer.Serialize("hello");
            var result = _serializer.Deserialize<string>(json);
            Assert.That(result, Is.EqualTo("hello"));
        }

        [Test]
        public void RoundTrip_Dictionary()
        {
            var original = new Dictionary<string, object>
            {
                { "name", "test" },
                { "value", 42L }
            };
            var json = _serializer.Serialize(original);
            var result = _serializer.Deserialize<Dictionary<string, object>>(json);
            Assert.That(result["name"], Is.EqualTo("test"));
            Assert.That(result["value"], Is.EqualTo(42L));
        }

        [Test]
        public void Deserialize_InvalidJson_ThrowsException()
        {
            Assert.That(() => _serializer.Deserialize<object>("invalid"), 
                Throws.TypeOf<JsonSerializationException>());
        }

        [Test]
        public void Deserialize_EmptyString_ThrowsException()
        {
            Assert.That(() => _serializer.Deserialize<object>(""), 
                Throws.TypeOf<JsonSerializationException>());
        }

        [Test]
        public void Deserialize_Null_ThrowsException()
        {
            Assert.That(() => _serializer.Deserialize<object>(null), 
                Throws.TypeOf<JsonSerializationException>());
        }
    }
}
```

### 4. `JsonSerializerRegistryTests.cs`

```csharp
using NUnit.Framework;
using TurboHTTP.JSON;
using TurboHTTP.JSON.Lite;
using System;

namespace TurboHTTP.Tests.JSON
{
    [TestFixture]
    public class JsonSerializerRegistryTests
    {
        [TearDown]
        public void TearDown()
        {
            // Reset after each test
            JsonSerializer.ResetToDefault();
        }

        [Test]
        public void Default_IsLiteJsonSerializer()
        {
            Assert.That(JsonSerializer.Default, Is.SameAs(LiteJsonSerializer.Instance));
        }

        [Test]
        public void HasCustomSerializer_WhenDefault_ReturnsFalse()
        {
            Assert.That(JsonSerializer.HasCustomSerializer(), Is.False);
        }

        [Test]
        public void SetDefault_ChangesSerializer()
        {
            var mock = new MockSerializer();
            JsonSerializer.SetDefault(mock);
            Assert.That(JsonSerializer.Default, Is.SameAs(mock));
            Assert.That(JsonSerializer.HasCustomSerializer(), Is.True);
        }

        [Test]
        public void SetDefault_Null_ThrowsException()
        {
            Assert.That(() => JsonSerializer.SetDefault(null), 
                Throws.TypeOf<ArgumentNullException>());
        }

        [Test]
        public void ResetToDefault_RestoresLiteJson()
        {
            JsonSerializer.SetDefault(new MockSerializer());
            JsonSerializer.ResetToDefault();
            Assert.That(JsonSerializer.Default, Is.SameAs(LiteJsonSerializer.Instance));
        }

        [Test]
        public void Serialize_UsesRegisteredSerializer()
        {
            var mock = new MockSerializer();
            JsonSerializer.SetDefault(mock);
            JsonSerializer.Serialize(42);
            Assert.That(mock.SerializeCalled, Is.True);
        }

        [Test]
        public void Deserialize_UsesRegisteredSerializer()
        {
            var mock = new MockSerializer();
            JsonSerializer.SetDefault(mock);
            JsonSerializer.Deserialize<int>("42");
            Assert.That(mock.DeserializeCalled, Is.True);
        }

        private class MockSerializer : IJsonSerializer
        {
            public bool SerializeCalled { get; private set; }
            public bool DeserializeCalled { get; private set; }

            public string Serialize<T>(T value) { SerializeCalled = true; return "{}"; }
            public T Deserialize<T>(string json) { DeserializeCalled = true; return default; }
            public string Serialize(object value, Type type) { SerializeCalled = true; return "{}"; }
            public object Deserialize(string json, Type type) { DeserializeCalled = true; return null; }
        }

        // === Concurrency Tests ===

        [Test]
        public void ConcurrentSerialize_WithVolatileDefault_IsThreadSafe()
        {
            // Verify that reads during a write don't cause issues
            var tasks = new System.Threading.Tasks.Task[10];
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        for (int j = 0; j < 100; j++)
                        {
                            // Concurrent reads
                            var json = JsonSerializer.Serialize(42);
                            Assert.That(json, Is.Not.Null);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }
            
            // Concurrent write during reads
            JsonSerializer.SetDefault(new MockSerializer());
            
            System.Threading.Tasks.Task.WaitAll(tasks);
            Assert.That(exceptions, Is.Empty, 
                "Concurrent access should not throw exceptions");
        }
    }
}
```

### 5. `UHttpRequestBuilderJsonTests.cs`

```csharp
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.JSON.Lite;
using System.Collections.Generic;
using System.Text;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class UHttpRequestBuilderJsonTests
    {
        private UHttpClient _client;

        [SetUp]
        public void SetUp()
        {
            _client = new UHttpClient(new UHttpClientOptions
            {
                BaseUrl = "https://example.com"
            });
            JsonSerializer.ResetToDefault();
        }

        [TearDown]
        public void TearDown()
        {
            JsonSerializer.ResetToDefault();
        }

        [Test]
        public void WithJsonBody_String_SetsBodyAndContentType()
        {
            var request = _client.Post("/api")
                .WithJsonBody("{\"name\":\"test\"}")
                .Build();

            var body = Encoding.UTF8.GetString(request.Body);
            Assert.That(body, Is.EqualTo("{\"name\":\"test\"}"));
            Assert.That(request.Headers.GetValue("Content-Type"), Is.EqualTo("application/json"));
        }

        [Test]
        public void WithJsonBody_Dictionary_UsesDefaultSerializer()
        {
            var data = new Dictionary<string, object> { { "name", "test" } };
            var request = _client.Post("/api")
                .WithJsonBody(data)
                .Build();

            var body = Encoding.UTF8.GetString(request.Body);
            Assert.That(body, Does.Contain("name"));
            Assert.That(body, Does.Contain("test"));
        }

        [Test]
        public void WithJsonBody_WithCustomSerializer_UsesCustom()
        {
            var mock = new MockSerializer("{\"custom\":true}");
            var data = new Dictionary<string, object> { { "name", "test" } };
            var request = _client.Post("/api")
                .WithJsonBody(data, mock)
                .Build();

            var body = Encoding.UTF8.GetString(request.Body);
            Assert.That(body, Is.EqualTo("{\"custom\":true}"));
        }

        private class MockSerializer : IJsonSerializer
        {
            private readonly string _output;
            public MockSerializer(string output) => _output = output;
            public string Serialize<T>(T value) => _output;
            public T Deserialize<T>(string json) => default;
            public string Serialize(object value, System.Type type) => _output;
            public object Deserialize(string json, System.Type type) => null;
        }
    }
}
```

## Running Tests

```bash
# Unity Test Runner (Editor)
# Window → General → Test Runner → EditMode → Run All

# Command Line (CI)
# Unity -batchmode -projectPath . -runTests -testResults results.xml
```

## Validation Criteria

- [ ] All JsonReader tests pass
- [ ] All JsonWriter tests pass  
- [ ] All LiteJsonSerializer round-trip tests pass
- [ ] Registry tests verify proper registration/reset
- [ ] UHttpRequestBuilder integration tests pass
- [ ] No test depends on System.Text.Json availability
- [ ] Tests run in both Edit Mode and Play Mode

## Coverage Goals

| Component | Coverage Target |
|-----------|-----------------|
| JsonReader | 90%+ (all paths) |
| JsonWriter | 90%+ (all types) |
| LiteJsonSerializer | 80%+ (common cases) |
| JsonSerializer (registry) | 100% |
| UHttpRequestBuilder | New methods only |
