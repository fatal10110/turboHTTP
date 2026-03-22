using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    [TestFixture]
    public class Http11ResponseBodySourceTests
    {
        [Test]
        public void ReadToEnd_ExceedsMaxBodySize_ThrowsUHttpException()
        {
            AssertAsync.Run(async () =>
            {
                const int chunkBytes = 64 * 1024;
                const long payloadBytes = (100L * 1024 * 1024) + 1;

                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var stream = new RepeatingReadStream(payloadBytes);
                using var head = new ParsedResponseHead(new Http11ResponseParser.BufferedStreamReader(stream))
                {
                    StatusCode = HttpStatusCode.OK,
                    Headers = HttpHeaders.Empty,
                    KeepAlive = false,
                    BodyKind = Http11ResponseBodyKind.ReadToEnd
                };

                var lease = new ConnectionLease(
                    null,
                    new SemaphoreSlim(0, 1),
                    new PooledConnection(socket, stream, "example.test", 80, false));

                await using var source = new Http11ResponseBodySource(
                    head,
                    lease,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(30));

                var buffer = new byte[chunkBytes];
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, CancellationToken.None);
                        if (read == 0)
                            break;
                    }
                });

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.That(ex.HttpError.Message, Does.Contain("Response body exceeds maximum size"));
            });
        }

        [Test]
        public void TryDetachBufferedBody_AfterReadAttemptOnEmptyBody_ReturnsFalse()
        {
            AssertAsync.Run(async () =>
            {
                await using var source = CreateEmptyBodySource(Http11ResponseBodyKind.Empty, contentLength: null);

                Assert.AreEqual(0, await source.ReadAsync(new byte[1], CancellationToken.None));
                Assert.IsFalse(source.TryDetachBufferedBody(out _));
            });
        }

        [Test]
        public void TryDetachBufferedBody_AfterDrainAttemptOnAlreadyCompletedEmptyBody_ReturnsTrue()
        {
            AssertAsync.Run(async () =>
            {
                await using var source = CreateEmptyBodySource(Http11ResponseBodyKind.ContentLength, contentLength: 0);

                await source.DrainAsync(CancellationToken.None);
                Assert.IsTrue(source.TryDetachBufferedBody(out _));
            });
        }

        [Test]
        public void GetTrailersAsync_ChunkedBody_ReturnsParsedTrailers()
        {
            AssertAsync.Run(async () =>
            {
                var bodyBytes = Encoding.ASCII.GetBytes(
                    "1\r\nA\r\n0\r\nX-Trailer: yes\r\nDigest: sha-256=abc\r\n\r\n");

                await using var source = CreateBodySource(bodyBytes, Http11ResponseBodyKind.Chunked, contentLength: null);

                var buffer = new byte[1];
                Assert.AreEqual(1, await source.ReadAsync(buffer, CancellationToken.None));
                Assert.AreEqual((byte)'A', buffer[0]);

                var trailers = await source.GetTrailersAsync(CancellationToken.None);
                Assert.AreEqual("yes", trailers.Get("X-Trailer"));
                Assert.AreEqual("sha-256=abc", trailers.Get("Digest"));
                Assert.AreEqual(0, await source.ReadAsync(buffer, CancellationToken.None));
            });
        }

        [Test]
        public void GetTrailersAsync_ChunkedBody_WithoutPriorReads_DrainsAndReturnsTrailers()
        {
            AssertAsync.Run(async () =>
            {
                var bodyBytes = Encoding.ASCII.GetBytes("1\r\nA\r\n0\r\nX-Trailer: yes\r\n\r\n");
                await using var source = CreateBodySource(bodyBytes, Http11ResponseBodyKind.Chunked, contentLength: null);

                var trailers = await source.GetTrailersAsync(CancellationToken.None);
                Assert.AreEqual("yes", trailers.Get("X-Trailer"));
                Assert.AreEqual(0, await source.ReadAsync(new byte[1], CancellationToken.None));
            });
        }

        [Test]
        public void GetTrailersAsync_ContentLengthBody_DrainsAndReturnsEmpty()
        {
            AssertAsync.Run(async () =>
            {
                await using var source = CreateBodySource(
                    Encoding.ASCII.GetBytes("Hello"),
                    Http11ResponseBodyKind.ContentLength,
                    contentLength: 5);

                var trailers = await source.GetTrailersAsync(CancellationToken.None);
                Assert.AreSame(HttpHeaders.Empty, trailers);
                Assert.AreEqual(0, await source.ReadAsync(new byte[1], CancellationToken.None));
            });
        }

        [Test]
        public void GetTrailersAsync_AfterDisposeBeforeEof_ThrowsObjectDisposedException()
        {
            AssertAsync.Run(async () =>
            {
                var bodyBytes = Encoding.ASCII.GetBytes("1\r\nA\r\n0\r\nX-Trailer: yes\r\n\r\n");
                var source = CreateBodySource(bodyBytes, Http11ResponseBodyKind.Chunked, contentLength: null);

                await source.DisposeAsync();

                AssertAsync.ThrowsAsync<ObjectDisposedException>(
                    async () => await source.GetTrailersAsync(CancellationToken.None));
            });
        }

        [Test]
        public void Abort_FaultsChunkedTrailerTask()
        {
            AssertAsync.Run(async () =>
            {
                var bodyBytes = Encoding.ASCII.GetBytes("1\r\nA\r\n0\r\nX-Trailer: yes\r\n\r\n");
                await using var source = CreateBodySource(bodyBytes, Http11ResponseBodyKind.Chunked, contentLength: null);

                source.Abort();

                AssertAsync.ThrowsAsync<ObjectDisposedException>(
                    async () => await source.GetTrailersAsync(CancellationToken.None));
            });
        }

        [Test]
        public void GetTrailersAsync_ChunkedBody_WithEmbeddedCarriageReturnInTrailer_ThrowsUHttpException()
        {
            AssertAsync.Run(async () =>
            {
                var bodyBytes = Encoding.ASCII.GetBytes("1\r\nA\r\n0\r\nX-Trailer: ok\rinjected\r\n\r\n");
                await using var source = CreateBodySource(bodyBytes, Http11ResponseBodyKind.Chunked, contentLength: null);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(
                    async () => await source.GetTrailersAsync(CancellationToken.None));
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                StringAssert.Contains("Malformed HTTP response", ex.HttpError.Message);
            });
        }

        [Test]
        public void GetTrailersAsync_ChunkedBody_TotalTrailerBytesExceedLimit_ThrowsUHttpException()
        {
            AssertAsync.Run(async () =>
            {
                var oversizedValue = new string('a', 7000);
                var bodyBytes = Encoding.ASCII.GetBytes(
                    "1\r\nA\r\n0\r\n" +
                    "X-One: " + oversizedValue + "\r\n" +
                    "X-Two: " + oversizedValue + "\r\n" +
                    "X-Three: " + oversizedValue + "\r\n" +
                    "X-Four: " + oversizedValue + "\r\n" +
                    "X-Five: " + oversizedValue + "\r\n\r\n");
                await using var source = CreateBodySource(bodyBytes, Http11ResponseBodyKind.Chunked, contentLength: null);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(
                    async () => await source.GetTrailersAsync(CancellationToken.None));
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                StringAssert.Contains("Response trailers exceed maximum size", ex.HttpError.Message);
            });
        }

        [Test]
        public void DeferredTrailerCompletion_IsAllocatedOnlyForChunkedBodies()
        {
            AssertAsync.Run(async () =>
            {
                await using var chunked = CreateBodySource(
                    Encoding.ASCII.GetBytes("0\r\n\r\n"),
                    Http11ResponseBodyKind.Chunked,
                    contentLength: null);
                await using var contentLength = CreateBodySource(
                    Encoding.ASCII.GetBytes("Hello"),
                    Http11ResponseBodyKind.ContentLength,
                    contentLength: 5);

                Assert.IsTrue(chunked.HasDeferredTrailersForTests);
                Assert.IsFalse(contentLength.HasDeferredTrailersForTests);
            });
        }

        private static Http11ResponseBodySource CreateEmptyBodySource(
            Http11ResponseBodyKind bodyKind,
            long? contentLength)
        {
            return CreateBodySource(Array.Empty<byte>(), bodyKind, contentLength);
        }

        private static Http11ResponseBodySource CreateBodySource(
            byte[] bodyBytes,
            Http11ResponseBodyKind bodyKind,
            long? contentLength)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var stream = new MemoryStream(bodyBytes ?? Array.Empty<byte>(), writable: false);
            var head = new ParsedResponseHead(new Http11ResponseParser.BufferedStreamReader(stream))
            {
                StatusCode = HttpStatusCode.OK,
                Headers = HttpHeaders.Empty,
                KeepAlive = false,
                BodyKind = bodyKind,
                ContentLength = contentLength
            };

            var lease = new ConnectionLease(
                null,
                new SemaphoreSlim(0, 1),
                new PooledConnection(socket, stream, "example.test", 80, false));

            var source = new Http11ResponseBodySource(
                head,
                lease,
                CancellationToken.None,
                TimeSpan.FromSeconds(30));
            head.Dispose();
            return source;
        }

        private sealed class RepeatingReadStream : Stream
        {
            private long _remaining;
            private bool _disposed;

            public RepeatingReadStream(long bytes)
            {
                _remaining = bytes;
            }

            public override bool CanRead => !_disposed;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(RepeatingReadStream));

                if (_remaining <= 0)
                    return 0;

                var toWrite = (int)Math.Min(count, _remaining);
                Array.Clear(buffer, offset, toWrite);
                _remaining -= toWrite;
                return toWrite;
            }

            public override int Read(Span<byte> buffer)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(RepeatingReadStream));

                if (_remaining <= 0)
                    return 0;

                var toWrite = (int)Math.Min(buffer.Length, _remaining);
                buffer.Slice(0, toWrite).Clear();
                _remaining -= toWrite;
                return toWrite;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush()
            {
            }

            protected override void Dispose(bool disposing)
            {
                _disposed = true;
                base.Dispose(disposing);
            }
        }
    }
}
