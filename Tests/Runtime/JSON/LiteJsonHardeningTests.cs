using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.JSON.Lite;

namespace TurboHTTP.Tests.JSON
{
    /// <summary>
    /// LiteJson hardening (#75): unsupported (POCO) targets, BOM, UTC timestamps, strict RFC 8259
    /// grammar, the input-size cap and exact 64-bit integers.
    /// </summary>
    [TestFixture]
    public class LiteJsonHardeningTests
    {
        private static LiteJsonSerializer Lite => LiteJsonSerializer.Instance;

        // ── POCO / unsupported targets ──────────────────────────────────────

        private sealed class Poco
        {
            public int id;
            public string name;
        }

        private struct PocoStruct
        {
            public int x;
        }

        private static void AssertNotSupported(TestDelegate action)
        {
            var ex = Assert.Throws<NotSupportedException>(action);
            StringAssert.Contains("System.Text.Json", ex.Message);
            StringAssert.Contains("JsonUtility", ex.Message);
        }

        [Test]
        public void AsJson_Poco_ThrowsNotSupportedNamingAlternatives()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("http://localhost/poco"));
            using (var response = new UHttpResponse(
                       HttpStatusCode.OK,
                       new HttpHeaders(),
                       Encoding.UTF8.GetBytes("{\"id\":1,\"name\":\"a\"}"),
                       TimeSpan.Zero,
                       request))
            {
                AssertNotSupported(() => response.AsJson<Poco>(Lite));
            }
        }

        [Test]
        public void Deserialize_UnsupportedTargets_ThrowNotSupported()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("{\"id\":1}");
            AssertNotSupported(() => Lite.Deserialize<Poco>("{\"id\":1}"));
            AssertNotSupported(() => Lite.Deserialize<Poco>(new ReadOnlySequence<byte>(bytes)));
            AssertNotSupported(() => Lite.Deserialize("{\"id\":1}", typeof(Poco)));
            AssertNotSupported(() => Lite.Deserialize<PocoStruct>("{\"x\":1}"));
            AssertNotSupported(() => Lite.Deserialize<PocoStruct?>("{\"x\":1}"));
            AssertNotSupported(() => Lite.Deserialize<List<int>>("[1,2]"));
            AssertNotSupported(() => Lite.Deserialize<Dictionary<string, string>>("{\"a\":\"b\"}"));
        }

        [Test]
        public void Serialize_Poco_ThrowsNotSupported()
        {
            AssertNotSupported(() => Lite.Serialize(new Poco { id = 1 }));
            AssertNotSupported(() => Lite.Serialize(new Dictionary<string, object> { ["user"] = new Poco() }));
        }

        [Test]
        public void Deserialize_BuiltInShapes_StillWork()
        {
            Assert.That(Lite.Deserialize<IDictionary<string, object>>("{\"a\":1}")["a"], Is.EqualTo(1L));
            Assert.That(Lite.Deserialize<IReadOnlyList<object>>("[1]").Count, Is.EqualTo(1));
            Assert.That(Lite.Deserialize<object>("\"s\""), Is.EqualTo("s"));
            Assert.That(Lite.Deserialize<Guid>("\"6f9619ff-8b86-d011-b42d-00cf4fc964ff\""),
                Is.EqualTo(new Guid("6f9619ff-8b86-d011-b42d-00cf4fc964ff")));
            Assert.That(Lite.Deserialize<Uri>("\"http://x.test/\""), Is.EqualTo(new Uri("http://x.test/")));
        }

        // ── BOM (RFC 8259 s8.1: parsers MAY ignore it) ──────────────────────

        [Test]
        public void Deserialize_Utf8BomBytes_ParsesObject()
        {
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' };
            var result = Lite.Deserialize<Dictionary<string, object>>(new ReadOnlySequence<byte>(bytes));
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void Deserialize_LeadingBomChar_Parses()
        {
            Assert.That(Lite.Deserialize<List<object>>("\uFEFF[1]"), Has.Count.EqualTo(1));
            Assert.That(new JsonReader("\uFEFF{}").Parse(), Is.InstanceOf<Dictionary<string, object>>());
        }

        [TestCase("[1,\uFEFF2]")]
        [TestCase(" \uFEFF{}")]
        [TestCase("{}\uFEFF")]
        public void Reader_BomOutsideTheStart_Throws(string json)
        {
            Assert.Throws<JsonParseException>(() => new JsonReader(json).Parse());
        }

        // ── Timestamps ──────────────────────────────────────────────────────

        [Test]
        public void Deserialize_ZuluTimestamp_IsUtcWithSameInstant()
        {
            var value = Lite.Deserialize<DateTime>("\"2024-01-01T00:00:00Z\"");
            Assert.That(value.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(value, Is.EqualTo(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(value.Ticks, Is.EqualTo(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks));
        }

        [Test]
        public void Deserialize_NullableZuluTimestamp_IsUtc()
        {
            DateTime? value = Lite.Deserialize<DateTime?>("\"2024-01-01T00:00:00Z\"");
            Assert.That(value.HasValue, Is.True);
            Assert.That(value.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
        }

        [Test]
        public void Deserialize_TimestampWithoutZone_IsUnspecified()
        {
            var value = Lite.Deserialize<DateTime>("\"2024-01-01T12:30:00\"");
            Assert.That(value.Kind, Is.EqualTo(DateTimeKind.Unspecified));
            Assert.That(value.Hour, Is.EqualTo(12));
        }

        [Test]
        public void UtcDateTime_RoundTripsWithKind()
        {
            var original = new DateTime(2024, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc);
            var back = Lite.Deserialize<DateTime>(Lite.Serialize(original));
            Assert.That(back.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(back.Ticks, Is.EqualTo(original.Ticks));
        }

        // ── Strict RFC 8259 grammar ─────────────────────────────────────────

        [TestCase("1.", TestName = "Strict_FractionWithoutDigits")]
        [TestCase("1.e5", TestName = "Strict_FractionWithoutDigitsBeforeExponent")]
        [TestCase(".5", TestName = "Strict_LeadingDot")]
        [TestCase("01", TestName = "Strict_LeadingZero")]
        [TestCase("-01", TestName = "Strict_NegativeLeadingZero")]
        [TestCase("[00]", TestName = "Strict_DoubleZero")]
        [TestCase("-", TestName = "Strict_LoneMinus")]
        [TestCase("-a", TestName = "Strict_MinusWithoutDigit")]
        [TestCase("1e", TestName = "Strict_ExponentWithoutDigits")]
        [TestCase("1e+", TestName = "Strict_SignedExponentWithoutDigits")]
        [TestCase("+1", TestName = "Strict_PlusSign")]
        [TestCase("\u0661", TestName = "Strict_NonAsciiDigit")]
        [TestCase("1\u0661", TestName = "Strict_TrailingNonAsciiDigit")]
        [TestCase("\"a\u0001b\"", TestName = "Strict_RawControlCharInString")]
        [TestCase("\"a\tb\"", TestName = "Strict_RawTabInString")]
        [TestCase("\"a\nb\"", TestName = "Strict_RawNewlineInString")]
        [TestCase("\"\u001f\"", TestName = "Strict_RawUnitSeparatorInString")]
        [TestCase("\u00A0[1]", TestName = "Strict_NbspLeadingWhitespace")]
        [TestCase("[1,\u00A02]", TestName = "Strict_NbspInsideArray")]
        [TestCase("{\"a\":1}\u00A0", TestName = "Strict_NbspTrailing")]
        [TestCase("\u2003{}", TestName = "Strict_EmSpace")]
        [TestCase("\u000B1", TestName = "Strict_VerticalTab")]
        [TestCase("\u000C1", TestName = "Strict_FormFeed")]
        [TestCase("\u00851", TestName = "Strict_NextLine")]
        [TestCase("\"\\u 123\"", TestName = "Strict_HexEscapeLeadingSpace")]
        [TestCase("\"\\u123 \"", TestName = "Strict_HexEscapeTrailingSpace")]
        [TestCase("\"\\u12G4\"", TestName = "Strict_HexEscapeNonHex")]
        public void Strict_InvalidGrammar_Throws(string json)
        {
            Assert.Throws<JsonParseException>(() => new JsonReader(json).Parse(), "reader");

            var ex = Assert.Throws<JsonSerializationException>(() => Lite.Deserialize<object>(json), "serializer");
            Assert.That(ex.InnerException, Is.InstanceOf<JsonParseException>());
        }

        [TestCase("0", 0L)]
        [TestCase("-0", 0L)]
        [TestCase("10", 10L)]
        [TestCase("-120", -120L)]
        public void Strict_ValidIntegers_Parse(string json, long expected)
        {
            Assert.That(new JsonReader(json).Parse(), Is.EqualTo(expected));
        }

        [TestCase("0.5", 0.5)]
        [TestCase("-0.5e-3", -0.0005)]
        [TestCase("1E+2", 100.0)]
        [TestCase("1e2", 100.0)]
        [TestCase("0e0", 0.0)]
        [TestCase("10.25", 10.25)]
        public void Strict_ValidFloats_Parse(string json, double expected)
        {
            Assert.That(new JsonReader(json).Parse(), Is.EqualTo(expected));
        }

        [Test]
        public void Strict_RfcWhitespaceAndEscapes_Accepted()
        {
            var list = (List<object>)new JsonReader(" \t\r\n[ 1 ,\t2\r\n]\n ").Parse();
            Assert.That(list, Is.EqualTo(new object[] { 1L, 2L }));
            Assert.That(new JsonReader("\"\\u00e9\\u00A0x\"").Parse(), Is.EqualTo("\u00e9\u00a0x"));
            Assert.That(new JsonReader("\"\u00A0\u2003\"").Parse(), Is.EqualTo("\u00A0\u2003"));
            Assert.That(new JsonReader("\"\\t\\n\"").Parse(), Is.EqualTo("\t\n"));
        }

        // ── MaxInputBytes ───────────────────────────────────────────────────

        [Test]
        public void MaxInputBytes_DefaultIsDocumentedValue()
        {
            Assert.That(LiteJsonSerializer.DefaultMaxInputBytes, Is.EqualTo(16 * 1024 * 1024));
            Assert.That(Lite.MaxInputBytes, Is.EqualTo(LiteJsonSerializer.DefaultMaxInputBytes));
            Assert.That(new LiteJsonSerializer().MaxInputBytes, Is.EqualTo(LiteJsonSerializer.DefaultMaxInputBytes));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void MaxInputBytes_NonPositive_Rejected(int limit)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LiteJsonSerializer(limit));
        }

        [Test]
        public void MaxInputBytes_BytesAtLimit_Parse()
        {
            var serializer = new LiteJsonSerializer(8);
            var bytes = Encoding.UTF8.GetBytes("[1,2,34]");
            Assert.That(bytes.Length, Is.EqualTo(8));
            Assert.That(serializer.Deserialize<List<object>>(new ReadOnlySequence<byte>(bytes)), Has.Count.EqualTo(3));
        }

        [TestCase("[1,2,345]", TestName = "MaxInputBytes_ValidJsonOverLimit_Throws")]
        [TestCase("{{{{{{{{{", TestName = "MaxInputBytes_InvalidJsonOverLimit_ThrowsBeforeParsing")]
        public void MaxInputBytes_LimitPlusOneBytes_ThrowsBeforeParsing(string json)
        {
            var serializer = new LiteJsonSerializer(8);
            var bytes = Encoding.UTF8.GetBytes(json);
            Assert.That(bytes.Length, Is.EqualTo(9));

            var fromBytes = Assert.Throws<JsonSerializationException>(
                () => serializer.Deserialize<object>(new ReadOnlySequence<byte>(bytes)));
            StringAssert.Contains("MaxInputBytes", fromBytes.Message);
            Assert.That(fromBytes.InnerException, Is.Null, "size is checked before the parser runs");

            var fromString = Assert.Throws<JsonSerializationException>(() => serializer.Deserialize<object>(json));
            StringAssert.Contains("MaxInputBytes", fromString.Message);
            Assert.That(fromString.InnerException, Is.Null);

            var nonGeneric = Assert.Throws<JsonSerializationException>(() => serializer.Deserialize(json, typeof(object)));
            StringAssert.Contains("MaxInputBytes", nonGeneric.Message);
        }

        [Test]
        public void MaxInputBytes_StringInputCountsUtf8Bytes()
        {
            var serializer = new LiteJsonSerializer(8);
            // "\"ééé\"" is 5 chars / 8 UTF-8 bytes; "\"éééé\"" is 6 chars / 10 UTF-8 bytes.
            Assert.That(serializer.Deserialize<string>("\"\u00e9\u00e9\u00e9\""), Is.EqualTo("\u00e9\u00e9\u00e9"));
            var ex = Assert.Throws<JsonSerializationException>(
                () => serializer.Deserialize<string>("\"\u00e9\u00e9\u00e9\u00e9\""));
            StringAssert.Contains("MaxInputBytes", ex.Message);
        }

        [Test]
        public void MaxInputBytes_BomCountsTowardsTheLimit()
        {
            var serializer = new LiteJsonSerializer(4);
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' };
            Assert.Throws<JsonSerializationException>(
                () => serializer.Deserialize<object>(new ReadOnlySequence<byte>(bytes)));
        }

        // ── 64-bit integer precision ────────────────────────────────────────

        [TestCase(9007199254740993L)]
        [TestCase(-9007199254740993L)]
        [TestCase(long.MaxValue)]
        [TestCase(long.MinValue)]
        public void Int64_AboveDoublePrecision_RoundTripsExactly(long value)
        {
            string json = Lite.Serialize(value);
            Assert.That(json, Is.EqualTo(value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That(Lite.Deserialize<long>(json), Is.EqualTo(value));
            Assert.That(Lite.Deserialize<object>(json), Is.EqualTo(value));

            var dict = Lite.Deserialize<Dictionary<string, object>>(
                Lite.Serialize(new Dictionary<string, object> { ["v"] = value }));
            Assert.That(dict["v"], Is.EqualTo(value));
        }

        [TestCase(9007199254740993UL)]
        [TestCase(9223372036854775808UL)]
        [TestCase(ulong.MaxValue)]
        public void UInt64_AboveDoublePrecision_RoundTripsExactly(ulong value)
        {
            string json = Lite.Serialize(value);
            Assert.That(json, Is.EqualTo(value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That(Lite.Deserialize<ulong>(json), Is.EqualTo(value));

            var list = Lite.Deserialize<List<object>>("[" + json + "]");
            Assert.That(Convert.ToUInt64(list[0], System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(value));
        }
    }
}
