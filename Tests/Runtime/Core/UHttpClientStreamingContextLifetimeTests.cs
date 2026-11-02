using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Observability;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// A streaming send keeps its <see cref="RequestContext"/> running and readable until the
    /// response is disposed, so handlers that complete at body end see the full duration (#70).
    /// </summary>
    [TestFixture]
    public class UHttpClientStreamingContextLifetimeTests
    {
        private const string ProbeKey = "test.context-lifetime.probe";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private static readonly byte[] Payload = { 1, 2, 3 };

        [Test]
        public void StreamingResponse_KeepsContextRunningAndReadable_UntilDisposed()
        {
            AssertAsync.Run(async () =>
            {
                var probe = new ContextProbeInterceptor();
                using var client = CreateClient(new GatedBodySource(Payload, gated: false), probe);

                var response = await client.SendStreamingAsync(Get());
                var context = probe.Captured;
                Assert.IsNotNull(context);
                try
                {
                    Assert.AreEqual("alive", context.GetState<string>(ProbeKey), "state was cleared at headers");
                    var atHeaders = context.Elapsed;
                    Assert.IsTrue(
                        SpinWait.SpinUntil(() => context.Elapsed > atHeaders, Timeout),
                        "the context stopwatch was stopped at headers");
                }
                finally
                {
                    await response.DisposeAsync();
                }

                Assert.IsNull(context.GetState<string>(ProbeKey), "state survived response disposal");
                AssertStopped(context);
            });
        }

        [Test]
        public void StreamingMetrics_DurationIncludesBodyDeliveryDelay()
        {
            AssertAsync.Run(async () =>
            {
                var metrics = new MetricsInterceptor();
                var probe = new ContextProbeInterceptor();
                var body = new GatedBodySource(Payload, gated: true);
                using var client = CreateClient(body, metrics, probe);

                var response = await client.SendStreamingAsync(Get());
                var sinceHeaders = Stopwatch.StartNew();
                var headersElapsed = probe.Captured.Elapsed;
                TimeSpan delivered;
                try
                {
                    var buffer = new byte[16];
                    var firstRead = response.Body.ReadAsync(buffer, 0, buffer.Length);
                    await TestHelpers.AssertCompletesWithinAsync(body.ReadStarted.Task, Timeout, "body read not started");

                    // Hold the body back for longer than the request took up to its headers, so a
                    // duration measured at headers cannot reach it. A controlled delay, not a sync point.
                    var target = headersElapsed + TimeSpan.FromMilliseconds(50);
                    while (sinceHeaders.Elapsed < target)
                        await Task.Delay(5);

                    body.Release();
                    Assert.AreEqual(Payload.Length, await firstRead);
                    Assert.AreEqual(0, await response.Body.ReadAsync(buffer, 0, buffer.Length));
                    delivered = sinceHeaders.Elapsed;
                }
                finally
                {
                    await response.DisposeAsync();
                }

                Assert.AreEqual(1, metrics.Metrics.SuccessfulRequests);
                Assert.GreaterOrEqual(
                    metrics.Metrics.GetAverageResponseTimeMs(),
                    Math.Floor(delivered.TotalMilliseconds),
                    "recorded duration excludes the body transfer");
                Assert.GreaterOrEqual(probe.Captured.Elapsed, delivered);
            });
        }

        [Test]
        public void StreamingSend_TransportFailure_StopsAndClearsContextImmediately()
        {
            AssertAsync.Run(async () =>
            {
                var probe = new ContextProbeInterceptor();
                var transport = new StreamingTransport((request, handler, context, ct) =>
                {
                    handler.OnRequestStart(request, context);
                    throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError, "boom"));
                });
                using var client = CreateClient(transport, probe);

                await TestHelpers.AssertThrowsAsync<UHttpException>(() => client.SendStreamingAsync(Get()));

                Assert.IsNull(probe.Captured.GetState<string>(ProbeKey));
                AssertStopped(probe.Captured);
            });
        }

        [Test]
        public void StreamingSend_Cancelled_StopsAndClearsContextImmediately()
        {
            AssertAsync.Run(async () =>
            {
                var probe = new ContextProbeInterceptor();
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var transport = new StreamingTransport(async (request, handler, context, ct) =>
                {
                    handler.OnRequestStart(request, context);
                    entered.TrySetResult(true);
                    await Task.Delay(System.Threading.Timeout.Infinite, ct).ConfigureAwait(false);
                });
                using var client = CreateClient(transport, probe);
                using var cts = new CancellationTokenSource();

                var send = client.SendStreamingAsync(Get(), cts.Token);
                await TestHelpers.AssertCompletesWithinAsync(entered.Task, Timeout, "dispatch not entered");
                cts.Cancel();

                Exception failure = null;
                try
                {
                    await TestHelpers.AssertCompletesWithinAsync(send, Timeout, "cancelled send did not complete");
                }
                catch (AssertionException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Assert.IsNotNull(failure, "a cancelled send must fail");
                Assert.IsNull(probe.Captured.GetState<string>(ProbeKey));
                AssertStopped(probe.Captured);
            });
        }

        [Test]
        public void StreamingResponse_RepeatedDispose_CompletesContextOnce()
        {
            AssertAsync.Run(async () =>
            {
                var probe = new ContextProbeInterceptor();
                using var client = CreateClient(new GatedBodySource(Payload, gated: true), probe);

                var response = await client.SendStreamingAsync(Get());
                var context = probe.Captured;

                // Synchronous dispose of an unread body takes the abort path.
                response.Dispose();
                Assert.IsNull(context.GetState<string>(ProbeKey));
                AssertStopped(context);

                // A second completion would clear this value again.
                context.SetState(ProbeKey, "late");
                Assert.DoesNotThrow(() => response.Dispose());
                await response.DisposeAsync();
                Assert.AreEqual("late", context.GetState<string>(ProbeKey));
            });
        }

        private static void AssertStopped(RequestContext context)
        {
            var first = context.Elapsed;
            var window = Stopwatch.StartNew();
            SpinWait.SpinUntil(() => window.ElapsedMilliseconds >= 20);
            Assert.AreEqual(first, context.Elapsed, "the context stopwatch is still running");
        }

        private static UHttpRequest Get()
        {
            return new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/stream"));
        }

        private static UHttpClient CreateClient(GatedBodySource body, params IHttpInterceptor[] interceptors)
        {
            var transport = new StreamingTransport((request, handler, context, ct) =>
            {
                handler.OnRequestStart(request, context);
                return handler.OnResponseStartAsync((int)HttpStatusCode.OK, new HttpHeaders(), body, context).AsTask();
            });
            return CreateClient(transport, interceptors);
        }

        private static UHttpClient CreateClient(IHttpTransport transport, params IHttpInterceptor[] interceptors)
        {
            var options = new UHttpClientOptions
            {
                Transport = transport,
                FollowRedirects = false
            };
            options.Interceptors.AddRange(interceptors);
            return new UHttpClient(options);
        }

        private sealed class ContextProbeInterceptor : IHttpInterceptor
        {
            public RequestContext Captured { get; private set; }

            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                {
                    Captured = context;
                    context.SetState(ProbeKey, "alive");
                    return next(request, handler, context, cancellationToken);
                };
            }
        }

        private sealed class StreamingTransport : IHttpTransport
        {
            private readonly Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> _dispatch;

            internal StreamingTransport(Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> dispatch)
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

            public ValueTask<UHttpResponse> SendAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
            }
        }

        /// <summary>A body whose first read waits for <see cref="Release"/>.</summary>
        private sealed class GatedBodySource : IResponseBodySource
        {
            private readonly byte[] _payload;
            private readonly TaskCompletionSource<bool> _release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _delivered;

            public GatedBodySource(byte[] payload, bool gated)
            {
                _payload = payload;
                if (!gated)
                    _release.TrySetResult(true);
            }

            public TaskCompletionSource<bool> ReadStarted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public long? Length => _payload.Length;

            public void Release() => _release.TrySetResult(true);

            public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
            {
                data = default;
                return false;
            }

            public bool TryDetachBufferedBody(out DetachedBufferedBody body)
            {
                body = default;
                return false;
            }

            public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
            {
                ReadStarted.TrySetResult(true);
                await _release.Task.ConfigureAwait(false);
                if (Interlocked.Exchange(ref _delivered, 1) != 0)
                    return 0;

                _payload.CopyTo(destination);
                return _payload.Length;
            }

            public ValueTask DrainAsync(CancellationToken ct) => default;

            public void Abort() => _release.TrySetResult(true);

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct) =>
                new ValueTask<HttpHeaders>(HttpHeaders.Empty);

            public ValueTask DisposeAsync()
            {
                _release.TrySetResult(true);
                return default;
            }
        }
    }
}
