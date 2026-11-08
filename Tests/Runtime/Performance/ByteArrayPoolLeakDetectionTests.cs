using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Http1;

namespace TurboHTTP.Tests.Performance
{
    /// <summary>
    /// The byte-buffer leak oracle of the soak harness (#292): every runtime byte-array rent and
    /// return goes through <see cref="ByteArrayPool"/>, so a rent that is never returned shows up as
    /// <see cref="ByteArrayPoolDiagnostics.ActiveCount"/> above the baseline. The counters are static
    /// and shared by the whole run, so every assertion is a delta from a snapshot taken by the test.
    /// </summary>
    public class ByteArrayPoolLeakDetectionTests
    {
        private static long Active() => ByteArrayPool.GetDiagnostics().ActiveCount;

        [Test]
        public void Rent_NotReturned_IsReportedActive_UntilReturned()
        {
            long baseline = Active();

            var buffer = ByteArrayPool.Rent(256);
            Assert.That(Active() - baseline, Is.EqualTo(1), "an unreturned rent must be outstanding");

            ByteArrayPool.Return(buffer);
            Assert.That(Active() - baseline, Is.EqualTo(0));
        }

        [Test]
        public void Abandon_LeavesArrayToGc_IsNotOutstanding()
        {
            var before = ByteArrayPool.GetDiagnostics();

            var buffer = ByteArrayPool.Rent(128);
            ByteArrayPool.Abandon(buffer);

            var after = ByteArrayPool.GetDiagnostics();
            Assert.That(after.ActiveCount - before.ActiveCount, Is.EqualTo(0));
            Assert.That(after.AbandonCount - before.AbandonCount, Is.EqualTo(1));
            Assert.That(after.ReturnCount - before.ReturnCount, Is.EqualTo(0), "an abandoned array is not returned");
        }

        [Test]
        public void BufferedStreamReader_AbandonedRead_DisposeCountsBufferAsAbandoned()
        {
            AssertAsync.Run(async () =>
            {
                var before = ByteArrayPool.GetDiagnostics();
                var stream = new StuckReadStream();
                using var cts = new CancellationTokenSource();
                var reader = new Http11ResponseParser.BufferedStreamReader(stream);
                Assert.That(Active() - before.ActiveCount, Is.EqualTo(1));

                var wait = reader.EnsureBufferedAsync(cts.Token);
                await stream.ReadPending.ConfigureAwait(false);
                cts.Cancel();
                try
                {
                    await wait.ConfigureAwait(false);
                    Assert.Fail("the cancelled read must fail");
                }
                catch (OperationCanceledException)
                {
                }

                // The abandoned read still targets the buffer, so Dispose leaves it to the GC: that is
                // deliberate, not a leak, and must not stay outstanding.
                reader.Dispose();
                var after = ByteArrayPool.GetDiagnostics();
                Assert.That(after.ActiveCount - before.ActiveCount, Is.EqualTo(0));
                Assert.That(after.AbandonCount - before.AbandonCount, Is.EqualTo(1));
                stream.EndRead();
            }, TimeSpan.FromSeconds(15));
        }

        [Test]
        public void ZeroLengthRentAndReturn_AreNotCounted()
        {
            long baseline = Active();

            var empty = ByteArrayPool.Rent(0);
            ByteArrayPool.Return(empty);
            ByteArrayPool.Return(null);

            Assert.That(Active() - baseline, Is.EqualTo(0));
        }

        [Test]
        public void Http11FixedBody_NotReleased_IsReportedActive_ReleasedBackToBaseline()
        {
            AssertAsync.Run(async () =>
            {
                long baseline = Active();
                var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");
                ParsedResponse parsed;
                using (var stream = new MemoryStream(bytes))
                    parsed = await Http11ResponseParser.ParseAsync(stream, HttpMethod.GET, CancellationToken.None);

                Assert.That(parsed.BodyFromPool, Is.True, "fixed-length bodies are read into a pooled buffer");
                Assert.That(Active() - baseline, Is.EqualTo(1),
                    "the transport's pooled body buffer must be counted while the response holds it");

                parsed.ReleaseBodyBuffers();
                Assert.That(Active() - baseline, Is.EqualTo(0), "releasing the body must return the buffer");
            });
        }

        [Test]
        public void PooledWriterDetachedOwner_NotDisposed_IsReportedActive_DisposedBackToBaseline()
        {
            long baseline = Active();

            var writer = new PooledArrayBufferWriter(64);
            var span = writer.GetSpan(100);
            span.Slice(0, 100).Fill(0x5A);
            writer.Advance(100);
            var owner = writer.DetachAsOwner();
            writer.Dispose();

            Assert.That(owner.Memory.Length, Is.EqualTo(100));
            Assert.That(Active() - baseline, Is.EqualTo(1),
                "the detached owner holds the writer's buffer until it is disposed");

            owner.Dispose();
            owner.Dispose();
            Assert.That(Active() - baseline, Is.EqualTo(0), "disposing the owner returns the buffer exactly once");
        }

        [Test]
        public void ArrayPoolMemoryOwnerOfByte_RentsAndReturnsThroughCountingPool()
        {
            long baseline = Active();

            var owner = ArrayPoolMemoryOwner<byte>.Rent(32);
            Assert.That(Active() - baseline, Is.EqualTo(1));

            owner.Dispose();
            Assert.That(Active() - baseline, Is.EqualTo(0));
        }

        /// <summary>One read that ignores its token; it ends only when the test calls <see cref="EndRead"/>.</summary>
        private sealed class StuckReadStream : Stream
        {
            private readonly TaskCompletionSource<int> _read = new TaskCompletionSource<int>();
            private readonly TaskCompletionSource<bool> _readPending =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _readCount;

            public Task ReadPending => _readPending.Task;

            public void EndRead() => _read.TrySetResult(0);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _readCount) > 1)
                    return Task.FromResult(0);

                _readPending.TrySetResult(true);
                return _read.Task;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
