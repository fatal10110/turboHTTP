using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class PluginInterceptorCapabilityTests
    {
        [Test]
        public void RequestReplacement_WithoutMutateRequests_IsRejected()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient();
                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "replace",
                    new ReplacingInterceptor(),
                    PluginCapabilities.ObserveRequests));

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await client.Get("https://example.test/replace").SendBufferedAsync();
                });

                Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
                Assert.IsInstanceOf<PluginException>(ex.HttpError.InnerException);
                StringAssert.Contains("MutateRequests capability", ex.HttpError.Message);
            });
        }

        [Test]
        public void Redispatch_WithoutAllowRedispatch_IsRejected()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient();
                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "redispatch",
                    new RedispatchingInterceptor(),
                    PluginCapabilities.MutateRequests | PluginCapabilities.MutateResponses));

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await client.Get("https://example.test/redispatch").SendBufferedAsync();
                });

                Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
                Assert.IsInstanceOf<PluginException>(ex.HttpError.InnerException);
                StringAssert.Contains("AllowRedispatch capability", ex.HttpError.Message);
            });
        }

        [Test]
        public void Redispatch_WithAllowRedispatch_IsAllowed()
        {
            AssertAsync.Run(async () =>
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

                using var response = await client.Get("https://example.test/redispatch-ok").SendBufferedAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, transport.RequestCount);
            });
        }

        [Test]
        public void ResponseMutation_WithoutMutateResponses_IsRejected()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient();
                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "response-mutate",
                    new ResponseMutatingInterceptor(),
                    PluginCapabilities.ReadOnlyMonitoring));

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await client.Get("https://example.test/response-mutate").SendBufferedAsync();
                });

                Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
                Assert.IsInstanceOf<PluginException>(ex.HttpError.InnerException);
                StringAssert.Contains("MutateResponses capability", ex.HttpError.Message);
            });
        }

        [Test]
        public void ResponseDataMutation_WithoutMutateResponses_IsRejected_WhenPrefixAndSuffixMatch()
        {
            AssertAsync.Run(async () =>
            {
                var body = new byte[24];
                for (int i = 0; i < body.Length; i++)
                    body[i] = (byte)'A';

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new MockTransport(HttpStatusCode.OK, body: body),
                    DisposeTransport = true
                });

                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "response-data-mutate",
                    new MiddleByteMutatingInterceptor(),
                    PluginCapabilities.ReadOnlyMonitoring));

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await client.Get("https://example.test/response-data-mutate").SendBufferedAsync();
                });

                Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
                Assert.IsInstanceOf<PluginException>(ex.HttpError.InnerException);
                StringAssert.Contains("MutateResponses capability", ex.HttpError.Message);
            });
        }

        [Test]
        public void ReadOnlyMonitoring_WithCustomHandler_IsAllowed()
        {
            AssertAsync.Run(async () =>
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

                using var response = await client.Get("https://example.test/observe-custom-handler").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                CollectionAssert.AreEqual(
                    new[] { "start:200", "data:3", "end" },
                    recorder);
            });
        }

        [Test]
        public void ReadOnlyMonitoring_StreamingResponseMetadataObservation_IsAllowed()
        {
            AssertAsync.Run(async () =>
            {
                var recorder = new List<string>();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = CreateStreamingTransport(),
                    DisposeTransport = true
                });

                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "observe-streaming-metadata",
                    new StreamingMetadataObservingInterceptor(recorder),
                    PluginCapabilities.ReadOnlyMonitoring));

                await using var response = await client.Get("https://example.test/observe-streaming").SendStreamingAsync();
                var bytes = await ReadAllAsync(response.Body).ConfigureAwait(false);

                Assert.AreEqual("hello", Encoding.UTF8.GetString(bytes));
                CollectionAssert.AreEqual(
                    new[] { "start:200", "length:5" },
                    recorder);
            });
        }

        [Test]
        public void StreamingBodyWrap_WithoutMutateResponses_IsRejected()
        {
            AssertAsync.Run(async () =>
            {
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = CreateStreamingTransport(),
                    DisposeTransport = true
                });

                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "stream-wrap",
                    new StreamingBodyWrappingInterceptor(),
                    PluginCapabilities.ReadOnlyMonitoring));

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await client.Get("https://example.test/stream-wrap").SendBufferedAsync();
                });

                Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
                Assert.IsInstanceOf<PluginException>(ex.HttpError.InnerException);
                StringAssert.Contains("MutateResponses capability", ex.HttpError.Message);
            });
        }

        [Test]
        public void StreamingBodyRead_WithoutMutateResponses_IsRejected()
        {
            AssertAsync.Run(async () =>
            {
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = CreateStreamingTransport(),
                    DisposeTransport = true
                });

                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "stream-read",
                    new StreamingBodyReadingInterceptor(),
                    PluginCapabilities.ReadOnlyMonitoring));

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await client.Get("https://example.test/stream-read").SendBufferedAsync();
                });

                Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
                Assert.IsInstanceOf<PluginException>(ex.HttpError.InnerException);
                StringAssert.Contains("MutateResponses capability", ex.HttpError.Message);
            });
        }

        [Test]
        public void StreamingTrailerFetch_WithoutMutateResponses_IsRejected()
        {
            AssertAsync.Run(async () =>
            {
                var trailers = new HttpHeaders();
                trailers.Set("X-Trailer", "seen");

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new CallbackTransport((request, handler, context, cancellationToken) =>
                    {
                        handler.OnRequestStart(request, context);
                        return handler.OnResponseStartAsync(
                            (int)HttpStatusCode.OK,
                            new HttpHeaders(),
                            new MockResponseBodySource(
                                new[] { (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("hello") },
                                length: 5,
                                trailers: trailers,
                                exposeBufferedData: false),
                            context).AsTask();
                    }),
                    DisposeTransport = true
                });

                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "stream-trailers",
                    new StreamingTrailerInspectingInterceptor(),
                    PluginCapabilities.ReadOnlyMonitoring));

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await client.Get("https://example.test/stream-trailers").SendBufferedAsync();
                });

                Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
                Assert.IsInstanceOf<PluginException>(ex.HttpError.InnerException);
                StringAssert.Contains("MutateResponses capability", ex.HttpError.Message);
            });
        }

        [Test]
        public void ReadOnlyMonitoring_PassThroughTransportError_IsNotMisclassifiedAsHandleErrors()
        {
            AssertAsync.Run(async () =>
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
                    await client.Get("https://example.test/failure").SendBufferedAsync();
                });

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.IsNull(ex.HttpError.InnerException);
            });
        }

        private static UHttpClient CreateClient()
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = new MockTransport(),
                DisposeTransport = true
            });
        }

        private static IHttpTransport CreateStreamingTransport()
        {
            return new CallbackTransport((request, handler, context, cancellationToken) =>
            {
                handler.OnRequestStart(request, context);
                return handler.OnResponseStartAsync(
                    (int)HttpStatusCode.OK,
                    new HttpHeaders(),
                    new MockResponseBodySource(
                        new[]
                        {
                            (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("hel"),
                            Encoding.UTF8.GetBytes("lo")
                        },
                        length: 5,
                        trailers: HttpHeaders.Empty,
                        exposeBufferedData: false),
                    context).AsTask();
            });
        }

        private static async Task<byte[]> ReadAllAsync(ResponseBodyStream body)
        {
            var buffer = new byte[5];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await body.ReadAsync(
                        buffer.AsMemory(total, buffer.Length - total),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                total += read;
            }

            if (total == buffer.Length)
                return buffer;

            var copy = new byte[total];
            Buffer.BlockCopy(buffer, 0, copy, 0, total);
            return copy;
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
                handler.OnRequestStart(request, context);
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

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                return _inner.OnResponseStartAsync(statusCode + 1, headers, body, context);
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

        private sealed class StreamingMetadataObservingInterceptor : IHttpInterceptor
        {
            private readonly List<string> _recorder;

            public StreamingMetadataObservingInterceptor(List<string> recorder)
            {
                _recorder = recorder;
            }

            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                    next(request, new StreamingMetadataObservingHandler(handler, _recorder), context, cancellationToken);
            }
        }

        private sealed class StreamingMetadataObservingHandler : IHttpHandler
        {
            private readonly IHttpHandler _inner;
            private readonly List<string> _recorder;

            public StreamingMetadataObservingHandler(IHttpHandler inner, List<string> recorder)
            {
                _inner = inner;
                _recorder = recorder;
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                _inner.OnRequestStart(request, context);
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                _recorder.Add("start:" + statusCode);
                _recorder.Add("length:" + (body?.Length?.ToString() ?? "unknown"));
                return _inner.OnResponseStartAsync(statusCode, headers, body, context);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                _inner.OnResponseError(error, context);
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

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                _recorder.Add("start:" + statusCode);
                if (body != null && body.TryGetBufferedData(out var buffered) && !buffered.IsEmpty)
                    _recorder.Add("data:" + buffered.Length);
                _recorder.Add("end");
                return _inner.OnResponseStartAsync(statusCode, headers, body, context);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                _inner.OnResponseError(error, context);
            }
        }

        private sealed class MiddleByteMutatingInterceptor : IHttpInterceptor
        {
            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                    next(request, new MiddleByteMutatingHandler(handler), context, cancellationToken);
            }
        }

        private sealed class StreamingBodyWrappingInterceptor : IHttpInterceptor
        {
            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                    next(request, new StreamingBodyWrappingHandler(handler), context, cancellationToken);
            }
        }

        private sealed class StreamingBodyWrappingHandler : IHttpHandler
        {
            private readonly IHttpHandler _inner;

            public StreamingBodyWrappingHandler(IHttpHandler inner)
            {
                _inner = inner;
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                _inner.OnRequestStart(request, context);
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                return _inner.OnResponseStartAsync(
                    statusCode,
                    headers,
                    new PassThroughBodySource(body),
                    context);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                _inner.OnResponseError(error, context);
            }
        }

        private sealed class StreamingBodyReadingInterceptor : IHttpInterceptor
        {
            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                    next(request, new StreamingBodyReadingHandler(handler), context, cancellationToken);
            }
        }

        private sealed class StreamingBodyReadingHandler : IHttpHandler
        {
            private readonly IHttpHandler _inner;

            public StreamingBodyReadingHandler(IHttpHandler inner)
            {
                _inner = inner;
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                _inner.OnRequestStart(request, context);
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                var scratch = new byte[1];
                await body.ReadAsync(scratch, CancellationToken.None).ConfigureAwait(false);
                await _inner.OnResponseStartAsync(statusCode, headers, body, context).ConfigureAwait(false);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                _inner.OnResponseError(error, context);
            }
        }

        private sealed class StreamingTrailerInspectingInterceptor : IHttpInterceptor
        {
            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                    next(request, new StreamingTrailerInspectingHandler(handler), context, cancellationToken);
            }
        }

        private sealed class StreamingTrailerInspectingHandler : IHttpHandler
        {
            private readonly IHttpHandler _inner;

            public StreamingTrailerInspectingHandler(IHttpHandler inner)
            {
                _inner = inner;
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                _inner.OnRequestStart(request, context);
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                await body.GetTrailersAsync(CancellationToken.None).ConfigureAwait(false);
                await _inner.OnResponseStartAsync(statusCode, headers, body, context).ConfigureAwait(false);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                _inner.OnResponseError(error, context);
            }
        }

        private sealed class MiddleByteMutatingHandler : IHttpHandler
        {
            private readonly IHttpHandler _inner;

            public MiddleByteMutatingHandler(IHttpHandler inner)
            {
                _inner = inner;
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                _inner.OnRequestStart(request, context);
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                if (body == null || !body.TryGetBufferedData(out var buffered))
                {
                    await _inner.OnResponseStartAsync(statusCode, headers, body, context).ConfigureAwait(false);
                    return;
                }

                var mutated = buffered.ToArray();
                if (mutated.Length > 16)
                    mutated[mutated.Length / 2] ^= 0x01;

                var trailers = await body.GetTrailersAsync(CancellationToken.None).ConfigureAwait(false);
                await body.DisposeAsync().ConfigureAwait(false);
                await _inner.OnResponseStartAsync(
                    statusCode,
                    headers,
                    new BufferedResponseBodySource(mutated, trailers),
                    context).ConfigureAwait(false);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                _inner.OnResponseError(error, context);
            }
        }

        private sealed class PassThroughBodySource : IResponseBodySource
        {
            private readonly IResponseBodySource _inner;

            public PassThroughBodySource(IResponseBodySource inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public long? Length => _inner.Length;

            public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
            {
                return _inner.TryGetBufferedData(out data);
            }

            public bool TryDetachBufferedBody(out DetachedBufferedBody body)
            {
                return _inner.TryDetachBufferedBody(out body);
            }

            public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
            {
                return _inner.ReadAsync(destination, ct);
            }

            public ValueTask DrainAsync(CancellationToken ct)
            {
                return _inner.DrainAsync(ct);
            }

            public void Abort()
            {
                _inner.Abort();
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                return _inner.GetTrailersAsync(ct);
            }

            public ValueTask DisposeAsync()
            {
                return _inner.DisposeAsync();
            }
        }

        private sealed class CallbackTransport : IHttpTransport
        {
            private readonly Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> _dispatch;

            public CallbackTransport(Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> dispatch)
            {
                _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            }

            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                return _dispatch(request, handler, context, cancellationToken);
            }

            public void Dispose()
            {
            }
        }
    }
}
