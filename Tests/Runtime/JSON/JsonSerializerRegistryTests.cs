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
    }
}
