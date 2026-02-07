using NUnit.Framework;
using TurboHTTP.JSON;
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

        // === P3 Fix: ulong-backed enum serialization ===

        [Test]
        public void Write_UlongBackedEnum_DoesNotOverflow()
        {
            var writer = new JsonWriter();
            var json = writer.Write(ULongEnum.MaxValue);
            Assert.That(json, Is.EqualTo("18446744073709551615"));
        }

        [Test]
        public void Write_RegularEnum_StillWorks()
        {
            var writer = new JsonWriter();
            var json = writer.Write(RegularEnum.Value);
            Assert.That(json, Is.EqualTo("42"));
        }

        private class CustomClass { }

        private enum ULongEnum : ulong
        {
            MaxValue = ulong.MaxValue
        }

        private enum RegularEnum : int
        {
            Value = 42
        }
    }
}
