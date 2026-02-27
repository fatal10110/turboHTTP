using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Transport;
using System.Collections.Generic;
using System.Buffers;
using System.Text;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class UHttpRequestJsonTests
    {
        private UHttpClient _client;

        [SetUp]
        public void SetUp()
        {
            HttpTransportFactory.Reset();
            RawSocketTransport.EnsureRegistered();
            _client = new UHttpClient(new UHttpClientOptions
            {
                BaseUrl = "https://example.com"
            });
            JsonSerializer.ResetToDefault();
        }

        [TearDown]
        public void TearDown()
        {
            HttpTransportFactory.Reset();
            JsonSerializer.ResetToDefault();
        }

        [Test]
        public void WithJsonBody_String_SetsBodyAndContentType()
        {
            using var request = _client.Post("/api")
                .WithJsonBody("{\"name\":\"test\"}");

            var body = Encoding.UTF8.GetString(request.Body);
            Assert.That(body, Is.EqualTo("{\"name\":\"test\"}"));
            Assert.That(request.Headers.Get("Content-Type"), Is.EqualTo("application/json"));
        }

        [Test]
        public void WithJsonBody_Dictionary_UsesDefaultSerializer()
        {
            var data = new Dictionary<string, object> { { "name", "test" } };
            using var request = _client.Post("/api")
                .WithJsonBody(data);

            var body = Encoding.UTF8.GetString(request.Body);
            Assert.That(body, Does.Contain("name"));
            Assert.That(body, Does.Contain("test"));
        }

        [Test]
        public void WithJsonBody_WithCustomSerializer_UsesCustom()
        {
            var mock = new MockSerializer("{\"custom\":true}");
            var data = new Dictionary<string, object> { { "name", "test" } };
            using var request = _client.Post("/api")
                .WithJsonBody(data, mock);

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
            public void Serialize<T>(T value, IBufferWriter<byte> output)
            {
                var bytes = Encoding.UTF8.GetBytes(_output);
                var span = output.GetSpan(bytes.Length);
                for (int i = 0; i < bytes.Length; i++)
                    span[i] = bytes[i];
                output.Advance(bytes.Length);
            }
            public T Deserialize<T>(ReadOnlySequence<byte> input) => default;
        }
    }
}
