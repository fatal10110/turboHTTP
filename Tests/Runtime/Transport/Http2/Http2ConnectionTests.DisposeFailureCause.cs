using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    public partial class Http2ConnectionTests
    {
        /// <summary>
        /// #1296: Dispose cancels the connection token before it fails the active streams, and the read
        /// loop's pending read observes that cancellation on its own thread. The read loop used to fail
        /// the streams with the OperationCanceledException, so whenever it got there first (seen once in
        /// an Android emulator run) a stream active at dispose reported the cancellation instead of the
        /// dispose. Holding Dispose's GOAWAY write until the read loop ended forces that interleaving.
        /// </summary>
        [Test]
        public void Dispose_ReadLoopEndsBeforeStreamsFail_ActiveStreamReportsDispose()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                HoldSyncWriteStream client = null;
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(
                    cts.Token,
                    wrapClientStream: inner => client = new HoldSyncWriteStream(inner));
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                await serverCodec.ReadFrameAsync(16384, cts.Token);

                // Once the dispatch released the write lock after HEADERS, Dispose's GOAWAY gets it.
                var writeLock = (SemaphoreSlim)ReadPrivateConnectionField(conn, "_writeLock");
                await writeLock.WaitAsync(cts.Token);
                writeLock.Release();

                client.HoldNextSyncWriteUntil((Task)ReadPrivateConnectionField(conn, "_readLoopTask"));
                conn.Dispose();

                Assert.IsTrue(client.HeldWriteSawRelease, "Dispose's GOAWAY write was held until the read loop ended");
                Exception failure = null;
                try
                {
                    await responseTask;
                }
                catch (Exception e)
                {
                    failure = e;
                }

                AssertActiveStreamFailedByDispose(failure);
            });
        }

        /// <summary>
        /// A stream whose HEADERS were sent fails on local dispose as a connection failure: a NetworkError
        /// replayable only for idempotent requests (RFC 9113 Section 8.7: the server may have processed
        /// it), caused by the dispose.
        /// </summary>
        private static void AssertActiveStreamFailedByDispose(Exception failure)
        {
            var retryable = failure as Http2RetryableRequestException;
            Assert.IsNotNull(retryable, "a stream active at dispose fails as a connection failure: " + failure);
            Assert.AreEqual(UHttpErrorType.NetworkError, retryable.HttpError.Type);
            Assert.IsFalse(retryable.RequestNotProcessed, "the server read HEADERS, so the request may have been processed");
            Assert.IsInstanceOf<ObjectDisposedException>(retryable.InnerException, "the cause is the dispose: " + failure);
        }

        private static object ReadPrivateConnectionField(Http2Connection conn, string name)
        {
            var field = typeof(Http2Connection).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name);
            return field.GetValue(conn);
        }

        /// <summary>
        /// Passes everything through, except that it can hold the next synchronous write (only Dispose's
        /// best-effort GOAWAY writes synchronously) until a task ended.
        /// </summary>
        private sealed class HoldSyncWriteStream : Stream
        {
            // A watchdog only: the held task ends within milliseconds when the code is right.
            private static readonly TimeSpan HoldBound = TimeSpan.FromSeconds(10);

            private readonly Stream _inner;
            private Task _holdUntil;
            private int _heldWriteSawRelease;

            internal HoldSyncWriteStream(Stream inner)
            {
                _inner = inner;
            }

            internal bool HeldWriteSawRelease => Volatile.Read(ref _heldWriteSawRelease) != 0;

            internal void HoldNextSyncWriteUntil(Task task) => Volatile.Write(ref _holdUntil, task);

            public override bool CanRead => _inner.CanRead;
            public override bool CanWrite => _inner.CanWrite;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => _inner.ReadAsync(buffer, offset, count, cancellationToken);

            public override void Write(byte[] buffer, int offset, int count)
            {
                var hold = Interlocked.Exchange(ref _holdUntil, null);
                if (hold != null && ((IAsyncResult)hold).AsyncWaitHandle.WaitOne(HoldBound))
                    Volatile.Write(ref _heldWriteSawRelease, 1);
                _inner.Write(buffer, offset, count);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => _inner.WriteAsync(buffer, offset, count, cancellationToken);

            public override void Flush() => _inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
