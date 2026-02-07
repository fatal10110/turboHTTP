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

        [Test]
        public void Deserialize_LongToInt_Converts()
        {
            var json = "42";
            var result = _serializer.Deserialize<int>(json);
            Assert.That(result, Is.EqualTo(42));
        }

        [Test]
        public void Deserialize_LongToDouble_Converts()
        {
            var json = "42";
            var result = _serializer.Deserialize<double>(json);
            Assert.That(result, Is.EqualTo(42.0));
        }

        // === P1 Fix: Nullable numeric round-trips ===

        [Test]
        public void Deserialize_NullableInt_FromNumber()
        {
            var result = _serializer.Deserialize<int?>("42");
            Assert.That(result, Is.EqualTo(42));
        }

        [Test]
        public void Deserialize_NullableInt_FromNull()
        {
            var result = _serializer.Deserialize<int?>("null");
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Deserialize_NullableLong_FromNumber()
        {
            var result = _serializer.Deserialize<long?>("9223372036854775807");
            Assert.That(result, Is.EqualTo(long.MaxValue));
        }

        [Test]
        public void Deserialize_NullableDouble_FromNumber()
        {
            var result = _serializer.Deserialize<double?>("3.14");
            Assert.That(result, Is.EqualTo(3.14).Within(0.001));
        }

        [Test]
        public void Deserialize_NullableDecimal_FromNumber()
        {
            var result = _serializer.Deserialize<decimal?>("123.456");
            Assert.That(result, Is.EqualTo(123.456m));
        }

        // === P2 Fix: Non-generic type mismatch ===

        [Test]
        public void Deserialize_NonGeneric_ArrayToDictionary_ThrowsTypeMismatch()
        {
            Assert.That(() => _serializer.Deserialize("[1,2,3]", typeof(Dictionary<string, object>)),
                Throws.TypeOf<JsonSerializationException>()
                    .With.Message.Contains("Expected JSON object but got array"));
        }

        [Test]
        public void Deserialize_NonGeneric_ObjectToList_ThrowsTypeMismatch()
        {
            Assert.That(() => _serializer.Deserialize("{\"a\":1}", typeof(List<object>)),
                Throws.TypeOf<JsonSerializationException>()
                    .With.Message.Contains("Expected JSON array but got object"));
        }

        [Test]
        public void Deserialize_NonGeneric_ValidDictionary_Succeeds()
        {
            var result = _serializer.Deserialize("{\"a\":1}", typeof(Dictionary<string, object>));
            Assert.That(result, Is.TypeOf<Dictionary<string, object>>());
            Assert.That(((Dictionary<string, object>)result)["a"], Is.EqualTo(1L));
        }

        [Test]
        public void Deserialize_NonGeneric_ValidList_Succeeds()
        {
            var result = _serializer.Deserialize("[1,2,3]", typeof(List<object>));
            Assert.That(result, Is.TypeOf<List<object>>());
            Assert.That(((List<object>)result).Count, Is.EqualTo(3));
        }
    }
}
