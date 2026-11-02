using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Sync <see cref="UHttpStreamingResponse.Dispose"/> of a fully read response must not block its
    /// caller on a body dispose that is still running (#181).
    /// </summary>
    [TestFixture]
    public class UHttpStreamingResponseSyncDisposeTests
    {
        [Test]
        public void Dispose_WhileBodyDisposeIsPending_ReturnsAtOnce_AndReleasesWhenItCompletes()
        {
            AssertAsync.Run(async () =>
            {
                var source = new PendingDisposeBodySource("payload");
                var response = new UHttpStreamingResponse(HttpStatusCode.OK, new HttpHeaders(), source);
                var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int releaseCount = 0;
                response.AttachRequestRelease(() =>
                {
                    Interlocked.Increment(ref releaseCount);
                    released.TrySetResult(true);
                });
                await ReadToEndAsync(response);

                DisposeUnderWatchdog(response, source);

                Assert.IsFalse(source.DisposeFinished, "Dispose waited for the body dispose to finish.");
                Assert.AreEqual(1, source.DisposeAsyncCount);
                Assert.AreEqual(0, Volatile.Read(ref releaseCount), "The request is released only after the body dispose.");
                Assert.AreEqual(0, source.AbortCount);

                source.CompleteDispose();
                await released.Task;

                Assert.AreEqual(1, Volatile.Read(ref releaseCount));
                Assert.AreEqual(0, source.AbortCount);

                response.Dispose();
                Assert.AreEqual(1, source.DisposeAsyncCount, "A second Dispose is a no-op.");
            });
        }

        [Test]
        public void Dispose_WhenPendingBodyDisposeFails_AbortsSource_AndStillReleases()
        {
            AssertAsync.Run(async () =>
            {
                var source = new PendingDisposeBodySource("payload");
                var response = new UHttpStreamingResponse(HttpStatusCode.OK, new HttpHeaders(), source);
                var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                response.AttachRequestRelease(() => released.TrySetResult(true));
                await ReadToEndAsync(response);

                DisposeUnderWatchdog(response, source);
                Assert.IsFalse(source.DisposeFinished, "Dispose waited for the body dispose to finish.");

                source.FailDispose(new IOException("dispose failed"));
                await released.Task;

                Assert.AreEqual(1, source.AbortCount);
            });
        }

        [Test]
        public void Dispose_WhenBodyDisposeFailsSynchronously_AbortsAndReleasesBeforeReturning()
        {
            AssertAsync.Run(async () =>
            {
                var source = new PendingDisposeBodySource("payload");
                var response = new UHttpStreamingResponse(HttpStatusCode.OK, new HttpHeaders(), source);
                int releaseCount = 0;
                response.AttachRequestRelease(() => releaseCount++);
                await ReadToEndAsync(response);
                source.FailDispose(new IOException("dispose failed"));

                response.Dispose();

                Assert.AreEqual(1, source.AbortCount);
                Assert.AreEqual(1, releaseCount);
            });
        }

        [Test]
        public void Dispose_WhenBodyDisposeCompletesSynchronously_ReleasesBeforeReturning()
        {
            AssertAsync.Run(async () =>
            {
                var source = new MockResponseBodySource(Encoding.UTF8.GetBytes("payload"), length: 7);
                var response = new UHttpStreamingResponse(HttpStatusCode.OK, new HttpHeaders(), source);
                int releaseCount = 0;
                response.AttachRequestRelease(() => releaseCount++);
                await ReadToEndAsync(response);

                response.Dispose();

                Assert.AreEqual(1, source.DisposeAsyncCount);
                Assert.AreEqual(0, source.AbortCount);
                Assert.AreEqual(1, releaseCount);
            });
        }

        /// <summary>
        /// The watchdog matters only for a Dispose that blocks: it fails the pending body dispose so
        /// such a Dispose returns and the test fails on the assertions instead of hanging. A Dispose
        /// that returns at once removes the watchdog before it can fire.
        /// </summary>
        private static void DisposeUnderWatchdog(UHttpStreamingResponse response, PendingDisposeBodySource source)
        {
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var registration = watchdog.Token.Register(
                () => source.FailDispose(new TimeoutException("Dispose blocked on the body dispose.")));
            try
            {
                response.Dispose();
            }
            finally
            {
                registration.Dispose();
            }
        }

        private static async Task ReadToEndAsync(UHttpStreamingResponse response)
        {
            var buffer = new byte[16];
            while (await response.Body.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None) > 0)
            {
            }
        }

        private sealed class PendingDisposeBodySource : IResponseBodySource
        {
            private readonly MockResponseBodySource _inner;
            private readonly TaskCompletionSource<bool> _dispose =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _disposeAsyncCount;

            public PendingDisposeBodySource(string payload)
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                _inner = new MockResponseBodySource(bytes, length: bytes.Length);
            }

            public long? Length => _inner.Length;

            public int AbortCount => _inner.AbortCount;

            public int DisposeAsyncCount => Volatile.Read(ref _disposeAsyncCount);

            public bool DisposeFinished => _dispose.Task.IsCompleted;

            public void CompleteDispose() => _dispose.TrySetResult(true);

            public void FailDispose(Exception error) => _dispose.TrySetException(error);

            public bool TryGetBufferedData(out ReadOnlyMemory<byte> data) => _inner.TryGetBufferedData(out data);

            public bool TryDetachBufferedBody(out DetachedBufferedBody body) => _inner.TryDetachBufferedBody(out body);

            public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct) =>
                _inner.ReadAsync(destination, ct);

            public ValueTask DrainAsync(CancellationToken ct) => _inner.DrainAsync(ct);

            public void Abort() => _inner.Abort();

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct) => _inner.GetTrailersAsync(ct);

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _disposeAsyncCount);
                return new ValueTask(_dispose.Task);
            }
        }
    }
}
