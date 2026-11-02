using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.JSON.Lite;

namespace TurboHTTP.Tests.JSON
{
    /// <summary>
    /// The README Quick Start (<c>[Serializable] MyData</c> + <c>AsJson&lt;MyData&gt;()</c>) works
    /// with the serializer the startup bootstrap installs (#271).
    /// </summary>
    [TestFixture]
    public class JsonUtilitySerializerTests
    {
        private IJsonSerializer _previous;

        [Serializable]
        public class MyData
        {
            public int id;
            public string name;
        }

        [Serializable]
        public class Inner
        {
            public double score;
        }

        [Serializable]
        public class Envelope
        {
            public string room;
            public List<string> tags;
            public Inner inner;
        }

        public class PropertyOnlyDto
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        [Serializable]
        public class PrivateSerializeFieldDto
        {
            [UnityEngine.SerializeField]
            private int value;

            public int Value => value;
        }

        [Serializable]
        public struct Point
        {
            public int x;
            public int y;
        }

        [SetUp]
        public void SetUp()
        {
            _previous = JsonSerializer.Default;
            // Start from the state a fresh player has before the bootstrap runs.
            JsonSerializer.ResetToDefault();
        }

        [TearDown]
        public void TearDown()
        {
            JsonSerializer.Default = _previous;
            JsonModuleRegistration.EnsureRegistered();
        }

        [Test]
        public void ReadmeQuickStart_AsJsonMyData_WorksWithStartupRegistration()
        {
            TurboHttpBootstrap.RegisterJsonBridge();
            var response = MakeResponse("{\"id\":7,\"name\":\"seven\"}");

            MyData data = response.AsJson<MyData>();

            Assert.That(data, Is.Not.Null);
            Assert.That(data.id, Is.EqualTo(7));
            Assert.That(data.name, Is.EqualTo("seven"));
        }

        [Test]
        public void StartupRegistration_InstallsJsonUtility_OnlyOverTheBuiltInDefault()
        {
            TurboHttpBootstrap.RegisterJsonBridge();
            Assert.That(JsonSerializer.Default, Is.SameAs(JsonUtilitySerializer.Instance));

            var custom = new LiteJsonWrapper();
            JsonSerializer.SetDefault(custom);
            TurboHttpBootstrap.RegisterJsonBridge();

            Assert.That(JsonSerializer.Default, Is.SameAs(custom), "a serializer the project set first is kept");
        }

        [Test]
        public void Poco_RoundTrips_WithNestedListAndObject()
        {
            var value = new Envelope
            {
                room = "lobby",
                tags = new List<string> { "a", "b" },
                inner = new Inner { score = 1.5 }
            };

            string json = JsonUtilitySerializer.Instance.Serialize(value);
            var back = JsonUtilitySerializer.Instance.Deserialize<Envelope>(json);

            Assert.That(back.room, Is.EqualTo("lobby"));
            CollectionAssert.AreEqual(new[] { "a", "b" }, back.tags);
            Assert.That(back.inner.score, Is.EqualTo(1.5));
        }

        [Test]
        public void TopLevelArray_DeserializesToArray_AndSerializesBack()
        {
            const string json = "[{\"id\":1,\"name\":\"a\"},null,{\"id\":2,\"name\":\"b\"}]";

            var items = JsonUtilitySerializer.Instance.Deserialize<MyData[]>(json);

            Assert.That(items.Length, Is.EqualTo(3));
            Assert.That(items[0].id, Is.EqualTo(1));
            Assert.That(items[1], Is.Null);
            Assert.That(items[2].name, Is.EqualTo("b"));
            Assert.That(JsonUtilitySerializer.Instance.Serialize(items), Is.EqualTo(json));
        }

        [Test]
        public void BuiltInShapes_GoThroughLiteJson()
        {
            var map = JsonUtilitySerializer.Instance.Deserialize<Dictionary<string, object>>("{\"a\":1,\"b\":[true]}");
            Assert.That(Convert.ToInt32(map["a"]), Is.EqualTo(1));
            Assert.That(map["b"], Is.InstanceOf<List<object>>());

            Assert.That(JsonUtilitySerializer.Instance.Deserialize<int>("42"), Is.EqualTo(42));
            Assert.That(JsonUtilitySerializer.Instance.Deserialize<string>("\"x\""), Is.EqualTo("x"));
            Assert.That(JsonUtilitySerializer.Instance.Deserialize(" null ", typeof(MyData)), Is.Null);

            var mixed = new Dictionary<string, object> { ["user"] = new MyData { id = 3, name = "c" }, ["n"] = 2 };
            Assert.That(JsonUtilitySerializer.Instance.Serialize(mixed),
                Is.EqualTo("{\"user\":{\"id\":3,\"name\":\"c\"},\"n\":2}"));
        }

        [Test]
        public void TopLevelListOfPoco_ThrowsSerializationException()
        {
            Assert.That(() => JsonUtilitySerializer.Instance.Deserialize<List<MyData>>("[{\"id\":1}]"),
                Throws.TypeOf<JsonSerializationException>().With.Message.Contains("T[]"));
        }

        [Test]
        public void AnonymousType_ThrowsInsteadOfWritingEmptyObject()
        {
            Assert.That(() => JsonUtilitySerializer.Instance.Serialize(new { Name = "Alice" }),
                Throws.TypeOf<JsonSerializationException>().With.Message.Contains("Anonymous"));
        }

        [Test]
        public void PropertyOnlyDto_ThrowsInBothDirections_InsteadOfLosingData()
        {
            // JsonUtility writes "{}" and reads an all-defaults instance for this type, silently.
            Assert.That(() => JsonUtilitySerializer.Instance.Serialize(new PropertyOnlyDto { Id = 1, Name = "a" }),
                Throws.TypeOf<JsonSerializationException>().With.Message.Contains("properties"));
            Assert.That(() => JsonUtilitySerializer.Instance.Deserialize<PropertyOnlyDto>("{\"Id\":1}"),
                Throws.TypeOf<JsonSerializationException>().With.Message.Contains("properties"));
        }

        [Test]
        public void PrivateSerializeField_CountsAsSerializable()
        {
            var dto = JsonUtilitySerializer.Instance.Deserialize<PrivateSerializeFieldDto>("{\"value\":7}");

            Assert.That(dto.Value, Is.EqualTo(7));
        }

        [Test]
        public void NullableStruct_JsonNull_IsNull_AndValueParses()
        {
            Assert.That(JsonUtilitySerializer.Instance.Deserialize<Point?>("null"), Is.Null);

            Point? point = JsonUtilitySerializer.Instance.Deserialize<Point?>("{\"x\":1,\"y\":2}");

            Assert.That(point.HasValue, Is.True);
            Assert.That(point.Value.x, Is.EqualTo(1));
            Assert.That(point.Value.y, Is.EqualTo(2));
        }

        [Test]
        public void InvalidJson_ThrowsSerializationException()
        {
            Assert.That(() => JsonUtilitySerializer.Instance.Deserialize<MyData>("not json"),
                Throws.TypeOf<JsonSerializationException>());
            Assert.That(() => JsonUtilitySerializer.Instance.Deserialize<MyData>(""),
                Throws.TypeOf<JsonSerializationException>());
        }

        [Test]
        public void BufferOverloads_RoundTrip()
        {
            using var writer = new TurboHTTP.Core.Internal.PooledArrayBufferWriter();
            JsonUtilitySerializer.Instance.Serialize(new MyData { id = 5, name = "é" }, writer);

            var back = JsonUtilitySerializer.Instance.Deserialize<MyData>(
                new ReadOnlySequence<byte>(writer.WrittenMemory));

            Assert.That(back.id, Is.EqualTo(5));
            Assert.That(back.name, Is.EqualTo("é"));
        }

        [Test]
        public void Deserialize_OffTheMainThread_Works()
        {
            AssertAsync.Run(async () =>
            {
                MyData data = await Task.Run(
                    () => JsonUtilitySerializer.Instance.Deserialize<MyData>("{\"id\":9,\"name\":\"bg\"}"));
                string json = await Task.Run(() => JsonUtilitySerializer.Instance.Serialize(data));

                Assert.That(data.id, Is.EqualTo(9));
                Assert.That(json, Is.EqualTo("{\"id\":9,\"name\":\"bg\"}"));
            });
        }

        private static UHttpResponse MakeResponse(string json)
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            return new UHttpResponse(
                HttpStatusCode.OK, new HttpHeaders(), Encoding.UTF8.GetBytes(json), TimeSpan.Zero, request);
        }

        private sealed class LiteJsonWrapper : IJsonSerializer
        {
            public string Serialize<T>(T value) => LiteJsonSerializer.Instance.Serialize(value);
            public string Serialize(object value, Type type) => LiteJsonSerializer.Instance.Serialize(value, type);
            public T Deserialize<T>(string json) => LiteJsonSerializer.Instance.Deserialize<T>(json);
            public object Deserialize(string json, Type type) => LiteJsonSerializer.Instance.Deserialize(json, type);
            public void Serialize<T>(T value, IBufferWriter<byte> output) => LiteJsonSerializer.Instance.Serialize(value, output);
            public T Deserialize<T>(ReadOnlySequence<byte> input) => LiteJsonSerializer.Instance.Deserialize<T>(input);
        }
    }
}
