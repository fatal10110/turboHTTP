using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using TurboHTTP.JSON;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// <see cref="JsonWebSocketSerializer{T}"/> goes through the registered JSON serializer only; the
    /// reflection mapper (Activator.CreateInstance, MakeGenericType, reflective SetValue) is gone (#192).
    /// </summary>
    [TestFixture]
    public class JsonWebSocketSerializerBridgeTests
    {
        private IJsonSerializer _previous;

        public sealed class NoPublicCtorDto
        {
            public string name;

            private NoPublicCtorDto()
            {
            }
        }

        public sealed class PropertyDto
        {
            public string Room { get; set; }
        }

        [Serializable]
        public sealed class Item
        {
            public string id;
        }

        [Serializable]
        public sealed class ChatDto
        {
            public string room;
            public List<Item> items;
            public Dictionary<string, int> counts;
        }

        [SetUp]
        public void SetUp()
        {
            _previous = JsonSerializer.Default;
            JsonModuleRegistration.EnsureRegistered();
        }

        [TearDown]
        public void TearDown()
        {
            JsonSerializer.Default = _previous;
        }

        [Test]
        public void Deserialize_DtoWithoutPublicParameterlessCtor_FailsWhenSerializerCannotMapIt()
        {
            // The serializer can read the payload as a dictionary but not as the DTO. The removed
            // mapper used to fall back to the dictionary and build the DTO through its private
            // constructor; the documented behaviour now is SerializationFailed, one serializer call.
            var stub = new StubSerializer { DictionaryOnly = true };
            JsonSerializer.SetDefault(stub);
            var serializer = new JsonWebSocketSerializer<NoPublicCtorDto>();

            var ex = Assert.Throws<WebSocketException>(() => serializer.Deserialize(Text("{\"name\":\"x\"}")));

            Assert.That(ex.Error, Is.EqualTo(WebSocketError.SerializationFailed));
            CollectionAssert.AreEqual(new[] { typeof(NoPublicCtorDto) }, stub.DeserializeTypes);
        }

        [Test]
        public void Deserialize_ReturnsTheSerializersInstance_ForNestedListAndDictionaryDto()
        {
            var expected = new ChatDto
            {
                room = "lobby",
                items = new List<Item> { new Item { id = "a" } },
                counts = new Dictionary<string, int> { ["a"] = 1 }
            };
            var stub = new StubSerializer { Result = expected };
            JsonSerializer.SetDefault(stub);

            ChatDto actual = new JsonWebSocketSerializer<ChatDto>().Deserialize(Text("{}"));

            Assert.That(actual, Is.SameAs(expected));
            CollectionAssert.AreEqual(new[] { typeof(ChatDto) }, stub.DeserializeTypes);
        }

        [Test]
        public void Deserialize_JsonNull_ReturnsNull()
        {
            JsonSerializer.SetDefault(JsonUtilitySerializer.Instance);

            Assert.That(new JsonWebSocketSerializer<ChatDto>().Deserialize(Text("null")), Is.Null);
        }

        [Test]
        public void Serialize_SerializerFailure_IsNotRetriedThroughNormalization()
        {
            // Throws for the DTO type only; the removed fallback re-serialized a normalized
            // dictionary and succeeded.
            var stub = new StubSerializer { DictionaryOnly = true };
            JsonSerializer.SetDefault(stub);
            var serializer = new JsonWebSocketSerializer<ChatDto>();

            var ex = Assert.Throws<WebSocketException>(() => serializer.Serialize(new ChatDto { room = "r" }));

            Assert.That(ex.Error, Is.EqualTo(WebSocketError.SerializationFailed));
            CollectionAssert.AreEqual(new[] { typeof(ChatDto) }, stub.SerializeTypes);
        }

        [Test]
        public void RoundTrip_SerializableDto_WithJsonUtilitySerializer()
        {
            JsonSerializer.SetDefault(JsonUtilitySerializer.Instance);
            var serializer = new JsonWebSocketSerializer<ChatDto>();
            var outbound = new ChatDto { room = "lobby", items = new List<Item> { new Item { id = "a" } } };

            ReadOnlyMemory<byte> payload = serializer.Serialize(outbound);
            ChatDto inbound = serializer.Deserialize(Text(Encoding.UTF8.GetString(payload.ToArray())));

            Assert.That(serializer.MessageType, Is.EqualTo(WebSocketOpcode.Text));
            Assert.That(inbound.room, Is.EqualTo("lobby"));
            Assert.That(inbound.items.Count, Is.EqualTo(1));
            Assert.That(inbound.items[0].id, Is.EqualTo("a"));
        }

        [Test]
        public void PropertyOnlyDto_WithJsonUtilitySerializer_FailsInsteadOfSendingEmptyObject()
        {
            // The removed mapper read and wrote properties; JsonUtility skips them, so the type is
            // rejected rather than sent as "{}" or received as defaults.
            JsonSerializer.SetDefault(JsonUtilitySerializer.Instance);
            var serializer = new JsonWebSocketSerializer<PropertyDto>();

            var send = Assert.Throws<WebSocketException>(() => serializer.Serialize(new PropertyDto { Room = "r" }));
            var receive = Assert.Throws<WebSocketException>(() => serializer.Deserialize(Text("{\"Room\":\"r\"}")));

            Assert.That(send.Error, Is.EqualTo(WebSocketError.SerializationFailed));
            Assert.That(receive.Error, Is.EqualTo(WebSocketError.SerializationFailed));
        }

        private static WebSocketMessage Text(string text)
        {
            return new WebSocketMessage(WebSocketMessageType.Text, null, 0, text, returnToPool: false);
        }

        private sealed class StubSerializer : IJsonSerializer
        {
            public bool DictionaryOnly;
            public object Result;
            public readonly List<Type> SerializeTypes = new List<Type>();
            public readonly List<Type> DeserializeTypes = new List<Type>();

            public string Serialize<T>(T value) => Serialize(value, typeof(T));

            public string Serialize(object value, Type type)
            {
                SerializeTypes.Add(type);
                if (DictionaryOnly && type != typeof(Dictionary<string, object>) && type != typeof(object))
                    throw new JsonSerializationException("stub: only dictionaries");
                return "{}";
            }

            public T Deserialize<T>(string json) => (T)Deserialize(json, typeof(T));

            public object Deserialize(string json, Type type)
            {
                DeserializeTypes.Add(type);
                if (DictionaryOnly)
                {
                    if (type == typeof(object) || type == typeof(Dictionary<string, object>))
                        return new Dictionary<string, object> { ["name"] = "x" };
                    throw new JsonSerializationException("stub: only dictionaries");
                }

                return Result;
            }

            public void Serialize<T>(T value, IBufferWriter<byte> output) => throw new NotSupportedException();

            public T Deserialize<T>(ReadOnlySequence<byte> input) => throw new NotSupportedException();
        }
    }
}
