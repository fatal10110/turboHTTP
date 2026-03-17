using System;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Transport;

namespace TurboHTTP.Tests.Core
{
    public partial class UHttpClientTests
    {
        [Test]
        public void Constructor_WithNullOptions_UsesDefaults()
        {
            using var client = new UHttpClient(null);
            Assert.IsNotNull(client);
        }

        [Test]
        public void Constructor_WithDefaultOptions_Succeeds()
        {
            using var client = new UHttpClient(new UHttpClientOptions());
            Assert.IsNotNull(client);
        }

        [Test]
        public void Get_ReturnsBuilder()
        {
            using var client = new UHttpClient();
            var builder = client.Get("http://example.com/");
            Assert.IsNotNull(builder);
            Assert.AreEqual(HttpMethod.GET, builder.Method);
        }

        [Test]
        public void Post_ReturnsBuilder()
        {
            using var client = new UHttpClient();
            var builder = client.Post("http://example.com/");
            Assert.IsNotNull(builder);
            Assert.AreEqual(HttpMethod.POST, builder.Method);
        }

        [Test]
        public void Put_ReturnsBuilder()
        {
            using var client = new UHttpClient();
            var builder = client.Put("http://example.com/");
            Assert.IsNotNull(builder);
            Assert.AreEqual(HttpMethod.PUT, builder.Method);
        }

        [Test]
        public void Delete_ReturnsBuilder()
        {
            using var client = new UHttpClient();
            var builder = client.Delete("http://example.com/");
            Assert.IsNotNull(builder);
            Assert.AreEqual(HttpMethod.DELETE, builder.Method);
        }

        [Test]
        public void Patch_ReturnsBuilder()
        {
            using var client = new UHttpClient();
            var builder = client.Patch("http://example.com/");
            Assert.IsNotNull(builder);
            Assert.AreEqual(HttpMethod.PATCH, builder.Method);
        }

        [Test]
        public void Head_ReturnsBuilder()
        {
            using var client = new UHttpClient();
            var builder = client.Head("http://example.com/");
            Assert.IsNotNull(builder);
            Assert.AreEqual(HttpMethod.HEAD, builder.Method);
        }

        [Test]
        public void Options_ReturnsBuilder()
        {
            using var client = new UHttpClient();
            var builder = client.Options("http://example.com/");
            Assert.IsNotNull(builder);
            Assert.AreEqual(HttpMethod.OPTIONS, builder.Method);
        }

        [Test]
        public void RequestBuilder_WithRelativeUrl_ResolvesAgainstBaseUrl()
        {
            using var client = new UHttpClient(new UHttpClientOptions { BaseUrl = "https://example.com/api" });
            var request = client.Get("users");
            Assert.AreEqual("https://example.com/api/users", request.Uri.ToString());
        }

        [Test]
        public void RequestBuilder_WithAbsoluteUrl_IgnoresBaseUrl()
        {
            using var client = new UHttpClient(new UHttpClientOptions { BaseUrl = "https://example.com/api" });
            var request = client.Get("https://other.com/path");
            Assert.AreEqual("https://other.com/path", request.Uri.ToString());
        }

        [Test]
        public void RequestBuilder_WithRelativeUrl_NoBaseUrl_ThrowsInvalidOperationException()
        {
            using var client = new UHttpClient(new UHttpClientOptions());
            Assert.Throws<InvalidOperationException>(() => client.Get("users"));
        }

        [Test]
        public void RequestBuilder_MergesDefaultHeaders_WithRequestHeaders()
        {
            var options = new UHttpClientOptions();
            options.DefaultHeaders.Set("X-Default", "A");
            options.DefaultHeaders.Set("X-Override", "Default");

            using var client = new UHttpClient(options);
            var request = client.Get("http://example.com/")
                .WithHeader("X-Override", "Request");

            Assert.AreEqual("A", request.Headers.Get("X-Default"));
            Assert.AreEqual("Request", request.Headers.Get("X-Override"));
        }

        [Test]
        public void RequestBuilder_MultiValueHeaders_AllValuesCopied()
        {
            var headers = new HttpHeaders();
            headers.Add("Set-Cookie", "a=1");
            headers.Add("Set-Cookie", "b=2");

            using var client = new UHttpClient();
            var request = client.Get("http://example.com/")
                .WithHeaders(headers);

            var values = request.Headers.GetValues("Set-Cookie");
            Assert.AreEqual(2, values.Count);
            Assert.AreEqual("a=1", values[0]);
            Assert.AreEqual("b=2", values[1]);
        }

        [Test]
        public void RequestBuilder_WithJsonBody_SetsContentTypeAndBody()
        {
            using var client = new UHttpClient();
            var payload = new System.Collections.Generic.Dictionary<string, object>
            {
                ["Name"] = "Test"
            };
            var request = client.Post("http://example.com/")
                .WithJsonBody(payload);

            Assert.AreEqual("application/json", request.Headers.Get("Content-Type"));
            Assert.IsFalse(request.Body.IsEmpty);
            Assert.IsTrue(request.Body.Length > 0);
        }

#if TURBOHTTP_USE_SYSTEM_TEXT_JSON
        [Test]
        public void RequestBuilder_WithJsonBody_WithOptions_AcceptsJsonSerializerOptions()
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = false
            };

            using var client = new UHttpClient();
            var request = client.Post("http://example.com/")
                .WithJsonBody(new { Name = "Test" }, options);

            Assert.AreEqual("application/json", request.Headers.Get("Content-Type"));
            Assert.IsFalse(request.Body.IsEmpty);
            Assert.IsTrue(request.Body.Length > 0);
        }
#endif

        [Test]
        public void RequestBuilder_WithTimeout_OverridesDefault()
        {
            using var client = new UHttpClient(new UHttpClientOptions { DefaultTimeout = TimeSpan.FromSeconds(10) });
            var request = client.Get("http://example.com/")
                .WithTimeout(TimeSpan.FromSeconds(2));
            Assert.AreEqual(TimeSpan.FromSeconds(2), request.Timeout);
        }

        [Test]
        public void RequestBuilder_WithBearerToken_SetsAuthorizationHeader()
        {
            using var client = new UHttpClient();
            var request = client.Get("http://example.com/")
                .WithBearerToken("token123");
            Assert.AreEqual("Bearer token123", request.Headers.Get("Authorization"));
        }

        [Test]
        public void ClientOptions_Clone_ProducesIndependentCopy()
        {
            var options = new UHttpClientOptions();
            options.DefaultHeaders.Set("X-Test", "A");
            var clone = options.Clone();

            options.DefaultHeaders.Set("X-Test", "B");

            Assert.AreEqual("A", clone.DefaultHeaders.Get("X-Test"));
            Assert.AreEqual("B", options.DefaultHeaders.Get("X-Test"));
        }

        [Test]
        public void ClientOptions_Clone_CopiesHttp2MaxDecodedHeaderBytes()
        {
            var options = new UHttpClientOptions
            {
                Http2 = new Http2Options { MaxDecodedHeaderBytes = 512 * 1024 }
            };

            var clone = options.Clone();

            Assert.AreEqual(512 * 1024, clone.Http2.MaxDecodedHeaderBytes);
        }

        [Test]
        public void Client_ImplementsIDisposable()
        {
            using var client = new UHttpClient();
            Assert.IsTrue(client is IDisposable);
        }

        [Test]
        public void HttpTransportFactory_Default_ReturnsRawSocketTransport()
        {
            var transport = HttpTransportFactory.Default;
            Assert.IsNotNull(transport);
            Assert.IsInstanceOf<RawSocketTransport>(transport);
        }

        [Test]
        public void HttpTransportFactory_Default_CalledTwice_ReturnsSameInstance()
        {
            var first = HttpTransportFactory.Default;
            var second = HttpTransportFactory.Default;
            Assert.AreSame(first, second);
        }

        [Test]
        public void RequestBuilder_WithJsonBodyString_SetsContentTypeAndBody()
        {
            using var client = new UHttpClient();
            var request = client.Post("http://example.com/")
                .WithJsonBody("{\"key\":\"value\"}");

            Assert.AreEqual("application/json", request.Headers.Get("Content-Type"));
            Assert.IsFalse(request.Body.IsEmpty);
            Assert.IsTrue(request.Body.Length > 0);
        }

        [Test]
        public void ClientOptions_SnapshotAtConstruction_MutationsDoNotAffectClient()
        {
            var options = new UHttpClientOptions
            {
                BaseUrl = "https://example.com/",
                DefaultTimeout = TimeSpan.FromSeconds(5)
            };
            options.DefaultHeaders.Set("X-Default", "A");

            using var client = new UHttpClient(options);

            options.BaseUrl = "https://mutated.com/";
            options.DefaultTimeout = TimeSpan.FromSeconds(20);
            options.DefaultHeaders.Set("X-Default", "B");

            var request = client.Get("path");
            Assert.AreEqual("https://example.com/path", request.Uri.ToString());
            Assert.AreEqual(TimeSpan.FromSeconds(5), request.Timeout);
            Assert.AreEqual("A", request.Headers.Get("X-Default"));
        }

        [Test]
        public void RequestBuilder_WithoutWithTimeout_UsesOptionsDefaultTimeout()
        {
            using var client = new UHttpClient(new UHttpClientOptions
            {
                DefaultTimeout = TimeSpan.FromSeconds(7)
            });
            var request = client.Get("http://example.com/");
            Assert.AreEqual(TimeSpan.FromSeconds(7), request.Timeout);
        }
    }
}
