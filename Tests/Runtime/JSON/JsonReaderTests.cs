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

        // === Surrogate Pair Tests ===

        [Test]
        public void Parse_SurrogatePair_DecodesEmoji()
        {
            // 😀 is U+1F600, encoded as \uD83D\uDE00
            var reader = new JsonReader("\"\\uD83D\\uDE00\"");
            var result = reader.Parse() as string;
            Assert.That(result, Is.EqualTo("😀"));
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
