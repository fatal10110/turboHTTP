using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Tests.Pipeline
{
    /// <summary>
    /// #1265: the buffered collector reads a body without its own read buffer, straight into the segments it
    /// hands to the response. A declared length that fits <see cref="BufferedResponseCollectorHandler.MaxPresizedBodyBytes"/>
    /// takes one data read and the end-of-body read; a missing, lying or over-cap length changes the read
    /// sizes only, never the collected bytes or the failure.
    /// </summary>
    [TestFixture]
    public class BufferedCollectorReadSizingTests
    {
        private const int SixteenKiB = 16 * 1024;
        private const int ThirtyTwoKiB = 32 * 1024;

        [TestCase(1024)]
        [TestCase(SixteenKiB)]
        [TestCase(ThirtyTwoKiB)]
        [TestCase(BufferedResponseCollectorHandler.MaxPresizedBodyBytes)]
        public void KnownLength_SmallBody_OneDataReadThenEnd(int size)
        {
            var source = new RecordingBodySource(Pattern(size), declaredLength: size);
            var response = Collect(source, out var activeBefore);

            using (response)
            {
                AssertBody(size, response);
                Assert.IsTrue(response.Body.IsSingleSegment, "a known small body fits one segment");
            }

            Assert.AreEqual(2, source.Reads.Count, "one data read and one end-of-body read: " + Describe(source));
            Assert.GreaterOrEqual(source.Reads[0], size, "the first read can take the whole declared body");
            Assert.IsTrue(source.Disposed);
            Assert.AreEqual(activeBefore, ByteArrayPool.GetDiagnostics().ActiveCount, "pooled buffers returned");
        }

        [Test]
        public void KnownLength_AboveMaxPresized_UsesDefaultSegments()
        {
            var size = BufferedResponseCollectorHandler.MaxPresizedBodyBytes + 1;
            var source = new RecordingBodySource(Pattern(size), declaredLength: size);
            var response = Collect(source, out var activeBefore);

            using (response)
                AssertBody(size, response);

            foreach (var length in source.Reads)
                Assert.LessOrEqual(length, SegmentedBuffer.DefaultSegmentSize, "a large declared length sizes nothing: " + Describe(source));
            Assert.AreEqual(activeBefore, ByteArrayPool.GetDiagnostics().ActiveCount);
        }

        [TestCase(ThirtyTwoKiB, 0)]
        [TestCase(ThirtyTwoKiB, 4000)]
        [TestCase(0, 0)]
        public void UnknownLength_ChunkedOrReadToEnd_CollectsEveryByte(int size, int bytesPerRead)
        {
            var source = new RecordingBodySource(Pattern(size), declaredLength: null, maxPerRead: bytesPerRead);
            var response = Collect(source, out var activeBefore);

            using (response)
                AssertBody(size, response);

            foreach (var length in source.Reads)
                Assert.LessOrEqual(length, SegmentedBuffer.DefaultSegmentSize, "no length, no pre-sizing: " + Describe(source));
            Assert.IsTrue(source.Disposed);
            Assert.AreEqual(activeBefore, ByteArrayPool.GetDiagnostics().ActiveCount);
        }

        // The read that reports the end may start a segment; the collector returns it before the response
        // is handed out, so a live response holds exactly the segments its bytes need.
        [TestCase(0, 0)]
        [TestCase(SixteenKiB, 1)]
        [TestCase(ThirtyTwoKiB, 2)]
        public void UnknownLength_EndOnSegmentBoundary_HoldsNoEmptySegment(int size, int expectedSegments)
        {
            var source = new RecordingBodySource(Pattern(size), declaredLength: null);
            var response = Collect(source, out var activeBefore);

            using (response)
            {
                AssertBody(size, response);
                var segments = 0;
                // An empty ReadOnlySequence enumerates one empty memory; only a non-empty body has segments.
                foreach (var segment in response.Body.IsEmpty ? default : response.Body)
                {
                    Assert.Greater(segment.Length, 0, "no empty segment in the body");
                    segments++;
                }

                Assert.AreEqual(expectedSegments, segments, "segments in the body");
                Assert.AreEqual(
                    activeBefore + expectedSegments,
                    ByteArrayPool.GetDiagnostics().ActiveCount,
                    "pooled arrays held while the response is alive: " + Describe(source));
            }

            Assert.IsTrue(source.Disposed);
            Assert.AreEqual(activeBefore, ByteArrayPool.GetDiagnostics().ActiveCount);
        }

        // A source that delivers fewer or more bytes than it declared: the collector keeps reading until
        // the source reports the end, as it did with a fixed read buffer.
        [TestCase(ThirtyTwoKiB, 20000)]
        [TestCase(1000, 40000)]
        [TestCase(SixteenKiB, SixteenKiB + 10)]
        [TestCase(ThirtyTwoKiB, ThirtyTwoKiB + 1)]
        public void LyingLength_CollectsWhatTheSourceDelivers(int declared, int actual)
        {
            var source = new RecordingBodySource(Pattern(actual), declaredLength: declared);
            var response = Collect(source, out var activeBefore);

            using (response)
                AssertBody(actual, response);

            Assert.IsTrue(source.Disposed);
            Assert.AreEqual(activeBefore, ByteArrayPool.GetDiagnostics().ActiveCount);
        }

        [Test]
        public void ShortBody_SourceFailure_PropagatesAndReturnsBuffers()
        {
            AssertAsync.Run(async () =>
            {
                var source = new RecordingBodySource(Pattern(20000), declaredLength: ThirtyTwoKiB, throwOnShortBody: true);
                var activeBefore = ByteArrayPool.GetDiagnostics().ActiveCount;

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    using var response = await StartCollect(source, UHttpClientOptions.DefaultMaxBufferedResponseBodySize);
                });

                Assert.IsInstanceOf<IOException>(ex.InnerException);
                Assert.IsTrue(source.Disposed);
                Assert.AreEqual(activeBefore, ByteArrayPool.GetDiagnostics().ActiveCount, "segments returned on failure");
            });
        }

        // Over the cap: the same NetworkError as before, a declared length above the cap sizes nothing, and
        // an under-declared body is still stopped by the byte count.
        [TestCase(ThirtyTwoKiB, ThirtyTwoKiB, 20000)]
        [TestCase(1000, 40000, 20000)]
        [TestCase(ThirtyTwoKiB, ThirtyTwoKiB, ThirtyTwoKiB - 1)]
        public void AboveCap_FailsWithNetworkError(int declared, int actual, long cap)
        {
            AssertAsync.Run(async () =>
            {
                var source = new RecordingBodySource(Pattern(actual), declaredLength: declared);
                var activeBefore = ByteArrayPool.GetDiagnostics().ActiveCount;

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    using var response = await StartCollect(source, cap);
                });

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                StringAssert.Contains("exceeds maximum size", ex.HttpError.Message);
                Assert.LessOrEqual(source.Reads[0], SegmentedBuffer.DefaultSegmentSize, "no pre-sizing past the cap: " + Describe(source));
                Assert.IsTrue(source.Disposed);
                Assert.AreEqual(activeBefore, ByteArrayPool.GetDiagnostics().ActiveCount, "segments returned on failure");
            });
        }

        [Test]
        public void AtCap_KnownLength_SucceedsInOneDataRead()
        {
            var source = new RecordingBodySource(Pattern(ThirtyTwoKiB), declaredLength: ThirtyTwoKiB);
            UHttpResponse response = null;
            AssertAsync.Run(async () => response = await StartCollect(source, ThirtyTwoKiB));

            using (response)
                AssertBody(ThirtyTwoKiB, response);
            Assert.AreEqual(2, source.Reads.Count, Describe(source));
        }

        [Test]
        public void Cancellation_MidBody_ReturnsBuffers()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource();
                var source = new RecordingBodySource(Pattern(ThirtyTwoKiB), declaredLength: ThirtyTwoKiB, maxPerRead: 1000)
                {
                    CancelAfterReads = 2,
                    Cancellation = cts
                };
                var activeBefore = ByteArrayPool.GetDiagnostics().ActiveCount;

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/sizing"));
                var task = BufferedDispatchBridge.CollectResponseAsync(
                    async (dispatchRequest, handler, dispatchContext, ct) =>
                        await handler.OnResponseStartAsync(200, new HttpHeaders(), source, dispatchContext),
                    request,
                    new RequestContext(request),
                    cts.Token);

                Exception failure = null;
                try
                {
                    using var response = await task;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Assert.IsInstanceOf<OperationCanceledException>(failure);
                Assert.IsTrue(source.Disposed);
                Assert.AreEqual(activeBefore, ByteArrayPool.GetDiagnostics().ActiveCount, "segments returned on cancellation");
            });
        }

        private static UHttpResponse Collect(RecordingBodySource source, out long activeBefore)
        {
            activeBefore = ByteArrayPool.GetDiagnostics().ActiveCount;
            UHttpResponse response = null;
            AssertAsync.Run(async () =>
                response = await StartCollect(source, UHttpClientOptions.DefaultMaxBufferedResponseBodySize));
            return response;
        }

        private static Task<UHttpResponse> StartCollect(RecordingBodySource source, long maxBodyBytes)
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/sizing"));
            return BufferedDispatchBridge.CollectResponseAsync(
                async (dispatchRequest, handler, dispatchContext, ct) =>
                    await handler.OnResponseStartAsync(200, new HttpHeaders(), source, dispatchContext),
                request,
                new RequestContext(request),
                CancellationToken.None,
                maxBodyBytes);
        }

        private static void AssertBody(int expectedLength, UHttpResponse response)
        {
            Assert.AreEqual(expectedLength, response.Body.Length);
            var expected = Pattern(expectedLength);
            var actual = new byte[expectedLength];
            var offset = 0;
            foreach (var segment in response.Body)
            {
                segment.Span.CopyTo(actual.AsSpan(offset));
                offset += segment.Length;
            }

            CollectionAssert.AreEqual(expected, actual);
        }

        private static string Describe(RecordingBodySource source) =>
            "read sizes [" + string.Join(", ", source.Reads) + "]";

        private static byte[] Pattern(int size)
        {
            var bytes = new byte[size];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)(i * 31 + 7);
            return bytes;
        }

        /// <summary>Serves a fixed byte sequence and records the destination size of every read.</summary>
        private sealed class RecordingBodySource : IResponseBodySource
        {
            private readonly byte[] _content;
            private readonly int _maxPerRead;
            private readonly bool _throwOnShortBody;
            private int _offset;

            public RecordingBodySource(byte[] content, long? declaredLength, int maxPerRead = 0, bool throwOnShortBody = false)
            {
                _content = content;
                Length = declaredLength;
                _maxPerRead = maxPerRead;
                _throwOnShortBody = throwOnShortBody;
            }

            public List<int> Reads { get; } = new List<int>();
            public bool Disposed { get; private set; }
            public long? Length { get; }
            public int CancelAfterReads { get; set; }
            public CancellationTokenSource Cancellation { get; set; }

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
                Reads.Add(destination.Length);
                if (CancelAfterReads > 0 && Reads.Count > CancelAfterReads)
                {
                    Cancellation.Cancel();
                    ct.ThrowIfCancellationRequested();
                }

                if (destination.IsEmpty)
                    return new ValueTask<int>(0);

                var remaining = _content.Length - _offset;
                if (remaining == 0)
                {
                    if (_throwOnShortBody && Length.HasValue && _offset < Length.Value)
                        throw new IOException("Unexpected end of stream");
                    return new ValueTask<int>(0);
                }

                var count = Math.Min(destination.Length, remaining);
                if (_maxPerRead > 0)
                    count = Math.Min(count, _maxPerRead);
                _content.AsSpan(_offset, count).CopyTo(destination.Span);
                _offset += count;
                return new ValueTask<int>(count);
            }

            public ValueTask DrainAsync(CancellationToken ct) => default;

            public void Abort()
            {
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct) => new ValueTask<HttpHeaders>(HttpHeaders.Empty);

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return default;
            }
        }
    }
}
