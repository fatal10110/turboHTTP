using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Pipeline
{
    [TestFixture]
    public class BufferedDispatchBridgeTests
    {
        [Test]
        public void CollectResponseAsync_UsesDispatchCancellationTokenForBodyRead()
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/read"));
                var context = new RequestContext(request);
                var source = new ReadCancellationProbeBodySource();
                using var cts = new CancellationTokenSource();

                var responseTask = TransportDispatchHelper.CollectResponseAsync(
                    async (dispatchRequest, handler, dispatchContext, cancellationToken) =>
                    {
                        handler.OnRequestStart(dispatchRequest, dispatchContext);
                        await handler.OnResponseStartAsync(200, new HttpHeaders(), source, dispatchContext);
                    },
                    request,
                    context,
                    cts.Token);

                await source.ReadStarted.Task;
                cts.Cancel();

                var ex = AssertAsync.ThrowsAsync<OperationCanceledException>(async () => await responseTask);
                Assert.AreEqual(cts.Token, source.LastReadToken);
                Assert.AreEqual(cts.Token, ex.CancellationToken);
            });
        }

        [Test]
        public void CollectResponseAsync_UsesDispatchCancellationTokenForTrailers()
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/trailers"));
                var context = new RequestContext(request);
                var source = new TrailersCancellationProbeBodySource();
                using var cts = new CancellationTokenSource();

                var responseTask = TransportDispatchHelper.CollectResponseAsync(
                    async (dispatchRequest, handler, dispatchContext, cancellationToken) =>
                    {
                        handler.OnRequestStart(dispatchRequest, dispatchContext);
                        await handler.OnResponseStartAsync(200, new HttpHeaders(), source, dispatchContext);
                    },
                    request,
                    context,
                    cts.Token);

                await source.TrailersStarted.Task;
                cts.Cancel();

                var ex = AssertAsync.ThrowsAsync<OperationCanceledException>(async () => await responseTask);
                Assert.AreEqual(cts.Token, source.LastTrailersToken);
                Assert.AreEqual(cts.Token, ex.CancellationToken);
            });
        }

        [Test]
        public void CollectResponseAsync_DetachedBufferedBody_SkipsTrailersAndSourceDispose()
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/detached"));
                var context = new RequestContext(request);
                var source = new DetachedBodyProbeSource();

                using var response = await TransportDispatchHelper.CollectResponseAsync(
                    async (dispatchRequest, handler, dispatchContext, cancellationToken) =>
                    {
                        handler.OnRequestStart(dispatchRequest, dispatchContext);
                        await handler.OnResponseStartAsync(200, new HttpHeaders(), source, dispatchContext);
                    },
                    request,
                    context,
                    CancellationToken.None);

                Assert.AreEqual("payload", response.GetBodyAsString());
                Assert.AreEqual(0, source.ReadCalls);
                Assert.AreEqual(0, source.TrailersCalls);
                Assert.AreEqual(0, source.DisposeCalls);
                Assert.AreEqual(0, source.OwnerDisposeCalls);
            });
        }

        [Test]
        public void CollectResponseAsync_DetachedBufferedSequence_PreservesSequenceOwnership()
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/detached-sequence"));
                var context = new RequestContext(request);
                var source = new DetachedBodyProbeSource();

                UHttpResponse response = null;
                try
                {
                    response = await TransportDispatchHelper.CollectResponseAsync(
                        async (dispatchRequest, handler, dispatchContext, cancellationToken) =>
                        {
                            handler.OnRequestStart(dispatchRequest, dispatchContext);
                            await handler.OnResponseStartAsync(200, new HttpHeaders(), source, dispatchContext);
                        },
                        request,
                        context,
                        CancellationToken.None);

                    Assert.IsFalse(response.Body.IsSingleSegment);
                    Assert.AreEqual("payload", response.GetBodyAsString());
                }
                finally
                {
                    response?.Dispose();
                }

                Assert.AreEqual(1, source.OwnerDisposeCalls);
            });
        }

        [Test]
        public void CollectResponseAsync_DetachDeclined_FallsBackToDrainAndDispose()
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/fallback-drain"));
                var context = new RequestContext(request);
                var source = new StreamingFallbackProbeSource();

                using var response = await TransportDispatchHelper.CollectResponseAsync(
                    async (dispatchRequest, handler, dispatchContext, cancellationToken) =>
                    {
                        handler.OnRequestStart(dispatchRequest, dispatchContext);
                        await handler.OnResponseStartAsync(200, new HttpHeaders(), source, dispatchContext);
                    },
                    request,
                    context,
                    CancellationToken.None);

                Assert.AreEqual("payload", response.GetBodyAsString());
                Assert.AreSame(HttpHeaders.Empty, response.Trailers);
                Assert.AreEqual(3, source.ReadCalls);
                Assert.AreEqual(1, source.TrailersCalls);
                Assert.AreEqual(1, source.DisposeCalls);
            });
        }

        [Test]
        public void CollectResponseAsync_PropagatesBufferedTrailersToResponse()
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/propagate-trailers"));
                var context = new RequestContext(request);
                var trailers = new HttpHeaders();
                trailers.Set("X-Trailer", "yes");

                using var response = await TransportDispatchHelper.CollectResponseAsync(
                    async (dispatchRequest, handler, dispatchContext, cancellationToken) =>
                    {
                        handler.OnRequestStart(dispatchRequest, dispatchContext);
                        await handler.OnResponseStartAsync(
                            200,
                            new HttpHeaders(),
                            new MockResponseBodySource(
                                new[] { (ReadOnlyMemory<byte>)new byte[] { (byte)'p', (byte)'a', (byte)'y', (byte)'l', (byte)'o', (byte)'a', (byte)'d' } },
                                length: 7,
                                trailers: trailers,
                                exposeBufferedData: false),
                            dispatchContext);
                    },
                    request,
                    context,
                    CancellationToken.None);

                Assert.AreEqual("payload", response.GetBodyAsString());
                Assert.AreEqual("yes", response.Trailers.Get("X-Trailer"));
            });
        }

        [Test]
        public void CollectResponseAsync_LateDispatchFaultAfterBufferedResponse_FailsResponseTask()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/late-fault"));
            var context = new RequestContext(request);

            var responseTask = TransportDispatchHelper.CollectResponseAsync(
                async (dispatchRequest, handler, dispatchContext, cancellationToken) =>
                {
                    handler.OnRequestStart(dispatchRequest, dispatchContext);
                    await handler.OnResponseStartAsync(
                            200,
                            new HttpHeaders(),
                            new MockResponseBodySource(
                                new[] { (ReadOnlyMemory<byte>)new byte[] { (byte)'o', (byte)'k' } },
                                length: 2,
                                trailers: HttpHeaders.Empty,
                                exposeBufferedData: false),
                            dispatchContext)
                        .ConfigureAwait(false);

                    throw new InvalidOperationException("late fault");
                },
                request,
                context,
                CancellationToken.None);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(async () => await responseTask);
            Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
            Assert.IsInstanceOf<InvalidOperationException>(ex.HttpError.InnerException);
            StringAssert.Contains("late fault", ex.HttpError.Message);
        }

        private sealed class ReadCancellationProbeBodySource : IResponseBodySource
        {
            public readonly TaskCompletionSource<bool> ReadStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public CancellationToken LastReadToken { get; private set; }

            public long? Length => null;

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
                LastReadToken = ct;
                ReadStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return 0;
            }

            public ValueTask DrainAsync(CancellationToken ct)
            {
                return default;
            }

            public void Abort()
            {
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                return new ValueTask<HttpHeaders>(HttpHeaders.Empty);
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }

        private sealed class TrailersCancellationProbeBodySource : IResponseBodySource
        {
            public readonly TaskCompletionSource<bool> TrailersStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public CancellationToken LastTrailersToken { get; private set; }

            public long? Length => 0;

            public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
            {
                data = ReadOnlyMemory<byte>.Empty;
                return true;
            }

            public bool TryDetachBufferedBody(out DetachedBufferedBody body)
            {
                body = default;
                return false;
            }

            public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
            {
                return new ValueTask<int>(0);
            }

            public ValueTask DrainAsync(CancellationToken ct)
            {
                return default;
            }

            public void Abort()
            {
            }

            public async ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                LastTrailersToken = ct;
                TrailersStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return HttpHeaders.Empty;
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }

        private sealed class DetachedBodyProbeSource : IResponseBodySource
        {
            private readonly ProbeOwner _owner = new ProbeOwner();

            public long? Length => 7;

            public int ReadCalls { get; private set; }

            public int TrailersCalls { get; private set; }

            public int DisposeCalls { get; private set; }

            public int OwnerDisposeCalls => _owner.DisposeCalls;

            public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
            {
                data = default;
                return false;
            }

            public bool TryDetachBufferedBody(out DetachedBufferedBody body)
            {
                var first = new byte[] { (byte)'p', (byte)'a', (byte)'y' };
                var second = new byte[] { (byte)'l', (byte)'o', (byte)'a', (byte)'d' };
                var sequence = CreateSequence(
                    new ReadOnlyMemory<byte>(first),
                    new ReadOnlyMemory<byte>(second));
                body = new DetachedBufferedBody(sequence, _owner);
                return true;
            }

            public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
            {
                ReadCalls++;
                return new ValueTask<int>(0);
            }

            public ValueTask DrainAsync(CancellationToken ct)
            {
                return default;
            }

            public void Abort()
            {
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                TrailersCalls++;
                return new ValueTask<HttpHeaders>(HttpHeaders.Empty);
            }

            public ValueTask DisposeAsync()
            {
                DisposeCalls++;
                return default;
            }

            private static ReadOnlySequence<byte> CreateSequence(
                ReadOnlyMemory<byte> first,
                ReadOnlyMemory<byte> second)
            {
                var firstSegment = new TestSequenceSegment(first, 0);
                var secondSegment = new TestSequenceSegment(second, first.Length);
                firstSegment.SetNext(secondSegment);
                return new ReadOnlySequence<byte>(firstSegment, 0, secondSegment, second.Length);
            }
        }

        private sealed class StreamingFallbackProbeSource : IResponseBodySource
        {
            private int _readIndex;

            public long? Length => null;

            public int ReadCalls { get; private set; }

            public int TrailersCalls { get; private set; }

            public int DisposeCalls { get; private set; }

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

            public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                ReadCalls++;

                byte[] payload;
                switch (_readIndex++)
                {
                    case 0:
                        payload = new byte[] { (byte)'p', (byte)'a', (byte)'y' };
                        break;
                    case 1:
                        payload = new byte[] { (byte)'l', (byte)'o', (byte)'a', (byte)'d' };
                        break;
                    default:
                        return new ValueTask<int>(0);
                }

                payload.AsSpan().CopyTo(destination.Span);
                return new ValueTask<int>(payload.Length);
            }

            public ValueTask DrainAsync(CancellationToken ct)
            {
                return default;
            }

            public void Abort()
            {
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                TrailersCalls++;
                return new ValueTask<HttpHeaders>(HttpHeaders.Empty);
            }

            public ValueTask DisposeAsync()
            {
                DisposeCalls++;
                return default;
            }
        }

        private sealed class ProbeOwner : IDisposable
        {
            public int DisposeCalls { get; private set; }

            public void Dispose()
            {
                DisposeCalls++;
            }
        }

        private sealed class TestSequenceSegment : ReadOnlySequenceSegment<byte>
        {
            public TestSequenceSegment(ReadOnlyMemory<byte> memory, long runningIndex)
            {
                Memory = memory;
                RunningIndex = runningIndex;
            }

            public void SetNext(TestSequenceSegment next)
            {
                Next = next;
            }
        }
    }
}
