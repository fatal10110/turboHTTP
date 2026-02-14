using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    public class JsonExtensionsTests
    {
        // --- AsJson<T> ---

        [Test]
        public void AsJson_ValidJson_Deserializes()
        {
            var json = "{\"Name\":\"Alice\",\"Age\":30}";
            var body = Encoding.UTF8.GetBytes(json);
            var response = MakeResponse(HttpStatusCode.OK, body);

            var result = response.AsJson<Dictionary<string, object>>();

            Assert.IsNotNull(result);
            Assert.AreEqual("Alice", result["Name"].ToString());
        }

        [Test]
        public void AsJson_NullBody_ReturnsDefault()
        {
            var response = MakeResponse(HttpStatusCode.OK, null);

            var result = response.AsJson<Dictionary<string, object>>();

            Assert.IsNull(result);
        }

        [Test]
        public void AsJson_EmptyBody_ReturnsDefault()
        {
            var response = MakeResponse(HttpStatusCode.OK, Array.Empty<byte>());

            var result = response.AsJson<Dictionary<string, object>>();

            Assert.IsNull(result);
        }

        [Test]
        public void AsJson_InvalidJson_ThrowsSerializationException()
        {
            var body = Encoding.UTF8.GetBytes("not json at all");
            var response = MakeResponse(HttpStatusCode.OK, body);

            Assert.Throws<TurboHTTP.JSON.JsonSerializationException>(
                () => response.AsJson<Dictionary<string, object>>());
        }

        [Test]
        public void AsJson_NullResponse_ThrowsArgumentNull()
        {
            UHttpResponse response = null;
            Assert.Throws<ArgumentNullException>(() => response.AsJson<string>());
        }

        // --- TryAsJson<T> ---

        [Test]
        public void TryAsJson_ValidJson_ReturnsTrueAndValue()
        {
            var json = "{\"Key\":\"Value\"}";
            var body = Encoding.UTF8.GetBytes(json);
            var response = MakeResponse(HttpStatusCode.OK, body);

            var success = response.TryAsJson<Dictionary<string, object>>(out var result);

            Assert.IsTrue(success);
            Assert.IsNotNull(result);
            Assert.AreEqual("Value", result["Key"].ToString());
        }

        [Test]
        public void TryAsJson_InvalidJson_ReturnsFalse()
        {
            var body = Encoding.UTF8.GetBytes("<<<not json>>>");
            var response = MakeResponse(HttpStatusCode.OK, body);

            var success = response.TryAsJson<Dictionary<string, object>>(out var result);

            Assert.IsFalse(success);
            Assert.IsNull(result);
        }

        [Test]
        public void TryAsJson_EmptyBody_ReturnsFalse()
        {
            var response = MakeResponse(HttpStatusCode.OK, Array.Empty<byte>());

            var success = response.TryAsJson<Dictionary<string, object>>(out var result);

            Assert.IsFalse(success);
        }

        // --- GetBodyAsString(Encoding) ---

        [Test]
        public void GetBodyAsString_WithEncoding_DecodesCorrectly()
        {
            var text = "Hello World";
            var body = Encoding.ASCII.GetBytes(text);
            var response = MakeResponse(HttpStatusCode.OK, body);

            var result = response.GetBodyAsString(Encoding.ASCII);

            Assert.AreEqual("Hello World", result);
        }

        [Test]
        public void GetBodyAsString_NullEncoding_ThrowsArgumentNull()
        {
            var response = MakeResponse(HttpStatusCode.OK, new byte[] { 0x41 });

            Assert.Throws<ArgumentNullException>(() => response.GetBodyAsString(null));
        }

        [Test]
        public void GetBodyAsString_NullBody_ReturnsNull()
        {
            var response = MakeResponse(HttpStatusCode.OK, null);

            Assert.IsNull(response.GetBodyAsString(Encoding.UTF8));
        }

        // --- GetContentEncoding ---

        [Test]
        public void GetContentEncoding_WithCharset_ReturnsEncoding()
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "text/html; charset=utf-8");
            var response = MakeResponse(HttpStatusCode.OK, null, headers);

            var encoding = response.GetContentEncoding();

            Assert.AreEqual(Encoding.UTF8, encoding);
        }

        [Test]
        public void GetContentEncoding_NoCharset_FallsBackToUtf8()
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "application/json");
            var response = MakeResponse(HttpStatusCode.OK, null, headers);

            var encoding = response.GetContentEncoding();

            Assert.AreEqual(Encoding.UTF8, encoding);
        }

        [Test]
        public void GetContentEncoding_NoContentType_FallsBackToUtf8()
        {
            var response = MakeResponse(HttpStatusCode.OK, null);

            var encoding = response.GetContentEncoding();

            Assert.AreEqual(Encoding.UTF8, encoding);
        }

        [Test]
        public void GetContentEncoding_UnknownCharset_FallsBackToUtf8()
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "text/html; charset=totally-fake-encoding");
            var response = MakeResponse(HttpStatusCode.OK, null, headers);

            var encoding = response.GetContentEncoding();

            Assert.AreEqual(Encoding.UTF8, encoding);
        }

        // --- JSON round-trip via WithJsonBody + AsJson ---

        [Test]
        public void JsonRoundTrip_WithJsonBody_ThenAsJson_ProducesEquivalentObject()
        {
            Task.Run(async () =>
            {
                // Simulate a JSON round-trip: serialize with WithJsonBody, capture body,
                // feed it into a response, and deserialize with AsJson
                var original = new Dictionary<string, object>
                {
                    { "Name", "Bob" },
                    { "Count", 42 }
                };

                // Capture the serialized body via MockTransport
                byte[] capturedBody = null;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    capturedBody = req.Body;
                    var respHeaders = new HttpHeaders();
                    respHeaders.Set("Content-Type", "application/json");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, respHeaders, capturedBody, ctx.Elapsed, req));
                });

                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                var response = await client.Post("https://test.com/echo")
                    .WithJsonBody(original)
                    .SendAsync();

                var deserialized = response.AsJson<Dictionary<string, object>>();

                Assert.IsNotNull(deserialized);
                Assert.AreEqual("Bob", deserialized["Name"].ToString());
                Assert.AreEqual("42", deserialized["Count"].ToString());
            }).GetAwaiter().GetResult();
        }

        // --- GetJsonAsync / PostJsonAsync via MockTransport ---

        [Test]
        public void GetJsonAsync_ReturnsDeserialized()
        {
            Task.Run(async () =>
            {
                var json = "{\"Id\":1,\"Title\":\"Test\"}";
                var body = Encoding.UTF8.GetBytes(json);
                var transport = new MockTransport(HttpStatusCode.OK, body: body);
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });

                var result = await client.GetJsonAsync<Dictionary<string, object>>(
                    "https://test.com/api");

                Assert.IsNotNull(result);
                Assert.AreEqual("Test", result["Title"].ToString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void GetJsonAsync_NonSuccess_Throws()
        {
            AssertAsync.ThrowsAsync<UHttpException>(() =>
            {
                return Task.Run(async () =>
                {
                    var transport = new MockTransport(HttpStatusCode.NotFound);
                    var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                    await client.GetJsonAsync<Dictionary<string, object>>(
                        "https://test.com/missing");
                });
            });
        }

        [Test]
        public void PostJsonAsync_SendsAndDeserializes()
        {
            Task.Run(async () =>
            {
                var responseJson = "{\"Id\":99,\"Created\":true}";
                var responseBody = Encoding.UTF8.GetBytes(responseJson);
                var transport = new MockTransport(
                    HttpStatusCode.Created, body: responseBody);
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });

                var input = new Dictionary<string, object>
                {
                    { "Name", "NewItem" }
                };
                var result = await client.PostJsonAsync<
                    Dictionary<string, object>,
                    Dictionary<string, object>>(
                    "https://test.com/api", input);

                Assert.IsNotNull(result);
                Assert.AreEqual("99", result["Id"].ToString());

                // Verify request was sent with JSON content type
                Assert.AreEqual("application/json",
                    transport.LastRequest.Headers.Get("Content-Type"));
            }).GetAwaiter().GetResult();
        }

        // --- Helper ---

        private static UHttpResponse MakeResponse(
            HttpStatusCode status, byte[] body, HttpHeaders headers = null)
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            return new UHttpResponse(
                status, headers ?? new HttpHeaders(), body, TimeSpan.Zero, request);
        }
    }
}
