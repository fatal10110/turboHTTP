#if TURBOHTTP_UNITASK
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.UniTask;

namespace TurboHTTP.Tests.UniTaskAdapter
{
    /// <summary>
    /// The UniTask adapters cancel the request through its token, and the race wrappers dispose a
    /// response that arrives after they stopped waiting (#97). Compiled only when com.cysharp.unitask is
    /// installed; the bodies run on the thread pool, so nothing here needs the player loop.
    /// </summary>
    [TestFixture]
    public class UniTaskCancellationTests
    {
        private const string Url = "https://example.test/resource";

        [Test]
        public void AsUniTask_CancellingTheToken_CancelsTheInFlightRequest()
        {
            Task.Run(async () =>
            {
                var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                var transport = new MockTransport(async (req, ctx, ct) =>
                {
                    started.TrySetResult(ct);
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                    throw new InvalidOperationException("the transport was not cancelled");
                });
                using var client = NewClient(transport);
                using var cts = new CancellationTokenSource();

                var pending = client.Get(Url).AsUniTask(cts.Token);
                var transportToken = await started.Task.ConfigureAwait(false);
                cts.Cancel();

                Exception failure = null;
                try
                {
                    using var unexpected = await pending;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Assert.That(transportToken.IsCancellationRequested, Is.True, "the transport's token");
                Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void AsUniTask_RequestTimeout_ReachesTheTransport()
        {
            Task.Run(async () =>
            {
                var seen = TimeSpan.Zero;
                var transport = new MockTransport(req =>
                {
                    seen = req.Timeout;
                    return new UHttpResponse(HttpStatusCode.OK, null, ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, req);
                });
                using var client = NewClient(transport);

                using var response = await client.Get(Url).WithTimeout(TimeSpan.FromMilliseconds(1234)).AsUniTask();

                Assert.That(seen, Is.EqualTo(TimeSpan.FromMilliseconds(1234)));
            }).GetAwaiter().GetResult();
        }

#pragma warning disable CS0618 // the obsolete race wrappers are the subject of these tests
        [Test]
        public void AttachToCancellationToken_Cancelled_DisposesTheLateResponse()
        {
            Task.Run(async () =>
            {
                using var client = NewClient(new MockTransport(HttpStatusCode.OK, body: new byte[] { 1, 2, 3 }));
                var source = new UniTaskCompletionSource<UHttpResponse>();
                using var cts = new CancellationTokenSource();

                var raced = source.Task.AttachToCancellationToken(cts.Token);
                cts.Cancel();
                Exception failure = null;
                try
                {
                    using var unexpected = await raced;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
                var late = await client.Get(Url).AsUniTask();
                source.TrySetResult(late);
                Assert.Throws<ObjectDisposedException>(() => _ = late.Body, "the late response is disposed");
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void WithTimeout_TimedOut_DisposesTheLateResponse()
        {
            Task.Run(async () =>
            {
                using var client = NewClient(new MockTransport(HttpStatusCode.OK, body: new byte[] { 1, 2, 3 }));
                var source = new UniTaskCompletionSource<UHttpResponse>();

                Exception failure = null;
                try
                {
                    using var unexpected = await source.Task.WithTimeout(TimeSpan.Zero);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Assert.That(failure, Is.InstanceOf<TimeoutException>());
                var late = await client.Get(Url).AsUniTask();
                source.TrySetResult(late);
                Assert.Throws<ObjectDisposedException>(() => _ = late.Body, "the late response is disposed");
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void WithTimeout_CompletedInTime_ReturnsTheResponseUndisposed()
        {
            Task.Run(async () =>
            {
                using var client = NewClient(new MockTransport(HttpStatusCode.OK, body: new byte[] { 1, 2, 3 }));
                var source = new UniTaskCompletionSource<UHttpResponse>();
                var expected = await client.Get(Url).AsUniTask();

                var raced = source.Task.WithTimeout(Timeout.InfiniteTimeSpan);
                source.TrySetResult(expected);
                using var response = await raced;

                Assert.That(response, Is.SameAs(expected));
                Assert.That(response.Body.Length, Is.EqualTo(3));
            }).GetAwaiter().GetResult();
        }
        [Test]
        public void WithTimeout_BeyondCancelAfterRange_ThrowsBeforeTakingTheRequest()
        {
            Task.Run(async () =>
            {
                using var client = NewClient(new MockTransport(HttpStatusCode.OK, body: new byte[] { 1, 2, 3 }));
                var source = new UniTaskCompletionSource<UHttpResponse>();

                Assert.Throws<ArgumentOutOfRangeException>(() => source.Task.WithTimeout(TimeSpan.MaxValue));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => source.Task.WithTimeout(TimeSpan.FromMilliseconds((double)int.MaxValue + 1)));

                var raced = source.Task.WithTimeout(TimeSpan.FromMilliseconds(int.MaxValue));
                var expected = await client.Get(Url).AsUniTask();
                source.TrySetResult(expected);
                using var response = await raced;

                Assert.That(response, Is.SameAs(expected), "the largest CancelAfter value is accepted");
                Assert.That(response.Body.Length, Is.EqualTo(3), "the caller still owns an undisposed response");
            }).GetAwaiter().GetResult();
        }
#pragma warning restore CS0618

        private static UHttpClient NewClient(IHttpTransport transport)
        {
            return new UHttpClient(new UHttpClientOptions { Transport = transport, DisposeTransport = true });
        }
    }
}
#endif
