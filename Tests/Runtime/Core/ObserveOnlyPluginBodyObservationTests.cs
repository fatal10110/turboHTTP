using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// An observe-only plugin may register the Observability interceptors: they forward a streamed or empty
    /// body through <see cref="ObservedResponseBodySource"/>, a byte-for-byte pass-through, which the plugin
    /// capability guard must not count as response mutation (#1235). Real mutation behind that wrapper (a
    /// replaced body, a consumed body) is still rejected without <see cref="PluginCapabilities.MutateResponses"/>.
    /// </summary>
    [TestFixture]
    public class ObserveOnlyPluginBodyObservationTests
    {
        [Test]
        public void LoggingInterceptor_ObserveOnlyPlugin_StreamedBody_IsNotRejected_AndBytesAreUnchanged()
        {
            AssertAsync.Run(async () =>
            {
                var lines = new List<string>();
                using var client = CreateClient(CreateStreamingTransport());
                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "logging",
                    new LoggingInterceptor(lines.Add),
                    PluginCapabilities.ObserveRequests));

                await using (var response = await client.Get("https://example.test/log-stream").SendStreamingAsync())
                {
                    var bytes = await ReadAllAsync(response.Body).ConfigureAwait(false);
                    Assert.AreEqual("hello", Encoding.UTF8.GetString(bytes));
                }

                Assert.That(lines, Has.Some.Contains("-> GET"), "the request line is logged");
                Assert.That(lines, Has.Some.Contains("-> 200"), "the response line is logged");
            });
        }

        [Test]
        public void LoggingInterceptor_ObserveOnlyPlugin_EmptyBufferedBody_IsNotRejected()
        {
            AssertAsync.Run(async () =>
            {
                var lines = new List<string>();
                using var client = CreateClient(new MockTransport());
                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "logging-empty",
                    new LoggingInterceptor(lines.Add),
                    PluginCapabilities.ObserveRequests));

                using var response = await client.Get("https://example.test/log-empty").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.That(lines, Has.Some.Contains("-> 200"));
            });
        }

        [Test]
        public void MetricsInterceptor_ReadOnlyMonitoringPlugin_StreamedBody_IsNotRejected_AndCountsBytes()
        {
            AssertAsync.Run(async () =>
            {
                var metrics = new MetricsInterceptor();
                using var client = CreateClient(CreateStreamingTransport());
                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "metrics",
                    metrics,
                    PluginCapabilities.ReadOnlyMonitoring));

                using var response = await client.Get("https://example.test/metrics-stream").SendBufferedAsync();

                Assert.AreEqual("hello", Encoding.UTF8.GetString(response.Body.ToArray()));
                Assert.AreEqual(1, Interlocked.Read(ref metrics.Metrics.TotalRequests));
                Assert.AreEqual(5, Interlocked.Read(ref metrics.Metrics.TotalBytesReceived));
            });
        }

        [Test]
        public void PassThroughWrapper_OverAReplacedBody_IsRejected()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient(CreateStreamingTransport());
                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "replace-under-wrapper",
                    new HandlerInterceptor(inner => new ReplacingUnderWrapperHandler(inner)),
                    PluginCapabilities.ObserveRequests));

                await AssertResponseMutationRejectedAsync(client, "https://example.test/replace-under-wrapper");
            });
        }

        [Test]
        public void PassThroughWrapper_AfterReadingTheBody_IsRejected()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient(CreateStreamingTransport());
                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "read-then-wrap",
                    new HandlerInterceptor(inner => new ReadThenWrapHandler(inner)),
                    PluginCapabilities.ObserveRequests));

                await AssertResponseMutationRejectedAsync(client, "https://example.test/read-then-wrap");
            });
        }

        [Test]
        public void PassThroughWrapper_WithChangedStatus_IsRejected()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient(CreateStreamingTransport());
                await client.RegisterPluginAsync(new InterceptorPlugin(
                    "status-under-wrapper",
                    new HandlerInterceptor(inner => new StatusChangingWrapHandler(inner)),
                    PluginCapabilities.ObserveRequests));

                await AssertResponseMutationRejectedAsync(client, "https://example.test/status-under-wrapper");
            });
        }

        private static async Task AssertResponseMutationRejectedAsync(UHttpClient client, string url)
        {
            var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
            {
                await client.Get(url).SendBufferedAsync();
            });

            Assert.IsInstanceOf<PluginException>(ex.HttpError.InnerException);
            StringAssert.Contains("MutateResponses capability", ex.HttpError.Message);
        }

        private static UHttpClient CreateClient(IHttpTransport transport)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
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
                    CreateStreamingBody("hel", "lo"),
                    context).AsTask();
            });
        }

        private static MockResponseBodySource CreateStreamingBody(params string[] chunks)
        {
            var memory = new ReadOnlyMemory<byte>[chunks.Length];
            var length = 0;
            for (var i = 0; i < chunks.Length; i++)
            {
                memory[i] = Encoding.UTF8.GetBytes(chunks[i]);
                length += memory[i].Length;
            }

            return new MockResponseBodySource(memory, length: length, trailers: HttpHeaders.Empty, exposeBufferedData: false);
        }

        private static async Task<byte[]> ReadAllAsync(ResponseBodyStream body)
        {
            var buffer = new byte[64];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), CancellationToken.None)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                total += read;
            }

            var result = new byte[total];
            Buffer.BlockCopy(buffer, 0, result, 0, total);
            return result;
        }

        private sealed class InterceptorPlugin : IHttpPlugin
        {
            private readonly IHttpInterceptor _interceptor;

            public InterceptorPlugin(string name, IHttpInterceptor interceptor, PluginCapabilities capabilities)
            {
                Name = name;
                _interceptor = interceptor;
                Capabilities = capabilities;
            }

            public string Name { get; }
            public string Version => "1.0.0";
            public PluginCapabilities Capabilities { get; }

            public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
            {
                context.RegisterInterceptor(_interceptor);
                return default;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken) => default;
        }

        private sealed class HandlerInterceptor : IHttpInterceptor
        {
            private readonly Func<IHttpHandler, IHttpHandler> _wrapHandler;

            public HandlerInterceptor(Func<IHttpHandler, IHttpHandler> wrapHandler)
            {
                _wrapHandler = wrapHandler;
            }

            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                    next(request, _wrapHandler(handler), context, cancellationToken);
            }
        }

        private abstract class ForwardingHandler : IHttpHandler
        {
            protected ForwardingHandler(IHttpHandler inner)
            {
                Inner = inner;
            }

            protected IHttpHandler Inner { get; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                Inner.OnRequestStart(request, context);
            }

            public abstract ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context);

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Inner.OnResponseError(error, context);
            }
        }

        // Swaps the body for other bytes and hides the swap behind the trusted pass-through wrapper.
        private sealed class ReplacingUnderWrapperHandler : ForwardingHandler
        {
            public ReplacingUnderWrapperHandler(IHttpHandler inner) : base(inner)
            {
            }

            public override async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                await body.DisposeAsync().ConfigureAwait(false);
                var replaced = new ObservedResponseBodySource(CreateStreamingBody("evil!"), null, null);
                await Inner.OnResponseStartAsync(statusCode, headers, replaced, context).ConfigureAwait(false);
            }
        }

        // Consumes a byte the caller then never sees, then forwards the rest through the trusted wrapper.
        private sealed class ReadThenWrapHandler : ForwardingHandler
        {
            public ReadThenWrapHandler(IHttpHandler inner) : base(inner)
            {
            }

            public override async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                await body.ReadAsync(new byte[1], CancellationToken.None).ConfigureAwait(false);
                var wrapped = new ObservedResponseBodySource(body, null, null);
                await Inner.OnResponseStartAsync(statusCode, headers, wrapped, context).ConfigureAwait(false);
            }
        }

        // Forwards the original body through the trusted wrapper but reports another status code.
        private sealed class StatusChangingWrapHandler : ForwardingHandler
        {
            public StatusChangingWrapHandler(IHttpHandler inner) : base(inner)
            {
            }

            public override ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                var wrapped = new ObservedResponseBodySource(body, null, null);
                return Inner.OnResponseStartAsync(statusCode + 1, headers, wrapped, context);
            }
        }

        private sealed class CallbackTransport : IHttpTransport
        {
            private readonly Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> _dispatch;

            public CallbackTransport(Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> dispatch)
            {
                _dispatch = dispatch;
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
