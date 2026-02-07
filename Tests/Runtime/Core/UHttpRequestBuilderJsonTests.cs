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
