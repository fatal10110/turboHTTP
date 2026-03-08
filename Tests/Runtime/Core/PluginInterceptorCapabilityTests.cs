using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class PluginInterceptorCapabilityTests
    {
        [Test]
        public void RequestReplacement_WithoutMutateRequests_IsRejected()
        {
            Task.Run(async () =>
            {
                using var client = CreateClient();
                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "replace",
                    new ReplacingInterceptor(),
                    PluginCapabilities.ObserveRequests));

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await client.Get("https://example.test/replace").SendAsync();
                });

                Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
                Assert.IsInstanceOf<PluginException>(ex.HttpError.InnerException);
                StringAssert.Contains("MutateRequests capability", ex.HttpError.Message);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Redispatch_WithoutAllowRedispatch_IsRejected()
        {
            Task.Run(async () =>
            {
                using var client = CreateClient();
                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "redispatch",
                    new RedispatchingInterceptor(),
                    PluginCapabilities.MutateRequests | PluginCapabilities.MutateResponses));

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await client.Get("https://example.test/redispatch").SendAsync();
                });

                Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
                Assert.IsInstanceOf<PluginException>(ex.HttpError.InnerException);
                StringAssert.Contains("AllowRedispatch capability", ex.HttpError.Message);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Redispatch_WithAllowRedispatch_IsAllowed()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "redispatch-ok",
                    new RedispatchingInterceptor(),
                    PluginCapabilities.MutateRequests |
                    PluginCapabilities.MutateResponses |
                    PluginCapabilities.AllowRedispatch));

                using var response = await client.Get("https://example.test/redispatch-ok").SendAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ResponseMutation_WithoutMutateResponses_IsRejected()
        {
            Task.Run(async () =>
            {
                using var client = CreateClient();
                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "response-mutate",
                    new ResponseMutatingInterceptor(),
                    PluginCapabilities.ReadOnlyMonitoring));

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await client.Get("https://example.test/response-mutate").SendAsync();
                });

                Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
                Assert.IsInstanceOf<PluginException>(ex.HttpError.InnerException);
                StringAssert.Contains("MutateResponses capability", ex.HttpError.Message);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ReadOnlyMonitoring_WithCustomHandler_IsAllowed()
        {
            Task.Run(async () =>
            {
                var recorder = new List<string>();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new MockTransport(
                        HttpStatusCode.OK,
                        body: new byte[] { 1, 2, 3 }),
                    DisposeTransport = true
                });

                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "observe-custom-handler",
                    new ObservingResponseInterceptor(recorder),
                    PluginCapabilities.ReadOnlyMonitoring));

                using var response = await client.Get("https://example.test/observe-custom-handler").SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                CollectionAssert.AreEqual(
                    new[] { "start:200", "data:3", "end" },
                    recorder);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ReadOnlyMonitoring_PassThroughTransportError_IsNotMisclassifiedAsHandleErrors()
        {
            Task.Run(async () =>
            {
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new FailingTransport(),
                    DisposeTransport = true
                });

                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "observe-pass-through-error",
                    new ObservingResponseInterceptor(new List<string>()),
                    PluginCapabilities.ReadOnlyMonitoring));

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await client.Get("https://example.test/failure").SendAsync();
                });

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.IsNull(ex.HttpError.InnerException);
            }).GetAwaiter().GetResult();
        }

        private static UHttpClient CreateClient()
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = new MockTransport(),
                DisposeTransport = true
            });
        }

        private sealed class InterceptorPlugin : IHttpPlugin
        {
            private readonly IHttpInterceptor _interceptor;
            private readonly PluginCapabilities _capabilities;

            public InterceptorPlugin(string name, IHttpInterceptor interceptor, PluginCapabilities capabilities)
            {
                Name = name;
                _interceptor = interceptor;
                _capabilities = capabilities;
            }

            public string Name { get; }
            public string Version => "1.0.0";
            public PluginCapabilities Capabilities => _capabilities;

            public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
            {
                context.RegisterInterceptor(_interceptor);
                return default;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken)
            {
                return default;
            }
        }

        private sealed class FailingTransport : IHttpTransport
        {
            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    "Transport failure."));
            }

            public void Dispose()
            {
            }
        }

        private sealed class ReplacingInterceptor : IHttpInterceptor
        {
            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                {
                    var clone = request.Clone();
                    clone.WithHeader("X-Replaced", "yes");
                    context.UpdateRequest(clone);
                    return next(clone, handler, context, cancellationToken);
                };
            }
        }

        private sealed class RedispatchingInterceptor : IHttpInterceptor
        {
            public DispatchFunc Wrap(DispatchFunc next)
            {
                return async (request, handler, context, cancellationToken) =>
                {
                    await next(request, handler, context, cancellationToken).ConfigureAwait(false);
                    await next(request, handler, context, cancellationToken).ConfigureAwait(false);
                };
            }
        }

        private sealed class ResponseMutatingInterceptor : IHttpInterceptor
        {
            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                    next(request, new ResponseMutatingHandler(handler), context, cancellationToken);
            }
        }

        private sealed class ResponseMutatingHandler : IHttpHandler
        {
            private readonly IHttpHandler _inner;

            public ResponseMutatingHandler(IHttpHandler inner)
            {
                _inner = inner;
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                _inner.OnRequestStart(request, context);
            }

            public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
            {
                _inner.OnResponseStart(statusCode + 1, headers, context);
            }

            public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
            {
                _inner.OnResponseData(chunk, context);
            }

            public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
            {
                _inner.OnResponseEnd(trailers, context);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                _inner.OnResponseError(error, context);
            }
        }

        private sealed class ObservingResponseInterceptor : IHttpInterceptor
        {
            private readonly List<string> _recorder;

            public ObservingResponseInterceptor(List<string> recorder)
            {
                _recorder = recorder;
            }

            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                    next(request, new ObservingResponseHandler(handler, _recorder), context, cancellationToken);
            }
        }

        private sealed class ObservingResponseHandler : IHttpHandler
        {
            private readonly IHttpHandler _inner;
            private readonly List<string> _recorder;

            public ObservingResponseHandler(IHttpHandler inner, List<string> recorder)
            {
                _inner = inner;
                _recorder = recorder;
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                _inner.OnRequestStart(request, context);
            }

            public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
            {
                _recorder.Add("start:" + statusCode);
                _inner.OnResponseStart(statusCode, headers, context);
            }

            public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
            {
                _recorder.Add("data:" + chunk.Length);
                _inner.OnResponseData(chunk, context);
            }

            public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
            {
                _recorder.Add("end");
                _inner.OnResponseEnd(trailers, context);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                _inner.OnResponseError(error, context);
            }
        }
    }
}
