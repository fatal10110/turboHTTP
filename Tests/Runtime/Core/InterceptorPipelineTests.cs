using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Tests;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class InterceptorPipelineTests
    {
        [Test]
        public void Ordering_RequestForward_ResponseReverse()
        {
            Task.Run(async () =>
            {
                var order = new List<string>();
                var interceptors = new List<IHttpInterceptor>
                {
                    new RecordingInterceptor("A", order),
                    new RecordingInterceptor("B", order),
                    new RecordingInterceptor("C", order)
                };

                var transport = new MockTransport();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    Interceptors = interceptors
                });

                await client.Get("https://example.test/order").SendAsync();

                CollectionAssert.AreEqual(
                    new[]
                    {
                        "A:req",
                        "B:req",
                        "C:req",
                        "C:res",
                        "B:res",
                        "A:res"
                    },
                    order);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ShortCircuit_SkipsTransport()
        {
            Task.Run(async () =>
            {
                var order = new List<string>();
                var interceptors = new List<IHttpInterceptor>
                {
                    new RecordingInterceptor("A", order),
                    new ShortCircuitInterceptor("B", order, HttpStatusCode.Accepted),
                    new RecordingInterceptor("C", order)
                };

                var transport = new MockTransport();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    Interceptors = interceptors
                });

                var response = await client.Get("https://example.test/short").SendAsync();

                Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
                Assert.AreEqual(0, transport.RequestCount);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "A:req",
                        "B:req",
                        "B:res",
                        "A:res"
                    },
                    order);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void FailurePolicy_Propagate()
        {
            Task.Run(async () =>
            {
                var interceptors = new List<IHttpInterceptor>
                {
                    new ThrowingRequestInterceptor(new InvalidOperationException("request boom"))
                };

                var transport = new MockTransport();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    Interceptors = interceptors,
                    InterceptorFailurePolicy = InterceptorFailurePolicy.Propagate
                });

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await client.Get("https://example.test/fail").SendAsync();
                });

                StringAssert.Contains("request boom", ex.Message);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void FailurePolicy_ConvertToResponse()
        {
            Task.Run(async () =>
            {
                var interceptors = new List<IHttpInterceptor>
                {
                    new ThrowingRequestInterceptor(new InvalidOperationException("convert me"))
                };

                var transport = new MockTransport();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    Interceptors = interceptors,
                    InterceptorFailurePolicy = InterceptorFailurePolicy.ConvertToResponse
                });

                var response = await client.Get("https://example.test/convert").SendAsync();

                Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.IsNotNull(response.Error);
                Assert.AreEqual(UHttpErrorType.Unknown, response.Error.Type);
                Assert.AreEqual(0, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RequestClone_Isolation()
        {
            Task.Run(async () =>
            {
                var interceptors = new List<IHttpInterceptor>
                {
                    new CloneRequestInterceptor()
                };

                var transport = new MockTransport();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    Interceptors = interceptors
                });

                var original = client.Get("https://example.test/clone")
                    .WithHeader("X-Original", "1")
                    .Build();

                await client.SendAsync(original);

                Assert.AreEqual("1", original.Headers.Get("X-Original"));
                Assert.IsNull(original.Headers.Get("X-Intercepted"));
                Assert.AreEqual("yes", transport.LastRequest.Headers.Get("X-Intercepted"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Cancellation_StopsChain()
        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource();
                var order = new List<string>();
                var interceptors = new List<IHttpInterceptor>
                {
                    new CancelTokenInterceptor(order, cts),
                    new RecordingInterceptor("B", order)
                };

                var transport = new MockTransport();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    Interceptors = interceptors
                });

                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await client.Get("https://example.test/cancel").SendAsync(cts.Token);
                });

                CollectionAssert.AreEqual(new[] { "cancel:req" }, order);
                Assert.AreEqual(0, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void NoInterceptors_FastPath()
        {
            Task.Run(async () =>
            {
                string[] timelineNames = null;
                var transport = new MockTransport((request, context, ct) =>
                {
                    timelineNames = context.Timeline.Select(evt => evt.Name).ToArray();
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        context.Elapsed,
                        request));
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var response = await client.Get("https://example.test/none").SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(1, transport.RequestCount);
                Assert.IsNotNull(timelineNames);
                Assert.IsFalse(timelineNames.Any(name => name.StartsWith("interceptor.", StringComparison.Ordinal)));
            }).GetAwaiter().GetResult();
        }

        private sealed class RecordingInterceptor : IHttpInterceptor
        {
            private readonly string _name;
            private readonly List<string> _order;

            public RecordingInterceptor(string name, List<string> order)
            {
                _name = name;
                _order = order;
            }

            public ValueTask<InterceptorRequestResult> OnRequestAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                _order.Add(_name + ":req");
                return new ValueTask<InterceptorRequestResult>(InterceptorRequestResult.Continue());
            }

            public ValueTask<InterceptorResponseResult> OnResponseAsync(
                UHttpRequest request,
                UHttpResponse response,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                _order.Add(_name + ":res");
                return new ValueTask<InterceptorResponseResult>(InterceptorResponseResult.Continue());
            }
        }

        private sealed class ShortCircuitInterceptor : IHttpInterceptor
        {
            private readonly string _name;
            private readonly List<string> _order;
            private readonly HttpStatusCode _statusCode;

            public ShortCircuitInterceptor(string name, List<string> order, HttpStatusCode statusCode)
            {
                _name = name;
                _order = order;
                _statusCode = statusCode;
            }

            public ValueTask<InterceptorRequestResult> OnRequestAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                _order.Add(_name + ":req");
                var response = new UHttpResponse(
                    _statusCode,
                    new HttpHeaders(),
                    Array.Empty<byte>(),
                    context.Elapsed,
                    request);
                return new ValueTask<InterceptorRequestResult>(InterceptorRequestResult.ShortCircuit(response));
            }

            public ValueTask<InterceptorResponseResult> OnResponseAsync(
                UHttpRequest request,
                UHttpResponse response,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                _order.Add(_name + ":res");
                return new ValueTask<InterceptorResponseResult>(InterceptorResponseResult.Continue());
            }
        }

        private sealed class ThrowingRequestInterceptor : IHttpInterceptor
        {
            private readonly Exception _exception;

            public ThrowingRequestInterceptor(Exception exception)
            {
                _exception = exception;
            }

            public ValueTask<InterceptorRequestResult> OnRequestAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                throw _exception;
            }

            public ValueTask<InterceptorResponseResult> OnResponseAsync(
                UHttpRequest request,
                UHttpResponse response,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                return new ValueTask<InterceptorResponseResult>(InterceptorResponseResult.Continue());
            }
        }

        private sealed class CloneRequestInterceptor : IHttpInterceptor
        {
            public ValueTask<InterceptorRequestResult> OnRequestAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                var headers = request.Headers.Clone();
                headers.Set("X-Intercepted", "yes");
                var clone = request.WithHeaders(headers);
                return new ValueTask<InterceptorRequestResult>(InterceptorRequestResult.Continue(clone));
            }

            public ValueTask<InterceptorResponseResult> OnResponseAsync(
                UHttpRequest request,
                UHttpResponse response,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                return new ValueTask<InterceptorResponseResult>(InterceptorResponseResult.Continue());
            }
        }

        private sealed class CancelTokenInterceptor : IHttpInterceptor
        {
            private readonly List<string> _order;
            private readonly CancellationTokenSource _cts;

            public CancelTokenInterceptor(List<string> order, CancellationTokenSource cts)
            {
                _order = order;
                _cts = cts;
            }

            public ValueTask<InterceptorRequestResult> OnRequestAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                _order.Add("cancel:req");
                _cts.Cancel();
                return new ValueTask<InterceptorRequestResult>(InterceptorRequestResult.Continue());
            }

            public ValueTask<InterceptorResponseResult> OnResponseAsync(
                UHttpRequest request,
                UHttpResponse response,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                _order.Add("cancel:res");
                return new ValueTask<InterceptorResponseResult>(InterceptorResponseResult.Continue());
            }
        }
    }
}
