using System.IO;
using System.Text;
using System.Threading;
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
    }
}
