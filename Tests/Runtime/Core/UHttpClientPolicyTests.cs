using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Transport;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// #563: CreateRequest resolves relative URLs against BaseUrl on every runtime (Unix runtimes read
    /// "/path" as the implicit file URI file:///path) and rejects non-http(s) schemes.
    /// #564: the client policy (cleartext, proxy, redirects, timeout, default headers) applies to
    /// requests the client did not create and survives WithMetadata(dictionary).
    /// </summary>
    [TestFixture]
    public class UHttpClientPolicyTests
    {
        private static readonly Uri ProxyAddress = new Uri("http://proxy.example:8080");

        // ---- #563 ----

        [Test]
        public void CreateRequest_RootRelativePath_ResolvesAgainstBaseUrl()
        {
            using var client = new UHttpClient(new UHttpClientOptions { BaseUrl = "https://example.com" });

            using var plain = client.Get("/data");
            Assert.AreEqual("https", plain.Uri.Scheme);
            Assert.AreEqual("https://example.com/data", plain.Uri.AbsoluteUri);

            using var withQuery = client.Get("/api/users?x=1");
            Assert.AreEqual("https://example.com/api/users?x=1", withQuery.Uri.AbsoluteUri);
            Assert.AreEqual("/api/users", withQuery.Uri.AbsolutePath);
            Assert.AreEqual("?x=1", withQuery.Uri.Query);
        }

        [Test]
        public void CreateRequest_RootRelativePath_KeepsBaseUrlPath()
        {
            using var client = new UHttpClient(new UHttpClientOptions { BaseUrl = "https://example.com/api" });

            using var rooted = client.Get("/data?x=1");
            Assert.AreEqual("https://example.com/api/data?x=1", rooted.Uri.AbsoluteUri);

            using var relative = client.Get("data");
            Assert.AreEqual("https://example.com/api/data", relative.Uri.AbsoluteUri);
        }

        [Test]
        public void CreateRequest_BaseUrlWithTrailingSlash_RootRelativePath_NoDoubleSlash()
        {
            using var client = new UHttpClient(new UHttpClientOptions { BaseUrl = "https://example.com/api/" });

            using var request = client.Post("/users");
            Assert.AreEqual("https://example.com/api/users", request.Uri.AbsoluteUri);
        }

        [Test]
        public void CreateRequest_AbsoluteHttpUrl_UsedAsGivenWithBaseUrl()
        {
            using var client = new UHttpClient(new UHttpClientOptions { BaseUrl = "https://example.com/api" });

            using var request = client.Get("http://other.example:8080/x?y=2");
            Assert.AreEqual("http://other.example:8080/x?y=2", request.Uri.AbsoluteUri);
        }

        [TestCase("file:///etc/hosts")]
        [TestCase("ftp://example.com/file")]
        [TestCase("ws://example.com/socket")]
        public void CreateRequest_NonHttpScheme_ThrowsInvalidRequest(string url)
        {
            using var withBase = new UHttpClient(new UHttpClientOptions { BaseUrl = "https://example.com" });
            using var withoutBase = new UHttpClient(new UHttpClientOptions());

            var ex = Assert.Throws<UHttpException>(() => withBase.Get(url));
            Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
            ex = Assert.Throws<UHttpException>(() => withoutBase.Get(url));
            Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
        }

        [Test]
        public void CreateRequest_NonHttpBaseUrl_ThrowsInvalidRequest()
        {
            using var client = new UHttpClient(new UHttpClientOptions { BaseUrl = "ftp://example.com/" });

            var ex = Assert.Throws<UHttpException>(() => client.Get("/data"));
            Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
        }

        // ---- #564 ----

        [Test]
        public void SendBufferedAsync_StandaloneRequest_GetsClientPolicy_OriginalUntouched()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                using var client = new UHttpClient(CreatePolicyOptions(transport));

                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://host.example/x"));
                using (await client.SendBufferedAsync(request))
                {
                }

                var sent = transport.LastRequest;
                Assert.IsNotNull(sent);
                AssertClientPolicy(sent);
                Assert.AreEqual(TimeSpan.FromSeconds(7), sent.Timeout, "client DefaultTimeout, not the 30 s ctor default");
                Assert.AreEqual("key-1", sent.Headers.Get("X-Api-Key"));

                Assert.IsFalse(request.Headers.Contains("X-Api-Key"), "the caller's request is not mutated");
                Assert.AreEqual(0, request.Metadata.Count, "the caller's request is not mutated");
                request.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SendStreamingAsync_StandaloneRequest_GetsClientPolicy()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                using var client = new UHttpClient(CreatePolicyOptions(transport));

                using var request = new UHttpRequest(HttpMethod.GET, new Uri("http://host.example/x"));
                using (await client.SendStreamingAsync(request))
                {
                }

                var sent = transport.LastRequest;
                Assert.IsNotNull(sent);
                AssertClientPolicy(sent);
                Assert.AreEqual(TimeSpan.FromSeconds(7), sent.Timeout);
                Assert.AreEqual("key-1", sent.Headers.Get("X-Api-Key"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SendBufferedAsync_StandaloneRequest_RequestValuesOverrideClientPolicy()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                using var client = new UHttpClient(CreatePolicyOptions(transport));

                var headers = new HttpHeaders();
                headers.Set("X-Api-Key", "mine");
                using var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("https://host.example/x"),
                    headers,
                    timeout: TimeSpan.FromSeconds(3),
                    metadata: new Dictionary<string, object>
                    {
                        [RequestMetadataKeys.AllowCleartext] = true,
                        [RequestMetadataKeys.FollowRedirects] = true,
                        [RequestMetadataKeys.ProxyDisabled] = true
                    });

                using (await client.SendBufferedAsync(request))
                {
                }

                var sent = transport.LastRequest;
                Assert.AreEqual(true, sent.Metadata[RequestMetadataKeys.AllowCleartext]);
                Assert.AreEqual(true, sent.Metadata[RequestMetadataKeys.FollowRedirects]);
                Assert.AreEqual(3, sent.Metadata[RequestMetadataKeys.MaxRedirects], "keys the request does not set still come from the client");
                Assert.IsFalse(sent.Metadata.ContainsKey(RequestMetadataKeys.ProxySettings), "ProxyDisabled suppresses the client proxy");
                Assert.AreEqual(TimeSpan.FromSeconds(3), sent.Timeout);
                Assert.AreEqual("mine", sent.Headers.Get("X-Api-Key"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SendBufferedAsync_StandaloneRequest_WithTimeout_KeepsRequestTimeout()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                using var client = new UHttpClient(CreatePolicyOptions(transport));

                using var request = new UHttpRequest(HttpMethod.GET, new Uri("https://host.example/x"))
                    .WithTimeout(TimeSpan.FromSeconds(11));
                using (await client.SendBufferedAsync(request))
                {
                }

                Assert.AreEqual(TimeSpan.FromSeconds(11), transport.LastRequest.Timeout);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SendBufferedAsync_SameStandaloneRequestThroughTwoClients_EachAppliesItsOwnPolicy()
        {
            Task.Run(async () =>
            {
                var permissiveTransport = new MockTransport();
                var strictTransport = new MockTransport();
                using var permissive = new UHttpClient(new UHttpClientOptions
                {
                    Transport = permissiveTransport,
                    AllowCleartext = true,
                    DefaultTimeout = TimeSpan.FromSeconds(20)
                });
                using var strict = new UHttpClient(CreatePolicyOptions(strictTransport));

                using var request = new UHttpRequest(HttpMethod.GET, new Uri("http://host.example/x"));
                using (await permissive.SendBufferedAsync(request))
                {
                }

                using (await strict.SendBufferedAsync(request))
                {
                }

                Assert.AreEqual(true, permissiveTransport.LastRequest.Metadata[RequestMetadataKeys.AllowCleartext]);
                Assert.AreEqual(TimeSpan.FromSeconds(20), permissiveTransport.LastRequest.Timeout);
                AssertClientPolicy(strictTransport.LastRequest);
                Assert.AreEqual(TimeSpan.FromSeconds(7), strictTransport.LastRequest.Timeout);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ClientRequest_WithMetadataDictionary_KeepsClientPolicy()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                using var client = new UHttpClient(CreatePolicyOptions(transport));

                var request = client.Get("http://host.example/x")
                    .WithMetadata(new Dictionary<string, object> { ["custom"] = 1 });

                AssertClientPolicy(request);
                Assert.AreEqual(1, request.Metadata["custom"]);

                using (await request.SendBufferedAsync())
                {
                }

                AssertClientPolicy(transport.LastRequest);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ClientRequest_WithMetadataDictionary_DictionaryValuesWin()
        {
            using var client = new UHttpClient(CreatePolicyOptions(new MockTransport()));

            using var request = client.Get("http://host.example/x")
                .WithMetadata(new Dictionary<string, object>
                {
                    [RequestMetadataKeys.AllowCleartext] = true,
                    [RequestMetadataKeys.ProxyDisabled] = true
                });

            Assert.AreEqual(true, request.Metadata[RequestMetadataKeys.AllowCleartext]);
            Assert.IsFalse(request.Metadata.ContainsKey(RequestMetadataKeys.ProxySettings));
            Assert.AreEqual(false, request.Metadata[RequestMetadataKeys.FollowRedirects]);
        }

        [Test]
        public void ClientRequest_WithNullMetadata_KeepsClientPolicy()
        {
            using var client = new UHttpClient(CreatePolicyOptions(new MockTransport()));

            using var request = client.Get("http://host.example/x").WithMetadata(null);

            AssertClientPolicy(request);
        }

        [Test]
        public void StandaloneHttpRequest_ClientAllowCleartextFalse_RejectedBeforeAnySocket()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    AllowCleartext = false,
                    DefaultTimeout = TimeSpan.FromSeconds(2)
                });

                // An explicit timeout bounds the pre-fix run, which connected and waited for a response.
                using var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("http://127.0.0.1:" + port + "/plain"),
                    timeout: TimeSpan.FromSeconds(2));

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () => await client.SendBufferedAsync(request));

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type, ex.ToString());
                StringAssert.Contains("AllowCleartext", ex.HttpError.Message);
                Assert.IsFalse(listener.Pending(), "no connection may be opened");
            }
            finally
            {
                listener.Stop();
            }
        }

        private static UHttpClientOptions CreatePolicyOptions(IHttpTransport transport)
        {
            var options = new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true,
                AllowCleartext = false,
                FollowRedirects = false,
                MaxRedirects = 3,
                DefaultTimeout = TimeSpan.FromSeconds(7),
                Proxy = new ProxySettings { Address = ProxyAddress }
            };
            options.DefaultHeaders.Set("X-Api-Key", "key-1");
            return options;
        }

        private static void AssertClientPolicy(UHttpRequest request)
        {
            Assert.AreEqual(false, request.Metadata[RequestMetadataKeys.AllowCleartext], "client AllowCleartext");
            Assert.AreEqual(false, request.Metadata[RequestMetadataKeys.FollowRedirects], "client FollowRedirects");
            Assert.AreEqual(3, request.Metadata[RequestMetadataKeys.MaxRedirects], "client MaxRedirects");
            Assert.IsTrue(
                request.Metadata.TryGetValue(RequestMetadataKeys.ProxySettings, out var proxy) && proxy is ProxySettings,
                "client proxy");
            Assert.AreEqual(ProxyAddress, ((ProxySettings)proxy).Address);
        }
    }
}
