using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Transport;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class UHttpClientTests
    {
        private sealed class TrackingTransport : IHttpTransport
        {
            public bool Disposed { get; private set; }
            public Func<UHttpRequest, RequestContext, CancellationToken, ValueTask<UHttpResponse>> OnSendAsync { get; set; }

            public ValueTask<UHttpResponse> SendAsync(UHttpRequest request, RequestContext context, CancellationToken cancellationToken = default)
            {
                if (OnSendAsync != null)
                    return OnSendAsync(request, context, cancellationToken);

                return new ValueTask<UHttpResponse>(new UHttpResponse(
                    HttpStatusCode.OK,
                    new HttpHeaders(),
                    Array.Empty<byte>(),
                    TimeSpan.Zero,
                    request));
            }

            public void Dispose()
            {
                Disposed = true;
            }
        }

        private sealed class TrackingDisposeMiddleware : IHttpMiddleware, IDisposable
        {
            private readonly List<string> _disposeOrder;
            private readonly string _name;
            private readonly bool _throwOnDispose;

            public bool Disposed { get; private set; }

            public TrackingDisposeMiddleware(List<string> disposeOrder, string name, bool throwOnDispose = false)
            {
                _disposeOrder = disposeOrder;
                _name = name;
                _throwOnDispose = throwOnDispose;
            }

            public ValueTask<UHttpResponse> InvokeAsync(
                UHttpRequest request,
                RequestContext context,
                HttpPipelineDelegate next,
                CancellationToken cancellationToken)
            {
                return next(request, context, cancellationToken);
            }

            public void Dispose()
            {
                Disposed = true;
                _disposeOrder?.Add(_name);

                if (_throwOnDispose)
                    throw new InvalidOperationException($"{_name} dispose failed");
            }
        }

        private sealed class TrackingDisposeTransport : IHttpTransport
        {
            private readonly List<string> _disposeOrder;
            private readonly string _name;
            private readonly bool _throwOnDispose;

            public bool Disposed { get; private set; }

            public TrackingDisposeTransport(List<string> disposeOrder, string name = "transport", bool throwOnDispose = false)
            {
                _disposeOrder = disposeOrder;
                _name = name;
                _throwOnDispose = throwOnDispose;
            }

            public ValueTask<UHttpResponse> SendAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                return new ValueTask<UHttpResponse>(new UHttpResponse(
                    HttpStatusCode.OK,
                    new HttpHeaders(),
                    Array.Empty<byte>(),
                    TimeSpan.Zero,
                    request));
            }

            public void Dispose()
            {
                Disposed = true;
                _disposeOrder?.Add(_name);

                if (_throwOnDispose)
                    throw new InvalidOperationException($"{_name} dispose failed");
            }
        }

        [SetUp]
        public void SetUp()
        {
            HttpTransportFactory.Reset();
            RawSocketTransport.EnsureRegistered();
        }

        [TearDown]
        public void TearDown()
        {
            HttpTransportFactory.Reset();
        }

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
            Assert.AreEqual(HttpMethod.GET, builder.Build().Method);
        }

        [Test]
        public void Post_ReturnsBuilder()
        {
            using var client = new UHttpClient();
            var builder = client.Post("http://example.com/");
            Assert.IsNotNull(builder);
            Assert.AreEqual(HttpMethod.POST, builder.Build().Method);
        }

        [Test]
        public void Put_ReturnsBuilder()
        {
            using var client = new UHttpClient();
            var builder = client.Put("http://example.com/");
            Assert.IsNotNull(builder);
            Assert.AreEqual(HttpMethod.PUT, builder.Build().Method);
        }

        [Test]
        public void Delete_ReturnsBuilder()
        {
            using var client = new UHttpClient();
            var builder = client.Delete("http://example.com/");
            Assert.IsNotNull(builder);
            Assert.AreEqual(HttpMethod.DELETE, builder.Build().Method);
        }

        [Test]
        public void Patch_ReturnsBuilder()
        {
            using var client = new UHttpClient();
            var builder = client.Patch("http://example.com/");
            Assert.IsNotNull(builder);
            Assert.AreEqual(HttpMethod.PATCH, builder.Build().Method);
        }

        [Test]
        public void Head_ReturnsBuilder()
        {
            using var client = new UHttpClient();
            var builder = client.Head("http://example.com/");
            Assert.IsNotNull(builder);
            Assert.AreEqual(HttpMethod.HEAD, builder.Build().Method);
        }

        [Test]
        public void Options_ReturnsBuilder()
        {
            using var client = new UHttpClient();
            var builder = client.Options("http://example.com/");
            Assert.IsNotNull(builder);
            Assert.AreEqual(HttpMethod.OPTIONS, builder.Build().Method);
        }

        [Test]
        public void RequestBuilder_WithRelativeUrl_ResolvesAgainstBaseUrl()
        {
            using var client = new UHttpClient(new UHttpClientOptions { BaseUrl = "https://example.com/api" });
            var request = client.Get("users").Build();
            Assert.AreEqual("https://example.com/api/users", request.Uri.ToString());
        }

        [Test]
        public void RequestBuilder_WithAbsoluteUrl_IgnoresBaseUrl()
        {
            using var client = new UHttpClient(new UHttpClientOptions { BaseUrl = "https://example.com/api" });
            var request = client.Get("https://other.com/path").Build();
            Assert.AreEqual("https://other.com/path", request.Uri.ToString());
        }

        [Test]
        public void RequestBuilder_WithRelativeUrl_NoBaseUrl_ThrowsInvalidOperationException()
        {
            using var client = new UHttpClient(new UHttpClientOptions());
            Assert.Throws<InvalidOperationException>(() => client.Get("users").Build());
        }

        [Test]
        public void RequestBuilder_MergesDefaultHeaders_WithRequestHeaders()
        {
            var options = new UHttpClientOptions();
            options.DefaultHeaders.Set("X-Default", "A");
            options.DefaultHeaders.Set("X-Override", "Default");

            using var client = new UHttpClient(options);
            var request = client.Get("http://example.com/")
                .WithHeader("X-Override", "Request")
                .Build();

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
                .WithHeaders(headers)
                .Build();

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
                .WithJsonBody(payload)
                .Build();

            Assert.AreEqual("application/json", request.Headers.Get("Content-Type"));
            Assert.IsNotNull(request.Body);
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
                .WithJsonBody(new { Name = "Test" }, options)
                .Build();

            Assert.AreEqual("application/json", request.Headers.Get("Content-Type"));
            Assert.IsNotNull(request.Body);
            Assert.IsTrue(request.Body.Length > 0);
        }
#endif

        [Test]
        public void RequestBuilder_WithTimeout_OverridesDefault()
        {
            using var client = new UHttpClient(new UHttpClientOptions { DefaultTimeout = TimeSpan.FromSeconds(10) });
            var request = client.Get("http://example.com/")
                .WithTimeout(TimeSpan.FromSeconds(2))
                .Build();
            Assert.AreEqual(TimeSpan.FromSeconds(2), request.Timeout);
        }

        [Test]
        public void RequestBuilder_WithBearerToken_SetsAuthorizationHeader()
        {
            using var client = new UHttpClient();
            var request = client.Get("http://example.com/")
                .WithBearerToken("token123")
                .Build();
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
                Http2MaxDecodedHeaderBytes = 512 * 1024
            };

            var clone = options.Clone();

            Assert.AreEqual(512 * 1024, clone.Http2MaxDecodedHeaderBytes);
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
                .WithJsonBody("{\"key\":\"value\"}")
                .Build();

            Assert.AreEqual("application/json", request.Headers.Get("Content-Type"));
            Assert.IsNotNull(request.Body);
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

            var request = client.Get("path").Build();
            Assert.AreEqual("https://example.com/path", request.Uri.ToString());
            Assert.AreEqual(TimeSpan.FromSeconds(5), request.Timeout);
            Assert.AreEqual("A", request.Headers.Get("X-Default"));
        }

        [Test]
        public void SendAsync_TransportThrowsUHttpException_NotDoubleWrapped()        {
            Task.Run(async () =>
            {
                var expected = new UHttpException(new UHttpError(UHttpErrorType.NetworkError, "boom"));
                var transport = new TrackingTransport
                {
                    OnSendAsync = (req, ctx, ct) => throw expected
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"));
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () => await client.SendAsync(request));
                Assert.AreSame(expected, ex);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SendAsync_TransportThrowsIOException_WrappedInUHttpException()        {
            Task.Run(async () =>
            {
                var transport = new TrackingTransport
                {
                    OnSendAsync = (req, ctx, ct) => throw new IOException("fail")
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"));
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () => await client.SendAsync(request));
                Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Client_Dispose_DoesNotDisposeFactoryTransport()
        {
            var transport = new TrackingTransport();
            HttpTransportFactory.SetForTesting(transport);

            using var client = new UHttpClient();
            client.Dispose();

            Assert.IsFalse(transport.Disposed);
        }

        [Test]
        public void Client_CustomHttp2HeaderLimit_UsesOwnedFactoryTransport()
        {
            var defaultTransport = new TrackingTransport();
            var customTransport = new TrackingTransport();
            int capturedMaxDecodedHeaderBytes = -1;
            TlsBackend capturedTlsBackend = TlsBackend.Auto;

            HttpTransportFactory.Register(
                () => defaultTransport,
                tlsBackend => throw new InvalidOperationException("Backend factory should not be used."),
                (tlsBackend, http2MaxDecodedHeaderBytes) =>
                {
                    capturedTlsBackend = tlsBackend;
                    capturedMaxDecodedHeaderBytes = http2MaxDecodedHeaderBytes;
                    return customTransport;
                });

            var client = new UHttpClient(new UHttpClientOptions
            {
                Http2MaxDecodedHeaderBytes = 384 * 1024
            });

            client.Dispose();

            Assert.AreEqual(TlsBackend.Auto, capturedTlsBackend);
            Assert.AreEqual(384 * 1024, capturedMaxDecodedHeaderBytes);
            Assert.IsTrue(customTransport.Disposed);
            Assert.IsFalse(defaultTransport.Disposed);
        }

        [Test]
        public void Constructor_InvalidHttp2HeaderLimit_ThrowsArgumentOutOfRangeException()
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
                new UHttpClient(new UHttpClientOptions
                {
                    Http2MaxDecodedHeaderBytes = 0
                }));

            Assert.IsNotNull(ex);
            Assert.That(ex.ParamName, Is.EqualTo("Http2MaxDecodedHeaderBytes").Or.EqualTo("value"));
        }

        [Test]
        public void HttpTransportFactory_Register_BackwardCompatibleOverloadExists()
        {
            var method = typeof(HttpTransportFactory).GetMethod(
                "Register",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(Func<IHttpTransport>), typeof(Func<TlsBackend, IHttpTransport>) },
                modifiers: null);

            Assert.IsNotNull(method);
        }

        [Test]
        public void RawSocketTransport_BackwardCompatibleConstructorExists()
        {
            var ctor = typeof(RawSocketTransport).GetConstructor(
                new[] { typeof(TurboHTTP.Transport.Tcp.TcpConnectionPool), typeof(TlsBackend) });

            Assert.IsNotNull(ctor);
        }

        [Test]
        public void Client_Dispose_DisposesUserTransport_WhenDisposeTransportTrue()
        {
            var transport = new TrackingTransport();
            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });

            client.Dispose();
            Assert.IsTrue(transport.Disposed);
        }

        [Test]
        public void Client_Dispose_DoesNotDisposeUserTransport_WhenDisposeTransportFalse()
        {
            var transport = new TrackingTransport();
            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = false
            });

            client.Dispose();
            Assert.IsFalse(transport.Disposed);
        }

        [Test]
        public void Client_Dispose_DisposesMiddlewaresInReverseOrder_ThenTransport()
        {
            var disposeOrder = new List<string>();
            var middleware1 = new TrackingDisposeMiddleware(disposeOrder, "mw1");
            var middleware2 = new TrackingDisposeMiddleware(disposeOrder, "mw2");
            var middleware3 = new TrackingDisposeMiddleware(disposeOrder, "mw3");
            var transport = new TrackingDisposeTransport(disposeOrder);

            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true,
                Middlewares = new List<IHttpMiddleware> { middleware1, middleware2, middleware3 }
            });

            client.Dispose();

            Assert.AreEqual(new[] { "mw3", "mw2", "mw1", "transport" }, disposeOrder.ToArray());
            Assert.IsTrue(transport.Disposed);
        }

        [Test]
        public void Client_Dispose_ContinuesAfterDisposeErrors_DoesNotThrow()
        {
            var disposeOrder = new List<string>();
            var middleware1 = new TrackingDisposeMiddleware(disposeOrder, "mw1");
            var middleware2 = new TrackingDisposeMiddleware(disposeOrder, "mw2", throwOnDispose: true);
            var transport = new TrackingDisposeTransport(disposeOrder);

            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true,
                Middlewares = new List<IHttpMiddleware> { middleware1, middleware2 }
            });

            Assert.DoesNotThrow(() => client.Dispose());
            Assert.AreEqual(new[] { "mw2", "mw1", "transport" }, disposeOrder.ToArray());
            Assert.IsTrue(middleware1.Disposed);
            Assert.IsTrue(middleware2.Disposed);
            Assert.IsTrue(transport.Disposed);
            Assert.DoesNotThrow(() => client.Dispose());
        }

        [Test]
        public void SendAsync_ClearsRequestContextAfterCompletion()
        {
            Task.Run(async () =>
            {
                RequestContext capturedContext = null;
                var transport = new TrackingTransport
                {
                    OnSendAsync = (req, ctx, ct) =>
                    {
                        capturedContext = ctx;
                        ctx.SetState("k", 123);
                        ctx.RecordEvent("TransportEvent");
                        return new ValueTask<UHttpResponse>(new UHttpResponse(
                            HttpStatusCode.OK,
                            new HttpHeaders(),
                            Array.Empty<byte>(),
                            TimeSpan.Zero,
                            req));
                    }
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"));
                var response = await client.SendAsync(request);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsNotNull(capturedContext);
                Assert.AreEqual(0, capturedContext.Timeline.Count);
                Assert.AreEqual(0, capturedContext.State.Count);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RequestBuilder_WithoutWithTimeout_UsesOptionsDefaultTimeout()
        {
            using var client = new UHttpClient(new UHttpClientOptions
            {
                DefaultTimeout = TimeSpan.FromSeconds(7)
            });
            var request = client.Get("http://example.com/").Build();
            Assert.AreEqual(TimeSpan.FromSeconds(7), request.Timeout);
        }

        [Test]
        public void SendAsync_RelativeUri_ThrowsUHttpException()        {
            Task.Run(async () =>
            {
                var transport = new RawSocketTransport();
                var request = new UHttpRequest(HttpMethod.GET, new Uri("relative", UriKind.Relative));
                var context = new RequestContext(request);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await transport.SendAsync(request, context, CancellationToken.None));

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
            }).GetAwaiter().GetResult();
        }
    }
}
