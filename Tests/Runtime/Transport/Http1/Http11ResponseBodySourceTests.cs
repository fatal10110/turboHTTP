using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
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

        private static Http11ResponseBodySource CreateEmptyBodySource(
            Http11ResponseBodyKind bodyKind,
            long? contentLength)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var stream = new MemoryStream(Array.Empty<byte>(), writable: false);
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

            return new Http11ResponseBodySource(
                head,
                lease,
                CancellationToken.None,
                TimeSpan.FromSeconds(30));
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
