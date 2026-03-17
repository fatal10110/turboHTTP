using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Pipeline
{
    [TestFixture]
    public class BufferedDispatchBridgeTests
    {
        [Test]
        public async Task CollectResponseAsync_UsesDispatchCancellationTokenForBodyRead()
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

            var ex = Assert.ThrowsAsync<OperationCanceledException>(async () => await responseTask);
            Assert.AreEqual(cts.Token, source.LastReadToken);
            Assert.AreEqual(cts.Token, ex.CancellationToken);
        }

        [Test]
        public async Task CollectResponseAsync_UsesDispatchCancellationTokenForTrailers()
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

            var ex = Assert.ThrowsAsync<OperationCanceledException>(async () => await responseTask);
            Assert.AreEqual(cts.Token, source.LastTrailersToken);
            Assert.AreEqual(cts.Token, ex.CancellationToken);
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
    }
}
