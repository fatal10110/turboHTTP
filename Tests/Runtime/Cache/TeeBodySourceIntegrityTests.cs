using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Cache
{
    /// <summary>
    /// The streaming cache tee stores only a complete body (#496) and returns its pooled accumulator exactly once,
    /// never while a Write is in flight (#497).
    /// </summary>
    [TestFixture]
    public sealed class TeeBodySourceIntegrityTests
    {
        private static readonly TimeSpan WaitBound = TimeSpan.FromSeconds(10);

        private List<string> _messages;

        [SetUp]
        public void SetUp()
        {
            _messages = new List<string>();
            TurboDiagnostics.SetSink(message =>
            {
                lock (_messages)
                    _messages.Add(message);
            });
        }

        [TearDown]
        public void TearDown()
        {
            TurboDiagnostics.ResetSink();
        }

        [TestCase(null)]
        [TestCase(7L)]
        public void TrailersRequestedBeforeEof_DrainedBodyIsNotStored(long? length)
        {
            AssertAsync.Run(async () =>
            {
                var accumulator = new RecordingAccumulator();
                var inner = new DrainOnTrailersBodySource(length, "pay", "load");
                var tee = CreateTee(inner, accumulator);

                // UHttpStreamingResponse.GetTrailersAsync allows this before the end of the body; the inner source
                // drains the unread body itself.
                await tee.GetTrailersAsync(CancellationToken.None);
                Assert.AreEqual(1, inner.TrailerDrains);
                Assert.AreEqual(0, await tee.ReadAsync(new byte[16], CancellationToken.None));
                await tee.DisposeAsync();

                Assert.AreEqual(0, accumulator.DetachBufferCount, "A body drained past the tee was stored.");
                Assert.AreEqual(1, accumulator.DisposeCount);
            });
        }

        [Test]
        public void ZeroLengthReadThenDispose_IsNotStored()
        {
            AssertAsync.Run(async () =>
            {
                var accumulator = new RecordingAccumulator();
                var inner = new DrainOnTrailersBodySource(null, "pay", "load");
                var tee = CreateTee(inner, accumulator);

                Assert.AreEqual(0, await tee.ReadAsync(Memory<byte>.Empty, CancellationToken.None));
                await tee.DisposeAsync();

                Assert.AreEqual(0, accumulator.DetachBufferCount, "A zero-length read was taken for the end of the body.");
                Assert.AreEqual(0, inner.TrailerDrains, "Dispose drained an unread body to load trailers.");
                Assert.AreEqual(1, accumulator.DisposeCount);
            });
        }

        [Test]
        public void BodyShorterThanContentLength_IsNotStored()
        {
            AssertAsync.Run(async () =>
            {
                var accumulator = new RecordingAccumulator();
                var inner = new MockResponseBodySource(
                    new[] { (ReadOnlyMemory<byte>)Encoding.ASCII.GetBytes("pay") },
                    length: 7,
                    trailers: HttpHeaders.Empty,
                    exposeBufferedData: false);
                var tee = CreateTee(inner, accumulator);

                await ReadToEndAsync(tee);
                await tee.DisposeAsync();

                Assert.AreEqual(0, accumulator.DetachBufferCount, "A body shorter than its Content-Length was stored.");
                Assert.AreEqual(1, accumulator.DisposeCount);
            });
        }

        [TestCase(null)]
        [TestCase(7L)]
        public void CompleteBodyThenTrailers_IsStored(long? length)
        {
            AssertAsync.Run(async () =>
            {
                var accumulator = new RecordingAccumulator();
                var inner = new DrainOnTrailersBodySource(length, "pay", "load");
                var tee = CreateTee(inner, accumulator);

                Assert.AreEqual(7, await ReadToEndAsync(tee));
                await tee.GetTrailersAsync(CancellationToken.None);
                await tee.DisposeAsync();

                Assert.AreEqual(1, accumulator.DetachBufferCount);
                Assert.AreEqual("payload", accumulator.Text);
            });
        }

        [Test]
        public void BodyReadToContentLength_ThenTrailersBeforeEof_IsStored()
        {
            AssertAsync.Run(async () =>
            {
                var accumulator = new RecordingAccumulator();
                var inner = new DrainOnTrailersBodySource(7, "payload");
                var tee = CreateTee(inner, accumulator);

                Assert.AreEqual(7, await tee.ReadAsync(new byte[7], CancellationToken.None));
                await tee.GetTrailersAsync(CancellationToken.None);
                Assert.AreEqual(0, await tee.ReadAsync(new byte[7], CancellationToken.None));
                await tee.DisposeAsync();

                Assert.AreEqual(1, accumulator.DetachBufferCount);
                Assert.AreEqual("payload", accumulator.Text);
            });
        }

        [Test]
        public void HeadResponseWithContentLength_IsStoredWithoutBody()
        {
            AssertAsync.Run(async () =>
            {
                var accumulator = new RecordingAccumulator();
                var inner = new DrainOnTrailersBodySource(7);
                var tee = CreateTee(inner, accumulator, HttpMethod.HEAD);

                Assert.AreEqual(0, await ReadToEndAsync(tee));
                await tee.DisposeAsync();

                Assert.AreEqual(1, accumulator.DetachBufferCount);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AbandonDuringInFlightWrite_ReturnsAccumulatorOnceAfterWrite(bool viaDispose)
        {
            AssertAsync.Run(async () =>
            {
                var accumulator = new RecordingAccumulator { BlockWrites = true };
                var inner = new MockResponseBodySource(
                    new[] { (ReadOnlyMemory<byte>)Encoding.ASCII.GetBytes("payload") },
                    length: 7,
                    trailers: HttpHeaders.Empty,
                    exposeBufferedData: false);
                var tee = CreateTee(inner, accumulator);

                var readTask = Task.Run(async () => await tee.ReadAsync(new byte[16], CancellationToken.None));
                var entered = await Task.WhenAny(accumulator.WriteEntered.Task, Task.Delay(WaitBound));
                Assert.AreSame(accumulator.WriteEntered.Task, entered, "The read never reached the accumulator.");

                // The caller thread abandons the body while the reader is inside Write (UHttpStreamingResponse.Dispose
                // and ResponseBodyStream.Dispose call Abort; a handler may dispose).
                if (viaDispose)
                    await tee.DisposeAsync();
                else
                    tee.Abort();

                Assert.AreEqual(0, accumulator.DisposeCount, "The accumulator was returned while a Write was copying into it.");

                accumulator.ReleaseWrites();
                Assert.AreEqual(7, await readTask);

                if (!viaDispose)
                    await tee.DisposeAsync();

                Assert.AreEqual(1, accumulator.DisposeCount);
                Assert.IsFalse(accumulator.DisposedDuringWrite);
                Assert.AreEqual(0, accumulator.DetachBufferCount);
                lock (_messages)
                    Assert.That(_messages, Has.None.Contains("Tee accumulator detached after write failure"));
            });
        }

        private static TeeBodySource CreateTee(
            IResponseBodySource inner,
            RecordingAccumulator accumulator,
            HttpMethod method = HttpMethod.GET)
        {
            var owner = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
            owner.StreamingAccumulatorFactory = () => accumulator;
            var responseHeaders = new HttpHeaders();
            responseHeaders.Set("Cache-Control", "max-age=60");
            var now = DateTime.UtcNow;
            return new TeeBodySource(
                inner,
                owner,
                method,
                new Uri("https://example.test/tee"),
                new HttpHeaders(),
                "https://example.test/tee",
                200,
                responseHeaders,
                now,
                now,
                1024 * 1024);
        }

        private static async Task<int> ReadToEndAsync(IResponseBodySource body)
        {
            var buffer = new byte[4];
            var total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, CancellationToken.None)) != 0)
                total += read;

            return total;
        }

        /// <summary>Like the HTTP/1.1 source: 0 for an empty destination, and trailers drain the unread body.</summary>
        private sealed class DrainOnTrailersBodySource : IResponseBodySource
        {
            private readonly byte[] _body;
            private int _position;

            internal DrainOnTrailersBodySource(long? length, params string[] chunks)
            {
                Length = length;
                _body = Encoding.ASCII.GetBytes(string.Concat(chunks));
            }

            public long? Length { get; }

            public int TrailerDrains { get; private set; }

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
                if (destination.IsEmpty)
                    return new ValueTask<int>(0);

                var count = Math.Min(destination.Length, _body.Length - _position);
                _body.AsMemory(_position, count).CopyTo(destination);
                _position += count;
                return new ValueTask<int>(count);
            }

            public ValueTask DrainAsync(CancellationToken ct)
            {
                _position = _body.Length;
                return default;
            }

            public void Abort()
            {
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                if (_position < _body.Length)
                {
                    TrailerDrains++;
                    _position = _body.Length;
                }

                return new ValueTask<HttpHeaders>(HttpHeaders.Empty);
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }

        private sealed class RecordingAccumulator : IStreamingCacheAccumulator
        {
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
            private SegmentedBuffer _buffer = new SegmentedBuffer();
            private int _inWrite;
            private int _disposeCount;
            private int _detachBufferCount;
            private int _disposedDuringWrite;

            internal bool BlockWrites { get; set; }

            internal TaskCompletionSource<bool> WriteEntered { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal int DisposeCount => Volatile.Read(ref _disposeCount);

            internal int DetachBufferCount => Volatile.Read(ref _detachBufferCount);

            internal bool DisposedDuringWrite => Volatile.Read(ref _disposedDuringWrite) != 0;

            internal string Text { get; private set; }

            public long Length => _buffer?.Length ?? 0;

            public void Write(ReadOnlySpan<byte> chunk)
            {
                Interlocked.Exchange(ref _inWrite, 1);
                try
                {
                    if (BlockWrites)
                    {
                        WriteEntered.TrySetResult(true);
                        Assert.IsTrue(_release.Wait(WaitBound), "The test never released the Write.");
                    }

                    _buffer.Write(chunk);
                }
                finally
                {
                    Interlocked.Exchange(ref _inWrite, 0);
                }
            }

            internal void ReleaseWrites()
            {
                _release.Set();
            }

            public SegmentedBuffer DetachBuffer()
            {
                Interlocked.Increment(ref _detachBufferCount);
                var buffer = _buffer;
                _buffer = null;
                Text = Encoding.ASCII.GetString(buffer.ToArray());
                return buffer;
            }

            public void Dispose()
            {
                if (Volatile.Read(ref _inWrite) != 0)
                    Interlocked.Exchange(ref _disposedDuringWrite, 1);

                Interlocked.Increment(ref _disposeCount);
                _buffer?.Dispose();
                _buffer = null;
            }
        }
    }
}
