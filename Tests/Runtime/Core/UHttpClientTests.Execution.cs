using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Transport;

namespace TurboHTTP.Tests.Core
{
    public partial class UHttpClientTests
    {
        [Test]
        public void SendAsync_TransportThrowsUHttpException_NotDoubleWrapped()
        {
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
        public void SendAsync_TransportThrowsIOException_WrappedInUHttpException()
        {
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
                (tlsBackend, poolOptions, http2Options) =>
                {
                    capturedTlsBackend = tlsBackend;
                    capturedMaxDecodedHeaderBytes = http2Options.MaxDecodedHeaderBytes;
                    return customTransport;
                });

            var client = new UHttpClient(new UHttpClientOptions
            {
                Http2 = new Http2Options { MaxDecodedHeaderBytes = 384 * 1024 }
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
                    Http2 = new Http2Options { MaxDecodedHeaderBytes = 0 }
                }));

            Assert.IsNotNull(ex);
            Assert.That(ex.ParamName, Is.EqualTo("MaxDecodedHeaderBytes").Or.EqualTo("value"));
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
        public void Client_Dispose_DisposesInterceptorsInReverseOrder_ThenTransport()
        {
            var disposeOrder = new List<string>();
            var interceptor1 = new TrackingDisposeInterceptor(disposeOrder, "i1");
            var interceptor2 = new TrackingDisposeInterceptor(disposeOrder, "i2");
            var interceptor3 = new TrackingDisposeInterceptor(disposeOrder, "i3");
            var transport = new TrackingDisposeTransport(disposeOrder);

            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true,
                Interceptors = new List<IHttpInterceptor> { interceptor1, interceptor2, interceptor3 }
            });

            client.Dispose();

            Assert.AreEqual(new[] { "i3", "i2", "i1", "transport" }, disposeOrder.ToArray());
            Assert.IsTrue(transport.Disposed);
        }

        [Test]
        public void Client_Dispose_ContinuesAfterInterceptorDisposeErrors_DoesNotThrow()
        {
            var disposeOrder = new List<string>();
            var interceptor1 = new TrackingDisposeInterceptor(disposeOrder, "i1");
            var interceptor2 = new TrackingDisposeInterceptor(disposeOrder, "i2", throwOnDispose: true);
            var transport = new TrackingDisposeTransport(disposeOrder);

            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true,
                Interceptors = new List<IHttpInterceptor> { interceptor1, interceptor2 }
            });

            Assert.DoesNotThrow(() => client.Dispose());
            Assert.AreEqual(new[] { "i2", "i1", "transport" }, disposeOrder.ToArray());
            Assert.IsTrue(interceptor1.Disposed);
            Assert.IsTrue(interceptor2.Disposed);
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
        public void SendAsync_CancelledTransport_PropagatesOperationCanceledExceptionWithoutHandlerError()
        {
            Task.Run(async () =>
            {
                UHttpException observedError = null;
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new MockTransport(
                        (request, context, ct) => new ValueTask<UHttpResponse>(Task.FromCanceled<UHttpResponse>(ct)),
                        preferValueTaskHandler: true),
                    DisposeTransport = true,
                    Interceptors = new List<IHttpInterceptor>
                    {
                        new ErrorObservingInterceptor(error => observedError = error)
                    }
                });

                using var cts = new CancellationTokenSource();
                cts.Cancel();

                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await client.Get("https://example.test/cancelled").SendAsync(cts.Token);
                });

                Assert.IsNull(observedError);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SendAsync_CancelledRecordReplayPassthrough_PropagatesOperationCanceledExceptionWithoutHandlerError()
        {
            Task.Run(async () =>
            {
                UHttpException observedError = null;
                using var innerTransport = new MockTransport(
                    (request, context, ct) => new ValueTask<UHttpResponse>(Task.FromCanceled<UHttpResponse>(ct)),
                    preferValueTaskHandler: true);
                using var recordReplayTransport = new RecordReplayTransport(
                    innerTransport,
                    new RecordReplayTransportOptions
                    {
                        Mode = RecordReplayMode.Passthrough
                    });
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = recordReplayTransport,
                    DisposeTransport = true,
                    Interceptors = new List<IHttpInterceptor>
                    {
                        new ErrorObservingInterceptor(error => observedError = error)
                    }
                });

                using var cts = new CancellationTokenSource();
                cts.Cancel();

                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await client.Get("https://example.test/record-replay-cancelled").SendAsync(cts.Token);
                });

                Assert.IsNull(observedError);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SendAsync_RelativeUri_ThrowsUHttpException()
        {
            Task.Run(async () =>
            {
                var transport = new RawSocketTransport();
                var request = new UHttpRequest(HttpMethod.GET, new Uri("relative", UriKind.Relative));
                var context = new RequestContext(request);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await TransportDispatchHelper.CollectResponseAsync(
                        transport,
                        request,
                        context,
                        CancellationToken.None));

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
            }).GetAwaiter().GetResult();
        }
    }
}
